using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkinTextureTool.Core.Patcher;

namespace SkinTextureTool;

/// <summary>Search the load order's NPCs by name, editor ID, race or plugin and pick one.</summary>
public sealed class NpcPickerWindow : Window
{
    private readonly TextBox _search = new() { Margin = new Thickness(0, 4, 0, 6) };
    private readonly ListBox _list = new();
    private readonly IReadOnlyList<NpcAssets.Entry> _all;

    public NpcAssets.Entry? Picked { get; private set; }

    public NpcPickerWindow(IReadOnlyList<NpcAssets.Entry> npcs)
    {
        _all = npcs;
        Title = "Load an NPC";
        Width = 640; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(10) };
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Search by name, editor ID, race or plugin:" });
        top.Children.Add(_search);
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var note = new TextBlock
        {
            Text = "NPCs sharing a texture (most use the default body and head) change together; custom skins are theirs alone.",
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Width = 380, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var load = new Button { Content = "Load", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        bottom.Children.Add(note); bottom.Children.Add(load); bottom.Children.Add(cancel);
        DockPanel.SetDock(bottom, Dock.Bottom);
        panel.Children.Add(bottom);
        panel.Children.Add(_list);
        Content = panel;

        _search.TextChanged += (_, _) => Filter();
        load.Click += (_, _) => Accept();
        _list.MouseDoubleClick += (_, _) => Accept();
        _search.PreviewKeyDown += (_, e) => { if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = 0; ((ListBoxItem?)_list.ItemContainerGenerator.ContainerFromIndex(0))?.Focus(); } };
        Loaded += (_, _) => _search.Focus();
        Filter();
    }

    private void Filter()
    {
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _list.ItemsSource = _all.Where(e => words.All(w => e.ToString().Contains(w, StringComparison.OrdinalIgnoreCase))).Take(500).ToList();
    }

    private void Accept()
    {
        if (_list.SelectedItem is not NpcAssets.Entry e) return;
        Picked = e;
        DialogResult = true;
    }
}
