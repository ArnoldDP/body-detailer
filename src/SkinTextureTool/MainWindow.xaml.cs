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
    private readonly DispatcherTimer _hairDebounce;
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
        public Dictionary<string, DdsFormat> Formats { get; set; } = new();
        public int ShellSize { get; set; }
        public int HeadSize { get; set; }
        public int LimbSize { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); if (AutoUpdateBox.IsChecked == true) Regenerate(); else MarkStale(true); };
        _geometryDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _geometryDebounce.Tick += (_, _) => { _geometryDebounce.Stop(); _ = RebuildMeshAsync(); };
        _hairDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hairDebounce.Tick += (_, _) => { _hairDebounce.Stop(); _ = RebuildHairAsync(); };

        var state = LoadState();
        Settings.Exclude.Add(nameof(SkinSettings.HairMesh));
        Settings.Exclude.Add(nameof(SkinSettings.AddonHair));
        HairSettings.Only = nameof(SkinSettings.HairMesh);
        AddonHairSettings.Only = nameof(SkinSettings.AddonHair);
        AddonHairSettings.NoMasks = true;
        Settings.Settings = state.Settings;
        HairSettings.Settings = state.Settings;
        AddonHairSettings.Settings = state.Settings;
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
        BuildCompressionRows(state);
        HeadSizeBox.SelectedIndex = Math.Clamp(state.HeadSize, 0, HeadSizeBox.Items.Count - 1);
        LimbSizeBox.SelectedIndex = Math.Clamp(state.LimbSize, 0, LimbSizeBox.Items.Count - 1);
        Settings.Changed += () => _debounce.Start();
        Settings.GeometryChanged += () => _geometryDebounce.Start();
        HairSettings.HairChanged += () => { _hairDebounce.Stop(); _hairDebounce.Start(); };
        AddonHairSettings.HairChanged += () => { _hairDebounce.Stop(); _hairDebounce.Start(); };
        // regions (their masks, strengths and names) can be where the hair grows
        Regions.Changed += () => { _hairDebounce.Stop(); _hairDebounce.Start(); HairSettings.Settings = Settings.Settings; };
        Sliders.Changed += () => _geometryDebounce.Start();
        Closing += (_, _) => SaveState();
        Genitals.Bake.Click += OnBakeGenitals;
        Genitals.Addon.SelectionChanged += (_, _) => _ = ShowAddonAsync();
        Genitals.ShowInPreview.Checked += (_, _) => _ = ShowAddonAsync();
        Genitals.ShowInPreview.Unchecked += (_, _) => _ = ShowAddonAsync();
        Genitals.Addon.SelectionChanged += (_, _) => { if (Settings.Settings.AddonHair.Enabled) _hairDebounce.Start(); };
        Genitals.Mask.TextChanged += (_, _) => { if (Settings.Settings.AddonHair.Enabled) _hairDebounce.Start(); };
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
    /// <summary>The load order being read in the background (the session and a fresh NPC list).</summary>
    private Task<(SkinTextureTool.Core.Patcher.GameSession Session, List<SkinTextureTool.Core.Patcher.NpcAssets.Entry> Npcs)>? _sessionLoad;
    private string? _sessionFolder;
    private List<SkinTextureTool.Core.Patcher.NpcAssets.Entry>? _npcs;
    /// <summary>The NPC last loaded with Load NPC (the TNG add-on can be assigned to it).</summary>
    private SkinTextureTool.Core.Patcher.NpcAssets.Entry? _loadedNpc;

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
            if (_sessionLoad == null || _sessionFolder != mo2Folder)
            {
                // the list from last time shows at once; the load order is read meanwhile, and refreshes the list
                // (and the cache) when it's done
                var cachePath = Path.Combine(AppContext.BaseDirectory, "NPC list cache.json");
                var extract = Path.Combine(AppContext.BaseDirectory, "Extracted meshes");
                _session?.Dispose(); _session = null;
                _sessionFolder = mo2Folder;
                _npcs = await Task.Run(() => SkinTextureTool.Core.Patcher.NpcAssets.LoadCache(cachePath, mo2Folder));
                _sessionLoad = Task.Run(() =>
                {
                    var session = new SkinTextureTool.Core.Patcher.GameSession(mo2Folder, extract);
                    var list = SkinTextureTool.Core.Patcher.NpcAssets.List(session);
                    SkinTextureTool.Core.Patcher.NpcAssets.SaveCache(cachePath, mo2Folder, list);
                    return (session, list);
                });
                if (_npcs == null)
                {
                    Status("Reading the load order's NPCs…");
                    (_session, _npcs) = await _sessionLoad;
                }
            }
            if (_session == null && _sessionLoad.IsCompletedSuccessfully) (_session, _npcs) = _sessionLoad.Result;
            Status($"{_npcs!.Count} NPCs in the load order" + (_session == null ? " (from last time; the load order is still being read)." : "."));
            var picker = new NpcPickerWindow(_npcs) { Owner = this };
            if (picker.ShowDialog() != true || picker.Picked is not { } npc) return;
            _loadedNpc = npc;
            TngAssignBox.IsEnabled = true;
            TngAssignBox.Content = $"Add it to {npc.Name}'s skin ({npc.EditorId}, patch plugin)";
            TngAssignBox.IsChecked = true;

            if (_session == null)
            {
                Status($"Reading the load order before loading {npc.Name}…");
                (_session, _npcs) = await _sessionLoad;
            }
            Status($"Loading {npc.Name}…");
            var session = _session;
            var addons = await Task.Run(() => GenitalAddon.List(session));
            Genitals.Addon.ItemsSource = addons.Select(a => new AddonChoice(a)).ToList();
            if (addons.Count > 0) Genitals.Addon.SelectedIndex = 0;
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
            if (_sessionLoad is { IsFaulted: true }) _sessionLoad = null; // read the load order again next time
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
            HairSettings.Groups = _groups;
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
        _hairDebounce.Start();
        if (!names.SequenceEqual(_groups))
        {
            _groups = names;
            Settings.Groups = names;
            HairSettings.Groups = names;
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

    // ---------- compression per map ----------

    private static readonly (string Key, string Label)[] Maps =
        { ("diffuse", "Diffuse"), ("normal", "Normal (_msn)"), ("specular", "Specular (_s)"), ("subsurface", "Subsurface (_sk)"), ("rfaos", "RFAOS"), ("shell", "Fur shell") };
    private static readonly string[] FormatNames =
        { "BC7 fast", "BC7 balanced", "BC7 best (slow)", "BC1 / DXT1 (small, drops alpha)", "BC3 / DXT5", "Uncompressed" };
    private readonly Dictionary<string, ComboBox> _formatBoxes = new();
    private ComboBox? _shellSizeBox;

    private void BuildCompressionRows(State state)
    {
        void Row(string label, ComboBox box)
        {
            var r = CompressionGrid.RowDefinitions.Count;
            CompressionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 8, 2) };
            Grid.SetRow(t, r); Grid.SetRow(box, r); Grid.SetColumn(box, 1);
            box.Margin = new Thickness(0, 2, 0, 2);
            CompressionGrid.Children.Add(t); CompressionGrid.Children.Add(box);
        }
        foreach (var (key, label) in Maps)
        {
            var box = new ComboBox { ItemsSource = FormatNames };
            box.SelectedIndex = (int)(state.Formats.TryGetValue(key, out var f) ? f : DdsFormat.Bc7Fast);
            _formatBoxes[key] = box;
            Row(label, box);
        }
        _shellSizeBox = new ComboBox { ItemsSource = new[] { "Same as its texture", "512", "1024", "2048", "4096" } };
        _shellSizeBox.SelectedIndex = Math.Clamp(state.ShellSize, 0, 4);
        _shellSizeBox.ToolTip = "Fur shells read the strand pattern softly, so a smaller shell texture often looks the same";
        Row("Fur shell size", _shellSizeBox);
    }

    private DdsFormat FormatFor(string map) => _formatBoxes.TryGetValue(map, out var b) ? (DdsFormat)b.SelectedIndex : DdsFormat.Bc7Fast;

    /// <summary>The export size for a texture group: heads and hands/feet can have their own, otherwise the body's.</summary>
    private int ExportSizeFor(string group)
    {
        var box = group.Contains("head", StringComparison.OrdinalIgnoreCase) ? HeadSizeBox
            : group.Contains("hand", StringComparison.OrdinalIgnoreCase) || group.Contains("feet", StringComparison.OrdinalIgnoreCase)
              || group.Contains("foot", StringComparison.OrdinalIgnoreCase) ? LimbSizeBox : null;
        return box != null && int.TryParse(((ComboBoxItem)box.SelectedItem).Content.ToString(), out var n) ? n : ExportSize;
    }

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
                var max = await Preview.Run(() => OpenTK.Graphics.OpenGL4.GL.GetInteger(OpenTK.Graphics.OpenGL4.GetPName.MaxTextureSize));
                if (size > max) { Status($"This graphics card's largest texture is {max}×{max}; previewing at that."); size = max; }
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
        _hairDebounce.Start(); // the hair mesh may grow from one of these masks
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _imageTextures.Remove(path);
            await Preview.Run(() => Preview.Renderer.ForgetImage(path));
        }
        Regions.Settings = Settings.Settings;
        SaveState();
        Regenerate();
    }

    // ---------- hair mesh ----------

    private readonly Dictionary<string, (DateTime Stamp, float[] Grey, int W, int H)> _hairMasks = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The genital add-on's mesh (data path) the current hair also grew on, or null.</summary>
    private string? _hairAddon;
    private readonly Dictionary<string, ShapeData> _addonMeshes = new(StringComparer.OrdinalIgnoreCase);


    /// <summary>The selected genital add-on's mesh for the loaded NPC, with the blend mask as where hair grows (all of it without one).</summary>
    private async Task<(HairMeshBuilder.Surface Surface, string MeshPath)?> AddonSurfaceAsync()
    {
        if (_session is not { } session || _loadedNpc is not { } entry || Genitals.Addon.SelectedItem is not AddonChoice choice) return null;
        var maskPath = Genitals.Mask.Text.Trim();
        return await Task.Run<(HairMeshBuilder.Surface, string)?>(() =>
        {
            var npc = session.Cache.Resolve<Mutagen.Bethesda.Skyrim.INpcGetter>(Mutagen.Bethesda.Plugins.FormKey.Factory(entry.FormKey));
            if (GenitalAddon.For(session, choice.Armor, npc) is not { } piece) return null;
            if (!_addonMeshes.TryGetValue(piece.MeshPath, out var shape))
            {
                var file = session.Data.Materialize(piece.MeshPath);
                if (file == null) return null;
                lock (_addonMeshes) _addonMeshes[piece.MeshPath] = shape = Merge(BodyMesh.Load(file));
            }
            float[] grey; int w, h;
            if (maskPath.Length > 0 && File.Exists(maskPath))
            {
                var m = ImageFiles.Load(maskPath);
                (w, h) = (m.Width, m.Height);
                grey = new float[w * h];
                for (var i = 0; i < grey.Length; i++) grey[i] = m.Rgba[i * 4] / 255f;
            }
            else { (w, h) = (1, 1); grey = new[] { 1f }; }
            return (new HairMeshBuilder.Surface(shape, grey, w, h), piece.MeshPath);
        });
    }
    private bool _hairBuilding, _hairAgain;
    private string? _atlasKey;
    private byte[]? _hairAtlasPixels;
    private (HairMesh Mesh, IReadOnlyList<ShapeData> Surfaces, HairMeshLayer Look, byte[] Atlas)? _hairBuilt;
    private const int HairAtlasSize = 1024;

    /// <summary>Grows the hair cards from the hair mesh settings and its masks, and shows them in the preview.</summary>
    private async Task RebuildHairAsync()
    {
        if (_hairBuilding) { _hairAgain = true; return; }
        _hairBuilding = true;
        try
        {
            do
            {
                _hairAgain = false;
                var s = Settings.Settings;
                var main = s.HairMesh; var around = s.AddonHair;
                if (!main.Enabled && !around.Enabled || _meshGroups.Count == 0)
                {
                    await Preview.Run(() => Preview.Renderer.SetHairMesh(null, main.Color, 0));
                    HairInfo.Text = "";
                    Preview.Refresh();
                    continue;
                }
                var notes = new List<string>();

                // the main hair: on the loaded meshes, where its region's masks (or its own) are white
                var bodySurfaces = new List<HairMeshBuilder.Surface>();
                if (main.Enabled)
                {
                    var region = main.MaskRegion.Length > 0 ? s.Regions.FirstOrDefault(r => r.Name == main.MaskRegion) : null;
                    if (main.MaskRegion.Length > 0 && region == null) notes.Add($"no region named {main.MaskRegion}");
                    foreach (var (group, shapes) in _meshGroups)
                    {
                        var path = main.MaskRegion.Length > 0 ? region?.MaskFor(group) : main.MaskFor(group);
                        if (path == null || !File.Exists(path)) continue;
                        var m = await HairMaskAsync(path);
                        var grey = m.Grey;
                        if (region != null && region.Strength < 1) grey = grey.Select(g => g * region.Strength).ToArray();
                        bodySurfaces.AddRange(shapes.Select(x => new HairMeshBuilder.Surface(x.Shape, grey, m.W, m.H)));
                    }
                    if (bodySurfaces.Count == 0) notes.Add(main.MaskRegion.Length > 0 ? $"region {main.MaskRegion} has no masks" : "choose a mask region (or masks) for it");
                }

                // the hair around the add-on's base: its own settings, on the add-on's mesh where its blend mask is white
                _hairAddon = null;
                HairMeshBuilder.Surface? addonSurface = null;
                if (around.Enabled)
                {
                    var extra = await AddonSurfaceAsync();
                    if (extra == null) notes.Add("load an NPC and pick its add-on to grow hair around it");
                    else { addonSurface = extra.Value.Surface; _hairAddon = extra.Value.MeshPath; }
                }
                if (bodySurfaces.Count == 0 && addonSurface == null)
                {
                    await Preview.Run(() => Preview.Renderer.SetHairMesh(null, main.Color, 0));
                    HairInfo.Text = "Hair mesh: " + string.Join("; ", notes);
                    Preview.Refresh();
                    continue;
                }

                // one hair texture for both (the main layer's look, or the add-on hair's when it's alone)
                var clone = s.Clone();
                var look = main.Enabled && bodySurfaces.Count > 0 ? clone.HairMesh : clone.AddonHair;
                var seed = s.Seed;
                var atlasKey = $"{seed}|{look.Curl:0.000}|{look.Frizz:0.000}|{look.HairsPerCard}|{look.HairThickness:0.000}|{look.ColorVariation:0.000}|{look.RootFade:0.000}";
                if (atlasKey != _atlasKey)
                {
                    var atlas = await Task.Run(() => HairAtlas.Generate(HairAtlasSize, look, seed));
                    _hairAtlasPixels = atlas;
                    await Preview.Run(() => Preview.Renderer.SetHairAtlas(atlas, HairAtlasSize));
                    _atlasKey = atlasKey;
                }
                var (mesh, surfaces) = await Task.Run(() =>
                {
                    var parts = new List<(HairMesh Mesh, int Surfaces)>();
                    if (bodySurfaces.Count > 0) parts.Add((HairMeshBuilder.Build(bodySurfaces, clone.HairMesh, seed), bodySurfaces.Count));
                    if (addonSurface is { } a) parts.Add((HairMeshBuilder.Build(new[] { a }, clone.AddonHair, seed + 1), 1));
                    var all = bodySurfaces.Select(x => x.Shape).ToList();
                    if (addonSurface is { } b) all.Add(b.Shape);
                    return (HairMeshBuilder.Combine(parts), all);
                });
                await Preview.Run(() => Preview.Renderer.SetHairMesh(mesh, look.Color, look.RootDarkening));
                _hairBuilt = (mesh, surfaces, look, _hairAtlasPixels!);
                HairInfo.Text = $"Hair mesh: {mesh.CardCount:N0} cards, {mesh.TriangleCount:N0} triangles, {mesh.VertexCount:N0} vertices"
                                + (mesh.Dropped > 0 ? $" ({mesh.Dropped:N0} cards over the budget left out)" : "")
                                + (notes.Count > 0 ? " — " + string.Join("; ", notes) : "");
                Preview.Refresh();
            } while (_hairAgain);
        }
        catch (Exception ex)
        {
            Status("Hair mesh failed: " + ex.Message);
        }
        finally
        {
            _hairBuilding = false;
        }
    }

    /// <summary>A mask image as greys, cached until the file changes.</summary>
    private async Task<(DateTime Stamp, float[] Grey, int W, int H)> HairMaskAsync(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_hairMasks.TryGetValue(path, out var m) && m.Stamp == stamp) return m;
        var image = await Task.Run(() => ImageFiles.Load(path));
        var grey = new float[image.Width * image.Height];
        for (var i = 0; i < grey.Length; i++) grey[i] = image.Rgba[i * 4] / 255f;
        return _hairMasks[path] = (stamp, grey, image.Width, image.Height);
    }

    private async void OnExportHair(object sender, RoutedEventArgs e)
    {
        if (_hairBuilt is not { } built || built.Mesh.VertexCount == 0) { Status("Enable Hair Mesh and give it a mask first."); return; }
        var folder = ExportFolderBox.Text.Trim();
        if (folder.Length == 0) { Status("Choose an export folder first."); return; }
        HairExportButton.IsEnabled = false;
        try
        {
            Status("Writing the hair mesh…");
            var addonMesh = _hairAddon;
            var sessionNow = _session;
            var sets = await Task.Run(() => built.Surfaces.Select(s =>
                addonMesh != null && sessionNow != null && _addonMeshes.TryGetValue(addonMesh, out var a) && ReferenceEquals(a, s)
                    ? SkinTextureTool.Core.BodySlide.SliderSetFinder.ForMesh(sessionNow.Data, addonMesh, s.Name)
                    : Sliders.SetFor(s.NifPath)).ToList());
            var name = HairNameBox.Text;
            var result = await Task.Run(() => HairExporter.Export(folder, name, built.Mesh, built.Surfaces, sets, built.Look, built.Atlas, HairAtlasSize));
            var file = HairExporter.Safe(name);
            var meshPath = Path.Combine(HairExporter.Folder, file + "_1.nif");
            if (TngAddonBox.IsChecked == true)
            {
                // with hair on the add-on, the TNG add-on carries the add-on's pieces too, as one choice
                var with = addonMesh != null && Genitals.Addon.SelectedItem is AddonChoice c ? c.Armor : null;
                var plugin = await Task.Run(() => TngAddon.Write(folder, file, name.Trim(), meshPath, null, with));
                result.Written.AddRange(plugin);
                result.Notes.Add($"Enable {plugin[0]} after TheNewGentleman.esp{(with != null ? " (and " + with.FormKey.ModKey.FileName + ")" : "")} and pick it per NPC in TNG's menu.");
            }
            if (TngAssignBox.IsChecked == true && _loadedNpc is { } npc && _session is { } session)
            {
                var (plugin, notes) = await Task.Run(() => NpcSkinPatch.Write(session, Mutagen.Bethesda.Plugins.FormKey.Factory(npc.FormKey), folder, file, meshPath));
                result.Written.Add(plugin);
                result.Notes.AddRange(notes);
                result.Notes.Add($"Enable {plugin} at the end of your load order (it overrides {npc.EditorId}, like a patch).");
            }
            Status($"Wrote {result.Written.Count} hair file(s) to {folder}. " + string.Join(" ", result.Notes));
        }
        catch (Exception ex)
        {
            Status("Hair export failed: " + ex.Message);
        }
        finally
        {
            HairExportButton.IsEnabled = true;
        }
    }

    // ---------- genital add-on patch ----------

    private sealed record AddonChoice(Mutagen.Bethesda.Skyrim.IArmorGetter Armor)
    {
        public override string ToString() => $"{Armor.Name?.String ?? Armor.EditorID} ({Armor.FormKey.ModKey.FileName})";
    }

    /// <summary>A texture from the load order (a path under textures, with or without the prefix), or a file on disk.</summary>
    private static RgbaImage? LoadImage(SkinTextureTool.Core.Patcher.GameSession session, string? dataPath)
    {
        if (string.IsNullOrEmpty(dataPath)) return null;
        var rel = dataPath.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? dataPath : Path.Combine("textures", dataPath);
        var file = File.Exists(dataPath) ? dataPath : session.Data.Materialize(rel);
        if (file == null) return null;
        var image = ImageFiles.Load(file);
        return new RgbaImage(image.Width, image.Height, image.Rgba);
    }

    /// <summary>
    /// Bakes the selected genital add-on's textures for the loaded NPC: the NPC's body textures projected onto the
    /// add-on's layout and blended in by the mask, written as DDS under the export folder, and (if chosen) given to
    /// the NPC through its patch plugin.
    /// </summary>
    private async void OnBakeGenitals(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session || _loadedNpc is not { } entry) { Genitals.Info.Text = "Load an NPC first (Load NPC)."; return; }
        if (Genitals.Addon.SelectedItem is not AddonChoice choice) { Genitals.Info.Text = "No genital add-on (with TNG_AddonFemale and TNG_SkinWithPenis) was found in the load order."; return; }
        var folder = ExportFolderBox.Text.Trim();
        if (folder.Length == 0) { Genitals.Info.Text = "Choose an export folder first."; return; }
        // the body: the loaded group with the most vertices, with the textures it uses
        var body = _meshGroups.OrderByDescending(g => g.Shapes.Sum(s => s.Shape.VertexCount)).Select(g => g.Shapes.Select(s => s.Shape).ToList()).FirstOrDefault();
        if (body == null) { Genitals.Info.Text = "The NPC's body isn't loaded."; return; }
        var maskPath = Genitals.Mask.Text.Trim();
        var distribute = Genitals.Distribute.IsChecked == true;
        var formats = new[] { FormatFor("diffuse"), FormatFor("normal"), FormatFor("subsurface"), FormatFor("specular") };
        Genitals.Bake.IsEnabled = false;
        try
        {
            Genitals.Info.Text = "Baking…";
            var npcKey = Mutagen.Bethesda.Plugins.FormKey.Factory(entry.FormKey);
            var result = await Task.Run(() =>
            {
                var notes = new List<string>();
                var npc = session.Cache.Resolve<Mutagen.Bethesda.Skyrim.INpcGetter>(npcKey);
                var piece = GenitalAddon.For(session, choice.Armor, npc) ?? throw new InvalidOperationException($"{choice} has no piece for {entry.Race}.");
                var meshFile = session.Data.Materialize(piece.MeshPath) ?? throw new FileNotFoundException($"{piece.MeshPath} isn't in the load order.");
                var genital = Merge(BodyMesh.Load(meshFile));
                RgbaImage? mask = null;
                if (maskPath.Length > 0 && File.Exists(maskPath)) { var m = ImageFiles.Load(maskPath); mask = new RgbaImage(m.Width, m.Height, m.Rgba); }
                else if (maskPath.Length > 0) notes.Add("The mask file wasn't found, so the body texture covers the whole add-on.");

                // the add-on's own maps (from the texture set its piece swaps in, else the mesh's) and the body's
                var tex = piece.Textures;
                string? Own(int slot, string? fromSet) => fromSet ?? (slot < genital.TexturePaths.Length ? genital.TexturePaths[slot] : null);
                var maps = new (string Suffix, string Label, string? Genital, int BodySlot)[]
                {
                    ("", "diffuse", Own(0, tex?.Diffuse?.DataRelativePath.Path), 0),
                    ("_msn", "normal", Own(1, tex?.NormalOrGloss?.DataRelativePath.Path), 1),
                    ("_sk", "subsurface", Own(2, tex?.GlowOrDetailMap?.DataRelativePath.Path), 2),
                    ("_s", "specular", Own(7, tex?.BacklightMaskOrSpecular?.DataRelativePath.Path), 7),
                };
                var diffuse = LoadImage(session, maps[0].Genital) ?? throw new FileNotFoundException("The add-on's diffuse texture wasn't found.");
                var size = diffuse.Width;
                var projection = GenitalBaker.Project(genital, body, size, mask);
                var name = HairExporter.Safe(npc.EditorID ?? entry.Name);
                var relFolder = Path.Combine("Body Detailer Genitals", name);
                var written = new List<string>(); byte[]? preview = null;
                var bakedMaps = new RgbaImage?[2];
                var paths = new string?[4];
                for (var m = 0; m < maps.Length; m++)
                {
                    var own = m == 0 ? diffuse : LoadImage(session, maps[m].Genital);
                    if (own == null) { notes.Add($"The add-on has no {maps[m].Label} map; skipped."); continue; }
                    var bodyPath = body[0].TexturePaths.Length > maps[m].BodySlot ? body[0].TexturePaths[maps[m].BodySlot] : null;
                    var bodyImage = LoadImage(session, bodyPath);
                    if (bodyImage == null) notes.Add($"The body's {maps[m].Label} map wasn't found; the add-on's own is kept.");
                    // every map at the diffuse's size, so the projection lines up
                    var ownSized = own.Width == size ? own : Resize(own, size);
                    var px = GenitalBaker.Combine(projection, ownSized, body.Select(_ => bodyImage).ToList(), mask);
                    paths[m] = Path.Combine(relFolder, "schlong" + maps[m].Suffix + ".dds");
                    DdsExporter.Write(Path.Combine(folder, "textures", paths[m]!), px, size, size, formats[m]);
                    written.Add(Path.Combine("textures", paths[m]!));
                    if (m == 0) preview = px;
                    if (m <= 1) bakedMaps[m] = new RgbaImage(size, size, px);
                }
                if (distribute)
                {
                    string Keep(int m, string? original) => paths[m] ?? original ?? "";
                    var (plugin, n) = piece.Write(session, npcKey, folder, (Keep(0, null), Keep(1, tex?.NormalOrGloss?.GivenPath), Keep(2, tex?.GlowOrDetailMap?.GivenPath), Keep(3, tex?.BacklightMaskOrSpecular?.GivenPath)));
                    written.Add(plugin); notes.AddRange(n);
                    notes.Add($"Enable {plugin} at the end of your load order.");
                }
                return (Written: written, Notes: notes, Preview: preview, Size: size, Mesh: piece.MeshPath, Maps: bakedMaps);
            });
            if (result.Preview != null) Genitals.ShowPreview(result.Preview, result.Size);
            if (result.Maps[0] is { } bakedDiffuse) { _bakedAddon = (entry.FormKey, bakedDiffuse, result.Maps[1]); _ = ShowAddonAsync(); }
            Genitals.Info.Text = $"Baked {result.Mesh} for {entry.Name}. Wrote {string.Join(", ", result.Written)}. {string.Join(" ", result.Notes)}";
        }
        catch (Exception ex)
        {
            Genitals.Info.Text = "Couldn't bake: " + ex.Message;
        }
        finally
        {
            Genitals.Bake.IsEnabled = true;
        }
    }

    /// <summary>The baked add-on maps for the loaded NPC (diffuse, normal), shown in the preview after a bake.</summary>
    private (string Npc, RgbaImage Diffuse, RgbaImage? Normal)? _bakedAddon;

    /// <summary>Draws the selected add-on's mesh for the loaded NPC in the preview, with the baked maps if there are any, else its own.</summary>
    private async Task ShowAddonAsync()
    {
        try
        {
            if (Genitals.ShowInPreview.IsChecked != true || _session is not { } session || _loadedNpc is not { } entry || Genitals.Addon.SelectedItem is not AddonChoice choice)
            {
                await Preview.Run(() => Preview.Renderer.SetAddon(null, null, null));
                Preview.Refresh();
                return;
            }
            var baked = _bakedAddon is { } b && b.Npc == entry.FormKey ? b : ((string, RgbaImage, RgbaImage?)?)null;
            var loaded = await Task.Run(() =>
            {
                var npc = session.Cache.Resolve<Mutagen.Bethesda.Skyrim.INpcGetter>(Mutagen.Bethesda.Plugins.FormKey.Factory(entry.FormKey));
                if (GenitalAddon.For(session, choice.Armor, npc) is not { } piece || session.Data.Materialize(piece.MeshPath) is not { } file) return null;
                if (!_addonMeshes.TryGetValue(piece.MeshPath, out var shape)) lock (_addonMeshes) _addonMeshes[piece.MeshPath] = shape = Merge(BodyMesh.Load(file));
                var diffuse = baked?.Item2 ?? LoadImage(session, piece.Textures?.Diffuse?.DataRelativePath.Path ?? (shape.TexturePaths.Length > 0 ? shape.TexturePaths[0] : null));
                var normal = baked != null ? baked.Value.Item3 : LoadImage(session, piece.Textures?.NormalOrGloss?.DataRelativePath.Path ?? (shape.TexturePaths.Length > 1 ? shape.TexturePaths[1] : null));
                return ((ShapeData Shape, RgbaImage? Diffuse, RgbaImage? Normal)?)(shape, diffuse, normal);
            });
            await Preview.Run(() => Preview.Renderer.SetAddon(loaded?.Shape, loaded?.Diffuse, loaded?.Normal));
            Preview.Refresh();
        }
        catch (Exception ex)
        {
            Status("Couldn't show the add-on: " + ex.Message);
        }
    }

    /// <summary>A NIF's shapes as one (they share the add-on's texture layout).</summary>
    private static ShapeData Merge(IReadOnlyList<ShapeData> shapes)
    {
        if (shapes.Count == 1) return shapes[0];
        var pos = new List<float>(); var uv = new List<float>(); var idx = new List<uint>();
        foreach (var s in shapes)
        {
            var b = (uint)(pos.Count / 3);
            pos.AddRange(s.Positions); uv.AddRange(s.Uvs); idx.AddRange(s.Indices.Select(i => i + b));
        }
        var first = shapes[0];
        return new ShapeData { Name = first.Name, NifPath = first.NifPath, Positions = pos.ToArray(), Uvs = uv.ToArray(), Indices = idx.ToArray(), DiffusePath = first.DiffusePath, TexturePaths = first.TexturePaths, ModelSpaceNormals = first.ModelSpaceNormals };
    }

    private static RgbaImage Resize(RgbaImage image, int size)
    {
        var px = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var c = image.Sample((x + 0.5f) / size, (y + 0.5f) / size);
            var o = (y * size + x) * 4;
            px[o] = (byte)c.X; px[o + 1] = (byte)c.Y; px[o + 2] = (byte)c.Z; px[o + 3] = (byte)c.W;
        }
        return new RgbaImage(size, size, px);
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
            var rfaos = RfaosBox.IsChecked == true;
            var formats = Maps.ToDictionary(m => m.Key, m => FormatFor(m.Key));
            var shellTarget = _shellSizeBox is { SelectedIndex: > 0 } sb ? int.Parse((string)sb.SelectedItem) : 0;
            var gamePaths = GamePathsBox.IsChecked == true;
            var written = new List<string>();
            foreach (var group in _groups)
            {
                var size = ExportSizeFor(group);
                Status($"Generating {group} at {size}×{size}…");
                var textures = await Preview.Run(() =>
                {
                    Preview.Renderer.Generate(snapshot, size, images, group);
                    return Preview.Renderer.Read(group);
                });
                Status($"Compressing {group}… (this takes a while at large sizes)");
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
                        () => DdsExporter.Write(Out(0, ".dds"), textures.Albedo, size, size, formats["diffuse"]),
                        () => DdsExporter.Write(Out(1, "_msn.dds"), textures.Normal, size, size, formats["normal"]),
                        () => DdsExporter.Write(Out(7, "_s.dds"), textures.Specular, size, size, formats["specular"]),
                        () => DdsExporter.Write(Out(2, "_sk.dds"), textures.Subsurface, size, size, formats["subsurface"]),
                    };
                    if (rfaos) jobs.Add(() => DdsExporter.Write(Out(-1, "_rfaos.dds"), textures.Rfaos, size, size, formats["rfaos"]));
                    // Bottled Shaders fur shells: matched by name next to the diffuse
                    if (textures.Shell != null && snapshot.Fur.Enabled && snapshot.Fur.ExportShells)
                    {
                        var shell = FillUncovered(textures.Shell, textures.Albedo, size);
                        var shellSize = shellTarget > 0 ? Math.Min(shellTarget, size) : size;
                        shell = DdsExporter.Downscale(shell, size, shellSize);
                        jobs.Add(() => DdsExporter.Write(Out(-1, "_shell.dds"), shell, shellSize, shellSize, formats["shell"]));
                        jobs.Add(() => File.WriteAllText(Out(-1, "_shell.json"), ShellJson(snapshot.Fur)));
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
        HairSettings.Settings = settings;
        AddonHairSettings.Settings = settings;
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
                HeadSize = HeadSizeBox.SelectedIndex,
                Formats = _formatBoxes.ToDictionary(kv => kv.Key, kv => (DdsFormat)kv.Value.SelectedIndex),
                ShellSize = _shellSizeBox?.SelectedIndex ?? 0,
                LimbSize = LimbSizeBox.SelectedIndex,
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
