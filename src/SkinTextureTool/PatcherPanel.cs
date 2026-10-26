using System.IO.Compression;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SkinTextureTool.Core.Patcher;

namespace SkinTextureTool;

/// <summary>
/// The load order patcher: reads a Mod Organizer 2 instance (enabled mods, plugins, archives) and reports which
/// heads, bodies, hands and feet each race uses and which NPC FaceGen heads could be fitted to their body.
/// Phase 1 is a dry run: it writes nothing but the report.
/// </summary>
public sealed class PatcherPanel : DockPanel
{
    private readonly TextBox _mo2 = new();
    private readonly TextBox _report = new()
    {
        IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 11, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 0),
    };
    private readonly Button _scan = new() { Content = "Scan load order", Padding = new Thickness(12, 4, 12, 4), FontWeight = FontWeights.SemiBold };
    private readonly Button _save = new() { Content = "Save report…", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
    private readonly Button _fit = new() { Content = "Fit NPC FaceGen heads", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
    private readonly Slider _neckBand = new() { Minimum = 1, Maximum = 10, Value = 4 };
    private readonly Slider _limbBand = new() { Minimum = 0, Maximum = 8, Value = 3 };
    private readonly CheckBox _skipBeasts = new() { Content = "Leave beast races alone (Khajiit, Argonian heads, NPCs, hands and feet)", IsChecked = true, Margin = new Thickness(0, 4, 0, 2) };
    private readonly CheckBox _zip = new() { Content = "Also zip the output (ready to install with MO2 or share)", Margin = new Thickness(0, 2, 0, 6) };
    private readonly TextBox _output = new() { Text = Path.Combine(AppContext.BaseDirectory, "Load order patch output") };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The MO2 folder, kept between sessions.</summary>
    public string Mo2Folder { get => _mo2.Text.Trim(); set => _mo2.Text = value; }

    public PatcherPanel()
    {
        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8),
            Text = "Reads your Mod Organizer 2 setup directly (enabled mods in priority order, enabled plugins, archives), so it "
                 + "doesn't need launching through MO2. It finds the head, body, hands and feet every race uses and checks every "
                 + "NPC's FaceGen head. \"Fit NPC FaceGen heads\" then bends each NPC's neck to meet the body it wears (blended "
                 + "for the NPC's weight, as the game draws it) and writes the heads into the output folder at their game paths. "
                 + "Install that folder as a mod at the very bottom of MO2's left pane, and re-run after changing bodies, heads "
                 + "or NPC replacers.",
        });
        top.Children.Add(new TextBlock { Text = "Mod Organizer 2 folder (the one with ModOrganizer.ini)" });
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        var browse = new Button { Content = "…", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Your Mod Organizer 2 folder" };
            if (dlg.ShowDialog() == true) _mo2.Text = dlg.FolderName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse);
        row.Children.Add(_mo2);
        top.Children.Add(row);
        top.Children.Add(BandRow("Neck bend height", _neckBand, "How far up the neck player and NPC heads are bent to follow the body."));
        top.Children.Add(BandRow("Wrist/ankle bend", _limbBand, "How far into the hands and feet they're bent to follow the arms and legs. 0 leaves them alone."));
        top.Children.Add(_skipBeasts);
        top.Children.Add(_zip);
        top.Children.Add(new TextBlock { Text = "Output folder (stands in for Data)", Margin = new Thickness(0, 6, 0, 0) });
        var outRow = new DockPanel { Margin = new Thickness(0, 2, 0, 8) };
        var outBrowse = new Button { Content = "…", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        outBrowse.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog { Title = "Folder for the patched meshes" };
            if (dlg.ShowDialog() == true) _output.Text = dlg.FolderName;
        };
        DockPanel.SetDock(outBrowse, Dock.Right);
        outRow.Children.Add(outBrowse);
        outRow.Children.Add(_output);
        top.Children.Add(outRow);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_scan);
        buttons.Children.Add(_fit);
        buttons.Children.Add(_save);
        buttons.Children.Add(_status);
        top.Children.Add(buttons);
        SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(_report);

        _mo2.Text = Mo2Instance.FindFromProcess() ?? "";
        _scan.Click += async (_, _) => await ScanAsync();
        _fit.Click += async (_, _) => await FitAsync();
        _save.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "load order scan.txt" };
            if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, _report.Text);
        };
    }

    /// <summary>
    /// Zips the output's meshes (and the marker) into "&lt;folder name&gt;.zip" next to the output folder, replacing an
    /// older zip. MO2's meta.ini stays out, so the archive installs as a fresh mod.
    /// </summary>
    private static string ZipOutput(string output)
    {
        var full = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar);
        var zip = Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileName(full) + ".zip");
        if (File.Exists(zip)) File.Delete(zip);
        using var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(full, file);
            if (rel.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)) continue;
            archive.CreateEntryFromFile(file, rel.Replace(Path.DirectorySeparatorChar, '/'), System.IO.Compression.CompressionLevel.Optimal);
        }
        return zip;
    }

    /// <summary>Beast races and zipping, kept between sessions.</summary>
    public (bool SkipBeasts, bool Zip) Options { get => (_skipBeasts.IsChecked == true, _zip.IsChecked == true); set { _skipBeasts.IsChecked = value.SkipBeasts; _zip.IsChecked = value.Zip; } }

    /// <summary>The bend heights, kept between sessions.</summary>
    public (double Neck, double Limb) Bands { get => (_neckBand.Value, _limbBand.Value); set { _neckBand.Value = value.Neck; _limbBand.Value = value.Limb; } }

    private static UIElement BandRow(string label, Slider slider, string help)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
        var row = new DockPanel();
        var name = new TextBlock { Text = label, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var value = new TextBlock { Width = 36, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = slider.Value.ToString("0.0") };
        slider.ValueChanged += (_, e) => value.Text = e.NewValue.ToString("0.0");
        DockPanel.SetDock(name, Dock.Left); DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(name); row.Children.Add(value); row.Children.Add(slider);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = help, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private async Task FitAsync()
    {
        var folder = Mo2Folder; var output = _output.Text.Trim();
        if (!File.Exists(Path.Combine(folder, "ModOrganizer.ini"))) { _status.Text = "That folder has no ModOrganizer.ini."; return; }
        if (output.Length == 0) { _status.Text = "Choose an output folder."; return; }
        _scan.IsEnabled = _fit.IsEnabled = false;
        try
        {
            // a fresh output each run (as the piercing distributor does): everything the patcher writes lives under
            // meshes, so that's cleared; the folder's other files (MO2's meta.ini…) stay. Only a folder this tool made
            // (it carries the marker) or one without meshes yet is cleared, so pointing it at another mod by mistake
            // can't delete that mod's meshes.
            var meshes = Path.Combine(output, "meshes");
            if (Directory.Exists(meshes))
            {
                if (!File.Exists(Path.Combine(output, Mo2Instance.OutputMarker)))
                {
                    _status.Text = "That folder already has meshes that this tool didn't write. Choose an empty folder or this tool's previous output.";
                    return;
                }
                _status.Text = "Clearing the previous output…";
                await Task.Run(() => Directory.Delete(meshes, true));
            }
            float neck = (float)_neckBand.Value, limb = (float)_limbBand.Value;
            var skipBeasts = _skipBeasts.IsChecked == true;
            var summary = await Task.Run(() => NpcBatch.Run(folder, output, neck, m => Dispatcher.BeginInvoke(() => _status.Text = m), limb, skipBeasts));
            _report.Text = summary.ToText();
            _status.Text = $"Wrote {summary.Written} heads to {output}.";
            if (_zip.IsChecked == true)
            {
                _status.Text = "Zipping the output…";
                var zip = await Task.Run(() => ZipOutput(output));
                _status.Text = $"Wrote {summary.Written} heads to {output}, zipped to {zip}.";
            }
            _save.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _report.Text = ex.ToString();
            _status.Text = "Fitting failed.";
        }
        finally
        {
            _scan.IsEnabled = _fit.IsEnabled = true;
        }
    }

    private async Task ScanAsync()
    {
        var folder = Mo2Folder;
        if (!File.Exists(Path.Combine(folder, "ModOrganizer.ini")))
        {
            _status.Text = "That folder has no ModOrganizer.ini.";
            return;
        }
        _scan.IsEnabled = false;
        _report.Text = "";
        var started = DateTime.Now;
        try
        {
            var report = await Task.Run(() => LoadOrderScan.Run(folder, m => Dispatcher.BeginInvoke(() => _status.Text = m.Length > 90 ? m[..90] + "…" : m)));
            _report.Text = report.ToText();
            _status.Text = $"Done in {(DateTime.Now - started).TotalSeconds:0} s.";
            _save.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _report.Text = ex.ToString();
            _status.Text = "The scan failed.";
        }
        finally
        {
            _scan.IsEnabled = true;
        }
    }
}
