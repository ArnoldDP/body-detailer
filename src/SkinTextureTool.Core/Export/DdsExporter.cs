using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace SkinTextureTool.Core.Export;

/// <summary>How a texture is stored in its DDS.</summary>
public enum DdsFormat
{
    /// <summary>BC7, fast encode: what Skyrim body textures use, quick to write.</summary>
    Bc7Fast,
    /// <summary>BC7, balanced encode: a little cleaner, several times slower.</summary>
    Bc7Balanced,
    /// <summary>BC7, best encode: cleanest gradients, very slow at large sizes.</summary>
    Bc7Best,
    /// <summary>BC1 (DXT1): half the size of BC7, no alpha, visible blocking in smooth gradients.</summary>
    Bc1,
    /// <summary>BC3 (DXT5): same size as BC7 with alpha, older and lower quality.</summary>
    Bc3,
    /// <summary>Uncompressed RGBA: lossless, four times the size of BC7.</summary>
    Rgba8,
}

/// <summary>Writes RGBA pixels (top row first) as a mipmapped DDS.</summary>
public static class DdsExporter
{
    public static void Write(string path, byte[] rgba, int width, int height, DdsFormat format = DdsFormat.Bc7Fast)
    {
        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                GenerateMipMaps = true,
                Quality = format switch
                {
                    DdsFormat.Bc7Balanced => CompressionQuality.Balanced,
                    DdsFormat.Bc7Best => CompressionQuality.BestQuality,
                    _ => CompressionQuality.Fast,
                },
                Format = format switch
                {
                    DdsFormat.Bc1 => CompressionFormat.Bc1,
                    DdsFormat.Bc3 => CompressionFormat.Bc3,
                    DdsFormat.Rgba8 => CompressionFormat.Rgba,
                    _ => CompressionFormat.Bc7,
                },
                FileFormat = OutputFileFormat.Dds,
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.EncodeToStream(rgba, width, height, PixelFormat.Rgba32, file);
    }

    public static void WriteBc7(string path, byte[] rgba, int width, int height) => Write(path, rgba, width, height);

    /// <summary>Box-filters square RGBA pixels down to a smaller power-of-two size.</summary>
    public static byte[] Downscale(byte[] rgba, int size, int target)
    {
        if (target >= size) return rgba;
        var f = size / target;
        var o = new byte[target * target * 4];
        Parallel.For(0, target, y =>
        {
            for (var x = 0; x < target; x++)
            for (var c = 0; c < 4; c++)
            {
                var sum = 0;
                for (var j = 0; j < f; j++)
                for (var i = 0; i < f; i++) sum += rgba[((y * f + j) * size + x * f + i) * 4 + c];
                o[(y * target + x) * 4 + c] = (byte)(sum / (f * f));
            }
        });
        return o;
    }
}
