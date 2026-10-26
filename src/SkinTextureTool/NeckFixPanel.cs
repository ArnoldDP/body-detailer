using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SkinTextureTool.Core.Mesh;
using SkinTextureTool.Core.NeckFix;

namespace SkinTextureTool;

/// <summary>
/// Reshapes the loaded head's neck to meet the body and exports the head NIF plus its FaceGen .tri files in
/// their game paths, ready to install as a mod. Applies to the player character, whose head is built from these
/// files at runtime; NPCs use pre-built FaceGen heads.
/// </summary>
public sealed class NeckFixPanel : StackPanel
{
    private static readonly string DefaultOutput = Path.Combine(AppContext.BaseDirectory, "Neck fix output");

    private readonly ComboBox _head = new() { DisplayMemberPath = nameof(ShapeData.Name), Margin = new Thickness(0, 2, 0, 0) };
    private readonly Slider _band = new() { Minimum = 1, Maximum = 10, Value = 4 };
    private readonly CheckBox _preview = new() { Content = "Show the reshaped neck in the preview (and bake textures from it)", Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBox _output = new() { Text = DefaultOutput };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private IReadOnlyList<ShapeData> _shapes = Array.Empty<ShapeData>();
    private bool _updating; // refilling the picker during a rebuild mustn't ask for another rebuild

    /// <summary>Raised when the preview needs rebuilding (head, band or preview toggle changed).</summary>
    public event Action? Changed;

    public NeckFixPanel()
    {
        Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8),
            Text = "Bends the bottom of the head's neck so it meets the body's neck seam without a crease, then writes the head "
                 + "NIF and every matching FaceGen .tri from the head's mod (expressions, races, character-creation and RaceMenu "
                 + "morphs) with the same file paths. Install the output folder as a mod that overrides the head mod.\n\n"
                 + "This fixes the player character. NPCs use pre-built FaceGen heads and keep the old neck until FaceGen is "
                 + "regenerated. Re-run it if you change the body's neck shape in BodySlide.",
        });
        Children.Add(new TextBlock { Text = "Head" });
        Children.Add(_head);
        var band = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var label = new TextBlock { Text = "Band height", Width = 90, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = "4.0" };
        _band.ValueChanged += (_, e) => { value.Text = e.NewValue.ToString("0.0", CultureInfo.InvariantCulture); if (_preview.IsChecked == true) Changed?.Invoke(); };
        DockPanel.SetDock(label, Dock.Left); DockPanel.SetDock(value, Dock.Right);
        band.Children.Add(label); band.Children.Add(value); band.Children.Add(_band);
        Children.Add(band);
        Children.Add(new TextBlock { Text = "How far up the neck the head is bent to follow the body. Higher is smoother but reshapes more of the neck.", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        _preview.Checked += (_, _) => Changed?.Invoke();
        _preview.Unchecked += (_, _) => Changed?.Invoke();
        _head.SelectionChanged += (_, _) => { if (!_updating && _preview.IsChecked == true) Changed?.Invoke(); };
        Children.Add(_preview);

        Children.Add(new TextBlock { Text = "Output folder (stands in for Data)", Margin = new Thickness(0, 10, 0, 2) });
        var row = new DockPanel();
        var browse = new Button { Content = "…", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Folder for the fixed head files" };
            if (dlg.ShowDialog() == true) _output.Text = dlg.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_output);
        Children.Add(row);
        var export = new Button { Content = "Export fixed head", Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
        export.Click += async (_, _) => await ExportAsync();
        Children.Add(export);
        Children.Add(_info);
    }

    /// <summary>The loaded shapes; heads are offered in the picker, the largest shape is the body.</summary>
    public void SetShapes(IReadOnlyList<ShapeData> shapes)
    {
        var previous = _head.SelectedItem as ShapeData;
        _shapes = shapes;
        var body = Body;
        // offer heads only when any are loaded; other parts (genitals, hands) aren't what this fixes
        var named = shapes.Where(s => s != body && (s.Name.Contains("head", StringComparison.OrdinalIgnoreCase)
                                                    || Path.GetFileName(s.NifPath).Contains("head", StringComparison.OrdinalIgnoreCase))).ToList();
        var heads = named.Count > 0 ? named : shapes.Where(s => s != body).ToList();
        _updating = true;
        _head.ItemsSource = heads;
        _head.SelectedItem = heads.Contains(previous!) ? previous : heads.FirstOrDefault();
        _updating = false;
    }

    private ShapeData? Body => _shapes.Count == 0 ? null : _shapes.MaxBy(s => s.VertexCount);

    /// <summary>The head with its reshaped positions when the preview toggle is on, for the mesh rebuild.</summary>
    public ShapeData Apply(ShapeData shape)
    {
        if (_preview.IsChecked != true || shape != _head.SelectedItem || Body is not { } body) return shape;
        var r = NeckReshaper.Reshape(shape, body, (float)_band.Value);
        return new ShapeData
        {
            Name = shape.Name, NifPath = shape.NifPath, Positions = r.Positions, Uvs = shape.Uvs, Indices = shape.Indices,
            DiffusePath = shape.DiffusePath, TexturePaths = shape.TexturePaths, ModelSpaceNormals = shape.ModelSpaceNormals,
        };
    }

    private async Task ExportAsync()
    {
        if (_head.SelectedItem is not ShapeData head || Body is not { } body)
        {
            _info.Text = "Load the body and the head first.";
            return;
        }
        var output = _output.Text.Trim();
        var band = (float)_band.Value;
        _info.Text = "Reshaping and writing…";
        try
        {
            var (result, report) = await Task.Run(() =>
            {
                var r = NeckReshaper.Reshape(head, body, band);
                return (r, r.SeamVertices == 0 ? null : HeadExporter.Export(head, r.Positions, output, r.Merges));
            });
            if (report == null)
            {
                _info.Text = $"{head.Name}'s neck edge doesn't touch {body.Name}'s neck edge, so there's nothing to fix. Are they built for each other?";
                return;
            }
            _info.Text = $"Moved {result.MovedVertices} vertices ({result.SeamVertices} on the seam, {result.Merges.Count} merged into a neighbour so the edge matches the body's points; at most {result.LargestMove:0.00} units).\n"
                       + $"Wrote {Path.GetRelativePath(output, report.NifPath)}\n"
                       + $"and {report.TriPaths.Count} .tri file(s):\n  " + string.Join("\n  ", report.TriPaths.Select(p => Path.GetRelativePath(output, p)))
                       + (report.Skipped.Count > 0 ? "\nSkipped:\n  " + string.Join("\n  ", report.Skipped) : "")
                       + $"\n\nInstall {output} as a mod below the head mod in MO2's left pane so it wins.";
        }
        catch (Exception ex)
        {
            _info.Text = "Export failed: " + ex.Message;
        }
    }
}
