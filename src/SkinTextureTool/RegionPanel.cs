using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SkinTextureTool.Core.Generation;
using SkinTextureTool.Imaging;

namespace SkinTextureTool;

/// <summary>
/// Masked regions with their own skin settings (face, palms, knees…). Each region renders the full skin with
/// its settings and blends over the base skin through its mask, per texture group.
/// </summary>
public sealed class RegionPanel : DockPanel
{
    private readonly ListBox _list = new() { Height = 120, DisplayMemberPath = nameof(Region.Name) };
    private readonly StackPanel _editor = new() { Margin = new Thickness(0, 8, 0, 0) };
    private SkinSettings _settings = new();
    private IReadOnlyList<string> _groups = Array.Empty<string>();

    /// <summary>Raised after any region value changes.</summary>
    public event Action? Changed;

    public SkinSettings Settings
    {
        get => _settings;
        set { _settings = value; RefreshList(null); }
    }

    public IReadOnlyList<string> Groups
    {
        get => _groups;
        set { _groups = value; ShowEditor(); }
    }

    public RegionPanel()
    {
        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 6),
            Text = "A region is a masked area with its own skin settings, blended over the base skin. Give it a greyscale mask "
                 + "per texture (white = this region) and change any layer: more pores on the face, no freckles on the palms… "
                 + "Regions apply in list order; the normal bake uses the blended result.",
        };
        SetDock(help, Dock.Top);
        Children.Add(help);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        buttons.Children.Add(Button("Add", () => Add(new Region { Name = UniqueName("Region"), Settings = _settings.CloneWithoutRegions() })));
        buttons.Children.Add(Button("Duplicate", () =>
        {
            if (_list.SelectedItem is not Region r) return;
            var copy = new Region
            {
                Name = UniqueName(r.Name), Enabled = r.Enabled, Strength = r.Strength,
                MaskPaths = new Dictionary<string, string>(r.MaskPaths, StringComparer.OrdinalIgnoreCase),
                Settings = r.Settings.CloneWithoutRegions(),
            };
            Add(copy);
        }));
        buttons.Children.Add(Button("Remove", () =>
        {
            if (_list.SelectedItem is not Region r) return;
            _settings.Regions.Remove(r);
            RefreshList(null);
            Changed?.Invoke();
        }));
        buttons.Children.Add(Button("Up", () => Move(-1)));
        buttons.Children.Add(Button("Down", () => Move(1)));
        SetDock(buttons, Dock.Top);
        Children.Add(buttons);
        SetDock(_list, Dock.Top);
        Children.Add(_list);
        _list.SelectionChanged += (_, _) => ShowEditor();
        Children.Add(_editor);
    }

    private static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    private void Add(Region r)
    {
        _settings.Regions.Add(r);
        RefreshList(r);
        Changed?.Invoke();
    }

    private void Move(int delta)
    {
        if (_list.SelectedItem is not Region r) return;
        var i = _settings.Regions.IndexOf(r);
        var j = i + delta;
        if (j < 0 || j >= _settings.Regions.Count) return;
        _settings.Regions.RemoveAt(i);
        _settings.Regions.Insert(j, r);
        RefreshList(r);
        Changed?.Invoke();
    }

    private string UniqueName(string wanted)
    {
        var name = wanted;
        for (var i = 2; _settings.Regions.Any(r => r.Name == name); i++) name = $"{wanted} {i}";
        return name;
    }

    private void RefreshList(Region? select)
    {
        _list.ItemsSource = null;
        _list.ItemsSource = _settings.Regions;
        _list.SelectedItem = select;
        ShowEditor();
    }

    private void ShowEditor()
    {
        _editor.Children.Clear();
        if (_list.SelectedItem is not Region region)
        {
            _editor.Children.Add(new TextBlock { Text = _settings.Regions.Count == 0 ? "No regions yet." : "Pick a region to edit it.", Foreground = Brushes.Gray });
            return;
        }

        var name = new TextBox { Text = region.Name };
        name.LostFocus += (_, _) =>
        {
            var n = name.Text.Trim();
            if (n.Length == 0 || n == region.Name) return;
            region.Name = n;
            RefreshList(region);
        };
        _editor.Children.Add(Row("Name", name));

        var enabled = new CheckBox { Content = "Enabled", IsChecked = region.Enabled, Margin = new Thickness(0, 2, 0, 2) };
        enabled.Checked += (_, _) => { region.Enabled = true; Changed?.Invoke(); };
        enabled.Unchecked += (_, _) => { region.Enabled = false; Changed?.Invoke(); };
        _editor.Children.Add(enabled);

        var strengthText = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, Text = region.Strength.ToString("0.00", CultureInfo.InvariantCulture) };
        var strength = new System.Windows.Controls.Slider { Minimum = 0, Maximum = 1, Value = region.Strength };
        strength.ValueChanged += (_, e) =>
        {
            region.Strength = (float)e.NewValue;
            strengthText.Text = e.NewValue.ToString("0.00", CultureInfo.InvariantCulture);
            Changed?.Invoke();
        };
        var strengthRow = new DockPanel();
        SetDock(strengthText, Dock.Right);
        strengthRow.Children.Add(strengthText);
        strengthRow.Children.Add(strength);
        _editor.Children.Add(Row("Strength", strengthRow));

        if (_groups.Count == 0)
            _editor.Children.Add(new TextBlock { Text = "Load a mesh to choose masks.", Foreground = Brushes.Gray });
        foreach (var group in _groups)
            _editor.Children.Add(MaskPicker(region, group));

        var copy = new Button { Content = "Copy base skin settings into this region", Margin = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 1, 8, 1) };
        copy.Click += (_, _) =>
        {
            region.Settings = _settings.CloneWithoutRegions();
            ShowEditor();
            Changed?.Invoke();
        };
        _editor.Children.Add(copy);

        _editor.Children.Add(new TextBlock { Text = "Region skin settings", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
        var panel = new SettingsPanel { Groups = _groups };
        panel.Exclude.Add(nameof(SkinSettings.HairMesh));
        panel.Exclude.Add(nameof(SkinSettings.AddonHair));
        panel.Settings = region.Settings;
        panel.Changed += () => Changed?.Invoke();
        _editor.Children.Add(panel);
    }

    private FrameworkElement MaskPicker(Region region, string group)
    {
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.Gray };
        void Show() { var v = region.MaskFor(group); label.Text = v == null ? "(none)" : Path.GetFileName(v); label.ToolTip = v; }
        Show();
        var browse = new Button { Content = "…", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = ImageFiles.Filter, Title = $"Mask for {region.Name} on {group} (white = region)" };
            if (dlg.ShowDialog() != true) return;
            region.MaskPaths[group] = dlg.FileName;
            Show();
            Changed?.Invoke();
        };
        var clear = new Button { Content = "✕", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        clear.Click += (_, _) => { region.MaskPaths.Remove(group); Show(); Changed?.Invoke(); };
        var right = new DockPanel();
        SetDock(clear, Dock.Right);
        SetDock(browse, Dock.Right);
        right.Children.Add(clear);
        right.Children.Add(browse);
        right.Children.Add(label);
        return Row($"Mask ({group})", right);
    }

    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var text = new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        SetDock(text, Dock.Left);
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }
}
