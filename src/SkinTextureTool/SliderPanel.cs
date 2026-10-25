using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SkinTextureTool.Core.BodySlide;
using SkinTextureTool.Core.Generation;
using SkinTextureTool.Core.Mesh;

namespace SkinTextureTool;

/// <summary>Which BodySlide slider set drives a loaded NIF's normals.</summary>
public sealed class SliderSetChoice
{
    public string OspPath { get; set; } = "";
    public string SetName { get; set; } = "";
}

/// <summary>
/// BodySlide sliders that reshape only the normal bake. Per loaded NIF: pick a .osp and slider set, then set
/// amounts (on top of the mesh as built). The game mesh is untouched; only the lighting changes.
/// </summary>
public sealed class SliderPanel : StackPanel
{
    private readonly Dictionary<string, List<SliderSet>> _ospCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after a slider amount or slider set changes.</summary>
    public event Action? Changed;

    public SkinSettings Settings { get; set; } = new();
    /// <summary>NIF path → chosen slider set; kept by the window between sessions.</summary>
    public Dictionary<string, SliderSetChoice> Choices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<(string Nif, IReadOnlyList<ShapeData> Shapes)> _nifs = Array.Empty<(string, IReadOnlyList<ShapeData>)>();

    public void SetNifs(IReadOnlyList<(string Nif, IReadOnlyList<ShapeData> Shapes)> nifs)
    {
        _nifs = nifs;
        Rebuild();
    }

    /// <summary>The slider set chosen for a NIF, if any and if it still loads.</summary>
    public SliderSet? SetFor(string nif)
    {
        if (!Choices.TryGetValue(nif, out var c) || !File.Exists(c.OspPath)) return null;
        return Load(c.OspPath).FirstOrDefault(s => s.Name == c.SetName);
    }

    private List<SliderSet> Load(string osp)
    {
        if (!_ospCache.TryGetValue(osp, out var sets))
        {
            try { sets = SliderSet.Load(osp); }
            catch { sets = new List<SliderSet>(); }
            _ospCache[osp] = sets;
        }
        return sets;
    }

    private void Rebuild()
    {
        Children.Clear();
        Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8),
            Text = "BodySlide sliders here only change the normal map: the mesh the game uses stays as built, but the lighting "
                 + "shows the shape (muscle definition, ribs, abs…). Amounts add to the body as built; 0 leaves it unchanged.",
        });
        if (_nifs.Count == 0)
        {
            Children.Add(new TextBlock { Text = "Load a mesh first.", Foreground = Brushes.Gray });
            return;
        }
        foreach (var (nif, shapes) in _nifs) Children.Add(NifSection(nif, shapes));
    }

    private Expander NifSection(string nif, IReadOnlyList<ShapeData> shapes)
    {
        var body = new StackPanel { Margin = new Thickness(4) };
        var ospText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Brushes.Gray };
        var setBox = new ComboBox { Margin = new Thickness(0, 4, 0, 4) };
        var sliderHost = new StackPanel();
        var search = new TextBox { Margin = new Thickness(0, 4, 0, 4), ToolTip = "Filter sliders by name" };

        void ShowSliders()
        {
            sliderHost.Children.Clear();
            var set = SetFor(nif);
            if (set == null) return;
            if (!Settings.NormalSliders.TryGetValue(set.Name, out var values))
                Settings.NormalSliders[set.Name] = values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var filter = search.Text.Trim();
            var shown = set.Sliders
                .Where(s => s.MovesVertices && !s.Hidden)
                .Where(s => filter.Length == 0 || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => values.ContainsKey(s.Name) ? 0 : 1).ThenBy(s => s.Name)
                .ToList();
            foreach (var slider in shown) sliderHost.Children.Add(SliderRow(slider, values));
            if (shown.Count == 0) sliderHost.Children.Add(new TextBlock { Text = "No sliders match.", Foreground = Brushes.Gray });
        }

        void ShowSets()
        {
            var choice = Choices.TryGetValue(nif, out var c) ? c : null;
            ospText.Text = choice == null ? "(no slider set)" : Path.GetFileName(choice.OspPath);
            ospText.ToolTip = choice?.OspPath;
            setBox.ItemsSource = choice == null ? null : Load(choice.OspPath).Select(s => s.Name).ToList();
            setBox.SelectedItem = choice?.SetName;
            ShowSliders();
        }

        var browse = new Button { Content = "Slider set (.osp)…", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "BodySlide slider set (*.osp)|*.osp", Title = "Choose the slider set this mesh was built from (CalienteTools\\BodySlide\\SliderSets)" };
            if (dlg.ShowDialog() != true) return;
            var sets = Load(dlg.FileName);
            if (sets.Count == 0) { ospText.Text = "No slider sets in that file."; return; }
            Choices[nif] = new SliderSetChoice { OspPath = dlg.FileName, SetName = BestMatch(sets, shapes).Name };
            ShowSets();
            Changed?.Invoke();
        };
        setBox.SelectionChanged += (_, _) =>
        {
            if (setBox.SelectedItem is not string name || !Choices.TryGetValue(nif, out var c) || c.SetName == name) return;
            c.SetName = name;
            ShowSliders();
            Changed?.Invoke();
        };
        search.TextChanged += (_, _) => ShowSliders();
        var reset = new Button { Content = "Reset sliders", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) =>
        {
            if (SetFor(nif) is { } set) Settings.NormalSliders.Remove(set.Name);
            ShowSliders();
            Changed?.Invoke();
        };

        var top = new DockPanel();
        DockPanel.SetDock(browse, Dock.Left);
        top.Children.Add(browse);
        top.Children.Add(ospText);
        body.Children.Add(top);
        body.Children.Add(setBox);
        body.Children.Add(new TextBlock { Text = "Search" });
        body.Children.Add(search);
        body.Children.Add(sliderHost);
        body.Children.Add(reset);
        ShowSets();

        return new Expander
        {
            Header = new TextBlock { Text = Path.GetFileName(nif), FontWeight = FontWeights.SemiBold, ToolTip = nif },
            Content = body, IsExpanded = true, Margin = new Thickness(0, 0, 0, 6),
        };
    }

    private FrameworkElement SliderRow(Core.BodySlide.Slider slider, Dictionary<string, float> values)
    {
        var value = values.TryGetValue(slider.Name, out var v) ? v * 100 : 0;
        var text = new TextBlock { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = value.ToString("0", CultureInfo.InvariantCulture) };
        var control = new System.Windows.Controls.Slider { Minimum = -100, Maximum = 100, Value = value, VerticalAlignment = VerticalAlignment.Center, TickFrequency = 5, IsSnapToTickEnabled = true };
        control.ValueChanged += (_, e) =>
        {
            text.Text = e.NewValue.ToString("0", CultureInfo.InvariantCulture);
            if (Math.Abs(e.NewValue) < 0.5) values.Remove(slider.Name);
            else values[slider.Name] = (float)(e.NewValue / 100);
            Changed?.Invoke();
        };
        var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
        var label = new TextBlock { Text = slider.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = slider.Name };
        DockPanel.SetDock(label, Dock.Left);
        DockPanel.SetDock(text, Dock.Right);
        row.Children.Add(label);
        row.Children.Add(text);
        row.Children.Add(control);
        return row;
    }

    /// <summary>
    /// The set whose shapes best fit the NIF: most shape names in common, then — read from the set's source
    /// mesh — matching vertex counts, which is what makes its offsets line up with the built mesh.
    /// </summary>
    private static SliderSet BestMatch(List<SliderSet> sets, IReadOnlyList<ShapeData> shapes)
    {
        int Score(SliderSet set)
        {
            var names = shapes.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var score = set.Shapes.Count(names.Contains) * 10;
            try
            {
                var source = Path.Combine(set.ShapeDataFolder, set.SourceFile);
                if (File.Exists(source))
                    foreach (var s in BodyMesh.Load(source))
                        if (shapes.Any(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase) && x.VertexCount == s.VertexCount))
                            score += 100;
            }
            catch
            {
                // an unreadable source just doesn't add to the score
            }
            return score;
        }
        return sets.OrderByDescending(Score).First();
    }
}
