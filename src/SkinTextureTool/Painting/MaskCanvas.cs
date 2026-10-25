using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTK.Mathematics;
using SkinTextureTool.Rendering;

namespace SkinTextureTool.Painting;

/// <summary>
/// A paintable mask for one texture group. Every covered texel knows the surface point and normal it shows, so
/// a brush placed on the 3D body paints every texel within its radius, whatever the UV layout looks like.
/// </summary>
internal sealed class MaskCanvas
{
    private const float CellSize = 1f;

    public string Group { get; }
    public int Size { get; }
    /// <summary>Mask values 0..1, row 0 at v = 0.</summary>
    public float[] Values { get; }

    private readonly int[] _texels;          // covered texel indices
    private readonly Vector3[] _positions;   // surface point per covered texel
    private readonly Vector3[] _normals;
    private readonly bool[] _covered;
    private readonly Dictionary<(int, int, int), List<int>> _cells = new(); // grid cell -> indices into _texels

    public MaskCanvas(string group, int size, IReadOnlyList<MeshInput> shapes)
    {
        Group = group;
        Size = size;
        Values = new float[size * size];
        _covered = new bool[size * size];
        var texels = new List<int>();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        foreach (var (s, f) in shapes)
        {
            var P = s.Positions; var N = f.Normals; var U = s.Uvs; var I = s.Indices;
            for (var t = 0; t + 2 < I.Length; t += 3)
            {
                int a = (int)I[t], b = (int)I[t + 1], c = (int)I[t + 2];
                float ux = U[a * 2] * size, uy = U[a * 2 + 1] * size, vx = U[b * 2] * size, vy = U[b * 2 + 1] * size, wx = U[c * 2] * size, wy = U[c * 2 + 1] * size;
                var area = (vx - ux) * (wy - uy) - (wx - ux) * (vy - uy);
                if (MathF.Abs(area) < 1e-9f) continue;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ux, MathF.Min(vx, wx)))), x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(ux, MathF.Max(vx, wx))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(uy, MathF.Min(vy, wy)))), y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(uy, MathF.Max(vy, wy))));
                for (var py = y0; py <= y1; py++)
                for (var px = x0; px <= x1; px++)
                {
                    var i = py * size + px;
                    if (_covered[i]) continue;
                    float qx = px + 0.5f, qy = py + 0.5f;
                    var l1 = ((wx - vx) * (qy - vy) - (wy - vy) * (qx - vx)) / area;
                    var l2 = ((ux - wx) * (qy - wy) - (uy - wy) * (qx - wx)) / area;
                    var l3 = 1 - l1 - l2;
                    if (l1 < -1e-3f || l2 < -1e-3f || l3 < -1e-3f) continue;
                    Vector3 V(float[] arr, int k) => new(arr[k * 3], arr[k * 3 + 1], arr[k * 3 + 2]);
                    _covered[i] = true;
                    texels.Add(i);
                    positions.Add(V(P, a) * l1 + V(P, b) * l2 + V(P, c) * l3);
                    var n = V(N, a) * l1 + V(N, b) * l2 + V(N, c) * l3;
                    normals.Add(n.LengthSquared > 0 ? n.Normalized() : Vector3.UnitZ);
                }
            }
        }
        _texels = texels.ToArray();
        _positions = positions.ToArray();
        _normals = normals.ToArray();
        for (var k = 0; k < _positions.Length; k++)
        {
            var key = Cell(_positions[k]);
            if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
            list.Add(k);
        }
    }

    private static (int, int, int) Cell(Vector3 p) =>
        ((int)MathF.Floor(p.X / CellSize), (int)MathF.Floor(p.Y / CellSize), (int)MathF.Floor(p.Z / CellSize));

    /// <summary>
    /// One dab of the brush. Texels facing away from the brush (the far side of an arm, the other breast) are
    /// left alone, so the brush doesn't paint through thin parts.
    /// </summary>
    public bool Dab(Vector3 centre, Vector3 normal, float radius, float strength, float hardness, bool erase)
    {
        var changed = false;
        var r2 = radius * radius;
        var lo = Cell(centre - new Vector3(radius));
        var hi = Cell(centre + new Vector3(radius));
        for (var x = lo.Item1; x <= hi.Item1; x++)
        for (var y = lo.Item2; y <= hi.Item2; y++)
        for (var z = lo.Item3; z <= hi.Item3; z++)
        {
            if (!_cells.TryGetValue((x, y, z), out var list)) continue;
            foreach (var k in list)
            {
                var d2 = (_positions[k] - centre).LengthSquared;
                if (d2 > r2) continue;
                var facing = Vector3.Dot(_normals[k], normal);
                if (facing < 0.1f) continue;
                var d = MathF.Sqrt(d2) / radius;
                var w = d <= hardness ? 1f : 1f - Smooth((d - hardness) / MathF.Max(1f - hardness, 1e-3f));
                w *= strength * MathF.Min(1f, facing / 0.4f);
                var i = _texels[k];
                var v = Values[i];
                Values[i] = erase ? v * (1 - w) : v + (1 - v) * w;
                changed |= Values[i] != v;
            }
        }
        return changed;
    }

    private static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    public void Fill(float value) { foreach (var i in _texels) Values[i] = value; }

    public bool IsEmpty => _texels.All(i => Values[i] < 1f / 255f);

    /// <summary>The mask as 8-bit greys for display, covered texels only.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Values.Length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(Values[i] * 255 + 0.5f);
        return bytes;
    }

    /// <summary>Loads an existing mask image, resampled to the canvas size.</summary>
    public void Load(ImageData image)
    {
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var sx = Math.Min(image.Width - 1, x * image.Width / Size);
            var sy = Math.Min(image.Height - 1, y * image.Height / Size);
            Values[y * Size + x] = image.Rgba[(sy * image.Width + sx) * 4] / 255f;
        }
    }

    /// <summary>Writes the mask as a greyscale PNG, bleeding values past island edges so filtering doesn't darken seams.</summary>
    public void Save(string path)
    {
        var values = (float[])Values.Clone();
        var have = (bool[])_covered.Clone();
        for (var step = 0; step < 8; step++)
        {
            var add = new List<(int, float)>();
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var i = y * Size + x;
                if (have[i]) continue;
                float sum = 0; var n = 0;
                if (x > 0 && have[i - 1]) { sum += values[i - 1]; n++; }
                if (x < Size - 1 && have[i + 1]) { sum += values[i + 1]; n++; }
                if (y > 0 && have[i - Size]) { sum += values[i - Size]; n++; }
                if (y < Size - 1 && have[i + Size]) { sum += values[i + Size]; n++; }
                if (n > 0) add.Add((i, sum / n));
            }
            foreach (var (i, v) in add) { values[i] = v; have[i] = true; }
        }
        var bytes = values.Select(v => (byte)(Math.Clamp(v, 0, 1) * 255 + 0.5f)).ToArray();
        var bitmap = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Gray8, null, bytes, Size);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }
}
