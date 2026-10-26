using System.IO;
using OpenTK.Mathematics;
using SkinTextureTool.Core.Generation;
using SkinTextureTool.Rendering;

namespace SkinTextureTool.Painting;

/// <summary>
/// Anatomical superficial veins: the main trunks (cephalic and basilic veins of the arm, the dorsal networks of
/// the hands and feet, the great and small saphenous veins, the external jugular, the thoracoepigastric veins)
/// laid along their real routes on the loaded meshes, with branches wandering off them. Routes are found from the
/// meshes themselves (limb centre lines, hands, feet, neck), then painted into each texture group's UV layout
/// through 3D brush strokes, so veins cross the seams between body, hands, feet and head.
/// </summary>
internal static class VeinNetwork
{
    private sealed record Stroke(List<(Vector3 P, Vector3 N)> Points, float RadiusStart, float RadiusEnd);

    private sealed class Surface
    {
        private readonly List<(Vector3 P, Vector3 N, string Shape)> _verts = new();
        private readonly Dictionary<(int, int, int), List<int>> _grid = new();
        private const float Cell = 1.5f;

        public Surface(IEnumerable<MeshInput> shapes)
        {
            foreach (var (s, f) in shapes)
                for (var i = 0; i < s.VertexCount; i++)
                {
                    var p = new Vector3(s.Positions[i * 3], s.Positions[i * 3 + 1], s.Positions[i * 3 + 2]);
                    var n = new Vector3(f.Normals[i * 3], f.Normals[i * 3 + 1], f.Normals[i * 3 + 2]);
                    var k = Key(p);
                    if (!_grid.TryGetValue(k, out var list)) _grid[k] = list = new List<int>();
                    list.Add(_verts.Count);
                    _verts.Add((p, n.LengthSquared > 0 ? n.Normalized() : Vector3.UnitZ, s.Name));
                }
        }

        private static (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

        public IEnumerable<(Vector3 P, Vector3 N, string Shape)> All => _verts;

        /// <summary>The surface point nearest <paramref name="p"/>, preferring ones facing <paramref name="facing"/>.</summary>
        public (Vector3 P, Vector3 N)? Snap(Vector3 p, Vector3? facing = null, float reach = 4.5f)
        {
            var best = float.MaxValue; (Vector3, Vector3)? hit = null;
            var r = (int)MathF.Ceiling(reach / Cell);
            var (cx, cy, cz) = Key(p);
            for (var x = cx - r; x <= cx + r; x++) for (var y = cy - r; y <= cy + r; y++) for (var z = cz - r; z <= cz + r; z++)
            {
                if (!_grid.TryGetValue((x, y, z), out var list)) continue;
                foreach (var i in list)
                {
                    var (vp, vn, _) = _verts[i];
                    var d = (vp - p).Length;
                    if (d > reach) continue;
                    var score = d - (facing is { } f ? Vector3.Dot(vn, f) * 1.5f : 0f);
                    if (score < best) { best = score; hit = (vp, vn); }
                }
            }
            // onto the surface near that vertex (its tangent plane), not onto the vertex itself: vertices are up to a
            // unit apart, and snapping to them would bunch a path into dots
            if (hit is { } h && (h.Item1 - p).Length < 1.2f)
            {
                var onPlane = p - h.Item2 * Vector3.Dot(p - h.Item1, h.Item2);
                return (onPlane, h.Item2);
            }
            return hit;
        }
    }

    /// <summary>Paints the network into one byte mask per texture group (0..255, a soft tube profile).</summary>
    public static Dictionary<string, byte[]> Build(IReadOnlyList<(string Name, IReadOnlyList<MeshInput> Shapes)> groups, AnatomicalVeinsLayer s, int seed, int size)
    {
        var all = groups.SelectMany(g => g.Shapes).ToList();
        var surface = new Surface(all);
        var rng = new Random(seed * 7919 + 13);
        var strokes = new List<Stroke>();
        var thick = s.Thickness;

        var body = all.Select(m => m.Shape).Where(sh => sh.ModelSpaceNormals).MaxBy(sh => sh.VertexCount);
        if (body != null)
        {
            var bp = body.Positions;
            var top = Enumerable.Range(0, body.VertexCount).Max(i => bp[i * 3 + 2]);
            var k = top / 114f; // routes are laid out for a body about 114 units to the neck, scaled to this one

            foreach (var side in new[] { 1f, -1f })
            {
                // arms: centre line per height from the slice of vertices beyond the gap between arm and torso
                var arm = ArmCentre(body, side, k);
                if (s.Arms && arm.Count > 4)
                {
                    var front = Vector3.UnitY; var outward = new Vector3(side, 0, 0);
                    // cephalic: thumb side (front, a little outward), from the wrist to the shoulder's front
                    strokes.Add(Trunk(surface, arm.Select(c => c.Centre + (front * 0.75f + outward * 0.45f) * (c.Radius + 2f)).ToList(), front * 0.75f + outward * 0.45f, 0.42f * thick, 0.32f * thick, s.Wiggle, rng));
                    // basilic: little-finger side (back, inward) up to the armpit
                    var lower = arm.Where(c => c.Z < 98 * k).ToList();
                    strokes.Add(Trunk(surface, lower.Select(c => c.Centre + (-front * 0.55f - outward * 0.55f) * (c.Radius + 2f)).ToList(), -front * 0.55f - outward * 0.55f, 0.4f * thick, 0.3f * thick, s.Wiggle, rng));
                    // median cubital: across the front of the elbow, and the median vein down the forearm
                    var elbow = arm.MinBy(c => MathF.Abs(c.Z - 90 * k));
                    var elbowLow = arm.MinBy(c => MathF.Abs(c.Z - 87 * k));
                    strokes.Add(Trunk(surface, new List<Vector3>
                    {
                        elbowLow.Centre + (-front * 0.2f - outward * 0.8f) * (elbowLow.Radius + 2f),
                        elbow.Centre + front * (elbow.Radius + 2f),
                        arm.MinBy(c => MathF.Abs(c.Z - 93 * k)).Centre + (front * 0.75f + outward * 0.45f) * (elbow.Radius + 2f),
                    }, front, 0.32f * thick, 0.32f * thick, s.Wiggle * 0.5f, rng));
                    strokes.Add(Trunk(surface, arm.Where(c => c.Z < 88 * k).Select(c => c.Centre + front * (c.Radius + 2f)).ToList(), front, 0.26f * thick, 0.2f * thick, s.Wiggle, rng));
                }

                // legs: centre line per height
                var leg = LegCentre(body, side, k);
                if (s.Legs && leg.Count > 4)
                {
                    var front = Vector3.UnitY; var medial = new Vector3(-side, 0, 0);
                    // great saphenous: front of the inner ankle, up the inside of the leg, behind the knee's inner side, to the groin
                    strokes.Add(Trunk(surface, leg.Select(c =>
                    {
                        var behind = c.Z > 30 * k && c.Z < 45 * k ? -0.35f : 0.15f;
                        return c.Centre + (medial + front * behind).Normalized() * (c.Radius + 2f);
                    }).ToList(), medial, 0.45f * thick, 0.4f * thick, s.Wiggle, rng));
                    // small saphenous: behind the outer ankle up the back of the calf to the knee pit
                    strokes.Add(Trunk(surface, leg.Where(c => c.Z < 40 * k).Select(c => c.Centre + (-front + (c.Z < 16 * k ? -medial * 0.6f : Vector3.Zero)).Normalized() * (c.Radius + 2f)).ToList(), -front, 0.38f * thick, 0.32f * thick, s.Wiggle, rng));
                }

                // neck: external jugular from below the ear, down the side of the neck, to the middle of the collarbone
                if (s.Neck)
                {
                    strokes.Add(Trunk(surface, new List<Vector3>
                    {
                        new(4.6f * side * k, -0.8f * k, 118f * k), new(5.0f * side * k, -0.2f * k, 114f * k), new(5.6f * side * k, 0.4f * k, 110f * k), new(6.4f * side * k, 1.0f * k, 106.5f * k),
                    }, new Vector3(side, 0.2f, 0), 0.36f * thick, 0.42f * thick, s.Wiggle * 0.4f, rng, branches: false));
                    strokes.Add(Trunk(surface, new List<Vector3>
                    {
                        new(1.0f * side * k, 3f * k, 113f * k), new(1.1f * side * k, 3.5f * k, 109f * k), new(1.3f * side * k, 4f * k, 106f * k),
                    }, Vector3.UnitY, 0.18f * thick, 0.2f * thick, s.Wiggle * 0.4f, rng, branches: false));
                }

                // torso: thoracoepigastric veins down the side from the armpit towards the groin, faint
                if (s.Torso)
                    strokes.Add(Trunk(surface, new List<Vector3>
                    {
                        new(11f * side * k, -1f * k, 97f * k), new(10.5f * side * k, 0.5f * k, 88f * k), new(9.5f * side * k, 2f * k, 78f * k), new(7.5f * side * k, 4f * k, 68f * k), new(5f * side * k, 5f * k, 63f * k),
                    }, new Vector3(side, 0.4f, 0), 0.26f * thick, 0.3f * thick, s.Wiggle, rng));
            }
        }

        // hands: the dorsal network on the back of each hand, fanning from the wrist towards the knuckles
        foreach (var hand in all.Where(m => m.Shape.ModelSpaceNormals && m.Shape.Name.Contains("hand", StringComparison.OrdinalIgnoreCase)))
            if (s.Hands)
                foreach (var side in new[] { 1f, -1f })
                    strokes.AddRange(DorsalNetwork(surface, hand.Shape, side, isFoot: false, thick, s.Wiggle, rng));
        // feet: the dorsal arch across the top of each foot, with the marginal veins up each side
        foreach (var foot in all.Where(m => m.Shape.ModelSpaceNormals && (m.Shape.Name.Contains("feet", StringComparison.OrdinalIgnoreCase) || m.Shape.Name.Contains("foot", StringComparison.OrdinalIgnoreCase))))
            if (s.Feet)
                foreach (var side in new[] { 1f, -1f })
                    strokes.AddRange(DorsalNetwork(surface, foot.Shape, side, isFoot: true, thick, s.Wiggle, rng));

        // branches wandering off every trunk, thinning out
        var trunks = strokes.ToList();
        foreach (var t in trunks.Where(t => t.Points.Count > 6))
            strokes.AddRange(Branches(surface, t, s.Branching, s.Wiggle, rng, depth: 0));

        // paint every stroke into every group's UV layout
        var masks = new Dictionary<string, byte[]>();
        foreach (var (name, shapes) in groups)
        {
            var canvas = new MaskCanvas(name, size, shapes);
            foreach (var stroke in strokes)
            {
                var n = stroke.Points.Count;
                for (var i = 0; i < n; i++)
                {
                    var t = n > 1 ? (float)i / (n - 1) : 0;
                    var radius = stroke.RadiusStart + (stroke.RadiusEnd - stroke.RadiusStart) * t;
                    canvas.Dab(stroke.Points[i].P, stroke.Points[i].N, radius, 0.6f, 0.15f, false);
                }
            }
            masks[name] = canvas.ToBytes();
        }
        return masks;
    }

    private sealed record Ring(float Z, Vector3 Centre, float Radius);

    private static List<Ring> ArmCentre(Core.Mesh.ShapeData body, float side, float k)
    {
        var p = body.Positions; var rings = new List<Ring>();
        for (var z = 99f * k; z >= 77f * k; z -= 1.5f * k)
        {
            var xs = Enumerable.Range(0, body.VertexCount).Where(i => MathF.Abs(p[i * 3 + 2] - z) < 0.5f && p[i * 3] * side > 0).Select(i => p[i * 3] * side).OrderBy(x => x).ToList();
            if (xs.Count < 10) continue;
            // the arm is beyond the widest gap in the slice
            float gapAt = -1, gap = 1.2f * k;
            for (var j = 1; j < xs.Count; j++) if (xs[j] - xs[j - 1] > gap) { gap = xs[j] - xs[j - 1]; gapAt = xs[j]; }
            if (gapAt < 0) continue;
            var arm = Enumerable.Range(0, body.VertexCount).Where(i => MathF.Abs(p[i * 3 + 2] - z) < 0.5f && p[i * 3] * side >= gapAt - 0.01f).ToList();
            var c = new Vector3(arm.Average(i => p[i * 3]), arm.Average(i => p[i * 3 + 1]), z);
            var r = arm.Average(i => MathF.Sqrt((p[i * 3] - c.X) * (p[i * 3] - c.X) + (p[i * 3 + 1] - c.Y) * (p[i * 3 + 1] - c.Y)));
            rings.Add(new Ring(z, c, r));
        }
        return rings.OrderBy(r => r.Z).ToList();
    }

    private static List<Ring> LegCentre(Core.Mesh.ShapeData body, float side, float k)
    {
        var p = body.Positions; var rings = new List<Ring>();
        for (var z = 13f * k; z <= 60f * k; z += 1.5f * k)
        {
            var leg = Enumerable.Range(0, body.VertexCount).Where(i => MathF.Abs(p[i * 3 + 2] - z) < 0.5f && p[i * 3] * side > 0.6f * k).ToList();
            if (leg.Count < 10) continue;
            var c = new Vector3(leg.Average(i => p[i * 3]), leg.Average(i => p[i * 3 + 1]), z);
            var r = leg.Average(i => MathF.Sqrt((p[i * 3] - c.X) * (p[i * 3] - c.X) + (p[i * 3 + 1] - c.Y) * (p[i * 3 + 1] - c.Y)));
            rings.Add(new Ring(z, c, r));
        }
        return rings;
    }

    /// <summary>A trunk through guide points, each snapped onto the surface facing the given way, smoothed and densified.</summary>
    private static Stroke Trunk(Surface surface, List<Vector3> guide, Vector3 facing, float r0, float r1, float wiggle, Random rng, bool branches = true)
    {
        var snapped = new List<Vector3>();
        foreach (var g in guide)
            if (surface.Snap(g, facing.Normalized(), 6f) is { } hit) snapped.Add(hit.P);
        var points = new List<(Vector3, Vector3)>();
        if (snapped.Count < 2) return new Stroke(points, r0, r1);
        // smooth the guide (Chaikin), then walk it in small steps, re-snapping with a slow sideways wiggle
        for (var pass = 0; pass < 2; pass++)
        {
            var next = new List<Vector3> { snapped[0] };
            for (var i = 0; i + 1 < snapped.Count; i++) { next.Add(Vector3.Lerp(snapped[i], snapped[i + 1], 0.25f)); next.Add(Vector3.Lerp(snapped[i], snapped[i + 1], 0.75f)); }
            next.Add(snapped[^1]);
            snapped = next;
        }
        var phase = (float)rng.NextDouble() * 10f;
        var travelled = 0f;
        for (var i = 0; i + 1 < snapped.Count; i++)
        {
            var a = snapped[i]; var b = snapped[i + 1];
            var len = (b - a).Length;
            for (var t = 0f; t < len; t += 0.2f)
            {
                var q = Vector3.Lerp(a, b, t / len);
                travelled += 0.2f;
                var dir = (b - a).Normalized();
                var sideways = Vector3.Cross(dir, facing.Normalized());
                if (sideways.LengthSquared > 1e-6f) q += sideways.Normalized() * (MathF.Sin(travelled * 0.35f + phase) * 0.5f + MathF.Sin(travelled * 0.9f + phase * 2) * 0.2f) * wiggle;
                if (surface.Snap(q, facing.Normalized(), 3f) is { } hit) points.Add((hit.P, hit.N));
            }
        }
        return new Stroke(points, r0, r1);
    }

    /// <summary>Side branches leaving a trunk at intervals, wandering over the surface and thinning; a few branch again.</summary>
    private static IEnumerable<Stroke> Branches(Surface surface, Stroke trunk, float amount, float wiggle, Random rng, int depth)
    {
        if (amount <= 0 || depth > 1) yield break;
        var spacing = 18f / (0.5f + amount * 2f); // points between branches
        for (var at = spacing * (0.5f + (float)rng.NextDouble()); at < trunk.Points.Count - 2; at += spacing * (0.6f + (float)rng.NextDouble() * 0.8f))
        {
            var i = (int)at;
            var (p, n) = trunk.Points[i];
            var along = (trunk.Points[Math.Min(i + 1, trunk.Points.Count - 1)].P - trunk.Points[Math.Max(i - 1, 0)].P);
            if (along.LengthSquared < 1e-6f) continue;
            along.Normalize();
            var sideways = Vector3.Cross(n, along).Normalized() * (rng.Next(2) == 0 ? 1 : -1);
            var dir = (along * (0.4f + (float)rng.NextDouble() * 0.6f) + sideways).Normalized();
            var length = (3f + (float)rng.NextDouble() * 6f) * (depth == 0 ? 1f : 0.6f);
            var r0 = MathF.Min(trunk.RadiusStart, trunk.RadiusEnd) * (depth == 0 ? 0.7f : 0.55f);
            var points = new List<(Vector3, Vector3)> { (p, n) };
            var q = p; var normal = n;
            for (var walked = 0f; walked < length; walked += 0.2f)
            {
                var turn = ((float)rng.NextDouble() - 0.5f) * 0.5f * (0.4f + wiggle);
                dir = (dir + Vector3.Cross(normal, dir) * turn).Normalized();
                if (surface.Snap(q + dir * 0.2f, normal, 1.5f) is not { } hit) break;
                var step = hit.P - q;
                if (step.LengthSquared < 1e-8f) break;
                q = hit.P; normal = hit.N;
                dir = (dir - normal * Vector3.Dot(dir, normal)).Normalized();
                points.Add((q, normal));
            }
            if (points.Count < 4) continue;
            var branch = new Stroke(points, r0, r0 * 0.35f);
            yield return branch;
            if (rng.NextDouble() < amount * 0.5)
                foreach (var b in Branches(surface, branch, amount, wiggle, rng, depth + 1)) yield return b;
        }
    }

    /// <summary>
    /// The back of a hand or the top of a foot: an arch across the knuckle/metatarsal line, veins from it towards
    /// the fingers or toes, and two marginal veins running back up to the wrist or ankle.
    /// </summary>
    private static IEnumerable<Stroke> DorsalNetwork(Surface surface, Core.Mesh.ShapeData piece, float side, bool isFoot, float thick, float wiggle, Random rng)
    {
        var p = piece.Positions;
        var verts = Enumerable.Range(0, piece.VertexCount).Where(i => p[i * 3] * side > 0).ToList();
        if (verts.Count < 50) yield break;
        float minZ = verts.Min(i => p[i * 3 + 2]), maxZ = verts.Max(i => p[i * 3 + 2]);
        Vector3 Pt(int i) => new(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]);
        // the dorsal side: the back of a hand faces outward (hands hang beside the thighs), the top of a foot faces up
        var facing = isFoot ? new Vector3(0, 0.3f, 1f).Normalized() : new Vector3(side, 0.15f, 0).Normalized();
        // along the piece: wrist/ankle to fingertips/toes
        Vector3 root, tip;
        if (isFoot)
        {
            var toe = verts.MaxBy(i => p[i * 3 + 1]); var ankle = verts.Where(i => p[i * 3 + 2] > maxZ - 1.5f).ToList();
            root = new Vector3(ankle.Average(i => p[i * 3]), ankle.Average(i => p[i * 3 + 1]), ankle.Average(i => p[i * 3 + 2]));
            tip = Pt(toe);
        }
        else
        {
            var wrist = verts.Where(i => p[i * 3 + 2] > maxZ - 1.5f).ToList(); var fingers = verts.Where(i => p[i * 3 + 2] < minZ + 2f).ToList();
            root = new Vector3(wrist.Average(i => p[i * 3]), wrist.Average(i => p[i * 3 + 1]), wrist.Average(i => p[i * 3 + 2]));
            tip = new Vector3(fingers.Average(i => p[i * 3]), fingers.Average(i => p[i * 3 + 1]), fingers.Average(i => p[i * 3 + 2]));
        }
        var axis = tip - root; var length = axis.Length; axis /= length;
        var across = Vector3.Cross(axis, facing).Normalized();
        var width = isFoot ? 3.2f : 2.4f;
        var archAt = root + axis * length * (isFoot ? 0.55f : 0.5f);
        var lift = facing * 4f;
        var arch = Enumerable.Range(0, 7).Select(j => archAt + across * width * (j / 3f - 1f) + axis * (MathF.Abs(j / 3f - 1f) * -0.8f) + lift).ToList();
        yield return Trunk(surface, arch, facing, 0.3f * thick, 0.3f * thick, wiggle * 0.4f, rng, branches: false);
        // marginal veins from the arch's ends back to the wrist/ankle
        foreach (var end in new[] { -1f, 1f })
            yield return Trunk(surface, new List<Vector3> { archAt + across * width * end + lift, root + axis * length * 0.25f + across * width * 0.8f * end + lift, root + across * width * 0.6f * end + lift },
                facing, 0.32f * thick, 0.36f * thick, wiggle * 0.5f, rng, branches: false);
        // veins from the arch towards each finger or toe
        for (var j = 0; j < 4; j++)
        {
            var start = archAt + across * width * (j / 1.5f - 1f) + lift;
            var end = start + axis * length * (isFoot ? 0.3f : 0.32f) + across * ((float)rng.NextDouble() - 0.5f) * 0.6f;
            yield return Trunk(surface, new List<Vector3> { start, Vector3.Lerp(start, end, 0.5f), end }, facing, 0.22f * thick, 0.12f * thick, wiggle, rng, branches: false);
        }
    }
}
