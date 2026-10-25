// Normal and tangent reconstruction ported from MuDynamicNormalMap by Acro748
// (https://github.com/Acro748/MuDynamicNormalMap, MIT licence): vertices split along UV seams are
// welded by position, face normals and UV tangents are averaged across the welded group, and faces
// bending more than the smoothing angle away from the vertex's own normal are left out so creases stay sharp.

namespace SkinTextureTool.Core.Mesh;

/// <summary>Per-vertex normal, tangent and bitangent for baking, all in the shape's vertex space.</summary>
public sealed class SurfaceFrames
{
    public required float[] Normals { get; init; }
    public required float[] Tangents { get; init; }
    public required float[] Bitangents { get; init; }
    /// <summary>World units per unit of U and of V at each vertex, so UV-space height slopes can be scaled to the surface.</summary>
    public required float[] UvScales { get; init; } // (|dP/du|, |dP/dv|) per vertex

    /// <param name="shapes">Shapes baked together; vertices are welded across all of them.</param>
    /// <param name="smoothDegree">Faces further than this from a vertex's own normal don't smooth into it (MuDynamicNormalMap default 60).</param>
    /// <param name="weldTolerance">Positions closer than this count as the same point.</param>
    /// <param name="positions">Optional per-shape positions to use instead of the shapes' own (a morphed copy, for slider-driven normals).</param>
    public static List<SurfaceFrames> Build(IReadOnlyList<ShapeData> shapes, float smoothDegree = 60f, bool allowInvertSmooth = false,
        float weldTolerance = 1e-4f, IReadOnlyList<float[]>? positions = null)
    {
        // Flatten every shape into one vertex/face list so seams between shapes are welded too.
        var offsets = new int[shapes.Count + 1];
        for (var s = 0; s < shapes.Count; s++) offsets[s + 1] = offsets[s] + shapes[s].VertexCount;
        var vertCount = offsets[^1];
        var pos = new float[vertCount * 3];
        var weldPos = new float[vertCount * 3]; // seams are matched on the unmorphed mesh, where they line up
        var uv = new float[vertCount * 2];
        var faces = new List<(int A, int B, int C)>();
        for (var s = 0; s < shapes.Count; s++)
        {
            var source = positions?[s] ?? shapes[s].Positions;
            Array.Copy(source, 0, pos, offsets[s] * 3, source.Length);
            Array.Copy(shapes[s].Positions, 0, weldPos, offsets[s] * 3, shapes[s].Positions.Length);
            Array.Copy(shapes[s].Uvs, 0, uv, offsets[s] * 2, shapes[s].Uvs.Length);
            var idx = shapes[s].Indices;
            for (var t = 0; t + 2 < idx.Length; t += 3)
                faces.Add(((int)idx[t] + offsets[s], (int)idx[t + 1] + offsets[s], (int)idx[t + 2] + offsets[s]));
        }

        var welded = Weld(weldPos, vertCount, weldTolerance);
        var vertexFaces = new List<int>[vertCount];
        for (var i = 0; i < vertCount; i++) vertexFaces[i] = new List<int>();
        var faceN = new Vec[faces.Count];
        var faceT = new Vec[faces.Count];
        var faceB = new Vec[faces.Count];
        for (var f = 0; f < faces.Count; f++)
        {
            var (a, b, c) = faces[f];
            vertexFaces[a].Add(f); vertexFaces[b].Add(f); vertexFaces[c].Add(f);
            Vec p0 = V(pos, a), p1 = V(pos, b), p2 = V(pos, c);
            var dp1 = p1 - p0;
            var dp2 = p2 - p0;
            faceN[f] = Vec.Cross(dp1, dp2).Normalized();
            float du1 = uv[b * 2] - uv[a * 2], dv1 = uv[b * 2 + 1] - uv[a * 2 + 1];
            float du2 = uv[c * 2] - uv[a * 2], dv2 = uv[c * 2 + 1] - uv[a * 2 + 1];
            var r = du1 * dv2 - du2 * dv1;
            r = MathF.Abs(r) < 1e-12f ? 1f : 1f / r;
            faceT[f] = (dp1 * dv2 - dp2 * dv1) * r;
            faceB[f] = (dp2 * du1 - dp1 * du2) * r;
        }

        var smoothCos = MathF.Cos(smoothDegree * MathF.PI / 180f);
        var normals = new float[vertCount * 3];
        var tangents = new float[vertCount * 3];
        var bitangents = new float[vertCount * 3];
        var uvScales = new float[vertCount * 2];
        Parallel.For(0, vertCount, i =>
        {
            if (vertexFaces[i].Count == 0) return;
            var self = default(Vec);
            foreach (var f in vertexFaces[i]) self += faceN[f];
            if (self.LengthSq < 1e-20f) return;
            self = self.Normalized();

            Vec nSum = default, tSum = default, bSum = default;
            float tLen = 0, bLen = 0; var used = 0;
            foreach (var link in welded[i])
            foreach (var f in vertexFaces[link])
            {
                var fn = faceN[f];
                var dot = Vec.Dot(fn, self);
                if (allowInvertSmooth && dot < 0) { dot = -dot; fn = -fn; }
                if (dot < smoothCos) continue;
                nSum += fn; tSum += faceT[f]; bSum += faceB[f];
                tLen += MathF.Sqrt(faceT[f].LengthSq); bLen += MathF.Sqrt(faceB[f].LengthSq); used++;
            }
            if (nSum.LengthSq < 1e-20f) return;
            var n = nSum.Normalized();
            var t = tSum.LengthSq > 1e-20f ? tSum.Normalized() : default;
            var bt = bSum.LengthSq > 1e-20f ? bSum.Normalized() : default;
            if (t.LengthSq > 0)
            {
                t = (t - n * Vec.Dot(n, t)).Normalized();
                // keep the UV handedness the face tangents had
                var cross = Vec.Cross(n, t);
                bt = Vec.Dot(cross, bt) < 0 ? -cross : cross;
            }
            Store(normals, i, n); Store(tangents, i, t); Store(bitangents, i, bt);
            uvScales[i * 2] = tLen / used; uvScales[i * 2 + 1] = bLen / used;
        });

        var result = new List<SurfaceFrames>();
        for (var s = 0; s < shapes.Count; s++)
        {
            var count = shapes[s].VertexCount * 3;
            result.Add(new SurfaceFrames
            {
                Normals = normals.AsSpan(offsets[s] * 3, count).ToArray(),
                Tangents = tangents.AsSpan(offsets[s] * 3, count).ToArray(),
                Bitangents = bitangents.AsSpan(offsets[s] * 3, count).ToArray(),
                UvScales = uvScales.AsSpan(offsets[s] * 2, shapes[s].VertexCount * 2).ToArray(),
            });
        }
        return result;
    }

    /// <summary>
    /// Groups vertices at the same position. Like MuDynamicNormalMap's "weld accuracy" mode, each vertex is
    /// keyed on two grids offset by half a cell, so points straddling a cell edge still find each other.
    /// </summary>
    private static List<int>[] Weld(float[] pos, int count, float tolerance)
    {
        var parent = new int[count];
        for (var i = 0; i < count; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        foreach (var shift in new[] { 0f, 0.5f })
        {
            var cells = new Dictionary<(long, long, long), int>();
            for (var i = 0; i < count; i++)
            {
                var key = ((long)MathF.Floor(pos[i * 3] / tolerance + shift),
                           (long)MathF.Floor(pos[i * 3 + 1] / tolerance + shift),
                           (long)MathF.Floor(pos[i * 3 + 2] / tolerance + shift));
                if (cells.TryGetValue(key, out var other)) parent[Find(i)] = Find(other);
                else cells[key] = i;
            }
        }

        var groups = new Dictionary<int, List<int>>();
        var result = new List<int>[count];
        for (var i = 0; i < count; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var g)) groups[root] = g = new List<int>();
            g.Add(i);
            result[i] = g;
        }
        return result;
    }

    private static Vec V(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);
    private static void Store(float[] a, int i, Vec v) { a[i * 3] = v.X; a[i * 3 + 1] = v.Y; a[i * 3 + 2] = v.Z; }

    private readonly record struct Vec(float X, float Y, float Z)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec operator -(Vec a) => new(-a.X, -a.Y, -a.Z);
        public static Vec operator *(Vec a, float s) => new(a.X * s, a.Y * s, a.Z * s);
        public static float Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec Cross(Vec a, Vec b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
        public float LengthSq => Dot(this, this);
        public Vec Normalized() { var l = MathF.Sqrt(LengthSq); return l > 0 ? this * (1f / l) : this; }
    }
}
