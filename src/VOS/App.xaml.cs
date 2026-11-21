using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VOS.Core;
using VOS.Core.Settings;
using VOS.Platform.Native;
using VOS.Platform.Sys;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        Log.Init(Program.Mode switch { "--app" => "apps", "--launcher" => "launcher", _ => "shell" });
        CrashReporter.Install();
        UnhandledException += (_, e) =>
        {
            CrashReporter.Report(e.Exception, fatal: false);
            // Keep the process alive for UI-level faults in a single view; the error is logged and the view recovers.
            e.Handled = Program.Mode != "--launcher";
        };
        UiThread.Queue = DispatcherQueue.GetForCurrentThread();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Application.Resources is only usable once the application is launched (not in the constructor).
            var settings = SettingsStore.Instance;
            ThemeService.Current.ApplySettings(settings.Current.Appearance, WindowsTheme.AppsUseLightTheme, WindowsTheme.AccentHex());
            ThemeService.Current.Install(Resources);
            switch (Program.Mode)
            {
                case "--app":
                    Win32.SetCurrentProcessExplicitAppUserModelID("VOS.Apps");
                    Apps.AppHost.Start(Program.Args.Skip(1).ToArray());
                    break;
                case "--glyph-test":
                    Shell.GlyphGallery.Show();
                    break;
                case "--launcher":
                    Win32.SetCurrentProcessExplicitAppUserModelID("VOS.Launcher");
                    Launcher.LauncherWindow.Start(Program.Args.Skip(1).ToArray());
                    break;
                default:
                    Win32.SetCurrentProcessExplicitAppUserModelID("VOS.Shell");
                    Shell.ShellHost.Start(Program.Args);
                    break;
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex, fatal: true);
            throw;
        }
    }
}
