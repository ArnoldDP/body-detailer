using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace SkinTextureTool.Core.Export;

/// <summary>Writes RGBA pixels (top row first) as a mipmapped BC7 DDS, the format Skyrim body textures use.</summary>
public static class DdsExporter
{
    public static void WriteBc7(string path, byte[] rgba, int width, int height, CompressionQuality quality = CompressionQuality.Fast)
    {
        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                GenerateMipMaps = true,
                Quality = quality,
                Format = CompressionFormat.Bc7,
                FileFormat = OutputFileFormat.Dds,
            },
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        encoder.EncodeToStream(rgba, width, height, PixelFormat.Rgba32, file);
    }
}
