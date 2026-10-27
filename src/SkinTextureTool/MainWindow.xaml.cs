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
using SkinTextureTool.Painting;
using SkinTextureTool.Rendering;

namespace SkinTextureTool;

public partial class MainWindow : Window
{
    // Portable: state lives next to the exe. A state file from older builds (in AppData) is read once if there's none yet.
    private static readonly string StateFolder = AppContext.BaseDirectory;
    private static readonly string StateFile = Path.Combine(StateFolder, "last.json");
    private static readonly string OldStateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkinTextureTool", "last.json");

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
    /// <summary>Per texture group, a shape that uses it (for its NIF's texture paths).</summary>
    private Dictionary<string, ShapeData> _groupShapes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The mesh as loaded, by texture group (for the vein network), and the key it was last built for.</summary>
    private IReadOnlyList<(string Name, IReadOnlyList<MeshInput> Shapes)> _meshGroups = Array.Empty<(string, IReadOnlyList<MeshInput>)>();
    private string? _veinKey;
    private SkinTextureTool.Core.Patcher.VirtualData? _gameData;
    private string? _gameDataFolder;

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
        public string? Mo2Folder { get; set; }
        public double NeckBand { get; set; } = 4;
        public double LimbBand { get; set; } = 3;
        public bool SkipBeasts { get; set; } = true;
        public bool ZipOutput { get; set; }
        public bool AutoUpdate { get; set; } = true;
    }

    public MainWindow()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (AutoUpdateBox.IsChecked == true) Regenerate(); else MarkStale(true); };
        _geometryDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _geometryDebounce.Tick += (_, _) => { _geometryDebounce.Stop(); _ = RebuildMeshAsync(); };

        var state = LoadState();
        Settings.Settings = state.Settings;
        Sliders.Settings = state.Settings;
        Regions.Settings = state.Settings;
        Regions.Changed += () => _debounce.Start();
        NeckFix.Changed += () => _geometryDebounce.Start();
        Painter.Attach(Preview);
        Painter.Settings = state.Settings;
        Painter.MasksSaved += OnMasksSaved;
        Painter.RegionsChanged += () => { Regions.Settings = Settings.Settings; _debounce.Start(); };
        PreviewKeyDown += (_, e) => { if (!(e.OriginalSource is TextBox) && Painter.HandleKey(e)) e.Handled = true; };
        Sliders.Choices = new Dictionary<string, SliderSetChoice>(state.SliderSets, StringComparer.OrdinalIgnoreCase);
        ExportFolderBox.Text = state.ExportFolder ?? "";
        if (string.IsNullOrEmpty(Patcher.Mo2Folder) && state.Mo2Folder != null) Patcher.Mo2Folder = state.Mo2Folder;
        Patcher.Bands = (state.NeckBand, state.LimbBand);
        Patcher.Options = (state.SkipBeasts, state.ZipOutput);
        AutoUpdateBox.IsChecked = state.AutoUpdate;
        Settings.Changed += () => _debounce.Start();
        Settings.GeometryChanged += () => _geometryDebounce.Start();
        Sliders.Changed += () => _geometryDebounce.Start();
        Closing += (_, _) => SaveState();
        var saved = state.Nifs.Where(File.Exists).ToList();
        // a command-line export brings its own meshes, so the saved ones aren't reloaded
        if (saved.Count > 0) Loaded += async (_, _) => { if (!Headless) await AddNifsAsync(saved); };
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

    /// <summary>NIFs, folders of NIFs, or a preset dropped anywhere on the window.</summary>
    private static List<string> DroppedNifs(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is not string[] items ? new()
            : items.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.nif") : new[] { p })
                .Where(p => p.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)).ToList();

    private static string? DroppedPreset(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] items ? items.FirstOrDefault(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedNifs(e).Count > 0 || DroppedPreset(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var nifs = DroppedNifs(e);
        if (nifs.Count > 0) await AddNifsAsync(nifs);
        if (DroppedPreset(e) is { } preset)
        {
            try { ApplySettings(SkinSettings.LoadPreset(preset)); Status("Loaded preset " + Path.GetFileName(preset)); }
            catch (Exception ex) { Status("Couldn't read that preset: " + ex.Message); }
        }
    }

    private async void OnAddFemaleBody(object sender, RoutedEventArgs e) => await AddBodyAsync("female");
    private async void OnAddMaleBody(object sender, RoutedEventArgs e) => await AddBodyAsync("male");

    /// <summary>
    /// Adds the body, hands and feet the MO2 load order actually uses (the winning copies: overwrite, then the
    /// highest-priority mod, then archives). Meshes found only in an archive are extracted next to the tool.
    /// </summary>
    private async Task AddBodyAsync(string sex)
    {
        var mo2Folder = Patcher.Mo2Folder;
        if (!File.Exists(Path.Combine(mo2Folder, "ModOrganizer.ini")))
        {
            var dlg = new OpenFolderDialog { Title = "Your Mod Organizer 2 folder (the one with ModOrganizer.ini)" };
            if (dlg.ShowDialog(this) != true) return;
            mo2Folder = Patcher.Mo2Folder = dlg.FolderName;
            if (!File.Exists(Path.Combine(mo2Folder, "ModOrganizer.ini"))) { Status("That folder has no ModOrganizer.ini."); return; }
        }
        Status($"Finding the {sex} body meshes your load order uses…");
        try
        {
            var extract = Path.Combine(AppContext.BaseDirectory, "Extracted meshes");
            var (found, missing) = await Task.Run(() =>
            {
                var mo2 = new SkinTextureTool.Core.Patcher.Mo2Instance(mo2Folder);
                using var data = new SkinTextureTool.Core.Patcher.VirtualData(mo2, extract);
                var paths = new List<string>(); var lost = new List<string>();
                foreach (var part in new[] { "body", "hands", "feet" })
                {
                    var rel = $"meshes/actors/character/character assets/{sex}{part}_1.nif";
                    var file = data.Materialize(rel);
                    if (file != null) paths.Add(file); else lost.Add($"{sex}{part}_1.nif");
                }
                return (paths, lost);
            });
            await AddNifsAsync(found);
            Status($"Added {string.Join(", ", found.Select(Path.GetFileName))} from your load order."
                   + (missing.Count > 0 ? $" Not found: {string.Join(", ", missing)}." : "") + " Add your head mesh with Add NIFs….");
        }
        catch (Exception ex)
        {
            Status("Couldn't read the MO2 setup: " + ex.Message);
        }
    }

    private SkinTextureTool.Core.Patcher.GameSession? _session;
    private string? _sessionFolder;
    private List<SkinTextureTool.Core.Patcher.NpcAssets.Entry>? _npcs;

    /// <summary>
    /// Loads one NPC as the game draws it: its FaceGen head, and the body, hands and feet of the skin it wears, with
    /// that skin's own texture set where it swaps one in. Starts from its current textures, and turns on export at
    /// game paths so the result overrides exactly the textures it uses.
    /// </summary>
    private async void OnLoadNpc(object sender, RoutedEventArgs e)
    {
        var mo2Folder = Patcher.Mo2Folder;
        if (!File.Exists(Path.Combine(mo2Folder, "ModOrganizer.ini")))
        {
            var dlg = new OpenFolderDialog { Title = "Your Mod Organizer 2 folder (the one with ModOrganizer.ini)" };
            if (dlg.ShowDialog(this) != true) return;
            mo2Folder = Patcher.Mo2Folder = dlg.FolderName;
            if (!File.Exists(Path.Combine(mo2Folder, "ModOrganizer.ini"))) { Status("That folder has no ModOrganizer.ini."); return; }
        }
        try
        {
            if (_session == null || _sessionFolder != mo2Folder)
            {
                Status("Reading the load order's NPCs…");
                var extract = Path.Combine(AppContext.BaseDirectory, "Extracted meshes");
                _session?.Dispose();
                _session = await Task.Run(() => new SkinTextureTool.Core.Patcher.GameSession(mo2Folder, extract));
                _sessionFolder = mo2Folder;
                _npcs = await Task.Run(() => SkinTextureTool.Core.Patcher.NpcAssets.List(_session));
            }
            Status($"{_npcs!.Count} NPCs in the load order.");
            var picker = new NpcPickerWindow(_npcs) { Owner = this };
            if (picker.ShowDialog() != true || picker.Picked is not { } npc) return;

            Status($"Loading {npc.Name}…");
            var session = _session;
            var (loaded, notes) = await Task.Run(() =>
            {
                var pieces = SkinTextureTool.Core.Patcher.NpcAssets.Pieces(session, npc, out var n);
                var shapesByFile = new List<(string Path, List<ShapeData> Shapes)>();
                foreach (var piece in pieces)
                {
                    var file = session.Data.Materialize(piece.MeshPath);
                    if (file == null) { n.Add($"{piece.MeshPath} not found."); continue; }
                    var shapes = BodyMesh.Load(file);
                    if (piece.TextureOverride is { } tex)
                        shapes = shapes.Select(s => !s.ModelSpaceNormals ? s : new ShapeData
                        {
                            Name = s.Name, NifPath = s.NifPath, Positions = s.Positions, Uvs = s.Uvs, Indices = s.Indices,
                            DiffusePath = tex[0] ?? s.DiffusePath, ModelSpaceNormals = s.ModelSpaceNormals,
                            TexturePaths = Enumerable.Range(0, Math.Max(tex.Length, s.TexturePaths.Length))
                                .Select(i => (i < tex.Length ? tex[i] : null) ?? (i < s.TexturePaths.Length ? s.TexturePaths[i] : null)).ToArray(),
                        }).ToList();
                    shapesByFile.Add((file, shapes));
                }
                return (shapesByFile, n);
            });
            _nifs.Clear();
            _unselected.Clear();
            foreach (var (file, shapes) in loaded)
            {
                foreach (var s in shapes.Where(s => !s.ModelSpaceNormals)) _unselected.Add(s);
                _nifs.Add((file, shapes));
            }
            Settings.Settings.StartFromExisting.Enabled = true;
            Settings.Settings = Settings.Settings; // refresh the panel
            GamePathsBox.IsChecked = true;
            await RefreshShapesAsync();
            Preview.Frame();
            Status($"Loaded {npc.Name}: {string.Join(", ", loaded.Select(l => Path.GetFileName(l.Path)))}. {string.Join(" ", notes)} "
                   + "Starting from its current textures; export writes them at their game paths.");
        }
        catch (Exception ex)
        {
            Status("Couldn't load that NPC: " + ex.Message);
        }
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
                // added meanwhile (two loads of the same file racing)
                if (_nifs.Any(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
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
        NeckFix.SetShapes(shapes);
        shapes = shapes.Select(NeckFix.Apply).ToList();
        if (shapes.Count == 0)
        {
            _meshReady = false;
            ExportButton.IsEnabled = false;
            _groups = Array.Empty<string>();
            Settings.Groups = _groups;
            Regions.Groups = _groups;
            await Preview.Run(() => Preview.Renderer.SetMesh(Array.Empty<(string, IReadOnlyList<MeshInput>)>()));
            Painter.SetMesh(Array.Empty<(string, IReadOnlyList<MeshInput>)>());
            Status(_nifs.Count == 0 ? "Add BodySlide-built NIFs (body, hands, feet, head) to begin." : "Pick at least one shape.");
            return;
        }
        var degree = Settings.Settings.Normals.SmoothDegree;
        var seamRings = Settings.Settings.Normals.SeamBlend;
        var conform = Settings.Settings.Normals.SeamConform;
        var subdivision = Settings.Settings.Normals.Subdivision;
        var sets = _nifs.GroupBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Sliders.SetFor(g.Key), StringComparer.OrdinalIgnoreCase);
        var sliderValues = Settings.Settings.NormalSliders;
        Status("Rebuilding normals…");
        var problems = new List<string>();
        var frames = await Task.Run(() =>
        {
            var morphed = shapes.Select(s =>
                sets.TryGetValue(s.NifPath, out var set) && set != null && sliderValues.TryGetValue(set.Name, out var values) && values.Count > 0
                    ? _morpher.Apply(set, s.Name, s.Positions, values, problems)
                    : s.Positions).ToList();
            return SurfaceFrames.Build(shapes, degree, positions: morphed, seamRings: seamRings, conformDistance: conform);
        });

        var groups = shapes.Select((s, i) => new MeshInput(s, frames[i]))
            .GroupBy(m => m.Shape.TextureName)
            .Select(g => (g.Key, (IReadOnlyList<MeshInput>)g.ToList()))
            .ToList();
        // the renderer bakes from a subdivided copy; painting and picking use the mesh as loaded
        var baked = await Task.Run(() => groups.Select(g => (g.Key, (IReadOnlyList<MeshInput>)g.Item2
            .Select(m => { var (s, f) = Subdivision.Apply(m.Shape, m.Frames, subdivision); return new MeshInput(s, f); }).ToList())).ToList());
        await Preview.Run(() => Preview.Renderer.SetMesh(baked));
        Painter.SetMesh(groups);
        var names = groups.Select(g => g.Key).ToList();
        _groupShapes = groups.ToDictionary(g => g.Key, g => g.Item2[0].Shape, StringComparer.OrdinalIgnoreCase);
        _meshGroups = groups.Select(g => (g.Key, g.Item2)).ToList();
        _veinKey = null; // the mesh changed, so the vein network is laid out again
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

    /// <summary>Shows (or clears) that the preview is behind the settings while auto-update is off.</summary>
    private void MarkStale(bool stale)
    {
        RegenerateButton.FontWeight = stale ? FontWeights.Bold : FontWeights.Normal;
        RegenerateButton.Content = stale ? "Regenerate •" : "Regenerate";
    }

    private void OnRegenerate(object sender, RoutedEventArgs e) => Regenerate();

    private void OnAutoUpdateChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && AutoUpdateBox.IsChecked == true && RegenerateButton.FontWeight == FontWeights.Bold) Regenerate();
    }

    private async void Regenerate()
    {
        if (!_meshReady) return;
        MarkStale(false);
        if (_generating) { _dirty = true; return; }
        _generating = true;
        try
        {
            do
            {
                _dirty = false;
                var snapshot = Settings.Settings.Clone();
                await EnsureImagesAsync(snapshot);
                await EnsureExistingTexturesAsync(snapshot);
                await EnsureVeinNetworkAsync(snapshot);
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

    // ---------- painting ----------

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != Tabs) return;
        Painter.Active = Tabs.SelectedItem == PaintTab;
    }

    /// <summary>Painted masks were written: drop the old copies from the GPU so the next generation reads them fresh.</summary>
    private async void OnMasksSaved(IReadOnlyList<string> paths)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _imageTextures.Remove(path);
            await Preview.Run(() => Preview.Renderer.ForgetImage(path));
        }
        Regions.Settings = Settings.Settings;
        SaveState();
        Regenerate();
    }

    // ---------- anatomical veins ----------

    /// <summary>Lays out and paints the anatomical vein network when it's on and its routes or the mesh changed.</summary>
    private async Task EnsureVeinNetworkAsync(SkinSettings s)
    {
        var v = s.AnatomicalVeins;
        if (!v.Enabled || _meshGroups.Count == 0) return;
        const int size = 2048;
        var key = $"{s.Seed}|{v.RouteKey}";
        if (key == _veinKey) return;
        Status("Laying out the vein network…");
        var groups = _meshGroups; var seed = s.Seed;
        var masks = await Task.Run(() => VeinNetwork.Build(groups, v, seed, size));
        foreach (var (group, bytes) in masks)
            await Preview.Run(() => Preview.Renderer.SetVeinNetwork(group, size, bytes));
        _veinKey = key;
        Status("Vein network ready.");
    }

    // ---------- starting from the meshes' current textures ----------

    /// <summary>
    /// When "start from existing" is on, finds each group's current diffuse, normal, subsurface and specular
    /// textures (texture slots 0, 1, 2 and 7 of its NIF) and hands them to the renderer. They're resolved through
    /// the MO2 load order when the tool knows it (the winning copies, extracted from archives if need be), else
    /// in the Data folder the mesh sits in.
    /// </summary>
    private async Task EnsureExistingTexturesAsync(SkinSettings s)
    {
        if (!s.StartFromExisting.Enabled) return;
        var found = new List<string>(); var missing = new List<string>();
        foreach (var (group, shape) in _groupShapes)
        {
            var ids = new int[4];
            var slots = new[] { 0, 1, 2, 7 };
            for (var k = 0; k < slots.Length; k++)
            {
                var rel = slots[k] < shape.TexturePaths.Length ? shape.TexturePaths[slots[k]] : null;
                if (rel == null) continue;
                var file = await Task.Run(() => ResolveTexture(rel, shape.NifPath));
                if (file == null) { missing.Add(Path.GetFileName(rel)); continue; }
                if (!_imageTextures.TryGetValue(file, out var id))
                {
                    try
                    {
                        var image = await Task.Run(() => ImageFiles.Load(file));
                        id = await Preview.Run(() => Preview.Renderer.ImageTexture(file, image));
                    }
                    catch { id = 0; missing.Add(Path.GetFileName(rel) + " (unreadable)"); }
                    _imageTextures[file] = id;
                }
                ids[k] = id;
                if (id != 0) found.Add(Path.GetFileName(rel));
            }
            await Preview.Run(() => Preview.Renderer.SetExisting(group, ids[0], ids[1], ids[3], ids[2]));
        }
        if (missing.Count > 0) Status($"Starting from existing textures; couldn't find {string.Join(", ", missing.Distinct().Take(6))}.");
    }

    private string? ResolveTexture(string texture, string nifPath)
    {
        var rel = texture.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (!rel.StartsWith("textures" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) rel = Path.Combine("textures", rel);
        var mo2Folder = Dispatcher.Invoke(() => Patcher.Mo2Folder);
        if (File.Exists(Path.Combine(mo2Folder, "ModOrganizer.ini")))
        {
            if (_gameData == null || _gameDataFolder != mo2Folder)
            {
                _gameData = new SkinTextureTool.Core.Patcher.VirtualData(new SkinTextureTool.Core.Patcher.Mo2Instance(mo2Folder),
                    Path.Combine(AppContext.BaseDirectory, "Extracted textures"));
                _gameDataFolder = mo2Folder;
            }
            var file = _gameData.Materialize(rel);
            if (file != null) return file;
        }
        // no MO2 setup (or not in it): the Data folder the mesh sits in
        var parts = Path.GetFullPath(nifPath).Split(Path.DirectorySeparatorChar);
        var i = Array.FindLastIndex(parts, p => p.Equals("meshes", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        var local = Path.Combine(string.Join(Path.DirectorySeparatorChar, parts[..i]), rel);
        return File.Exists(local) ? local : null;
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
            // the textures to start from and the vein network have to be ready before export generates
            await EnsureExistingTexturesAsync(snapshot);
            await EnsureVeinNetworkAsync(snapshot);
            var images = new Dictionary<string, int>(_imageTextures, StringComparer.OrdinalIgnoreCase);
            var size = ExportSize;
            var rfaos = RfaosBox.IsChecked == true;
            var gamePaths = GamePathsBox.IsChecked == true;
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
                // where each texture goes: next to each other by group name, or at the exact game paths the meshes use
                var shapeTextures = _groupShapes.TryGetValue(group, out var gs) ? gs.TexturePaths : Array.Empty<string?>();
                string Out(int slot, string suffix)
                {
                    if (!gamePaths) return Path.Combine(folder, group + suffix);
                    var own = slot >= 0 && slot < shapeTextures.Length ? shapeTextures[slot] : null;
                    if (own != null) return Path.Combine(folder, GameTexturePath(own));
                    var diffuse = shapeTextures.Length > 0 && shapeTextures[0] != null ? GameTexturePath(shapeTextures[0]!) : Path.Combine("textures", group + ".dds");
                    return Path.Combine(folder, Path.GetDirectoryName(diffuse)!, Path.GetFileNameWithoutExtension(diffuse) + suffix);
                }
                await Task.Run(() =>
                {
                    var jobs = new List<Action>
                    {
                        () => DdsExporter.WriteBc7(Out(0, ".dds"), textures.Albedo, size, size),
                        () => DdsExporter.WriteBc7(Out(1, "_msn.dds"), textures.Normal, size, size),
                        () => DdsExporter.WriteBc7(Out(7, "_s.dds"), textures.Specular, size, size),
                        () => DdsExporter.WriteBc7(Out(2, "_sk.dds"), textures.Subsurface, size, size),
                    };
                    if (rfaos) jobs.Add(() => DdsExporter.WriteBc7(Out(-1, "_rfaos.dds"), textures.Rfaos, size, size));
                    // Bottled Shaders fur shells: matched by name next to the diffuse
                    if (textures.Shell != null && (snapshot.Fur.Enabled ? snapshot.Fur.ExportShells : snapshot.BodyHair.ExportShells))
                    {
                        var shell = FillUncovered(textures.Shell, textures.Albedo, size);
                        jobs.Add(() => DdsExporter.WriteBc7(Out(-1, "_shell.dds"), shell, size, size));
                        jobs.Add(() => File.WriteAllText(Out(-1, "_shell.json"), snapshot.Fur.Enabled ? ShellJson(snapshot.Fur) : ShellJson(snapshot.BodyHair)));
                    }
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

    /// <summary>A texture path as the game resolves it: under textures\, backslashed.</summary>
    private static string GameTexturePath(string texture)
    {
        var rel = texture.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        return rel.StartsWith("textures" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? rel : Path.Combine("textures", rel);
    }

    private static string ShellJson(FurLayer f) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["Droop"] = f.Droop, ["Enabled"] = true, ["FadeEnd"] = f.FadeEnd, ["FadeStart"] = f.FadeStart, ["Length"] = f.ShellLength,
        ["RootDarkening"] = f.RootDarkening, ["RootThreshold"] = f.RootThreshold, ["ShellColor"] = f.ShellColor,
        ["ShellCount"] = f.ShellCount, ["TipThreshold"] = f.TipThreshold,
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Shell settings for body hair alone (no fur): full shell colour, thresholds spanning the whole strand.</summary>
    private static string ShellJson(BodyHairLayer h) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["Droop"] = h.Droop, ["Enabled"] = true, ["FadeEnd"] = h.FadeEnd, ["FadeStart"] = h.FadeStart, ["Length"] = h.ShellLength,
        ["RootDarkening"] = h.RootDarkening, ["RootThreshold"] = 0.0, ["ShellColor"] = 1.0, ["ShellCount"] = h.ShellCount, ["TipThreshold"] = 1.0,
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>
    /// The shell texture as rendered has nothing outside the UV islands; filtering and mipmaps would pull that
    /// emptiness into the edges, so uncovered texels take their nearest covered neighbour's value (the albedo's
    /// alpha marks coverage).
    /// </summary>
    private static byte[] FillUncovered(byte[] shell, byte[] albedo, int size)
    {
        var data = (byte[])shell.Clone();
        var have = new bool[size * size];
        for (var i = 0; i < have.Length; i++) have[i] = albedo[i * 4 + 3] > 0 || shell[i * 4 + 3] > 0 || shell[i * 4] > 0;
        for (var step = 0; step < 16; step++)
        {
            var add = new List<(int, int)>();
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var i = y * size + x;
                if (have[i]) continue;
                var from = x > 0 && have[i - 1] ? i - 1 : x < size - 1 && have[i + 1] ? i + 1 : y > 0 && have[i - size] ? i - size : y < size - 1 && have[i + size] ? i + size : -1;
                if (from >= 0) add.Add((i, from));
            }
            if (add.Count == 0) break;
            foreach (var (i, from) in add) { Array.Copy(data, from * 4, data, i * 4, 4); have[i] = true; }
        }
        return data;
    }

    /// <summary>Command-line export: add the NIFs, generate with the saved settings, export, and log the result.</summary>
    /// <summary>Set for a command-line export, before the window is shown.</summary>
    public bool Headless { get; set; }

    public async Task ExportHeadless(string folder, IReadOnlyList<string> nifs, int? size = null)
    {
        if (size is { } wanted)
        {
            // the nearest export size on offer
            var items = ExportSizeBox.Items.Cast<ComboBoxItem>().ToList();
            ExportSizeBox.SelectedItem = items.MinBy(i => Math.Abs(int.Parse(i.Content.ToString()!) - wanted));
        }
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
        Painter.Settings = settings;
        Sliders.SetNifs(_nifs.Select(n => (n.Path, (IReadOnlyList<ShapeData>)n.Shapes)).ToList());
        _ = RebuildMeshAsync(); // smoothing and sliders may have changed
    }

    private static State LoadState()
    {
        try
        {
            var file = File.Exists(StateFile) ? StateFile : OldStateFile;
            if (File.Exists(file))
            {
                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(file)) ?? new State();
                state.Settings.WithPaths(SkinSettings.ResolvePath);
                return state;
            }
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
                Settings = Settings.Settings.Clone().WithPaths(SkinSettings.PortablePath),
                ExportFolder = string.IsNullOrWhiteSpace(ExportFolderBox.Text) ? null : ExportFolderBox.Text.Trim(),
                Nifs = _nifs.Select(n => n.Path).ToList(),
                SliderSets = Sliders.Choices,
                Mo2Folder = string.IsNullOrWhiteSpace(Patcher.Mo2Folder) ? null : Patcher.Mo2Folder,
                NeckBand = Patcher.Bands.Neck,
                LimbBand = Patcher.Bands.Limb,
                SkipBeasts = Patcher.Options.SkipBeasts,
                ZipOutput = Patcher.Options.Zip,
                AutoUpdate = AutoUpdateBox.IsChecked == true,
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
