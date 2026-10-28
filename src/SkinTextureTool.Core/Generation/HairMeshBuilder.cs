using System.Numerics;
using SkinTextureTool.Core.Mesh;

namespace SkinTextureTool.Core.Generation;

/// <summary>Hair cards grown on the body: positions, a shading normal, the strand direction and card UVs.</summary>
public sealed class HairMesh
{
    public required float[] Positions { get; init; } // xyz
    public required float[] Normals { get; init; }   // xyz, the card's face (lit like the skin under it)
    public required float[] Tangents { get; init; }  // xyz, along the strand (for the hair highlight)
    /// <summary>u across the card (0..1 within the card's strip of the atlas), v from root (0) to tip (1).</summary>
    public required float[] Uvs { get; init; }
    public required uint[] Indices { get; init; }
    /// <summary>
    /// Where each vertex is anchored on the body, so it can be rebuilt on another shape of the same mesh (BodySlide's
    /// reference body) and follow its sliders and bone weights: the surface it grew on, the root triangle's three
    /// vertices and the root's barycentric weights in it, and the vertex's offset from the root in the triangle's
    /// frame (<see cref="HairMeshBuilder.Frame"/>).
    /// </summary>
    public required int[] RootSurface { get; init; }
    public required int[] RootVertices { get; init; } // 3 per vertex
    public required float[] RootWeights { get; init; } // 3 per vertex
    public required float[] RootOffsets { get; init; } // 3 per vertex
    public int CardCount { get; init; }
    /// <summary>Cards the triangle budget left out.</summary>
    public int Dropped { get; init; }
    public int TriangleCount => Indices.Length / 3;
    public int VertexCount => Positions.Length / 3;
}

/// <summary>
/// Grows hair cards on a body mesh, the way procedural hair in Blender does it: roots scattered over the surface
/// where the mask allows, each strand combed along a flow down the body (leaning in or out, swirling), lifting off
/// the skin and lying back down along its length, curling, and pulled into clumps. Each strand becomes a ribbon a
/// few hairs wide (the card), lying along the skin, tapering to the tip.
/// </summary>
public static class HairMeshBuilder
{
    /// <summary>The shapes to grow on, each with its mask (greys 0..1, row 0 at v = 0) sampled by UV.</summary>
    public sealed record Surface(ShapeData Shape, float[] Mask, int MaskWidth, int MaskHeight);

    /// <summary>Atlas columns: each card uses one strip of the hair texture, so neighbouring cards differ.</summary>
    public const int AtlasColumns = 4;

    public static HairMesh Build(IReadOnlyList<Surface> surfaces, HairMeshLayer s, int seed)
    {
        // 1. roots: per triangle, as many as its area × density × mask, scattered at random points on it
        var roots = new List<(Vector3 P, Vector3 N, int Surface, int A, int B, int C, Vector3 W, float Inner)>();
        // how deep inside the patch a point is (0 at its edge, 1 well inside): the mask blurred over a few
        // centimetres, so settings can blend from the patch's centre to its edge as procedural hair does
        var blurred = new Dictionary<float[], float[]>();
        var rng = new Random(seed * 7919 + 17);
        for (var si = 0; si < surfaces.Count; si++)
        {
            var surface = surfaces[si];
            var shape = surface.Shape;
            if (!blurred.TryGetValue(surface.Mask, out var soft)) blurred[surface.Mask] = soft = Blur(surface.Mask, surface.MaskWidth, surface.MaskHeight, Math.Max(2, surface.MaskWidth / 48));
            var normals = VertexNormals(shape);
            var P = shape.Positions; var U = shape.Uvs; var I = shape.Indices;
            for (var t = 0; t + 2 < I.Length; t += 3)
            {
                int a = (int)I[t], b = (int)I[t + 1], c = (int)I[t + 2];
                var pa = V(P, a); var pb = V(P, b); var pc = V(P, c);
                var area = Vector3.Cross(pb - pa, pc - pa).Length() * 0.5f;
                var expected = area * s.Density;
                var count = (int)expected + (rng.NextDouble() < expected - (int)expected ? 1 : 0);
                for (var k = 0; k < count; k++)
                {
                    var r1 = (float)rng.NextDouble(); var r2 = (float)rng.NextDouble();
                    if (r1 + r2 > 1) { r1 = 1 - r1; r2 = 1 - r2; }
                    float w0 = 1 - r1 - r2, w1 = r1, w2 = r2;
                    var u = U[a * 2] * w0 + U[b * 2] * w1 + U[c * 2] * w2;
                    var v = U[a * 2 + 1] * w0 + U[b * 2 + 1] * w1 + U[c * 2 + 1] * w2;
                    if (rng.NextDouble() >= Sample(surface, u, v)) continue; // the mask thins the roots out
                    var n = Vector3.Normalize(V(normals, a) * w0 + V(normals, b) * w1 + V(normals, c) * w2);
                    var inner = Smooth(0.35f, 0.95f, Sample(soft, surface.MaskWidth, surface.MaskHeight, u, v));
                    roots.Add((pa * w0 + pb * w1 + pc * w2, n, si, a, b, c, new Vector3(w0, w1, w2), inner));
                }
            }
        }

        // 2. the triangle budget: drop cards at random until it fits
        var segments = Math.Clamp(s.Segments, 1, 12);
        var maxCards = Math.Max(1, s.MaxTriangles / (2 * segments));
        var dropped = 0;
        if (roots.Count > maxCards)
        {
            dropped = roots.Count - maxCards;
            for (var i = roots.Count - 1; i > 0; i--) { var j = rng.Next(i + 1); (roots[i], roots[j]) = (roots[j], roots[i]); }
            roots.RemoveRange(maxCards, dropped);
        }

        // 3. a strand per root, then a card along it
        var rSurf = new List<int>(); var rVerts = new List<int>(); var rW = new List<float>(); var rOff = new List<float>();
        var pos = new List<float>(); var nrm = new List<float>(); var tan = new List<float>(); var uv = new List<float>(); var idx = new List<uint>();
        var clumpCell = 0.4f + 2.5f * (1 - s.Clumping); // clumps are this far apart (body units)
        foreach (var (root, n, si, ra, rb, rc, rw, inner) in roots)
        {
            var sp = surfaces[si].Shape.Positions;
            var (fx, fy, fz) = Frame(V(sp, ra), V(sp, rb), V(sp, rc));
            var h = Hash(root);
            var h2 = Hash(root * 3.7f + new Vector3(11.3f));
            var stray = h2.X < s.Strays * 0.15f;
            // toward the patch's edge hairs get shorter and lie flatter (inner and outer values, blended)
            var length = s.Length * (1 - s.LengthVariation * h.X) * (stray ? 1.7f : 1f) * float.Lerp(s.EdgeLength, 1, inner);
            var lieFlat = float.Lerp(MathF.Min(1, s.LieFlat + s.EdgeFlatten), s.LieFlat, inner);
            var twist = (h2.Y - 0.5f) * s.Twist * MathF.PI;
            var flow = Flow(root, n, s);
            var side = Vector3.Normalize(Vector3.Cross(n, flow));
            // strands spread a little around the flow
            var spread = (h.Y - 0.5f) * (0.3f + 0.9f * s.Curl);
            flow = Vector3.Normalize(flow * MathF.Cos(spread) + side * MathF.Sin(spread));
            side = Vector3.Normalize(Vector3.Cross(n, flow));
            var lift = MathF.Min(s.Lift * float.Lerp(1 - s.EdgeFlatten, 1, inner) + (stray ? 0.35f : 0f), 1f) * MathF.PI * 0.5f;

            // clump: the strand is pulled toward the clump's centre strand
            var clump = ClumpCentre(root, n, clumpCell);

            var points = new Vector3[segments + 1];
            var dirs = new Vector3[segments + 1];
            points[0] = root + n * 0.01f;
            var step = length / segments;
            var p = points[0];
            for (var i = 1; i <= segments; i++)
            {
                var t = i / (float)segments;
                // lifts off the skin at the root and lies back down along its length
                var angle = lift * (1 - lieFlat * t);
                var dir = flow * MathF.Cos(angle) + n * MathF.Sin(angle);
                // curl: a wave from side to side along the strand
                var wave = MathF.Sin(t * (4f + 6f * h.Z) + h.X * 6.28f) * s.Curl * 0.9f;
                // frizz: random kinks
                var kink = Hash(root + new Vector3(i * 1.7f)) - new Vector4(0.5f);
                dir = Vector3.Normalize(dir + side * (wave + kink.X * s.Frizz * 0.8f) + n * (kink.Y * s.Frizz * 0.4f));
                p += dir * step;
                // clumping pulls the outer part of the strand toward the clump's centre strand (same offset from its root)
                var pull = s.Clumping * t * t * 0.6f;
                p = Vector3.Lerp(p, clump + (p - root), pull);
                // never sink into the skin below the root
                var height = Vector3.Dot(p - root, n);
                if (height < 0.02f) p += n * (0.02f - height);
                points[i] = p;
            }
            for (var i = 0; i <= segments; i++)
            {
                var d = i == 0 ? points[1] - points[0] : i == segments ? points[i] - points[i - 1] : points[i + 1] - points[i - 1];
                dirs[i] = Vector3.Normalize(d);
            }

            // the card: a ribbon lying along the skin, tapering to the tip, using one strip of the atlas
            var column = (int)(h.W * AtlasColumns) % AtlasColumns;
            var baseIndex = (uint)(pos.Count / 3);
            for (var i = 0; i <= segments; i++)
            {
                var t = i / (float)segments;
                // across the card: along the skin, turned about the strand by the card's twist
                var flat = Vector3.Normalize(Vector3.Cross(n, dirs[i]));
                var up = Vector3.Normalize(Vector3.Cross(dirs[i], flat));
                var across = Vector3.Normalize(flat * MathF.Cos(twist) + up * MathF.Sin(twist));
                var face = Vector3.Normalize(Vector3.Cross(dirs[i], across));
                var half = s.Width * 0.5f * (1 - 0.2f * t); // only a slight taper: the hairs on the texture spread to the tip
                foreach (var sgn in new[] { -1f, 1f })
                {
                    var q = points[i] + across * (half * sgn);
                    pos.Add(q.X); pos.Add(q.Y); pos.Add(q.Z);
                    var off = q - root;
                    rSurf.Add(si); rVerts.Add(ra); rVerts.Add(rb); rVerts.Add(rc); rW.Add(rw.X); rW.Add(rw.Y); rW.Add(rw.Z);
                    rOff.Add(Vector3.Dot(off, fx)); rOff.Add(Vector3.Dot(off, fy)); rOff.Add(Vector3.Dot(off, fz));
                    nrm.Add(face.X); nrm.Add(face.Y); nrm.Add(face.Z);
                    tan.Add(dirs[i].X); tan.Add(dirs[i].Y); tan.Add(dirs[i].Z);
                    uv.Add((column + (sgn < 0 ? 0f : 1f)) / AtlasColumns); uv.Add(t);
                }
                if (i == 0) continue;
                var r = baseIndex + (uint)(i * 2);
                idx.Add(r - 2); idx.Add(r - 1); idx.Add(r);
                idx.Add(r - 1); idx.Add(r + 1); idx.Add(r);
            }
        }
        return new HairMesh
        {
            Positions = pos.ToArray(), Normals = nrm.ToArray(), Tangents = tan.ToArray(), Uvs = uv.ToArray(), Indices = idx.ToArray(),
            RootSurface = rSurf.ToArray(), RootVertices = rVerts.ToArray(), RootWeights = rW.ToArray(), RootOffsets = rOff.ToArray(),
            CardCount = roots.Count, Dropped = dropped,
        };
    }

    /// <summary>Hair meshes grown on different surfaces as one, each with how many surfaces it grew on (numbered after the previous ones').</summary>
    public static HairMesh Combine(IReadOnlyList<(HairMesh Mesh, int Surfaces)> parts)
    {
        if (parts.Count == 1) return parts[0].Mesh;
        var pos = new List<float>(); var nrm = new List<float>(); var tan = new List<float>(); var uv = new List<float>(); var idx = new List<uint>();
        var rs = new List<int>(); var rv = new List<int>(); var rw = new List<float>(); var ro = new List<float>();
        int surfaceBase = 0, cards = 0, dropped = 0;
        foreach (var (m, count) in parts)
        {
            var vertexBase = (uint)(pos.Count / 3);
            pos.AddRange(m.Positions); nrm.AddRange(m.Normals); tan.AddRange(m.Tangents); uv.AddRange(m.Uvs);
            idx.AddRange(m.Indices.Select(i => i + vertexBase));
            rs.AddRange(m.RootSurface.Select(x => x + surfaceBase)); rv.AddRange(m.RootVertices); rw.AddRange(m.RootWeights); ro.AddRange(m.RootOffsets);
            surfaceBase += count; cards += m.CardCount; dropped += m.Dropped;
        }
        return new HairMesh
        {
            Positions = pos.ToArray(), Normals = nrm.ToArray(), Tangents = tan.ToArray(), Uvs = uv.ToArray(), Indices = idx.ToArray(),
            RootSurface = rs.ToArray(), RootVertices = rv.ToArray(), RootWeights = rw.ToArray(), RootOffsets = ro.ToArray(),
            CardCount = cards, Dropped = dropped,
        };
    }

    /// <summary>A triangle's frame: along its first edge, across it, and its face normal.</summary>
    public static (Vector3 X, Vector3 Y, Vector3 Z) Frame(Vector3 a, Vector3 b, Vector3 c)
    {
        var x = Vector3.Normalize(b - a);
        var z = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        return (x, Vector3.Cross(z, x), z);
    }

    /// <summary>The hair's vertex positions rebuilt on other positions for the same shapes (same vertex order).</summary>
    public static float[] Rebase(HairMesh mesh, IReadOnlyList<float[]> surfacePositions)
    {
        var result = new float[mesh.Positions.Length];
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var sp = surfacePositions[mesh.RootSurface[v]];
            var a = V(sp, mesh.RootVertices[v * 3]); var b = V(sp, mesh.RootVertices[v * 3 + 1]); var c = V(sp, mesh.RootVertices[v * 3 + 2]);
            var root = a * mesh.RootWeights[v * 3] + b * mesh.RootWeights[v * 3 + 1] + c * mesh.RootWeights[v * 3 + 2];
            var (fx, fy, fz) = Frame(a, b, c);
            var q = root + fx * mesh.RootOffsets[v * 3] + fy * mesh.RootOffsets[v * 3 + 1] + fz * mesh.RootOffsets[v * 3 + 2];
            result[v * 3] = q.X; result[v * 3 + 1] = q.Y; result[v * 3 + 2] = q.Z;
        }
        return result;
    }

    /// <summary>Where a strand heads: down the body, leaning in or out, swirled by slow noise, along the skin.</summary>
    private static Vector3 Flow(Vector3 p, Vector3 n, HairMeshLayer s)
    {
        var dir = new Vector3(MathF.Sign(p.X + 1e-4f) * s.LeanInOut, 0, -1);
        var flow = dir - n * Vector3.Dot(dir, n);
        flow = flow.LengthSquared() > 1e-6f ? Vector3.Normalize(flow) : Vector3.Normalize(Vector3.Cross(n, Vector3.UnitX));
        var swirl = (ValueNoise(p * 0.15f) - 0.5f) * 2f * s.Swirl * MathF.PI * 0.6f;
        var side = Vector3.Normalize(Vector3.Cross(n, flow));
        return Vector3.Normalize(flow * MathF.Cos(swirl) + side * MathF.Sin(swirl));
    }

    /// <summary>The root of the clump's centre strand for the clump cell a root falls in.</summary>
    private static Vector3 ClumpCentre(Vector3 root, Vector3 n, float cell)
    {
        var c = new Vector3(MathF.Floor(root.X / cell), MathF.Floor(root.Y / cell), MathF.Floor(root.Z / cell));
        var h = Hash(c * 13.1f);
        var centre = (c + new Vector3(h.X, h.Y, h.Z)) * cell;
        // project the centre onto this root's tangent plane, so the clump stays on the skin
        centre -= n * Vector3.Dot(centre - root, n);
        return centre;
    }

    private static float Sample(float[] mask, int w, int h, float u, float v) =>
        mask[Math.Clamp((int)(v * h), 0, h - 1) * w + Math.Clamp((int)(u * w), 0, w - 1)];

    private static float Smooth(float e0, float e1, float x) { var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1); return t * t * (3 - 2 * t); }

    /// <summary>A box blur of a mask (two passes each way, close to a Gaussian).</summary>
    private static float[] Blur(float[] src, int w, int h, int r)
    {
        var a = (float[])src.Clone(); var b = new float[a.Length];
        for (var pass = 0; pass < 2; pass++)
        {
            Parallel.For(0, h, y =>
            {
                float sum = 0; var row = y * w;
                for (var x = -r; x <= r; x++) sum += a[row + Math.Clamp(x, 0, w - 1)];
                for (var x = 0; x < w; x++)
                {
                    b[row + x] = sum / (2 * r + 1);
                    sum += a[row + Math.Min(x + r + 1, w - 1)] - a[row + Math.Max(x - r, 0)];
                }
            });
            Parallel.For(0, w, x =>
            {
                float sum = 0;
                for (var y = -r; y <= r; y++) sum += b[Math.Clamp(y, 0, h - 1) * w + x];
                for (var y = 0; y < h; y++)
                {
                    a[y * w + x] = sum / (2 * r + 1);
                    sum += b[Math.Min(y + r + 1, h - 1) * w + x] - b[Math.Max(y - r, 0) * w + x];
                }
            });
        }
        return a;
    }

    private static float Sample(Surface s, float u, float v)
    {
        var x = Math.Clamp((int)(u * s.MaskWidth), 0, s.MaskWidth - 1);
        var y = Math.Clamp((int)(v * s.MaskHeight), 0, s.MaskHeight - 1);
        return s.Mask[y * s.MaskWidth + x];
    }

    private static float[] VertexNormals(ShapeData shape)
    {
        var P = shape.Positions; var I = shape.Indices;
        var n = new float[P.Length];
        for (var t = 0; t + 2 < I.Length; t += 3)
        {
            int a = (int)I[t], b = (int)I[t + 1], c = (int)I[t + 2];
            var f = Vector3.Cross(V(P, b) - V(P, a), V(P, c) - V(P, a)); // area weighted
            foreach (var i in new[] { a, b, c }) { n[i * 3] += f.X; n[i * 3 + 1] += f.Y; n[i * 3 + 2] += f.Z; }
        }
        for (var i = 0; i < n.Length; i += 3)
        {
            var v = new Vector3(n[i], n[i + 1], n[i + 2]);
            v = v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.UnitZ;
            n[i] = v.X; n[i + 1] = v.Y; n[i + 2] = v.Z;
        }
        return n;
    }

    private static Vector3 V(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);

    /// <summary>Four repeatable random numbers in [0, 1) for a point.</summary>
    private static Vector4 Hash(Vector3 p)
    {
        var h = (uint)BitConverter.SingleToInt32Bits(p.X * 1.31f) * 73856093u ^ (uint)BitConverter.SingleToInt32Bits(p.Y * 1.17f) * 19349663u ^ (uint)BitConverter.SingleToInt32Bits(p.Z * 1.07f) * 83492791u;
        float Next() { h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16; return (h & 0xffffff) / 16777216f; }
        return new Vector4(Next(), Next(), Next(), Next());
    }

    private static float ValueNoise(Vector3 p)
    {
        var i = new Vector3(MathF.Floor(p.X), MathF.Floor(p.Y), MathF.Floor(p.Z));
        var f = p - i;
        f = f * f * (new Vector3(3) - 2 * f);
        float C(float x, float y, float z) => Hash(i + new Vector3(x, y, z)).X;
        var x00 = float.Lerp(C(0, 0, 0), C(1, 0, 0), f.X); var x10 = float.Lerp(C(0, 1, 0), C(1, 1, 0), f.X);
        var x01 = float.Lerp(C(0, 0, 1), C(1, 0, 1), f.X); var x11 = float.Lerp(C(0, 1, 1), C(1, 1, 1), f.X);
        return float.Lerp(float.Lerp(x00, x10, f.Y), float.Lerp(x01, x11, f.Y), f.Z);
    }
}
