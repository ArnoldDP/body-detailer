using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenTK.Mathematics;
using SkinTextureTool.Core.Generation;
using SkinTextureTool.Imaging;
using SkinTextureTool.Painting;
using SkinTextureTool.Rendering;

namespace SkinTextureTool;

/// <summary>
/// Paints a region's masks straight onto the 3D preview: one canvas per texture group, so a stroke across the
/// neck seam or onto the hands lands in each texture it touches. Saving writes a greyscale PNG per group and
/// points the region at them.
/// </summary>
public sealed class PaintPanel : StackPanel
{
    private const int CanvasSize = 1024;
    private static readonly string MaskFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkinTextureTool", "painted masks");

    private readonly ComboBox _region = new() { DisplayMemberPath = nameof(Region.Name), Margin = new Thickness(0, 2, 0, 0) };
    private readonly Slider _size = new() { Minimum = 0.3, Maximum = 20, Value = 3 };
    private readonly Slider _strength = new() { Minimum = 0.02, Maximum = 1, Value = 0.5 };
    private readonly Slider _hardness = new() { Minimum = 0, Maximum = 0.95, Value = 0.4 };
    private readonly CheckBox _erase = new() { Content = "Erase", Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _mirror = new() { Content = "Mirror left/right (across the body's centre line)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) };

    private PreviewView? _preview;
    private SkinSettings _settings = new();
    private IReadOnlyList<(string Name, IReadOnlyList<MeshInput> Shapes)> _mesh = Array.Empty<(string, IReadOnlyList<MeshInput>)>();
    private List<MaskCanvas> _canvases = new();
    private readonly HashSet<MaskCanvas> _dirtyCanvases = new();
    private readonly Stack<float[][]> _undo = new();
    private bool _uploadQueued, _unsaved;
    private Region? _loadedRegion;

    /// <summary>Raised after masks are saved, with the paths written, so their textures can be reloaded.</summary>
    public event Action<IReadOnlyList<string>>? MasksSaved;
    /// <summary>Raised when a region is added here.</summary>
    public event Action? RegionsChanged;

    public PaintPanel()
    {
        Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8),
            Text = "Paint a region's mask on the body. Left-drag paints; Alt+drag or middle-drag rotates, right-drag moves. "
                 + "[ and ] change the brush size, Ctrl+Z undoes a stroke. Painted areas show in red while this tab is open.",
        });
        Children.Add(Label("Region"));
        var row = new DockPanel();
        var add = new Button { Content = "New region", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 2, 0, 0) };
        add.Click += (_, _) => NewRegion();
        DockPanel.SetDock(add, Dock.Right);
        row.Children.Add(add);
        row.Children.Add(_region);
        Children.Add(row);
        _region.SelectionChanged += (_, _) => LoadRegion();

        Children.Add(SliderRow("Brush size", _size, v => v.ToString("0.0", CultureInfo.InvariantCulture)));
        Children.Add(SliderRow("Strength", _strength, v => v.ToString("0.00", CultureInfo.InvariantCulture)));
        Children.Add(SliderRow("Hardness", _hardness, v => v.ToString("0.00", CultureInfo.InvariantCulture)));
        _size.ValueChanged += (_, _) => { if (_preview != null) { _preview.BrushRadius = (float)_size.Value; _preview.Refresh(); } };
        _mirror.Checked += (_, _) => SyncBrush();
        _mirror.Unchecked += (_, _) => SyncBrush();
        Children.Add(_erase);
        Children.Add(_mirror);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(Button("Save masks", Save, bold: true));
        buttons.Children.Add(Button("Undo", Undo));
        buttons.Children.Add(Button("Fill", () => { PushUndo(); foreach (var c in _canvases) { c.Fill(1); _dirtyCanvases.Add(c); } Changed(); }));
        buttons.Children.Add(Button("Clear", () => { PushUndo(); foreach (var c in _canvases) { c.Fill(0); _dirtyCanvases.Add(c); } Changed(); }));
        buttons.Children.Add(Button("Revert", LoadRegion));
        Children.Add(buttons);
        Children.Add(_info);
    }

    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(0, 8, 0, 0) };

    private static UIElement SliderRow(string label, Slider slider, Func<double, string> format)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var name = new TextBlock { Text = label, Width = 80, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = format(slider.Value) };
        slider.ValueChanged += (_, e) => value.Text = format(e.NewValue);
        DockPanel.SetDock(name, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        panel.Children.Add(name);
        panel.Children.Add(value);
        panel.Children.Add(slider);
        return panel;
    }

    private static Button Button(string text, Action click, bool bold = false)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4) };
        if (bold) b.FontWeight = FontWeights.SemiBold;
        b.Click += (_, _) => click();
        return b;
    }

    public void Attach(PreviewView preview)
    {
        _preview = preview;
        preview.Paint += OnPaint;
        preview.StrokeEnded += () => _info.Text = _unsaved ? "Unsaved changes — Save masks to use them." : _info.Text;
    }

    public SkinSettings Settings
    {
        get => _settings;
        set { _settings = value; RefreshRegions(); }
    }

    /// <summary>The mesh as drawn, by texture group. Canvases are rebuilt when it changes.</summary>
    public void SetMesh(IReadOnlyList<(string Name, IReadOnlyList<MeshInput> Shapes)> mesh)
    {
        _mesh = mesh;
        _canvases.Clear();
        _undo.Clear();
        if (_preview != null)
            _preview.PickMesh = mesh.SelectMany(g => g.Shapes).Select(m => (m.Shape.Positions, m.Shape.Indices)).ToList();
        if (Active) _ = BuildCanvasesAsync();
    }

    private bool _active;
    /// <summary>On while the paint tab is shown: turns on paint mode in the preview.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            SyncBrush();
            if (value)
            {
                RefreshRegions();
                if (_canvases.Count == 0) _ = BuildCanvasesAsync();
            }
        }
    }

    private void SyncBrush()
    {
        if (_preview == null) return;
        _preview.PaintMode = _active;
        _preview.BrushRadius = (float)_size.Value;
        _preview.BrushMirror = _mirror.IsChecked == true;
        _preview.Refresh();
    }

    public void RefreshRegions()
    {
        var selected = _region.SelectedItem as Region;
        _region.ItemsSource = null;
        _region.ItemsSource = _settings.Regions;
        if (selected != null && _settings.Regions.Contains(selected)) _region.SelectedItem = selected;
        else if (_settings.Regions.Count > 0) _region.SelectedIndex = 0;
    }

    private void NewRegion()
    {
        var name = "Painted region";
        for (var i = 2; _settings.Regions.Any(r => r.Name == name); i++) name = $"Painted region {i}";
        var region = new Region { Name = name, Settings = _settings.CloneWithoutRegions() };
        _settings.Regions.Add(region);
        RefreshRegions();
        _region.SelectedItem = region;
        RegionsChanged?.Invoke();
    }

    private async Task BuildCanvasesAsync()
    {
        if (_mesh.Count == 0) return;
        _info.Text = "Preparing the paint canvases…";
        var mesh = _mesh;
        var canvases = await Task.Run(() => mesh.AsParallel().AsOrdered().Select(g => new MaskCanvas(g.Name, CanvasSize, g.Shapes)).ToList());
        if (!ReferenceEquals(mesh, _mesh)) return; // replaced meanwhile
        _canvases = canvases;
        _loadedRegion = null;
        LoadRegion();
    }

    /// <summary>Loads the selected region's existing masks into the canvases (blank where it has none).</summary>
    private void LoadRegion()
    {
        if (_canvases.Count == 0) return;
        var region = _region.SelectedItem as Region;
        _undo.Clear();
        foreach (var c in _canvases)
        {
            c.Fill(0);
            var path = region?.MaskFor(c.Group);
            if (path != null && File.Exists(path))
            {
                try { c.Load(ImageFiles.Load(path)); }
                catch (Exception ex) { _info.Text = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}"; }
            }
            _dirtyCanvases.Add(c);
        }
        _loadedRegion = region;
        _unsaved = false;
        _info.Text = region == null ? "Add a region (New region) to paint its mask." : $"Painting \"{region.Name}\".";
        Upload();
    }

    private void OnPaint(Vector3 position, Vector3 normal, bool first)
    {
        if (_canvases.Count == 0 || _loadedRegion == null) return;
        if (first) PushUndo();
        var radius = (float)_size.Value;
        var strength = (float)_strength.Value;
        var hardness = (float)_hardness.Value;
        var erase = _erase.IsChecked == true;
        var mirror = _mirror.IsChecked == true;
        foreach (var c in _canvases)
        {
            var changed = c.Dab(position, normal, radius, strength, hardness, erase);
            if (mirror) changed |= c.Dab(new Vector3(-position.X, position.Y, position.Z), new Vector3(-normal.X, normal.Y, normal.Z), radius, strength, hardness, erase);
            if (changed) _dirtyCanvases.Add(c);
        }
        Changed();
    }

    private void Changed()
    {
        _unsaved = true;
        Upload();
    }

    /// <summary>Sends changed canvases to the preview, at most one upload in flight.</summary>
    private void Upload()
    {
        if (_preview == null || _uploadQueued || _dirtyCanvases.Count == 0) return;
        _uploadQueued = true;
        var batch = _dirtyCanvases.Select(c => (c.Group, c.Size, Bytes: c.ToBytes())).ToList();
        _dirtyCanvases.Clear();
        _ = _preview.Run(() =>
        {
            foreach (var (group, size, bytes) in batch) _preview.Renderer.SetPaint(group, size, bytes);
        }).ContinueWith(_ => Dispatcher.BeginInvoke(() => { _uploadQueued = false; Upload(); }));
    }

    private void PushUndo()
    {
        _undo.Push(_canvases.Select(c => (float[])c.Values.Clone()).ToArray());
        while (_undo.Count > 30) { var keep = _undo.Reverse().Skip(1).ToList(); _undo.Clear(); foreach (var u in keep) _undo.Push(u); }
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        var state = _undo.Pop();
        for (var i = 0; i < _canvases.Count && i < state.Length; i++)
        {
            Array.Copy(state[i], _canvases[i].Values, state[i].Length);
            _dirtyCanvases.Add(_canvases[i]);
        }
        Changed();
    }

    /// <summary>Keyboard shortcuts while painting; returns true when handled.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!_active) return false;
        if (e.Key == Key.OemOpenBrackets) { _size.Value = Math.Max(_size.Minimum, _size.Value / 1.15); return true; }
        if (e.Key == Key.OemCloseBrackets) { _size.Value = Math.Min(_size.Maximum, _size.Value * 1.15); return true; }
        if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { Undo(); return true; }
        return false;
    }

    private void Save()
    {
        if (_loadedRegion is not { } region || _canvases.Count == 0) return;
        var folder = Path.Combine(MaskFolder, Safe(region.Name));
        var written = new List<string>();
        foreach (var c in _canvases)
        {
            var path = Path.Combine(folder, c.Group + ".png");
            if (c.IsEmpty)
            {
                // nothing painted for this texture: the region leaves it alone
                if (region.MaskPaths.Remove(c.Group, out var old)) written.Add(old);
                continue;
            }
            c.Save(path);
            if (region.MaskPaths.TryGetValue(c.Group, out var previous) && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
                written.Add(previous);
            region.MaskPaths[c.Group] = path;
            written.Add(path);
        }
        _unsaved = false;
        _info.Text = $"Saved \"{region.Name}\" masks to {folder}.";
        MasksSaved?.Invoke(written);
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
}
