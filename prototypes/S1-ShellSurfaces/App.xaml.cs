using System.Text.Json;
using Microsoft.UI.Xaml;

namespace S1;

public partial class App : Application
{
    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        string mode = argv.Length > 1 ? argv[1] : "--probe";
        string outDir = argv.Length > 2 ? argv[2] : Path.Combine(AppContext.BaseDirectory, "results");
        int seconds = argv.Length > 3 && int.TryParse(argv[3], out var s) ? s : 30;
        Directory.CreateDirectory(outDir);
        int exit = 0;
        try
        {
            Dictionary<string, object?>? result = mode switch
            {
                "--probe" => new() { ["probe"] = WriteProbe(outDir) },
                "--pill" => await Tests.Pill(outDir),
                "--dock" => await Tests.Dock(outDir),
                "--appbar" => await Tests.AppBar(outDir),
                "--transparency" => await Transparency.Run(outDir),
                _ => null,
            };
            if (mode is "--idle" or "--anim") await Tests.Ambient(outDir, seconds, mode == "--anim");
            if (result != null)
                File.WriteAllText(Path.Combine(outDir, mode.TrimStart('-') + ".json"),
                    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outDir, mode.TrimStart('-') + ".error.txt"), ex.ToString());
            exit = 1;
        }
        Environment.Exit(exit);
    }

    private static string WriteProbe(string outDir)
    {
        var path = Path.Combine(outDir, "probe.txt");
        File.WriteAllText(path, Probe.Dump());
        return path;
    }
}
