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
        var width = Math.Max(1.1f, size / 900f) * s.HairThickness;
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

    /// <summary>The alpha cutoff hair is drawn with (the game's alpha test, and the preview's).</summary>
    public const float Cutoff = 0.35f;

    /// <summary>
    /// The mip chain for the atlas, each level half the last (down to 1×1). Colour is averaged by coverage, and each
    /// level's alpha is scaled so the same share of texels clears the cutoff as at full size: thin hairs then thicken
    /// into soft tufts with distance instead of fading below the cutoff and vanishing.
    /// </summary>
    public static List<byte[]> Mips(byte[] level0, int size)
    {
        var levels = new List<byte[]> { level0 };
        var target = Coverage(level0, 1f);
        var prev = level0;
        for (var s = size / 2; s >= 1; s /= 2)
        {
            var next = new byte[s * s * 4];
            var ps = s * 2;
            for (var y = 0; y < s; y++)
            for (var x = 0; x < s; x++)
            {
                float a = 0, r = 0, g = 0, b = 0;
                for (var j = 0; j < 2; j++)
                for (var i = 0; i < 2; i++)
                {
                    var o = ((y * 2 + j) * ps + x * 2 + i) * 4;
                    var w = prev[o + 3];
                    a += w; r += prev[o] * w; g += prev[o + 1] * w; b += prev[o + 2] * w;
                }
                var q = (y * s + x) * 4;
                if (a > 0) { next[q] = (byte)(r / a); next[q + 1] = (byte)(g / a); next[q + 2] = (byte)(b / a); }
                next[q + 3] = (byte)(a / 4);
            }
            // the alpha scale that keeps coverage: a binary search on the share of texels over the cutoff
            float lo = 0.5f, hi = 16f;
            for (var it = 0; it < 14; it++)
            {
                var mid = (lo + hi) / 2;
                if (Coverage(next, mid) < target) lo = mid; else hi = mid;
            }
            var scale = (lo + hi) / 2;
            for (var q = 3; q < next.Length; q += 4) next[q] = (byte)Math.Min(255, next[q] * scale);
            levels.Add(next);
            prev = next;
        }
        return levels;
    }

    private static float Coverage(byte[] px, float scale)
    {
        var over = 0; var cut = Cutoff * 255;
        for (var q = 3; q < px.Length; q += 4) if (px[q] * scale > cut) over++;
        return over / (float)(px.Length / 4);
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
