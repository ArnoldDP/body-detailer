using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
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

    private List<ShapeData> _shapes = new();
    private readonly Dictionary<string, int> _imageTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce;
    private bool _generating, _dirty;
    private bool _meshReady;
    private string? _lastNif;

    private sealed class ShapeRow : INotifyPropertyChanged
    {
        public required ShapeData Shape { get; init; }
        public string Name => Shape.Name;
        public string Detail => $"{Shape.VertexCount} vertices · {Path.GetFileName(Shape.DiffusePath ?? "no texture")}"
                                + (Shape.ModelSpaceNormals ? " · model-space normals" : "");
        public bool Selected { get; set; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class State
    {
        public SkinSettings Settings { get; set; } = new();
        public string? ExportFolder { get; set; }
        public string? LastNif { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Regenerate(); };

        var state = LoadState();
        Settings.Settings = state.Settings;
        ExportFolderBox.Text = state.ExportFolder ?? "";
        Settings.Changed += () => _debounce.Start();
        Settings.GeometryChanged += () => _ = RebuildMeshAsync();
        Closing += (_, _) => SaveState();
        if (state.LastNif != null && File.Exists(state.LastNif))
            Loaded += async (_, _) => await OpenNifAsync(state.LastNif);
    }

    // ---------- mesh ----------

    private async void OnOpenNif(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "NIF mesh (*.nif)|*.nif", Title = "Open a body mesh (BodySlide output, e.g. femalebody_1.nif)" };
        if (dlg.ShowDialog(this) == true) await OpenNifAsync(dlg.FileName);
    }

    private async Task OpenNifAsync(string path)
    {
        Status($"Reading {Path.GetFileName(path)}…");
        try
        {
            _shapes = await Task.Run(() => BodyMesh.Load(path));
        }
        catch (Exception ex)
        {
            Status("Couldn't read the mesh: " + ex.Message);
            return;
        }
        // Preselect the shapes that use the main body texture: the largest model-space shape's diffuse.
        var main = _shapes.Where(s => s.ModelSpaceNormals).OrderByDescending(s => s.VertexCount).FirstOrDefault()
                   ?? _shapes.OrderByDescending(s => s.VertexCount).FirstOrDefault();
        ShapeList.ItemsSource = _shapes.Select(s => new ShapeRow
        {
            Shape = s,
            Selected = main != null && string.Equals(s.DiffusePath, main.DiffusePath, StringComparison.OrdinalIgnoreCase),
        }).ToList();
        ExportNameBox.Text = main?.DiffusePath != null ? Path.GetFileNameWithoutExtension(main.DiffusePath) : "femalebody_1";
        _lastNif = path;
        await RebuildMeshAsync();
        Preview.Frame();
    }

    private void OnShapeSelectionChanged(object sender, RoutedEventArgs e) => _ = RebuildMeshAsync();

    private List<ShapeData> SelectedShapes() =>
        (ShapeList.ItemsSource as IEnumerable<ShapeRow>)?.Where(r => r.Selected).Select(r => r.Shape).ToList() ?? new();

    private async Task RebuildMeshAsync()
    {
        var shapes = SelectedShapes();
        if (shapes.Count == 0)
        {
            Status("Pick at least one shape.");
            return;
        }
        var degree = Settings.Settings.Normals.SmoothDegree;
        Status("Rebuilding normals…");
        var frames = await Task.Run(() => SurfaceFrames.Build(shapes, degree));
        await Preview.Run(() => Preview.Renderer.SetMesh(shapes, frames));
        _meshReady = true;
        ExportButton.IsEnabled = true;
        Status($"{shapes.Count} shape(s), {shapes.Sum(s => s.VertexCount)} vertices.");
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

    /// <summary>Every image the current settings use: masks and the detail map.</summary>
    private static IEnumerable<string> ImagePaths(SkinSettings s)
    {
        foreach (var layer in typeof(SkinSettings).GetProperties().Select(p => p.GetValue(s)).OfType<Layer>())
            if (layer.MaskPath != null) yield return layer.MaskPath;
        if (s.Normals.DetailMapPath != null) yield return s.Normals.DetailMapPath;
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

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var folder = ExportFolderBox.Text.Trim();
        var name = ExportNameBox.Text.Trim();
        if (folder.Length == 0 || name.Length == 0)
        {
            Status("Choose an export folder and file name first.");
            return;
        }
        ExportButton.IsEnabled = false;
        try
        {
            var snapshot = Settings.Settings.Clone();
            await EnsureImagesAsync(snapshot);
            var images = new Dictionary<string, int>(_imageTextures, StringComparer.OrdinalIgnoreCase);
            var size = ExportSize;
            var preview = PreviewSize;
            Status($"Generating at {size}×{size}…");
            var textures = await Preview.Run(() =>
            {
                Preview.Renderer.Generate(snapshot, size, images);
                var t = Preview.Renderer.Read();
                Preview.Renderer.Generate(snapshot, preview, images); // back to the preview size
                return t;
            });
            Status("Compressing to BC7… (this takes a while at large sizes)");
            var rfaos = RfaosBox.IsChecked == true;
            await Task.Run(() =>
            {
                var jobs = new List<Action>
                {
                    () => DdsExporter.WriteBc7(Path.Combine(folder, name + ".dds"), textures.Albedo, size, size),
                    () => DdsExporter.WriteBc7(Path.Combine(folder, name + "_msn.dds"), textures.Normal, size, size),
                    () => DdsExporter.WriteBc7(Path.Combine(folder, name + "_s.dds"), textures.Specular, size, size),
                    () => DdsExporter.WriteBc7(Path.Combine(folder, name + "_sk.dds"), textures.Subsurface, size, size),
                };
                if (rfaos) jobs.Add(() => DdsExporter.WriteBc7(Path.Combine(folder, name + "_rfaos.dds"), textures.Rfaos, size, size));
                Parallel.Invoke(jobs.ToArray());
            });
            SaveState();
            Status($"Exported {name}.dds, {name}_msn.dds, {name}_s.dds, {name}_sk.dds{(rfaos ? $" and {name}_rfaos.dds" : "")} to {folder}.");
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

    /// <summary>Command-line export: open, generate and export without user input. Writes a log next to the textures.</summary>
    public async Task ExportHeadless(string nif, string folder)
    {
        await OpenNifAsync(nif);
        ExportFolderBox.Text = folder;
        OnExport(this, new RoutedEventArgs());
        while (!ExportButton.IsEnabled) await Task.Delay(200);
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
        var smoothingChanged = Math.Abs(settings.Normals.SmoothDegree - Settings.Settings.Normals.SmoothDegree) > 0.01f;
        Settings.Settings = settings;
        if (smoothingChanged) _ = RebuildMeshAsync();
        else Regenerate();
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
                LastNif = _lastNif,
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
