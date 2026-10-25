using System.IO;
using System.Windows;

namespace SkinTextureTool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash(args.Exception);
            MessageBox.Show(args.Exception.Message, "Skin texture tool", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogCrash(args.ExceptionObject as Exception);

        base.OnStartup(e);
        var window = new MainWindow();
        // --export <folder> <nif>...: load the meshes, generate with the saved settings, export the textures and quit.
        // Hooked before Show, which can raise Loaded before it returns.
        if (e.Args.Length >= 3 && e.Args[0] == "--export")
            window.Loaded += async (_, _) => { await window.ExportHeadless(e.Args[1], e.Args.Skip(2).ToList()); Shutdown(); };
        window.Show();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SkinTextureTool");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTime.Now:u}\n{ex}\n\n");
        }
        catch
        {
            // nothing more to do
        }
    }
}
