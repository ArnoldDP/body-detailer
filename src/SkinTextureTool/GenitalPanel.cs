using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SkinTextureTool;

/// <summary>
/// Patches a TNG genital add-on (such as TRX Futanari) for the NPC from Load NPC so its skin continues the body's
/// texture without a seam, and gives it to that NPC through its patch plugin. The baking and records are done by
/// the main window, which holds the load order and the loaded meshes.
/// </summary>
public sealed class GenitalPanel : StackPanel
{
    private static readonly string MaskFolder = Path.Combine(AppContext.BaseDirectory, "Genital masks");

    public ComboBox Addon { get; } = new() { Margin = new Thickness(0, 2, 0, 0) };
    public TextBox Mask { get; } = new();
    public CheckBox AttachHair { get; } = new() { Content = "Attach the hair mesh (below) to it, as one slot-52 piece", IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };
    public ComboBox Preset { get; } = new() { Margin = new Thickness(0, 2, 0, 0) };
    public CheckBox BuildAfter { get; } = new() { Content = "Build it with this preset after baking (no BodySlide needed)", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    public Button BuildNow { get; } = new() { Content = "Build now", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    public CheckBox ShowInPreview { get; } = new() { Content = "Show the add-on in the preview", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    public CheckBox Distribute { get; } = new() { Content = "Give it to the loaded NPC (patch plugin, slot 52)", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
    public Button Bake { get; } = new() { Content = "Bake and export", Padding = new Thickness(0, 6, 0, 6), Margin = new Thickness(0, 10, 0, 0), FontWeight = FontWeights.SemiBold };
    public TextBlock Info { get; } = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Image _preview = new() { MaxHeight = 360, Margin = new Thickness(0, 8, 0, 0), Stretch = Stretch.Uniform };

    public GenitalPanel()
    {
        Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8),
            Text = "Makes a genital add-on's skin continue the NPC's own body texture: the body's diffuse, normal, specular and "
                 + "subsurface maps are projected onto the add-on's texture layout and blended in through a mask (white: body skin). "
                 + "Load an NPC first. The result goes into the export folder: the textures, and with the option below, the NPC's "
                 + "patch plugin (a copy of the add-on's piece for its race with a new texture set, on its skin with TNG_SkinWithPenis).",
        });
        Children.Add(new TextBlock { Text = "Add-on (load an NPC to list them)" });
        Children.Add(Addon);
        Addon.SelectionChanged += (_, _) => SuggestMask();
        Children.Add(new TextBlock { Text = "Blend mask (in the add-on's texture layout)", Margin = new Thickness(0, 8, 0, 2) });
        var row = new DockPanel();
        var browse = new Button { Content = "…", Width = 26, Margin = new Thickness(4, 0, 0, 0) };
        browse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "Images (*.png;*.dds;*.tga;*.jpg)|*.png;*.dds;*.tga;*.jpg", InitialDirectory = Directory.Exists(MaskFolder) ? MaskFolder : null };
            if (dlg.ShowDialog() == true) Mask.Text = dlg.FileName;
        };
        DockPanel.SetDock(browse, Dock.Right);
        row.Children.Add(browse); row.Children.Add(Mask);
        Children.Add(row);
        Children.Add(new TextBlock { Text = "Leave it empty to project the body texture over the whole add-on.", Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        Children.Add(ShowInPreview);
        Children.Add(Distribute);
        Children.Add(AttachHair);
        Children.Add(new TextBlock { Text = "BodySlide preset (for building the add-on with the hair)", Margin = new Thickness(0, 8, 0, 0) });
        Children.Add(Preset);
        Children.Add(BuildAfter);
        Children.Add(BuildNow);
        Children.Add(Bake);
        Children.Add(Info);
        Children.Add(_preview);
    }

    /// <summary>Picks a mask from the masks folder whose name the add-on's plugin or name contains (TRX.png for TRX).</summary>
    private void SuggestMask()
    {
        if (Addon.SelectedItem is not { } item || !Directory.Exists(MaskFolder) || Mask.Text.Length > 0 && File.Exists(Mask.Text)) return;
        var label = item.ToString() ?? "";
        var match = Directory.EnumerateFiles(MaskFolder).FirstOrDefault(f => label.Contains(Path.GetFileNameWithoutExtension(f), StringComparison.OrdinalIgnoreCase));
        if (match != null) Mask.Text = match;
    }

    /// <summary>Shows a baked map (RGBA, row 0 at the top).</summary>
    public void ShowPreview(byte[] rgba, int size)
    {
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4) { bgra[i] = rgba[i + 2]; bgra[i + 1] = rgba[i + 1]; bgra[i + 2] = rgba[i]; bgra[i + 3] = 255; }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bgra, size * 4);
        bitmap.Freeze();
        _preview.Source = bitmap;
    }
}
