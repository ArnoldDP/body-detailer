using System.Numerics;
using SkinTextureTool.Core.Mesh;

namespace SkinTextureTool.Core.Generation;

/// <summary>RGBA pixels, row 0 at v = 0.</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Pixels)
{
    /// <summary>Bilinear sample at a texture coordinate (clamped).</summary>
    public Vector4 Sample(float u, float v)
    {
        var x = Math.Clamp(u * Width - 0.5f, 0, Width - 1); var y = Math.Clamp(v * Height - 0.5f, 0, Height - 1);
        int x0 = (int)x, y0 = (int)y, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float fx = x - x0, fy = y - y0;
        Vector4 P(int px, int py) { var o = (py * Width + px) * 4; return new Vector4(Pixels[o], Pixels[o + 1], Pixels[o + 2], Pixels[o + 3]); }
        return Vector4.Lerp(Vector4.Lerp(P(x0, y0), P(x1, y0), fx), Vector4.Lerp(P(x0, y1), P(x1, y1), fx), fy);
    }
}

/// <summary>
/// Makes a genital mesh's textures continue the body's skin, as the TRX seamless Blender bake does: the body's
/// textures are projected onto the genital's own texture layout (each texel takes the body texel at the nearest
/// point of the body surface), then blended into the genital's textures by a mask (white: body skin, black: the
/// genital's own texture). Model-space normal maps carry over directly, since the body and the genitals share
/// one model space.
/// </summary>
public static class GenitalBaker
{
    /// <summary>For each texel of the genital's layout: whether a triangle covers it, and where on the body its point lies.</summary>
    public sealed class Projection
    {
        public required int Size { get; init; }
        public required bool[] Covered { get; init; }
        /// <summary>The body shape the nearest point is on (-1: not projected, as the mask keeps the genital's texture there).</summary>
        public required int[] Shape { get; init; }
        public required float[] U { get; init; }
        public required float[] V { get; init; }
    }

    /// <param name="mask">The blend mask in the genital's layout, or null to project everywhere.</param>
    public static Projection Project(ShapeData genital, IReadOnlyList<ShapeData> body, int size, RgbaImage? mask)
    {
        var count = size * size;
        var covered = new bool[count]; var pos = new Vector3[count];
        // 1. rasterize the genital's triangles in its texture layout, keeping each texel's point on the mesh
        var P = genital.Positions; var T = genital.Uvs; var I = genital.Indices;
        Parallel.For(0, I.Length / 3, t =>
        {
            int a = (int)I[t * 3], b = (int)I[t * 3 + 1], c = (int)I[t * 3 + 2];
            float ax = T[a * 2] * size, ay = T[a * 2 + 1] * size, bx = T[b * 2] * size, by = T[b * 2 + 1] * size, cx = T[c * 2] * size, cy = T[c * 2 + 1] * size;
            var area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (MathF.Abs(area) < 1e-9f) return;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)))), x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)))), y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                var w0 = ((bx - px) * (cy - py) - (cx - px) * (by - py)) / area;
                var w1 = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) / area;
                var w2 = 1 - w0 - w1;
                const float e = -0.02f; // a little past the edges, so seams have no gaps
                if (w0 < e || w1 < e || w2 < e) continue;
                var i = y * size + x;
                covered[i] = true;
                pos[i] = V3(P, a) * w0 + V3(P, b) * w1 + V3(P, c) * w2;
            }
        });

        // 2. the nearest point on the body for every texel the mask lets the body show through
        var grid = new TriangleGrid(body, genital);
        var shape = new int[count]; var u = new float[count]; var v = new float[count];
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                shape[i] = -1;
                if (!covered[i]) continue;
                if (mask != null && mask.Sample((x + 0.5f) / size, (y + 0.5f) / size).X < 1) continue;
                if (grid.Nearest(pos[i]) is { } hit) { shape[i] = hit.Shape; u[i] = hit.U; v[i] = hit.V; }
            }
        });
        return new Projection { Size = size, Covered = covered, Shape = shape, U = u, V = v };
    }

    /// <summary>
    /// One map: the genital's texture, with the body's texture (sampled where the projection found the body)
    /// blended in by the mask. Texels no triangle covers keep the genital's texture.
    /// </summary>
    public static byte[] Combine(Projection p, RgbaImage genital, IReadOnlyList<RgbaImage?> bodyPerShape, RgbaImage? mask)
    {
        var size = p.Size;
        var px = new byte[size * size * 4];
        Parallel.For(0, size, y =>
        {
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                float tu = (x + 0.5f) / size, tv = (y + 0.5f) / size;
                var c = genital.Sample(tu, tv);
                if (p.Shape[i] >= 0 && bodyPerShape[p.Shape[i]] is { } body)
                {
                    var m = mask == null ? 1f : mask.Sample(tu, tv).X / 255f;
                    c = Vector4.Lerp(c, body.Sample(p.U[i], p.V[i]), m);
                }
                px[i * 4] = (byte)Math.Clamp(c.X + 0.5f, 0, 255); px[i * 4 + 1] = (byte)Math.Clamp(c.Y + 0.5f, 0, 255);
                px[i * 4 + 2] = (byte)Math.Clamp(c.Z + 0.5f, 0, 255); px[i * 4 + 3] = (byte)Math.Clamp(c.W + 0.5f, 0, 255);
            }
        });
        return px;
    }

    private static Vector3 V3(float[] a, int i) => new(a[i * 3], a[i * 3 + 1], a[i * 3 + 2]);

    /// <summary>The body's triangles near the genital, bucketed in a grid for nearest-point queries.</summary>
    private sealed class TriangleGrid
    {
        private const float Cell = 0.75f;
        private readonly IReadOnlyList<ShapeData> _body;
        private readonly Dictionary<(int, int, int), List<(int Shape, int Tri)>> _cells = new();

        public TriangleGrid(IReadOnlyList<ShapeData> body, ShapeData near)
        {
            _body = body;
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            for (var i = 0; i < near.VertexCount; i++) { var p = V3(near.Positions, i); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            min -= new Vector3(6); max += new Vector3(6);
            for (var s = 0; s < body.Count; s++)
            {
                var P = body[s].Positions; var I = body[s].Indices;
                for (var t = 0; t + 2 < I.Length; t += 3)
                {
                    var a = V3(P, (int)I[t]); var b = V3(P, (int)I[t + 1]); var c = V3(P, (int)I[t + 2]);
                    var lo = Vector3.Min(a, Vector3.Min(b, c)); var hi = Vector3.Max(a, Vector3.Max(b, c));
                    if (hi.X < min.X || hi.Y < min.Y || hi.Z < min.Z || lo.X > max.X || lo.Y > max.Y || lo.Z > max.Z) continue;
                    for (var x = Key(lo.X); x <= Key(hi.X); x++)
                    for (var y = Key(lo.Y); y <= Key(hi.Y); y++)
                    for (var z = Key(lo.Z); z <= Key(hi.Z); z++)
                    {
                        if (!_cells.TryGetValue((x, y, z), out var list)) _cells[(x, y, z)] = list = new();
                        list.Add((s, t / 3));
                    }
                }
            }
        }

        private static int Key(float f) => (int)MathF.Floor(f / Cell);

        /// <summary>The nearest body point to <paramref name="p"/> (searching rings of cells outward) and its texture coordinate.</summary>
        public (int Shape, float U, float V)? Nearest(Vector3 p)
        {
            var best = float.MaxValue; (int Shape, float U, float V)? hit = null;
            int cx = Key(p.X), cy = Key(p.Y), cz = Key(p.Z);
            for (var r = 0; r <= 8; r++)
            {
                for (var x = cx - r; x <= cx + r; x++)
                for (var y = cy - r; y <= cy + r; y++)
                for (var z = cz - r; z <= cz + r; z++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Max(Math.Abs(y - cy), Math.Abs(z - cz))) != r) continue; // this ring only
                    if (!_cells.TryGetValue((x, y, z), out var list)) continue;
                    foreach (var (s, t) in list)
                    {
                        var shape = _body[s]; var I = shape.Indices;
                        int ia = (int)I[t * 3], ib = (int)I[t * 3 + 1], ic = (int)I[t * 3 + 2];
                        var (q, w) = Closest(p, V3(shape.Positions, ia), V3(shape.Positions, ib), V3(shape.Positions, ic));
                        var d = Vector3.DistanceSquared(p, q);
                        if (d >= best) continue;
                        best = d;
                        var U = shape.Uvs;
                        hit = (s, U[ia * 2] * w.X + U[ib * 2] * w.Y + U[ic * 2] * w.Z, U[ia * 2 + 1] * w.X + U[ib * 2 + 1] * w.Y + U[ic * 2 + 1] * w.Z);
                    }
                }
                // anything in a further ring is at least r cells away
                if (hit != null && MathF.Sqrt(best) <= r * Cell) break;
            }
            return hit;
        }

        /// <summary>The closest point on a triangle and its barycentric weights (after Ericson, Real-Time Collision Detection).</summary>
        private static (Vector3 Point, Vector3 Weights) Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return (a, new Vector3(1, 0, 0));
            var bp = p - b; float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return (b, new Vector3(0, 1, 0));
            var vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) { var t = d1 / (d1 - d3); return (a + t * ab, new Vector3(1 - t, t, 0)); }
            var cp = p - c; float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return (c, new Vector3(0, 0, 1));
            var vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) { var t = d2 / (d2 - d6); return (a + t * ac, new Vector3(1 - t, 0, t)); }
            var va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { var t = (d4 - d3) / (d4 - d3 + (d5 - d6)); return (b + t * (c - b), new Vector3(0, 1 - t, t)); }
            var denom = 1 / (va + vb + vc); float v = vb * denom, w = vc * denom;
            return (a + ab * v + ac * w, new Vector3(1 - v - w, v, w));
        }
    }
}
