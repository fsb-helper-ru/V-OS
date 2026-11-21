using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VOS.Core;
using VOS.Platform.Desktop;

namespace VOS;

/// <summary>
/// Entry point. Headless roles (watchdog, recovery, health check) run without starting XAML so they stay tiny;
/// UI roles (shell, app host, launcher) start the WinUI application.
/// </summary>
public static class Program
{
    public static string[] Args { get; private set; } = [];
    public static string Mode => Args.Length > 0 && Args[0].StartsWith("--", StringComparison.Ordinal) ? Args[0] : "--shell";

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        switch (Mode)
        {
            case "--watchdog": return Watchdog.Run(args);
            case "--recover":
                Log.Init("recovery");
                DesktopMode.Restore();
                Log.Flush();
                return 0;
            case "--health-check":
                // Used by the updater after installing a new version: proves the binaries load and the data folder is usable.
                Log.Init("health");
                Log.Info("Health check", new { AppInfo.Version });
                Log.Flush();
                return File.Exists(Path.Combine(AppContext.BaseDirectory, "VOS.dll")) ? 0 : 2;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}

/// <summary>
/// Waits for the shell process. If it ends without a clean-exit marker while immersive mode is active, restores the
/// Windows desktop and restarts the shell once. Consumes no CPU while waiting.
/// </summary>
public static class Watchdog
{
    public static string CleanExitMarker => Path.Combine(VosPaths.State, "shell-clean-exit");

    public static int Run(string[] args)
    {
        Log.Init("watchdog");
        if (args.Length < 2 || !int.TryParse(args[1], out int pid)) return 1;
        var restarts = new List<DateTime>();
        while (true)
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(); }
            catch (ArgumentException) { }
            bool clean = File.Exists(CleanExitMarker);
            if (clean) { Log.Info("Shell exited cleanly"); return 0; }
            Log.Warn("Shell ended unexpectedly; restoring the Windows desktop");
            DesktopMode.Restore();
            restarts.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(2));
            if (restarts.Count >= 2) { Log.Error("Shell crashed repeatedly; not restarting. Windows desktop restored."); Log.Flush(); return 3; }
            restarts.Add(DateTime.Now);
            try
            {
                var shell = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "VOS.exe"), "--shell --after-crash") { UseShellExecute = false });
                if (shell == null) return 4;
                pid = shell.Id;
            }
            catch (Exception ex) { Log.Error("Could not restart the shell", ex); Log.Flush(); return 5; }
        }
    }
}
