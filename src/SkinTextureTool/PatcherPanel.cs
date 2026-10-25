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
                 + "NPC's FaceGen head against its race's head mesh. This first version only reports; nothing is written.",
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
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_scan);
        buttons.Children.Add(_save);
        buttons.Children.Add(_status);
        top.Children.Add(buttons);
        SetDock(top, Dock.Top);
        Children.Add(top);
        Children.Add(_report);

        _mo2.Text = Mo2Instance.FindFromProcess() ?? "";
        _scan.Click += async (_, _) => await ScanAsync();
        _save.Click += (_, _) =>
        {
            var dlg = new SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "load order scan.txt" };
            if (dlg.ShowDialog() == true) File.WriteAllText(dlg.FileName, _report.Text);
        };
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
