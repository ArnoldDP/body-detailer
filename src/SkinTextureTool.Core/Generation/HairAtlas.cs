namespace SkinTextureTool.Core.Generation;

/// <summary>
/// The hair-card texture: <see cref="HairMeshBuilder.AtlasColumns"/> strips side by side, each a tuft of thin hairs
/// growing from the bottom of the card (v = 0, the root) toward the top, curling into arcs, hooks and loops, with
/// a few strays. Channels: R the hair's shade (varies hair to hair), G how far along its hair a texel is (0 root,
/// 1 tip: root darkening and the highlight from the middle to the tip), B the strand's sideways direction (for
/// the normal map), A coverage. Row 0 is v = 0.
/// </summary>
public static class HairAtlas
{
    public static byte[] Generate(int size, HairMeshLayer s, int seed)
    {
        var px = new byte[size * size * 4];
        var coverage = new float[size * size];
        var rng = new Random(seed * 31 + 7);
        var strip = size / HairMeshBuilder.AtlasColumns;
        var width = Math.Max(1.1f, size / 900f); // a hair is a little over a texel wide at 1024
        for (var col = 0; col < HairMeshBuilder.AtlasColumns; col++)
        {
            for (var k = 0; k < s.HairsPerCard; k++)
            {
                var stray = rng.NextDouble() < 0.08;
                // root along the bottom of the strip, the hair heading up with a little lean
                var x = col * strip + strip * (0.12f + 0.76f * (float)rng.NextDouble());
                var y = size * 0.02f * (float)rng.NextDouble();
                var length = size * (stray ? 0.95f : 0.45f + 0.5f * (float)rng.NextDouble());
                var angle = MathF.PI / 2 + ((float)rng.NextDouble() - 0.5f) * 0.6f;
                // curl: a steady turn that reverses now and then (arcs and hooks), tighter with Curl, plus frizz kinks
                // the curl's radius in texels: wide waves at low Curl, tight arcs and loops at full
                var radius = size * (0.02f + 0.05f * (float)rng.NextDouble()) / MathF.Max(s.Curl * s.Curl, 0.02f);
                var turn = (rng.NextDouble() < 0.5 ? -1 : 1) / radius;
                var shade = 0.6f + 0.4f * (float)rng.NextDouble();
                var steps = (int)(length / 0.5f);
                var stepLen = length / steps;
                for (var i = 0; i <= steps; i++)
                {
                    var t = i / (float)steps;
                    if (rng.NextDouble() < 0.02 + 0.03 * s.Curl) turn = -turn * (0.6f + 0.8f * (float)rng.NextDouble());
                    angle += turn * stepLen + ((float)rng.NextDouble() - 0.5f) * s.Frizz * 0.5f;
                    // keep the hair inside its strip: it bounces off the edges rather than sliding along them
                    var left = col * strip + width * 2; var right = (col + 1) * strip - width * 2;
                    var nx = x + MathF.Cos(angle) * stepLen;
                    if (nx < left || nx > right) { angle = MathF.PI - angle; turn = -turn; nx = Math.Clamp(x + MathF.Cos(angle) * stepLen, left, right); }
                    x = nx;
                    y += MathF.Sin(angle) * stepLen;
                    if (y >= size - 2) break;
                    var w = width * (1 - 0.6f * t);
                    Stamp(px, coverage, size, x, y, w, shade, t, MathF.Cos(angle));
                }
            }
        }
        return px;
    }

    /// <summary>Draws one anti-aliased dot of a hair, keeping whichever hair covers a texel most.</summary>
    private static void Stamp(byte[] px, float[] coverage, int size, float cx, float cy, float w, float shade, float t, float side)
    {
        int x0 = (int)(cx - w - 1), x1 = (int)(cx + w + 1), y0 = (int)(cy - w - 1), y1 = (int)(cy + w + 1);
        for (var y = Math.Max(0, y0); y <= Math.Min(size - 1, y1); y++)
        for (var x = Math.Max(0, x0); x <= Math.Min(size - 1, x1); x++)
        {
            var d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            var a = Math.Clamp((w * 0.5f + 0.5f - d) / 1f, 0, 1);
            var i = y * size + x;
            if (a <= coverage[i]) continue;
            coverage[i] = a;
            px[i * 4] = (byte)(shade * 255);
            px[i * 4 + 1] = (byte)(t * 255);
            px[i * 4 + 2] = (byte)((side * 0.5f + 0.5f) * 255);
            px[i * 4 + 3] = (byte)(a * 255);
        }
    }
}
