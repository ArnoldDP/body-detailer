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
    public static Result Reshape(ShapeData head, ShapeData body, float band = 4f, float reach = 0.1f)
    {
        var hp = head.Positions; var bp = body.Positions;
        var headEdges = BoundaryEdges(head);
        var bodyEdges = BoundaryEdges(body);
        var bodyEdgeVerts = bodyEdges.SelectMany(e => new[] { e.A, e.B }).Distinct().ToList();
        var bodyTangent = EdgeTangents(body, bodyEdgeVerts);

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
            if (found is { } anchor) merges[v] = anchor;
        }

        var moved = 0; var largest = 0f;
        var seamSet = seam.Select(s => s.V).ToHashSet();
        for (var v = 0; v < head.VertexCount; v++)
        {
            var p = V(hp, v);
            // where the body's surface would carry on to, blended over nearby seam points
            Vec q = default; float weights = 0, nearest = float.MaxValue;
            foreach (var (_, target, up) in seam)
            {
                var d = MathF.Sqrt((p - target).LengthSq);
                nearest = MathF.Min(nearest, d);
                if (d > band * 1.5f) continue;
                var w = 1f / (d * d * d * d + 1e-6f);
                q += (target + up * d) * w; weights += w;
            }
            Vec np;
            if (anchorPos.TryGetValue(v, out var exact)) np = exact;
            else if (merges.TryGetValue(v, out var into)) np = anchorPos[into];
            else if (seamSet.Contains(v)) np = seam.First(s => s.V == v).Target;
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
        return new Result(result, moved, seam.Count, largest, merges);
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

    private static Vec V(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);

    private readonly record struct Vec(float X, float Y, float Z)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec operator *(Vec a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public float LengthSq => Dot(this, this);
        public Vec Normalized() { var l = MathF.Sqrt(LengthSq); return l > 0 ? this * (1f / l) : this; }
    }
}
