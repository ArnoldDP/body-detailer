using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BCnEncoder.Decoder;
using BCnEncoder.Shared.ImageFiles;
using Pfim;
using SkinTextureTool.Rendering;

namespace SkinTextureTool.Imaging;

/// <summary>Reads mask and detail images (PNG, JPG, BMP, TIFF, TGA, DDS) as RGBA, top row first.</summary>
public static class ImageFiles
{
    public const string Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.tga;*.dds)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.tga;*.dds";

    public static ImageData Load(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".dds" => LoadDds(path),
            ".tga" => LoadPfim(path),
            _ => LoadWpf(path),
        };
    }

    private static ImageData LoadWpf(string path)
    {
        var frame = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var w = bgra.PixelWidth;
        var h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        for (var i = 0; i < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
        return new ImageData(w, h, px);
    }

    private static ImageData LoadDds(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var dds = DdsFile.Load(stream);
            var pixels = new BcDecoder().Decode(dds);
            var w = (int)dds.header.dwWidth;
            var h = (int)dds.header.dwHeight;
            var px = new byte[w * h * 4];
            for (var i = 0; i < pixels.Length; i++)
            {
                px[i * 4] = pixels[i].r; px[i * 4 + 1] = pixels[i].g; px[i * 4 + 2] = pixels[i].b; px[i * 4 + 3] = pixels[i].a;
            }
            return new ImageData(w, h, px);
        }
        catch
        {
            return LoadPfim(path); // uncompressed DDS
        }
    }

    private static ImageData LoadPfim(string path)
    {
        using var image = Pfimage.FromFile(path);
        var w = image.Width;
        var h = image.Height;
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 4;
            switch (image.Format)
            {
                case ImageFormat.Rgba32:
                {
                    var i = y * image.Stride + x * 4;
                    px[o] = image.Data[i + 2]; px[o + 1] = image.Data[i + 1]; px[o + 2] = image.Data[i]; px[o + 3] = image.Data[i + 3];
                    break;
                }
                case ImageFormat.Rgb24:
                {
                    var i = y * image.Stride + x * 3;
                    px[o] = image.Data[i + 2]; px[o + 1] = image.Data[i + 1]; px[o + 2] = image.Data[i]; px[o + 3] = 255;
                    break;
                }
                case ImageFormat.Rgb8:
                {
                    var v = image.Data[y * image.Stride + x];
                    px[o] = px[o + 1] = px[o + 2] = v; px[o + 3] = 255;
                    break;
                }
                default:
                    throw new NotSupportedException($"{Path.GetFileName(path)}: unsupported pixel format {image.Format}.");
            }
        }
        return new ImageData(w, h, px);
    }
}
