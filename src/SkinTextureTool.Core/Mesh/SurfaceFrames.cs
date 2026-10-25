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
        float weldTolerance = 1e-4f, IReadOnlyList<float[]>? positions = null, float boundaryTolerance = 5e-3f, int seamRings = 6)
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

        // Open-edge vertices (mesh borders, like a head's neck edge meeting the body's) get a second, coarser
        // weld, as in MuDynamicNormalMap: separate meshes rarely share their seam positions exactly, and an
        // unwelded seam bakes a hard line into the normal map.
        var edgeUse = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in faces)
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var e = x < y ? (x, y) : (y, x);
                edgeUse[e] = edgeUse.GetValueOrDefault(e) + 1;
            }
        var boundary = new bool[vertCount];
        foreach (var (e, n) in edgeUse)
            if (n == 1) { boundary[e.Item1] = true; boundary[e.Item2] = true; }

        var welded = Weld(weldPos, vertCount, weldTolerance, boundary, boundaryTolerance);
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
            // The crease reference is the whole welded group's average, so every vertex at a point (including
            // both sides of a seam between meshes) keeps the same faces and ends up with the same normal.
            var self = default(Vec);
            foreach (var link in welded[i])
                foreach (var f in vertexFaces[link]) self += faceN[f];
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

        if (seamRings > 0 && shapes.Count > 1)
            BlendSeams(shapes, offsets, faces, welded, normals, tangents, bitangents, seamRings);

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
    /// Where two meshes meet (a head's neck edge on the body), each side usually bends differently right at the
    /// border, and with only one shared row of vertices the whole change of direction lands in one thin strip
    /// of triangles: a hard line in the normal map even though both sides agree at the seam. This spreads the
    /// change over <paramref name="rings"/> rings of vertices either side, fading out with distance.
    /// </summary>
    private static void BlendSeams(IReadOnlyList<ShapeData> shapes, int[] offsets, List<(int A, int B, int C)> faces, List<int>[] welded,
        float[] normals, float[] tangents, float[] bitangents, int rings)
    {
        var count = normals.Length / 3;
        var shapeOf = new int[count];
        for (var s = 0; s < shapes.Count; s++)
            for (var v = offsets[s]; v < offsets[s + 1]; v++) shapeOf[v] = s;

        var neighbours = new HashSet<int>[count];
        for (var i = 0; i < count; i++) neighbours[i] = new HashSet<int>();
        foreach (var (a, b, c) in faces)
        {
            neighbours[a].Add(b); neighbours[a].Add(c);
            neighbours[b].Add(a); neighbours[b].Add(c);
            neighbours[c].Add(a); neighbours[c].Add(b);
        }

        // distance in rings from the nearest vertex whose welded group spans more than one mesh
        var dist = Enumerable.Repeat(int.MaxValue, count).ToArray();
        var queue = new Queue<int>();
        for (var i = 0; i < count; i++)
        {
            if (welded[i].Select(v => shapeOf[v]).Distinct().Count() < 2) continue;
            dist[i] = 0;
            queue.Enqueue(i);
        }
        if (queue.Count == 0) return;
        while (queue.Count > 0)
        {
            var v = queue.Dequeue();
            if (dist[v] >= rings) continue;
            foreach (var u in neighbours[v].Concat(welded[v]))
            {
                var d = welded[v].Contains(u) ? dist[v] : dist[v] + 1;
                if (d < dist[u]) { dist[u] = d; queue.Enqueue(u); }
            }
        }

        var band = Enumerable.Range(0, count).Where(i => dist[i] <= rings).ToArray();
        for (var iteration = 0; iteration < rings * 2; iteration++)
        {
            var next = (float[])normals.Clone();
            foreach (var v in band)
            {
                float sx = 0, sy = 0, sz = 0;
                foreach (var u in neighbours[v].Concat(welded[v]))
                {
                    sx += normals[u * 3]; sy += normals[u * 3 + 1]; sz += normals[u * 3 + 2];
                }
                var l = MathF.Sqrt(sx * sx + sy * sy + sz * sz);
                if (l <= 0) continue;
                var w = 1f - dist[v] / (float)(rings + 1); // full blending at the seam, none at the band's edge
                var nx = normals[v * 3] + (sx / l - normals[v * 3]) * w;
                var ny = normals[v * 3 + 1] + (sy / l - normals[v * 3 + 1]) * w;
                var nz = normals[v * 3 + 2] + (sz / l - normals[v * 3 + 2]) * w;
                var nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl > 0) { next[v * 3] = nx / nl; next[v * 3 + 1] = ny / nl; next[v * 3 + 2] = nz / nl; }
            }
            // welded copies stay identical
            foreach (var v in band)
            {
                if (welded[v].Count < 2 || welded[v][0] != v) continue;
                float ax = 0, ay = 0, az = 0;
                foreach (var u in welded[v]) { ax += next[u * 3]; ay += next[u * 3 + 1]; az += next[u * 3 + 2]; }
                var al = MathF.Sqrt(ax * ax + ay * ay + az * az);
                if (al <= 0) continue;
                foreach (var u in welded[v]) { next[u * 3] = ax / al; next[u * 3 + 1] = ay / al; next[u * 3 + 2] = az / al; }
            }
            Array.Copy(next, normals, normals.Length);
        }

        // keep tangent frames orthogonal to the blended normals
        foreach (var v in band)
        {
            var n = V(normals, v);
            var t = V(tangents, v);
            if (t.LengthSq <= 0) continue;
            var b = V(bitangents, v);
            t = (t - n * Vec.Dot(n, t)).Normalized();
            var cross = Vec.Cross(n, t);
            Store(tangents, v, t);
            Store(bitangents, v, Vec.Dot(cross, b) < 0 ? -cross : cross);
        }
    }

    /// <summary>
    /// Groups vertices at the same position. Like MuDynamicNormalMap's "weld accuracy" mode, each vertex is
    /// keyed on two grids offset by half a cell, so points straddling a cell edge still find each other.
    /// </summary>
    private static List<int>[] Weld(float[] pos, int count, float tolerance, bool[] boundary, float boundaryTolerance)
    {
        var parent = new int[count];
        for (var i = 0; i < count; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

        void Pass(float cell, Func<int, bool> include)
        {
            foreach (var shift in new[] { 0f, 0.5f })
            {
                var cells = new Dictionary<(long, long, long), int>();
                for (var i = 0; i < count; i++)
                {
                    if (!include(i)) continue;
                    var key = ((long)MathF.Floor(pos[i * 3] / cell + shift),
                               (long)MathF.Floor(pos[i * 3 + 1] / cell + shift),
                               (long)MathF.Floor(pos[i * 3 + 2] / cell + shift));
                    if (cells.TryGetValue(key, out var other)) parent[Find(i)] = Find(other);
                    else cells[key] = i;
                }
            }
        }
        Pass(tolerance, _ => true);

        // Boundary vertices: an exact radius search over neighbouring cells, so no near pair is missed.
        var grid = new Dictionary<(long, long, long), List<int>>();
        (long, long, long) Cell(int i) => ((long)MathF.Floor(pos[i * 3] / boundaryTolerance),
                                          (long)MathF.Floor(pos[i * 3 + 1] / boundaryTolerance),
                                          (long)MathF.Floor(pos[i * 3 + 2] / boundaryTolerance));
        for (var i = 0; i < count; i++)
        {
            if (!boundary[i]) continue;
            var c = Cell(i);
            if (!grid.TryGetValue(c, out var list)) grid[c] = list = new List<int>();
            list.Add(i);
        }
        var r2 = boundaryTolerance * boundaryTolerance;
        foreach (var (c, list) in grid)
            for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue((c.Item1 + dx, c.Item2 + dy, c.Item3 + dz), out var other)) continue;
                foreach (var i in list)
                foreach (var j in other)
                {
                    if (j <= i) continue;
                    float ex = pos[i * 3] - pos[j * 3], ey = pos[i * 3 + 1] - pos[j * 3 + 1], ez = pos[i * 3 + 2] - pos[j * 3 + 2];
                    if (ex * ex + ey * ey + ez * ez <= r2) parent[Find(i)] = Find(j);
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
