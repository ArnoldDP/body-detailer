using SkinTextureTool.Core.Mesh;

namespace SkinTextureTool.Core.NeckFix;

/// <summary>
/// Reshapes the bottom of a head's neck so it meets the body: neck-edge vertices that match a body vertex are
/// snapped onto it exactly, the extra ones a high-poly head has in between are merged into the nearest matching
/// neighbour (so the head's last loop has the body's points and no T-junctions), and a band above is bent so the
/// head's surface leaves the seam in the same direction the body's surface arrives. Above the band the head is
/// untouched. The vertex count never changes, so .tri morphs and skin weights stay compatible.
/// </summary>
public static class NeckReshaper
{
    /// <param name="Merges">In-between edge vertex → the neighbour it was merged into (copy its weights and morphs).</param>
    public sealed record Result(float[] Positions, int MovedVertices, int SeamVertices, float LargestMove, IReadOnlyDictionary<int, int> Merges);

    /// <param name="band">Height in world units over which the head is bent to continue the body.</param>
    /// <param name="merge">
    /// Merge in-between edge vertices into their matching neighbours (needs the head's .tri files patched to
    /// match, as the player export does). Off, they're snapped onto the body's edge and tucked a hair under it
    /// instead, which leaves every .tri valid — the choice for NPC FaceGen heads.
    /// </param>
    /// <param name="projectToPlane">Bend onto the body's surface plane instead of straight on (better for hands and feet).</param>
    /// <param name="only">When given, merge exactly these pairs (where they're still valid) instead of choosing:
    /// heads sharing .tri files must all merge the same vertices.</param>
    public static Result Reshape(ShapeData head, ShapeData body, float band = 4f, float reach = 0.1f, bool merge = true,
        IReadOnlyDictionary<int, int>? only = null, bool projectToPlane = false)
    {
        var hp = head.Positions; var bp = body.Positions;
        var headEdges = BoundaryEdges(head);
        var bodyEdges = BoundaryEdges(body);
        var bodyEdgeVerts = bodyEdges.SelectMany(e => new[] { e.A, e.B }).Distinct().ToList();
        var bodyTangent = EdgeTangents(body, bodyEdgeVerts);
        var bodyNormal = VertexNormals(body);

        (Vec Target, Vec Up, float Dist) Nearest(Vec p)
        {
            float best = float.MaxValue; Vec target = default, up = default;
            foreach (var (a, b) in bodyEdges)
            {
                var pa = V(bp, a); var pb = V(bp, b);
                var ab = pb - pa; var len2 = Vec.Dot(ab, ab);
                var t = len2 > 0 ? Math.Clamp(Vec.Dot(p - pa, ab) / len2, 0, 1) : 0;
                var q = pa + ab * t;
                var d2 = (q - p).LengthSq;
                if (d2 >= best) continue;
                best = d2; target = q;
                up = (bodyTangent[a] * (1 - t) + bodyTangent[b] * t).Normalized();
            }
            return (target, up, MathF.Sqrt(best));
        }
        // seam: head open-edge vertices lying on the body's open edge (within a fraction of a unit; eye and mouth
        // holes and the back of the head are far away), each with its point on the body's edge and the direction
        // the body's surface is heading when it gets there
        var seam = new List<(int V, Vec Target, Vec Up)>();
        foreach (var v in headEdges.SelectMany(e => new[] { e.A, e.B }).Distinct())
        {
            var (target, up, dist) = Nearest(V(hp, v));
            if (dist < reach) seam.Add((v, target, up));
        }
        var result = (float[])hp.Clone();
        if (seam.Count == 0) return new Result(result, 0, 0, 0, new Dictionary<int, int>());

        // Anchors: seam vertices sitting on a body vertex; they snap onto it exactly. Every other seam vertex is
        // merged into the anchor it reaches first walking along the head's edge (the nearer one either way).
        const float match = 0.01f;
        var anchorPos = new Dictionary<int, Vec>();
        foreach (var (v, _, _) in seam)
        {
            var p = V(hp, v);
            var j = bodyEdgeVerts.MinBy(j => (V(bp, j) - p).LengthSq);
            if ((V(bp, j) - p).LengthSq < match * match) anchorPos[v] = V(bp, j);
        }
        // Body edge vertices along the seam that no head vertex sits on would leave the head's edge cutting the
        // corner past them (a sliver gap): pin the nearest free seam vertex onto each.
        var covered = anchorPos.Values.ToList();
        var headEdgeVerts = headEdges.SelectMany(e => new[] { e.A, e.B }).Distinct().ToList();
        foreach (var j in bodyEdgeVerts)
        {
            var bj = V(bp, j);
            if (covered.Any(c => (c - bj).LengthSq < match * match)) continue;
            // only where the seam runs: a body vertex beside the head's neck, not elsewhere on the body's edge
            if (!seam.Any(x => (x.Target - bj).LengthSq < 1f)) continue;
            var near = headEdgeVerts.Where(v => !anchorPos.ContainsKey(v))
                .OrderBy(v => (V(hp, v) - bj).LengthSq).Cast<int?>().FirstOrDefault();
            if (near is not { } v || (V(hp, v) - bj).LengthSq > 0.3f * 0.3f) continue;
            anchorPos[v] = bj;
            covered.Add(bj);
            if (!seam.Any(x => x.V == v)) seam.Add((v, bj, Nearest(bj).Up));
        }
        var seamVerts = seam.Select(x => x.V).ToHashSet();
        var along = new Dictionary<int, List<int>>();
        foreach (var (a, b) in headEdges)
        {
            if (!seamVerts.Contains(a) || !seamVerts.Contains(b)) continue;
            (along.TryGetValue(a, out var la) ? la : along[a] = new()).Add(b);
            (along.TryGetValue(b, out var lb) ? lb : along[b] = new()).Add(a);
        }
        // the same point can be several vertices (UV seams), so walk by position
        var merges = new Dictionary<int, int>();
        foreach (var v in seamVerts.Where(v => !anchorPos.ContainsKey(v)))
        {
            var dist = new Dictionary<int, float> { [v] = 0 };
            var queue = new PriorityQueue<int, float>(); queue.Enqueue(v, 0);
            int? found = null;
            while (queue.TryDequeue(out var u, out var du))
            {
                if (anchorPos.ContainsKey(u)) { found = u; break; }
                foreach (var w in along.GetValueOrDefault(u) ?? new())
                {
                    var dw = du + MathF.Sqrt((V(hp, w) - V(hp, u)).LengthSq);
                    if (dist.TryGetValue(w, out var old) && old <= dw) continue;
                    dist[w] = dw; queue.Enqueue(w, dw);
                }
            }
            // only true in-between vertices (about half an edge from their anchor); a vertex further along would
            // drag across its neighbours when merged, so it's snapped and tucked instead
            if (found is { } anchor && (V(hp, anchor) - V(hp, v)).LengthSq < 0.8f * 0.8f) merges[v] = anchor;
        }
        if (!merge) merges.Clear();
        else if (only != null)
            merges = only.Where(kv => seamVerts.Contains(kv.Key) && anchorPos.ContainsKey(kv.Value) && !anchorPos.ContainsKey(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);

        var moved = 0; var largest = 0f;
        var seamSet = seam.Select(s => s.V).ToHashSet();
        // the body's surface direction at each seam point
        var seamNormal = seam.Select(x => bodyNormal[bodyEdgeVerts.MinBy(j => (V(bp, j) - x.Target).LengthSq)]).ToArray();
        for (var v = 0; v < head.VertexCount; v++)
        {
            var p = V(hp, v);
            // where the body's surface would carry on to, blended over nearby seam points: either straight on along
            // the body's surface direction (necks), or the vertex brought onto the body's surface plane, keeping
            // where it is along and around the seam (narrow wrists and ankles, where going straight on overshoots)
            Vec q = default; float weights = 0, nearest = float.MaxValue;
            for (var k = 0; k < seam.Count; k++)
            {
                var target = seam[k].Target;
                var d = MathF.Sqrt((p - target).LengthSq);
                nearest = MathF.Min(nearest, d);
                if (d > band * 1.5f) continue;
                var w = 1f / (d * d * d * d + 1e-6f);
                var nb = seamNormal[k];
                q += (projectToPlane ? p - nb * Vec.Dot(p - target, nb) : target + seam[k].Up * d) * w; weights += w;
            }
            Vec np;
            if (anchorPos.TryGetValue(v, out var exact)) np = exact;
            else if (merges.TryGetValue(v, out var into)) np = anchorPos[into];
            else if (seamSet.Contains(v))
            {
                // part-way along a body edge (a T-junction): tucked a hair under the edge so the head overlaps it
                // and no pixel cracks show
                var (_, target, up) = seam.First(s => s.V == v);
                var j = bodyEdgeVerts.MinBy(j => (V(bp, j) - target).LengthSq);
                np = target - up * 0.03f - bodyNormal[j] * 0.015f;
            }
            else if (weights > 0 && nearest < band)
            {
                var s = nearest / band;
                var w = 1 - s * s * (3 - 2 * s); // full at the seam, flat start so the slope matches the body's
                np = p + (q * (1f / weights) - p) * w;
            }
            else continue;
            var move = MathF.Sqrt((np - p).LengthSq);
            if (move < 1e-6f) continue;
            moved++; largest = MathF.Max(largest, move);
            result[v * 3] = np.X; result[v * 3 + 1] = np.Y; result[v * 3 + 2] = np.Z;
        }
        if (merges.Count > 0) RelaxAboveSeam(head, result, seamVerts, headEdges.SelectMany(e => new[] { e.A, e.B }).ToHashSet());
        Untangle(head, result, seamVerts, merges);
        return new Result(result, moved, seam.Count, largest, merges);
    }

    /// <summary>
    /// Merging slides each in-between edge vertex along the seam onto its neighbour, which leaves the triangles
    /// joining it to the row above skewed (visible facets). A few rounds of smoothing on the rows just above the
    /// seam, fading out with distance and keeping the seam itself fixed, even them out.
    /// </summary>
    /// <param name="openEdge">The head's open-edge vertices: where the edge leaves the body (up the back of the neck)
    /// they have neighbours on one side only, and smoothing would pull them inward and fold their triangles.</param>
    private static void RelaxAboveSeam(ShapeData head, float[] pos, HashSet<int> seam, HashSet<int> openEdge, float reach = 1.5f, int rounds = 6)
    {
        var neighbours = new Dictionary<int, HashSet<int>>();
        var I = head.Indices;
        for (var t = 0; t + 2 < I.Length; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = (int)I[t + k], b = (int)I[t + (k + 1) % 3];
                (neighbours.TryGetValue(a, out var na) ? na : neighbours[a] = new()).Add(b);
                (neighbours.TryGetValue(b, out var nb) ? nb : neighbours[b] = new()).Add(a);
            }
        var seamPoints = seam.Select(v => V(pos, v)).ToList();
        var weight = new Dictionary<int, float>();
        foreach (var v in neighbours.Keys)
        {
            if (seam.Contains(v) || openEdge.Contains(v)) continue;
            var p = V(pos, v);
            var d = seamPoints.Min(q => MathF.Sqrt((q - p).LengthSq));
            if (d < reach) weight[v] = 0.5f * (1 - d / reach);
        }
        for (var r = 0; r < rounds; r++)
        {
            var next = new Dictionary<int, Vec>();
            foreach (var (v, w) in weight)
            {
                Vec sum = default; var n = 0;
                // same-position duplicates (UV seams) share one point, so average by distinct positions
                foreach (var u in neighbours[v]) { sum += V(pos, u); n++; }
                if (n == 0) continue;
                var p = V(pos, v);
                next[v] = p + (sum * (1f / n) - p) * w;
            }
            foreach (var (v, p) in next) { pos[v * 3] = p.X; pos[v * 3 + 1] = p.Y; pos[v * 3 + 2] = p.Z; }
            // vertices at the same point must stay together
            foreach (var group in weight.Keys.GroupBy(v => (MathF.Round(head.Positions[v * 3] * 1000), MathF.Round(head.Positions[v * 3 + 1] * 1000), MathF.Round(head.Positions[v * 3 + 2] * 1000))))
            {
                if (group.Count() < 2) continue;
                Vec avg = default; foreach (var v in group) avg += V(pos, v);
                avg = avg * (1f / group.Count());
                foreach (var v in group) { pos[v * 3] = avg.X; pos[v * 3 + 1] = avg.Y; pos[v * 3 + 2] = avg.Z; }
            }
        }
    }

    /// <summary>
    /// Where the bend still flips or flattens a triangle (typically where the head's edge turns away from the
    /// body and the body's surface direction points steeply up), that triangle's free vertices are eased back
    /// towards where they started until nothing is folded. Seam vertices stay put; triangles collapsed on purpose
    /// by a merge don't count.
    /// </summary>
    private static void Untangle(ShapeData head, float[] pos, HashSet<int> seam, IReadOnlyDictionary<int, int> merges, int rounds = 12)
    {
        var orig = head.Positions; var I = head.Indices;
        for (var r = 0; r < rounds; r++)
        {
            var bad = new HashSet<int>();
            for (var t = 0; t + 2 < I.Length; t += 3)
            {
                int a = (int)I[t], b = (int)I[t + 1], c = (int)I[t + 2];
                if ((merges.TryGetValue(a, out var ia) && (ia == b || ia == c)) || (merges.TryGetValue(b, out var ib) && (ib == a || ib == c))
                    || (merges.TryGetValue(c, out var ic) && (ic == a || ic == b))) continue;
                var fo = Vec.Cross(V(orig, b) - V(orig, a), V(orig, c) - V(orig, a));
                var fn = Vec.Cross(V(pos, b) - V(pos, a), V(pos, c) - V(pos, a));
                var lo = MathF.Sqrt(fo.LengthSq); var ln = MathF.Sqrt(fn.LengthSq);
                if (lo < 1e-9f) continue;
                if (Vec.Dot(fo, fn) < 0 || ln < lo * 0.2f)
                    foreach (var v in new[] { a, b, c }) if (!seam.Contains(v)) bad.Add(v);
            }
            if (bad.Count == 0) return;
            foreach (var v in bad)
                for (var k = 0; k < 3; k++) pos[v * 3 + k] += (orig[v * 3 + k] - pos[v * 3 + k]) * 0.5f;
        }
    }

    private static List<(int A, int B)> BoundaryEdges(ShapeData s)
    {
        var use = new Dictionary<(int, int), int>();
        var I = s.Indices;
        for (var t = 0; t + 2 < I.Length; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = (int)I[t + k], b = (int)I[t + (k + 1) % 3];
                var e = a < b ? (a, b) : (b, a);
                use[e] = use.GetValueOrDefault(e) + 1;
            }
        return use.Where(kv => kv.Value == 1).Select(kv => kv.Key).ToList();
    }

    /// <summary>For each open-edge vertex: the direction from the surface just inside the edge out to the edge.</summary>
    private static Dictionary<int, Vec> EdgeTangents(ShapeData s, List<int> edgeVerts)
    {
        var edge = edgeVerts.ToHashSet();
        var inner = edgeVerts.ToDictionary(v => v, _ => new List<int>());
        var I = s.Indices;
        for (var t = 0; t + 2 < I.Length; t += 3)
            for (var k = 0; k < 3; k++)
            {
                int a = (int)I[t + k];
                if (!edge.Contains(a)) continue;
                for (var m = 1; m < 3; m++)
                {
                    var b = (int)I[t + (k + m) % 3];
                    if (!edge.Contains(b)) inner[a].Add(b);
                }
            }
        var result = new Dictionary<int, Vec>();
        foreach (var v in edgeVerts)
        {
            var p = V(s.Positions, v);
            Vec dir = default;
            foreach (var u in inner[v].Distinct()) dir += (p - V(s.Positions, u)).Normalized();
            result[v] = dir.Normalized();
        }
        return result;
    }

    private static Vec[] VertexNormals(ShapeData s)
    {
        var n = new Vec[s.VertexCount]; var P = s.Positions; var I = s.Indices;
        for (var t = 0; t + 2 < I.Length; t += 3)
        {
            int a = (int)I[t], b = (int)I[t + 1], c = (int)I[t + 2];
            var e1 = V(P, b) - V(P, a); var e2 = V(P, c) - V(P, a);
            var f = new Vec(e1.Y * e2.Z - e1.Z * e2.Y, e1.Z * e2.X - e1.X * e2.Z, e1.X * e2.Y - e1.Y * e2.X);
            n[a] += f; n[b] += f; n[c] += f;
        }
        for (var i = 0; i < n.Length; i++) n[i] = n[i].Normalized();
        return n;
    }

    private static Vec V(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);

    private readonly record struct Vec(float X, float Y, float Z)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec operator *(Vec a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec Cross(Vec a, Vec b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public float LengthSq => Dot(this, this);
        public Vec Normalized() { var l = MathF.Sqrt(LengthSq); return l > 0 ? this * (1f / l) : this; }
    }
}
