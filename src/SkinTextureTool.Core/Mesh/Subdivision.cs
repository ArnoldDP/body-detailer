namespace SkinTextureTool.Core.Mesh;

/// <summary>
/// Splits every triangle into smaller ones before baking, as MuDynamicNormalMap does, then relaxes the normals
/// across the finer mesh. A straight blend between corner normals changes direction abruptly at every original
/// edge (the "crunchy" look on strong curves); relaxing smooths those creases out. (Curved per-triangle patches
/// were tried first and baked each triangle as a little dome, like scales.)
/// </summary>
public static class Subdivision
{
    public static (ShapeData Shape, SurfaceFrames Frames) Apply(ShapeData shape, SurfaceFrames frames, int level)
    {
        if (level <= 0) return (shape, frames);
        var seg = 1 << level;
        var perTri = (seg + 1) * (seg + 2) / 2;
        var triCount = shape.Indices.Length / 3;
        var P = shape.Positions; var U = shape.Uvs; var N = frames.Normals; var T = frames.Tangents; var B = frames.Bitangents; var S = frames.UvScales;
        var pos = new float[triCount * perTri * 3]; var uv = new float[triCount * perTri * 2];
        var nrm = new float[pos.Length]; var tan = new float[pos.Length]; var bit = new float[pos.Length]; var scl = new float[uv.Length];
        var idx = new uint[triCount * seg * seg * 3];
        var vi = 0; var ii = 0;
        for (var t = 0; t < triCount; t++)
        {
            int a = (int)shape.Indices[t * 3], b = (int)shape.Indices[t * 3 + 1], c = (int)shape.Indices[t * 3 + 2];
            var na = V(N, a); var nb = V(N, b); var nc = V(N, c);
            var start = vi;
            for (var i = 0; i <= seg; i++)
            for (var j = 0; j <= seg - i; j++)
            {
                float wb = (float)i / seg, wc = (float)j / seg, wa = 1 - wb - wc;
                for (var k = 0; k < 3; k++)
                {
                    pos[vi * 3 + k] = P[a * 3 + k] * wa + P[b * 3 + k] * wb + P[c * 3 + k] * wc;
                    tan[vi * 3 + k] = T[a * 3 + k] * wa + T[b * 3 + k] * wb + T[c * 3 + k] * wc;
                    bit[vi * 3 + k] = B[a * 3 + k] * wa + B[b * 3 + k] * wb + B[c * 3 + k] * wc;
                }
                for (var k = 0; k < 2; k++)
                {
                    uv[vi * 2 + k] = U[a * 2 + k] * wa + U[b * 2 + k] * wb + U[c * 2 + k] * wc;
                    scl[vi * 2 + k] = S[a * 2 + k] * wa + S[b * 2 + k] * wb + S[c * 2 + k] * wc;
                }
                var n = (na * wa + nb * wb + nc * wc).Normalized();
                nrm[vi * 3] = n.X; nrm[vi * 3 + 1] = n.Y; nrm[vi * 3 + 2] = n.Z;
                vi++;
            }
            int Index(int i, int j) => start + i * (seg + 1) - i * (i - 1) / 2 + j;
            for (var i = 0; i < seg; i++)
            for (var j = 0; j < seg - i; j++)
            {
                idx[ii++] = (uint)Index(i, j); idx[ii++] = (uint)Index(i + 1, j); idx[ii++] = (uint)Index(i, j + 1);
                if (j < seg - i - 1)
                {
                    idx[ii++] = (uint)Index(i + 1, j); idx[ii++] = (uint)Index(i + 1, j + 1); idx[ii++] = (uint)Index(i, j + 1);
                }
            }
        }
        Array.Resize(ref idx, ii);
        Relax(pos, nrm, idx, vi, seg * 2);
        // keep tangents perpendicular to the relaxed normals
        for (var v = 0; v < vi; v++)
        {
            var n = V(nrm, v); var t = V(tan, v);
            t = t - n * Vec.Dot(n, t);
            var l = MathF.Sqrt(Vec.Dot(t, t));
            if (l <= 0) continue;
            tan[v * 3] = t.X / l; tan[v * 3 + 1] = t.Y / l; tan[v * 3 + 2] = t.Z / l;
        }
        var sub = new ShapeData
        {
            Name = shape.Name, NifPath = shape.NifPath, Positions = pos, Uvs = uv, Indices = idx,
            DiffusePath = shape.DiffusePath, TexturePaths = shape.TexturePaths, ModelSpaceNormals = shape.ModelSpaceNormals,
        };
        return (sub, new SurfaceFrames { Normals = nrm, Tangents = tan, Bitangents = bit, UvScales = scl });
    }

    /// <summary>
    /// Averages each normal with its neighbours a few times. Vertices at the same point (shared edges, UV seams)
    /// are treated as one, so the result stays continuous across triangles and seams.
    /// </summary>
    private static void Relax(float[] pos, float[] nrm, uint[] idx, int count, int iterations)
    {
        var key = new Dictionary<(long, long, long), int>();
        var point = new int[count];
        for (var v = 0; v < count; v++)
        {
            var k = ((long)MathF.Round(pos[v * 3] * 2000), (long)MathF.Round(pos[v * 3 + 1] * 2000), (long)MathF.Round(pos[v * 3 + 2] * 2000));
            if (!key.TryGetValue(k, out var id)) key[k] = id = key.Count;
            point[v] = id;
        }
        var points = key.Count;
        var neighbours = new HashSet<int>[points];
        for (var i = 0; i < points; i++) neighbours[i] = new HashSet<int>();
        for (var t = 0; t + 2 < idx.Length; t += 3)
        {
            int a = point[idx[t]], b = point[idx[t + 1]], c = point[idx[t + 2]];
            neighbours[a].Add(b); neighbours[a].Add(c); neighbours[b].Add(a); neighbours[b].Add(c); neighbours[c].Add(a); neighbours[c].Add(b);
        }
        var n = new Vec[points];
        for (var v = 0; v < count; v++) n[point[v]] += V(nrm, v);
        for (var i = 0; i < points; i++) n[i] = n[i].Normalized();
        for (var it = 0; it < iterations; it++)
        {
            var next = new Vec[points];
            Parallel.For(0, points, i =>
            {
                var sum = n[i];
                foreach (var j in neighbours[i]) sum += n[j];
                next[i] = sum.Normalized();
            });
            n = next;
        }
        for (var v = 0; v < count; v++) { var m = n[point[v]]; nrm[v * 3] = m.X; nrm[v * 3 + 1] = m.Y; nrm[v * 3 + 2] = m.Z; }
    }

    private static Vec V(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);

    private readonly record struct Vec(float X, float Y, float Z)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec operator *(Vec a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public Vec Normalized() { var l = MathF.Sqrt(Dot(this, this)); return l > 0 ? this * (1f / l) : this; }
    }
}
