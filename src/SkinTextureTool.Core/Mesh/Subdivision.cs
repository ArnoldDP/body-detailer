namespace SkinTextureTool.Core.Mesh;

/// <summary>
/// Splits every triangle into smaller ones before baking, as MuDynamicNormalMap does, with the normal following a
/// curved (PN-triangle quadratic) patch instead of a straight blend between the corners. Low-poly areas with
/// strong curvature then bake smooth gradients instead of visible triangle facets.
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
            var pa = V(P, a); var pb = V(P, b); var pc = V(P, c);
            var nab = EdgeNormal(pa, pb, na, nb); var nbc = EdgeNormal(pb, pc, nb, nc); var nca = EdgeNormal(pc, pa, nc, na);
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
                // quadratic normal: corners plus a bent mid-edge normal per edge
                var n = na * (wa * wa) + nb * (wb * wb) + nc * (wc * wc) + nab * (wa * wb) + nbc * (wb * wc) + nca * (wc * wa);
                n = n.Normalized();
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
        var sub = new ShapeData
        {
            Name = shape.Name, NifPath = shape.NifPath, Positions = pos, Uvs = uv, Indices = idx,
            DiffusePath = shape.DiffusePath, ModelSpaceNormals = shape.ModelSpaceNormals,
        };
        return (sub, new SurfaceFrames { Normals = nrm, Tangents = tan, Bitangents = bit, UvScales = scl });
    }

    // PN-triangle mid-edge normal: the two corner normals reflected about the plane across the edge
    private static Vec EdgeNormal(Vec p0, Vec p1, Vec n0, Vec n1)
    {
        var d = p1 - p0;
        var dd = Vec.Dot(d, d);
        var v = dd > 0 ? 2 * Vec.Dot(d, n0 + n1) / dd : 0;
        return (n0 + n1 - d * v).Normalized();
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
