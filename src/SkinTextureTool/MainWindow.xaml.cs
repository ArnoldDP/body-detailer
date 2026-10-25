using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SkinTextureTool.Core.BodySlide;
using SkinTextureTool.Core.Export;
using SkinTextureTool.Core.Generation;
using SkinTextureTool.Core.Mesh;
using SkinTextureTool.Imaging;
using SkinTextureTool.Rendering;

namespace SkinTextureTool;

public partial class MainWindow : Window
{
    private static readonly string StateFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkinTextureTool");
    private static readonly string StateFile = Path.Combine(StateFolder, "last.json");

    /// <summary>Loaded NIFs in load order, each with its shapes.</summary>
    private readonly List<(string Path, List<ShapeData> Shapes)> _nifs = new();
    private readonly HashSet<ShapeData> _unselected = new();
    private readonly Dictionary<string, int> _imageTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Morpher _morpher = new();
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _geometryDebounce;
    private bool _generating, _dirty;
    private bool _meshReady;
    private IReadOnlyList<string> _groups = Array.Empty<string>();

    private sealed class ShapeRow : INotifyPropertyChanged
    {
        public required ShapeData Shape { get; init; }
        public string Name => Shape.Name;
        public string Detail => $"{Path.GetFileName(Shape.NifPath)} · {Shape.VertexCount} vertices · texture {Shape.TextureName}"
                                + (Shape.ModelSpaceNormals ? " · model-space normals" : "");
        public bool Selected { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class State
    {
        public SkinSettings Settings { get; set; } = new();
        public string? ExportFolder { get; set; }
        public List<string> Nifs { get; set; } = new();
        public Dictionary<string, SliderSetChoice> SliderSets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public MainWindow()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Regenerate(); };
        _geometryDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _geometryDebounce.Tick += (_, _) => { _geometryDebounce.Stop(); _ = RebuildMeshAsync(); };

        var state = LoadState();
        Settings.Settings = state.Settings;
        Sliders.Settings = state.Settings;
        Regions.Settings = state.Settings;
        Regions.Changed += () => _debounce.Start();
        Sliders.Choices = new Dictionary<string, SliderSetChoice>(state.SliderSets, StringComparer.OrdinalIgnoreCase);
        ExportFolderBox.Text = state.ExportFolder ?? "";
        Settings.Changed += () => _debounce.Start();
        Settings.GeometryChanged += () => _geometryDebounce.Start();
        Sliders.Changed += () => _geometryDebounce.Start();
        Closing += (_, _) => SaveState();
        var saved = state.Nifs.Where(File.Exists).ToList();
        if (saved.Count > 0) Loaded += async (_, _) => await AddNifsAsync(saved);
    }

    // ---------- meshes ----------

    private async void OnAddNifs(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "NIF mesh (*.nif)|*.nif", Multiselect = true,
            Title = "Add meshes (BodySlide outputs: femalebody_1.nif, femalehands_1.nif, femalefeet_1.nif, a head…)",
        };
        if (dlg.ShowDialog(this) == true) await AddNifsAsync(dlg.FileNames);
    }

    private async void OnClearNifs(object sender, RoutedEventArgs e)
    {
        _nifs.Clear();
        _unselected.Clear();
        await RefreshShapesAsync();
    }

    private async Task AddNifsAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_nifs.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            Status($"Reading {Path.GetFileName(path)}…");
            try
            {
                var shapes = await Task.Run(() => BodyMesh.Load(path));
                // Preselect shapes using model-space normals (skin); others (eyes, brows, hair) start unticked.
                foreach (var s in shapes.Where(s => !s.ModelSpaceNormals)) _unselected.Add(s);
                _nifs.Add((path, shapes));
            }
            catch (Exception ex)
            {
                Status($"Couldn't read {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        await RefreshShapesAsync();
        Preview.Frame();
    }

    private async Task RefreshShapesAsync()
    {
        ShapeList.ItemsSource = _nifs.SelectMany(n => n.Shapes).Select(s => new ShapeRow { Shape = s, Selected = !_unselected.Contains(s) }).ToList();
        Sliders.SetNifs(_nifs.Select(n => (n.Path, (IReadOnlyList<ShapeData>)n.Shapes)).ToList());
        await RebuildMeshAsync();
    }

    private void OnShapeSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: ShapeRow row })
        {
            if (row.Selected) _unselected.Remove(row.Shape); else _unselected.Add(row.Shape);
        }
        _geometryDebounce.Start();
    }

    private List<ShapeData> SelectedShapes() =>
        _nifs.SelectMany(n => n.Shapes).Where(s => !_unselected.Contains(s)).ToList();

    /// <summary>
    /// Rebuilds normals for every selected shape together (so seams between NIFs weld), with the BodySlide
    /// slider amounts applied to a copy of each shape's positions, then regroups by texture and uploads.
    /// </summary>
    private async Task RebuildMeshAsync()
    {
        var shapes = SelectedShapes();
        if (shapes.Count == 0)
        {
            _meshReady = false;
            ExportButton.IsEnabled = false;
            _groups = Array.Empty<string>();
            Settings.Groups = _groups;
            Regions.Groups = _groups;
            await Preview.Run(() => Preview.Renderer.SetMesh(Array.Empty<(string, IReadOnlyList<MeshInput>)>()));
            Status(_nifs.Count == 0 ? "Add BodySlide-built NIFs (body, hands, feet, head) to begin." : "Pick at least one shape.");
            return;
        }
        var degree = Settings.Settings.Normals.SmoothDegree;
        var sets = _nifs.ToDictionary(n => n.Path, n => Sliders.SetFor(n.Path), StringComparer.OrdinalIgnoreCase);
        var sliderValues = Settings.Settings.NormalSliders;
        Status("Rebuilding normals…");
        var problems = new List<string>();
        var frames = await Task.Run(() =>
        {
            var morphed = shapes.Select(s =>
                sets.TryGetValue(s.NifPath, out var set) && set != null && sliderValues.TryGetValue(set.Name, out var values) && values.Count > 0
                    ? _morpher.Apply(set, s.Name, s.Positions, values, problems)
                    : s.Positions).ToList();
            return SurfaceFrames.Build(shapes, degree, positions: morphed);
        });

        var groups = shapes.Select((s, i) => new MeshInput(s, frames[i]))
            .GroupBy(m => m.Shape.TextureName)
            .Select(g => (g.Key, (IReadOnlyList<MeshInput>)g.ToList()))
            .ToList();
        await Preview.Run(() => Preview.Renderer.SetMesh(groups));
        var names = groups.Select(g => g.Key).ToList();
        if (!names.SequenceEqual(_groups))
        {
            _groups = names;
            Settings.Groups = names;
            Regions.Groups = names;
        }
        _meshReady = true;
        ExportButton.IsEnabled = true;
        ExportInfo.Text = $"Writes {string.Join(", ", names.Select(n => n + ".dds"))} with their _msn, _s and _sk maps (BC7 with mipmaps).";
        Status($"{shapes.Count} shape(s) in {names.Count} texture group(s): {string.Join(", ", names)}."
               + (problems.Count > 0 ? " Slider problems: " + string.Join("; ", problems.Distinct().Take(3)) : ""));
        Regenerate();
    }

    // ---------- generation ----------

    private int PreviewSize => int.Parse(((ComboBoxItem)PreviewSizeBox.SelectedItem).Content.ToString()!);
    private int ExportSize => int.Parse(((ComboBoxItem)ExportSizeBox.SelectedItem).Content.ToString()!);

    private void OnPreviewSizeChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Regenerate(); }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Preview.Mode = ModeBox.SelectedIndex;
        Preview.Refresh();
    }

    /// <summary>Every image the current settings use: masks and detail maps, for all groups.</summary>
    private static IEnumerable<string> ImagePaths(SkinSettings s)
    {
        foreach (var layer in typeof(SkinSettings).GetProperties().Select(p => p.GetValue(s)).OfType<Layer>())
            foreach (var path in layer.MaskPaths.Values) yield return path;
        foreach (var path in s.Normals.DetailMaps.Values) yield return path;
        foreach (var region in s.Regions)
        {
            foreach (var path in region.MaskPaths.Values) yield return path;
            foreach (var path in ImagePaths(region.Settings)) yield return path;
        }
    }

    /// <summary>Loads any images not on the GPU yet; images that fail to load are skipped with a message.</summary>
    private async Task EnsureImagesAsync(SkinSettings s)
    {
        foreach (var path in ImagePaths(s).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_imageTextures.ContainsKey(path)) continue;
            try
            {
                var image = await Task.Run(() => ImageFiles.Load(path));
                _imageTextures[path] = await Preview.Run(() => Preview.Renderer.ImageTexture(path, image));
            }
            catch (Exception ex)
            {
                _imageTextures[path] = 0;
                Status($"Couldn't read {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private async void Regenerate()
    {
        if (!_meshReady) return;
        if (_generating) { _dirty = true; return; }
        _generating = true;
        try
        {
            do
            {
                _dirty = false;
                var snapshot = Settings.Settings.Clone();
                await EnsureImagesAsync(snapshot);
                var size = PreviewSize;
                var images = new Dictionary<string, int>(_imageTextures, StringComparer.OrdinalIgnoreCase);
                await Preview.Run(() => Preview.Renderer.Generate(snapshot, size, images));
            } while (_dirty);
        }
        catch (Exception ex)
        {
            Status("Generation failed: " + ex.Message);
        }
        finally
        {
            _generating = false;
        }
    }

    // ---------- export ----------

    private void OnBrowseExport(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder for the exported textures" };
        if (dlg.ShowDialog(this) == true) ExportFolderBox.Text = dlg.FolderName;
    }

    private async void OnExport(object sender, RoutedEventArgs e) => await ExportAsync();

    private async Task ExportAsync()
    {
        var folder = ExportFolderBox.Text.Trim();
        if (folder.Length == 0)
        {
            Status("Choose an export folder first.");
            return;
        }
        ExportButton.IsEnabled = false;
        try
        {
            var snapshot = Settings.Settings.Clone();
            await EnsureImagesAsync(snapshot);
            var images = new Dictionary<string, int>(_imageTextures, StringComparer.OrdinalIgnoreCase);
            var size = ExportSize;
            var rfaos = RfaosBox.IsChecked == true;
            var written = new List<string>();
            foreach (var group in _groups)
            {
                Status($"Generating {group} at {size}×{size}…");
                var textures = await Preview.Run(() =>
                {
                    Preview.Renderer.Generate(snapshot, size, images, group);
                    return Preview.Renderer.Read(group);
                });
                Status($"Compressing {group} to BC7… (this takes a while at large sizes)");
                await Task.Run(() =>
                {
                    var jobs = new List<Action>
                    {
                        () => DdsExporter.WriteBc7(Path.Combine(folder, group + ".dds"), textures.Albedo, size, size),
                        () => DdsExporter.WriteBc7(Path.Combine(folder, group + "_msn.dds"), textures.Normal, size, size),
                        () => DdsExporter.WriteBc7(Path.Combine(folder, group + "_s.dds"), textures.Specular, size, size),
                        () => DdsExporter.WriteBc7(Path.Combine(folder, group + "_sk.dds"), textures.Subsurface, size, size),
                    };
                    if (rfaos) jobs.Add(() => DdsExporter.WriteBc7(Path.Combine(folder, group + "_rfaos.dds"), textures.Rfaos, size, size));
                    Parallel.Invoke(jobs.ToArray());
                });
                written.Add(group);
            }
            var preview = PreviewSize;
            await Preview.Run(() => Preview.Renderer.Generate(snapshot, preview, images)); // back to the preview size
            SaveState();
            Status($"Exported {string.Join(", ", written)} (diffuse, _msn, _s, _sk{(rfaos ? ", _rfaos" : "")}) to {folder}.");
        }
        catch (Exception ex)
        {
            Status("Export failed: " + ex.Message);
        }
        finally
        {
            ExportButton.IsEnabled = _meshReady;
        }
    }

    /// <summary>Command-line export: add the NIFs, generate with the saved settings, export, and log the result.</summary>
    public async Task ExportHeadless(string folder, IReadOnlyList<string> nifs)
    {
        await AddNifsAsync(nifs);
        ExportFolderBox.Text = folder;
        await ExportAsync();
        File.WriteAllText(Path.Combine(folder, "export.log"), StatusText.Text);
    }

    // ---------- presets and state ----------

    private void OnLoadPreset(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Skin preset (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ApplySettings(SkinSettings.LoadPreset(dlg.FileName));
            Status("Loaded preset " + Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            Status("Couldn't read that preset: " + ex.Message);
        }
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Skin preset (*.json)|*.json", FileName = "skin-preset.json" };
        if (dlg.ShowDialog(this) == true) Settings.Settings.SavePreset(dlg.FileName);
    }

    private void OnResetSettings(object sender, RoutedEventArgs e) => ApplySettings(new SkinSettings());

    private void ApplySettings(SkinSettings settings)
    {
        Settings.Settings = settings;
        Sliders.Settings = settings;
        Regions.Settings = settings;
        Sliders.SetNifs(_nifs.Select(n => (n.Path, (IReadOnlyList<ShapeData>)n.Shapes)).ToList());
        _ = RebuildMeshAsync(); // smoothing and sliders may have changed
    }

    private static State LoadState()
    {
        try
        {
            if (File.Exists(StateFile)) return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) ?? new State();
        }
        catch
        {
            // a broken state file just means starting fresh
        }
        return new State();
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(StateFolder);
            var state = new State
            {
                Settings = Settings.Settings,
                ExportFolder = string.IsNullOrWhiteSpace(ExportFolderBox.Text) ? null : ExportFolderBox.Text.Trim(),
                Nifs = _nifs.Select(n => n.Path).ToList(),
                SliderSets = Sliders.Choices,
            };
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // state is a convenience
        }
    }

    private void Status(string text) => StatusText.Text = text;
}
