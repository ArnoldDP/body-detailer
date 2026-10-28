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
    /// <summary>Raised when the hair mesh settings change (the hair cards rebuild; the textures don't).</summary>
    public event Action? HairChanged;

    public SkinSettings Settings
    {
        get => _settings;
        set { _settings = value; Rebuild(); }
    }

    private IReadOnlyList<string> _groups = Array.Empty<string>();
    /// <summary>Only this layer (by property name), or every layer but <see cref="Exclude"/> when null.</summary>
    public string? Only { get; set; }
    /// <summary>Leave out mask pickers (a layer whose mask comes from elsewhere).</summary>
    public bool NoMasks { get; set; }
    /// <summary>Layers left out (by property name), shown elsewhere.</summary>
    public HashSet<string> Exclude { get; } = new();
    /// <summary>The layers whose sections are open, kept when the panel is rebuilt.</summary>
    private readonly HashSet<string> _expanded = new();

    /// <summary>The loaded texture groups (texture names); masks and detail maps are chosen per group.</summary>
    public IReadOnlyList<string> Groups
    {
        get => _groups;
        set { _groups = value; Rebuild(); }
    }

    private static readonly string[] TopLevelOrder = { nameof(SkinSettings.Scale), nameof(SkinSettings.Seed), nameof(SkinSettings.UseUvCoordinates) };

    private void Rebuild()
    {
        _building = true;
        Children.Clear();

        if (Only == null)
        {
            var general = new StackPanel { Margin = new Thickness(4) };
            foreach (var name in TopLevelOrder) AddProperty(general, _settings, typeof(SkinSettings).GetProperty(name)!, null);
            Children.Add(new Expander { Header = Bold("General"), Content = general, IsExpanded = true, Margin = new Thickness(0, 0, 0, 4) });
        }

        foreach (var prop in typeof(SkinSettings).GetProperties().Where(p => typeof(Layer).IsAssignableFrom(p.PropertyType)
                     && (Only == null ? !Exclude.Contains(p.Name) : p.Name == Only)))
        {
            var layer = (Layer)prop.GetValue(_settings)!;
            var body = new StackPanel { Margin = new Thickness(4) };
            var enabled = new CheckBox { IsChecked = layer.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var hair = layer is HairMeshLayer;
            enabled.Checked += (_, _) => { layer.Enabled = true; if (hair) RaiseHair(); else Raise(); };
            enabled.Unchecked += (_, _) => { layer.Enabled = false; if (hair) RaiseHair(); else Raise(); };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(enabled);
            header.Children.Add(Bold(Spaced(prop.Name)));

            if (layer is HairMeshLayer hairLayer)
            {
                // preset buttons: set the shape sliders, then rebuild the panel to show them
                var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                presets.Children.Add(new TextBlock { Text = "Presets:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                foreach (var (presetName, apply) in HairMeshLayer.Presets)
                {
                    var button = new Button { Content = presetName, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 2) };
                    button.Click += (_, _) => { apply(hairLayer); Rebuild(); RaiseHair(); };
                    presets.Children.Add(button);
                }
                body.Children.Add(presets);
                if (!NoMasks)
                {
                    // where it grows: a region's masks, or its own
                    var regions = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
                    regions.Items.Add("(its own masks, below)");
                    foreach (var r in _settings.Regions) regions.Items.Add(r.Name);
                    regions.SelectedItem = hairLayer.MaskRegion.Length > 0 && _settings.Regions.Any(r => r.Name == hairLayer.MaskRegion) ? hairLayer.MaskRegion : regions.Items[0];
                    regions.SelectionChanged += (_, _) => { hairLayer.MaskRegion = regions.SelectedIndex <= 0 ? "" : (string)regions.SelectedItem; RaiseHair(); };
                    body.Children.Add(Row("Mask region", regions, null));
                }
            }
            foreach (var p in layer.GetType().GetProperties().Where(p => p.Name is not nameof(Layer.Enabled) and not nameof(Layer.MaskPaths) && p.CanWrite))
                AddProperty(body, layer, p, prop.Name);
            if (layer.GetType().GetCustomAttribute<MaskableAttribute>() != null && !NoMasks)
                AddPerGroupPickers(body, "Mask", layer.MaskPaths, hair ? RaiseHair : Raise);
            if (layer is NormalLayer normals)
                AddPerGroupPickers(body, "Detail map", normals.DetailMaps, Raise);

            var expander = new Expander { Header = header, Content = body, Margin = new Thickness(0, 0, 0, 4), IsExpanded = _expanded.Contains(prop.Name) };
            var layerName = prop.Name;
            expander.Expanded += (_, _) => _expanded.Add(layerName);
            expander.Collapsed += (_, _) => _expanded.Remove(layerName);
            Children.Add(expander);
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
                if (owner is HairMeshLayer) RaiseHair();
                else if (owner is NormalLayer && p.Name is nameof(NormalLayer.SmoothDegree) or nameof(NormalLayer.SeamBlend) or nameof(NormalLayer.SeamConform) or nameof(NormalLayer.Subdivision)) RaiseGeometry();
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
                if (owner is HairMeshLayer) RaiseHair(); else Raise();
            };
            // click the swatch for a colour wheel; it writes the hex box, which applies the colour
            swatch.Cursor = System.Windows.Input.Cursors.Hand;
            swatch.ToolTip = "Click to pick from a colour wheel";
            swatch.MouseLeftButtonUp += (_, _) => ColorWheel.Show(swatch, (Rgb)p.GetValue(owner)!, c => box.Text = c.ToHex());
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(swatch);
            right.Children.Add(box);
            panel.Children.Add(Row(label, right, null));
        }
    }

    /// <summary>One image picker per loaded texture group, stored in <paramref name="paths"/> by group name.</summary>
    private void AddPerGroupPickers(Panel panel, string label, Dictionary<string, string> paths, Action changed)
    {
        if (_groups.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = $"{label}: load a mesh to choose images.", Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) });
            return;
        }
        foreach (var group in _groups)
            AddFilePicker(panel, _groups.Count == 1 ? label : $"{label} ({group})",
                () => paths.TryGetValue(group, out var v) ? v : null,
                v => { if (v == null) paths.Remove(group); else paths[group] = v; }, changed);
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

    private void AddFilePicker(Panel panel, string label, Func<string?> get, Action<string?> set, Action changed)
    {
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.Gray };
        void Show() { var v = get(); name.Text = v == null ? "(none)" : Path.GetFileName(v); name.ToolTip = v; }
        Show();
        var browse = new Button { Content = "…", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = ImageFiles.Filter, Title = label };
            if (dlg.ShowDialog() != true) return;
            set(dlg.FileName); Show(); changed();
        };
        var clear = new Button { Content = "✕", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        clear.Click += (_, _) => { set(null); Show(); changed(); };
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
    private void RaiseHair() { if (!_building) HairChanged?.Invoke(); }
    private void RaiseGeometry() { if (!_building) GeometryChanged?.Invoke(); }
}
