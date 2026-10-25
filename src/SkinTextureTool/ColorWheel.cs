using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SkinTextureTool.Core.Generation;

namespace SkinTextureTool;

/// <summary>
/// A pop-up colour wheel: hue around the wheel, saturation from the centre out, and a brightness slider.
/// Picks update live through the callback.
/// </summary>
internal static class ColorWheel
{
    private const int Size = 200;
    private static WriteableBitmap? _wheel;

    public static void Show(UIElement anchor, Rgb start, Action<Rgb> changed)
    {
        var (h, s, v) = ToHsv(start);
        var image = new Image { Source = Wheel(), Width = Size, Height = Size, Cursor = Cursors.Cross };
        var marker = new Ellipse { Width = 10, Height = 10, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
        var dimmer = new Ellipse { Width = Size, Height = Size, Fill = Brushes.Black, IsHitTestVisible = false };
        var canvas = new Canvas { Width = Size, Height = Size };
        canvas.Children.Add(image);
        canvas.Children.Add(dimmer);
        canvas.Children.Add(marker);
        var brightness = new Slider { Minimum = 0, Maximum = 1, Value = v, Margin = new Thickness(0, 8, 0, 0) };
        var swatch = new Border { Height = 22, Margin = new Thickness(0, 6, 0, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };

        void Update(bool notify)
        {
            var angle = h * Math.PI * 2;
            Canvas.SetLeft(marker, Size / 2.0 + Math.Cos(angle) * s * Size / 2 - 5);
            Canvas.SetTop(marker, Size / 2.0 - Math.Sin(angle) * s * Size / 2 - 5);
            dimmer.Opacity = 1 - v;
            var c = FromHsv(h, s, v);
            swatch.Background = new SolidColorBrush(Color.FromRgb((byte)(c.R * 255 + 0.5f), (byte)(c.G * 255 + 0.5f), (byte)(c.B * 255 + 0.5f)));
            if (notify) changed(c);
        }

        void Pick(Point p)
        {
            var dx = p.X - Size / 2.0; var dy = Size / 2.0 - p.Y;
            s = (float)Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / (Size / 2.0));
            h = (float)((Math.Atan2(dy, dx) / (Math.PI * 2) + 1) % 1);
            Update(true);
        }
        image.MouseLeftButtonDown += (_, e) => { image.CaptureMouse(); Pick(e.GetPosition(image)); };
        image.MouseMove += (_, e) => { if (image.IsMouseCaptured) Pick(e.GetPosition(image)); };
        image.MouseLeftButtonUp += (_, _) => image.ReleaseMouseCapture();
        brightness.ValueChanged += (_, e) => { v = (float)e.NewValue; Update(true); };

        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(canvas);
        panel.Children.Add(new TextBlock { Text = "Brightness", Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(brightness);
        panel.Children.Add(swatch);
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = false,
            Child = new Border { Background = SystemColors.WindowBrush, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = panel },
        };
        Update(false);
        popup.IsOpen = true;
    }

    private static WriteableBitmap Wheel()
    {
        if (_wheel != null) return _wheel;
        var pixels = new byte[Size * Size * 4];
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            double dx = x + 0.5 - Size / 2.0, dy = Size / 2.0 - (y + 0.5);
            var r = Math.Sqrt(dx * dx + dy * dy) / (Size / 2.0);
            var i = (y * Size + x) * 4;
            if (r > 1) continue;
            var c = FromHsv((float)((Math.Atan2(dy, dx) / (Math.PI * 2) + 1) % 1), (float)r, 1);
            var edge = Math.Clamp((1 - r) * Size / 2, 0, 1); // antialiased rim
            pixels[i] = (byte)(c.B * 255); pixels[i + 1] = (byte)(c.G * 255); pixels[i + 2] = (byte)(c.R * 255); pixels[i + 3] = (byte)(edge * 255);
        }
        _wheel = new WriteableBitmap(Size, Size, 96, 96, PixelFormats.Bgra32, null);
        _wheel.WritePixels(new Int32Rect(0, 0, Size, Size), pixels, Size * 4, 0);
        _wheel.Freeze();
        return _wheel;
    }

    private static Rgb FromHsv(float h, float s, float v)
    {
        var k = (int)(h * 6) % 6; var f = h * 6 - MathF.Floor(h * 6);
        float p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        return k switch { 0 => new(v, t, p), 1 => new(q, v, p), 2 => new(p, v, t), 3 => new(p, q, v), 4 => new(t, p, v), _ => new(v, p, q) };
    }

    private static (float H, float S, float V) ToHsv(Rgb c)
    {
        var max = MathF.Max(c.R, MathF.Max(c.G, c.B)); var min = MathF.Min(c.R, MathF.Min(c.G, c.B)); var d = max - min;
        var h = d == 0 ? 0 : max == c.R ? ((c.G - c.B) / d + 6) % 6 : max == c.G ? (c.B - c.R) / d + 2 : (c.R - c.G) / d + 4;
        return (h / 6, max == 0 ? 0 : d / max, max);
    }
}
