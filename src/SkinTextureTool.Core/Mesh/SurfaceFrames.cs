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
        float weldTolerance = 1e-4f, IReadOnlyList<float[]>? positions = null, float boundaryTolerance = 5e-3f, int seamRings = 6, float conformDistance = 8f)
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

        var tJunctions = shapes.Count > 1 ? FindTJunctions(offsets, edgeUse, boundary, weldPos, welded) : new();
        PinTJunctions(tJunctions, normals);
        if (conformDistance > 0 && shapes.Count > 1)
            ConformSeams(shapes, offsets, faces, faceN, boundary, pos, welded, normals, conformDistance);
        if (seamRings > 0 && shapes.Count > 1)
            BlendSeams(shapes, offsets, faces, welded, normals, tangents, bitangents, seamRings);

        PinTJunctions(tJunctions, normals);
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
    /// A smaller mesh meeting a larger one along an open edge (a head on the body's neck) is often shaped
    /// differently right at the join and doesn't share most of its edge vertices. Here the smaller mesh's
    /// normals near that edge are turned to match the larger mesh's own surface direction at the nearest point,
    /// fully at the edge and fading out over <paramref name="distance"/> world units, so the larger surface
    /// carries on into the smaller one instead of meeting it at a crease.
    /// </summary>
    private static void ConformSeams(IReadOnlyList<ShapeData> shapes, int[] offsets, List<(int A, int B, int C)> faces, Vec[] faceN,
        bool[] boundary, float[] pos, List<int>[] welded, float[] normals, float distance)
    {
        const float reach = 2f; // how far apart two meshes' edges can be and still count as joined
        var count = normals.Length / 3;
        var shapeOf = new int[count];
        for (var s = 0; s < shapes.Count; s++)
            for (var v = offsets[s]; v < offsets[s + 1]; v++) shapeOf[v] = s;
        var area = new double[shapes.Count];
        var shapeFaces = new List<int>[shapes.Count];
        for (var s = 0; s < shapes.Count; s++) shapeFaces[s] = new List<int>();
        for (var f = 0; f < faces.Count; f++)
        {
            var (a, b, c) = faces[f];
            var s = shapeOf[a];
            area[s] += MathF.Sqrt(Vec.Cross(V(pos, b) - V(pos, a), V(pos, c) - V(pos, a)).LengthSq) * 0.5;
            shapeFaces[s].Add(f);
        }

        // face centres of each shape, gridded, to read a shape's own surface direction near a point
        var faceCentre = new Vec[faces.Count];
        var faceGrid = new Dictionary<(int, long, long, long), List<int>>();
        (long, long, long) Cell(Vec p, float size) => ((long)MathF.Floor(p.X / size), (long)MathF.Floor(p.Y / size), (long)MathF.Floor(p.Z / size));
        for (var f = 0; f < faces.Count; f++)
        {
            var (a, b, c) = faces[f];
            faceCentre[f] = (V(pos, a) + V(pos, b) + V(pos, c)) * (1f / 3f);
            var (x, y, z) = Cell(faceCentre[f], reach);
            var key = (shapeOf[a], x, y, z);
            if (!faceGrid.TryGetValue(key, out var list)) faceGrid[key] = list = new List<int>();
            list.Add(f);
        }
        Vec SurfaceNear(int shape, Vec p)
        {
            Vec sum = default;
            var (x, y, z) = Cell(p, reach);
            for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
            {
                if (!faceGrid.TryGetValue((shape, x + dx, y + dy, z + dz), out var list)) continue;
                foreach (var f in list)
                {
                    var d2 = (faceCentre[f] - p).LengthSq;
                    if (d2 < reach * reach) sum += faceN[f] * (1f - MathF.Sqrt(d2) / reach);
                }
            }
            return sum.Normalized();
        }

        // open-edge vertices of each shape, gridded, to find where a smaller shape's edge meets a larger one's
        var edgeGrid = new Dictionary<(long, long, long), List<int>>();
        for (var i = 0; i < count; i++)
        {
            if (!boundary[i]) continue;
            var c = Cell(V(pos, i), reach);
            if (!edgeGrid.TryGetValue(c, out var list)) edgeGrid[c] = list = new List<int>();
            list.Add(i);
        }

        var reach2 = SeamReach(shapes);
        var sizes = shapes.Select(Size).ToArray();
        var fullDistance = distance;
        for (var s = 0; s < shapes.Count; s++)
        {
            if (IsInset(sizes[s], sizes.Max())) continue; // insets keep their crease
            distance = MathF.Min(fullDistance, reach2[s]);
            // seam vertices of this shape and the correction (larger surface minus own normal) at each
            var seams = new List<(int V, Vec P, Vec Delta)>();
            for (var i = offsets[s]; i < offsets[s + 1]; i++)
            {
                if (!boundary[i]) continue;
                var p = V(pos, i);
                var (x, y, z) = Cell(p, reach);
                int leader = -1; var best = reach * reach;
                for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++) for (var dz = -1; dz <= 1; dz++)
                {
                    if (!edgeGrid.TryGetValue((x + dx, y + dy, z + dz), out var list)) continue;
                    foreach (var j in list)
                    {
                        if (area[shapeOf[j]] <= area[s]) continue;
                        var d2 = (V(pos, j) - p).LengthSq;
                        if (d2 < best) { best = d2; leader = shapeOf[j]; }
                    }
                }
                if (leader < 0) continue;
                var target = SurfaceNear(leader, p);
                if (target.LengthSq <= 0) continue;
                seams.Add((i, p, target - V(normals, i)));
            }
            if (seams.Count == 0) continue;

            // Within the band the normals are re-interpolated smoothly (repeated neighbour averaging) between the
            // larger surface's direction at the seam and the shape's own normals at the band's far edge, which
            // also irons out any bend the smaller mesh has right at its rim. The result fades in towards the seam.
            var seamTarget = new Dictionary<int, Vec>();
            foreach (var (v, sp, sd) in seams) seamTarget[v] = V(normals, v) + sd;
            var fade = new Dictionary<int, float>();
            for (var i = offsets[s]; i < offsets[s + 1]; i++)
            {
                var p = V(pos, i);
                var nearest = float.MaxValue;
                foreach (var (_, sp, _) in seams) nearest = MathF.Min(nearest, MathF.Sqrt((sp - p).LengthSq));
                if (nearest >= distance) continue;
                var t = 1f - nearest / distance;
                fade[i] = t * t * (3 - 2 * t);
            }
            var neighbours = new Dictionary<int, HashSet<int>>();
            foreach (var f in shapeFaces[s])
            {
                var (fa, fb, fc) = faces[f];
                foreach (var (x, y) in new[] { (fa, fb), (fb, fc), (fc, fa) })
                {
                    if (!fade.ContainsKey(x) && !fade.ContainsKey(y)) continue;
                    (neighbours.TryGetValue(x, out var nx) ? nx : neighbours[x] = new HashSet<int>()).Add(y);
                    (neighbours.TryGetValue(y, out var ny) ? ny : neighbours[y] = new HashSet<int>()).Add(x);
                }
            }
            var smooth = new Dictionary<int, Vec>();
            foreach (var v in neighbours.Keys) smooth[v] = seamTarget.TryGetValue(v, out var st) ? st.Normalized() : V(normals, v);
            var free = fade.Keys.Where(v => !seamTarget.ContainsKey(v) && neighbours.ContainsKey(v)).ToArray();
            for (var iteration = 0; iteration < 400; iteration++)
            {
                var next = new Vec[free.Length];
                for (var k = 0; k < free.Length; k++)
                {
                    Vec sum = default;
                    foreach (var u in neighbours[free[k]]) sum += smooth[u];
                    next[k] = sum.Normalized();
                }
                for (var k = 0; k < free.Length; k++) smooth[free[k]] = next[k];
            }

            var changed = new List<(int V, Vec N)>();
            foreach (var (v, w) in fade)
            {
                if (!smooth.TryGetValue(v, out var target)) continue;
                var own = V(normals, v);
                changed.Add((v, (own + (target - own) * w).Normalized()));
            }
            foreach (var (v, n) in changed)
                foreach (var u in welded[v]) Store(normals, u, n);
        }
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
        var origin = new int[count];
        var reach = SeamReach(shapes);
        var pos = new float[count * 3];
        for (var s = 0; s < shapes.Count; s++) Array.Copy(shapes[s].Positions, 0, pos, offsets[s] * 3, shapes[s].Positions.Length);
        var queue = new Queue<int>();
        for (var i = 0; i < count; i++)
        {
            if (welded[i].Select(v => shapeOf[v]).Distinct().Count() < 2) continue;
            dist[i] = 0;
            origin[i] = i;
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
                if (d >= dist[u]) continue;
                if ((V(pos, u) - V(pos, origin[v])).LengthSq > reach[shapeOf[u]] * reach[shapeOf[u]]) continue;
                dist[u] = d; origin[u] = origin[v]; queue.Enqueue(u);
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
    /// Open-edge vertices of one mesh lying part-way along another mesh's open edge (a high-poly head's extra
    /// neck vertices between the body's): no vertex to weld to, so their normal is taken from the edge instead.
    /// </summary>
    private static List<(int V, int A, int B, float T)> FindTJunctions(int[] offsets, Dictionary<(int, int), int> edgeUse, bool[] boundary, float[] pos, List<int>[] welded)
    {
        int ShapeOf(int v) { var s = Array.BinarySearch(offsets, v); return s >= 0 ? s : ~s - 1; }
        var edges = edgeUse.Where(e => e.Value == 1).Select(e => e.Key).ToList();
        var result = new List<(int, int, int, float)>();
        const float tolerance = 0.06f; // covers vertices the neck fix tucks just under the edge
        for (var v = 0; v < boundary.Length; v++)
        {
            if (!boundary[v] || welded[v].Select(ShapeOf).Distinct().Count() > 1) continue;
            var p = V(pos, v); var shape = ShapeOf(v);
            float best = tolerance * tolerance; (int, int, float)? hit = null;
            foreach (var (a, b) in edges)
            {
                if (ShapeOf(a) == shape) continue;
                var pa = V(pos, a); var ab = V(pos, b) - pa; var len2 = ab.LengthSq;
                if (len2 <= 0) continue;
                var t = Math.Clamp(Vec.Dot(p - pa, ab) / len2, 0, 1);
                var d2 = (pa + ab * t - p).LengthSq;
                if (d2 < best) { best = d2; hit = (a, b, t); }
            }
            if (hit is { } h) result.Add((v, h.Item1, h.Item2, h.Item3));
        }
        return result;
    }

    private static void PinTJunctions(List<(int V, int A, int B, float T)> junctions, float[] normals)
    {
        foreach (var (v, a, b, t) in junctions) Store(normals, v, (V(normals, a) * (1 - t) + V(normals, b) * t).Normalized());
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

    /// <summary>
    /// How far seam passes may reach into each shape: half its size, so a small part joined to the body
    /// keeps its own shape instead of being smoothed flat end to end. Tiny insets (genitals, under 2% of the
    /// largest shape's size) are meant to meet the body at a crease and only get a narrow strip.
    /// </summary>
    private static float[] SeamReach(IReadOnlyList<ShapeData> shapes)
    {
        var size = shapes.Select(Size).ToArray();
        var largest = size.Max();
        return size.Select(d => d * (IsInset(d, largest) ? 0.03f : 0.5f)).ToArray();
    }

    /// <summary>A small part set into a larger one (genitals in the body), as opposed to a head or hand that continues it.</summary>
    private static bool IsInset(float size, float largest) => size < largest * 0.12f;

    private static float Size(ShapeData s)
    {
        var P = s.Positions;
        if (P.Length == 0) return 0f;
        Vec min = new(float.MaxValue, float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue, float.MinValue);
        for (var i = 0; i < P.Length; i += 3)
        {
            min = new(MathF.Min(min.X, P[i]), MathF.Min(min.Y, P[i + 1]), MathF.Min(min.Z, P[i + 2]));
            max = new(MathF.Max(max.X, P[i]), MathF.Max(max.Y, P[i + 1]), MathF.Max(max.Z, P[i + 2]));
        }
        return MathF.Sqrt((max - min).LengthSq);
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
