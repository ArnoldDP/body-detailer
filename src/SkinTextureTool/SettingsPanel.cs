using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SkinTextureTool.Core.Generation;
using SkinTextureTool.Imaging;

namespace SkinTextureTool;

/// <summary>
/// Builds the settings controls from <see cref="SkinSettings"/> by reflection: one expander per layer, a slider
/// per [Range] number, a hex field per colour, a checkbox per bool and a file picker for masks and detail maps.
/// </summary>
public sealed class SettingsPanel : StackPanel
{
    private SkinSettings _settings = new();
    private bool _building;

    /// <summary>Raised after any value changes.</summary>
    public event Action? Changed;
    /// <summary>Raised when a change needs the mesh's normals rebuilt (the smoothing angle).</summary>
    public event Action? GeometryChanged;

    public SkinSettings Settings
    {
        get => _settings;
        set { _settings = value; Rebuild(); }
    }

    private static readonly string[] TopLevelOrder = { nameof(SkinSettings.Scale), nameof(SkinSettings.Seed), nameof(SkinSettings.UseUvCoordinates) };

    private void Rebuild()
    {
        _building = true;
        Children.Clear();

        var general = new StackPanel { Margin = new Thickness(4) };
        foreach (var name in TopLevelOrder) AddProperty(general, _settings, typeof(SkinSettings).GetProperty(name)!, null);
        Children.Add(new Expander { Header = Bold("General"), Content = general, IsExpanded = true, Margin = new Thickness(0, 0, 0, 4) });

        foreach (var prop in typeof(SkinSettings).GetProperties().Where(p => typeof(Layer).IsAssignableFrom(p.PropertyType)))
        {
            var layer = (Layer)prop.GetValue(_settings)!;
            var body = new StackPanel { Margin = new Thickness(4) };
            var enabled = new CheckBox { IsChecked = layer.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            enabled.Checked += (_, _) => { layer.Enabled = true; Raise(); };
            enabled.Unchecked += (_, _) => { layer.Enabled = false; Raise(); };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(enabled);
            header.Children.Add(Bold(Spaced(prop.Name)));

            foreach (var p in layer.GetType().GetProperties().Where(p => p.Name is not nameof(Layer.Enabled) and not nameof(Layer.MaskPath)))
                AddProperty(body, layer, p, prop.Name);
            if (layer.GetType().GetCustomAttribute<MaskableAttribute>() != null)
                AddFilePicker(body, "Mask", () => layer.MaskPath, v => layer.MaskPath = v);

            Children.Add(new Expander { Header = header, Content = body, Margin = new Thickness(0, 0, 0, 4) });
        }
        _building = false;
    }

    private void AddProperty(Panel panel, object owner, PropertyInfo p, string? layerName)
    {
        var label = Spaced(p.Name);
        if (p.PropertyType == typeof(float) || p.PropertyType == typeof(int))
        {
            var range = p.GetCustomAttribute<RangeAttribute>();
            if (p.Name == nameof(SkinSettings.Seed))
            {
                AddSeed(panel, owner, p);
                return;
            }
            var min = range?.Min ?? 0;
            var max = range?.Max ?? 1;
            var isInt = p.PropertyType == typeof(int);
            var value = Convert.ToDouble(p.GetValue(owner), CultureInfo.InvariantCulture);
            var text = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = Format(value, isInt) };
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, VerticalAlignment = VerticalAlignment.Center };
            slider.ValueChanged += (_, e) =>
            {
                var v = isInt ? Math.Round(e.NewValue) : e.NewValue;
                p.SetValue(owner, isInt ? (object)(int)v : (float)v);
                text.Text = Format(v, isInt);
                if (owner is NormalLayer && p.Name == nameof(NormalLayer.SmoothDegree)) RaiseGeometry();
                else Raise();
            };
            panel.Children.Add(Row(label, slider, text));
        }
        else if (p.PropertyType == typeof(bool))
        {
            var box = new CheckBox { Content = label, IsChecked = (bool)p.GetValue(owner)!, Margin = new Thickness(0, 3, 0, 3) };
            box.Checked += (_, _) => { p.SetValue(owner, true); Raise(); };
            box.Unchecked += (_, _) => { p.SetValue(owner, false); Raise(); };
            panel.Children.Add(box);
        }
        else if (p.PropertyType == typeof(Rgb))
        {
            var colour = (Rgb)p.GetValue(owner)!;
            var swatch = new Border { Width = 22, Height = 18, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Background = Brush(colour), Margin = new Thickness(0, 0, 6, 0) };
            var box = new TextBox { Text = colour.ToHex(), Width = 80, VerticalContentAlignment = VerticalAlignment.Center };
            box.TextChanged += (_, _) =>
            {
                if (!Regex.IsMatch(box.Text, "^#?[0-9A-Fa-f]{6}$")) return;
                var c = Rgb.FromHex(box.Text);
                p.SetValue(owner, c);
                swatch.Background = Brush(c);
                Raise();
            };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(swatch);
            right.Children.Add(box);
            panel.Children.Add(Row(label, right, null));
        }
        else if (p.PropertyType == typeof(string) && p.Name == nameof(NormalLayer.DetailMapPath))
        {
            AddFilePicker(panel, "Detail map", () => (string?)p.GetValue(owner), v => p.SetValue(owner, v));
        }
    }

    private void AddSeed(Panel panel, object owner, PropertyInfo p)
    {
        var box = new TextBox { Text = p.GetValue(owner)!.ToString(), Width = 70, VerticalContentAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) => { if (int.TryParse(box.Text, out var v)) { p.SetValue(owner, v); Raise(); } };
        var shuffle = new Button { Content = "New seed", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(6, 0, 0, 0) };
        shuffle.Click += (_, _) => box.Text = Random.Shared.Next(1, 1_000_000).ToString();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(box);
        right.Children.Add(shuffle);
        panel.Children.Add(Row("Seed", right, null));
    }

    private void AddFilePicker(Panel panel, string label, Func<string?> get, Action<string?> set)
    {
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.Gray };
        void Show() { var v = get(); name.Text = v == null ? "(none)" : Path.GetFileName(v); name.ToolTip = v; }
        Show();
        var browse = new Button { Content = "…", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = ImageFiles.Filter, Title = label };
            if (dlg.ShowDialog() != true) return;
            set(dlg.FileName); Show(); Raise();
        };
        var clear = new Button { Content = "✕", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        clear.Click += (_, _) => { set(null); Show(); Raise(); };
        var right = new DockPanel();
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        right.Children.Add(clear);
        right.Children.Add(browse);
        right.Children.Add(name);
        panel.Children.Add(Row(label, right, null));
    }

    private static FrameworkElement Row(string label, FrameworkElement control, FrameworkElement? trailing)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var text = new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(text, Dock.Left);
        row.Children.Add(text);
        if (trailing != null)
        {
            DockPanel.SetDock(trailing, Dock.Right);
            row.Children.Add(trailing);
        }
        row.Children.Add(control);
        return row;
    }

    private static TextBlock Bold(string s) => new() { Text = s, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private static string Spaced(string name) => Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
    private static string Format(double v, bool isInt) => isInt ? ((int)v).ToString() : v.ToString("0.00", CultureInfo.InvariantCulture);
    private static SolidColorBrush Brush(Rgb c) => new(Color.FromRgb((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255)));

    private void Raise() { if (!_building) Changed?.Invoke(); }
    private void RaiseGeometry() { if (!_building) GeometryChanged?.Invoke(); }
}
