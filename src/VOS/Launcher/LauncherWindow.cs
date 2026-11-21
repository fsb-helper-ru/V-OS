using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VOS.Core;
using VOS.Platform.Desktop;
using VOS.UI;
using VOS.UI.Theme;

namespace VOS.Launcher;

/// <summary>V-OS Launcher (maintenance UI): status, open, recovery. Installation, updates and rollback live in Launcher/*.</summary>
public static partial class LauncherWindow
{
    public static void Start(string[] args)
    {
        var frame = new AppFrame(new VosApp("launcher", "V-OS Launcher", Glyphs.Apps, "#8EA2FF", "#4C5FD6", "Install, update and recover V-OS"), "V-OS Launcher", 640, 520, 760, 600);
        var root = new StackPanel { Padding = new Thickness(40, 24, 40, 32), Spacing = 14, MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(new TextBlock { Text = "V-OS", FontFamily = Tokens.Instrument, FontWeight = FontWeights.Light, FontSize = 56, Foreground = Ui.B("Vos.TextPrimary") });
        root.Children.Add(Ui.Text($"Version {AppInfo.Version}", 14, "Vos.TextSecondary"));
        var status = Ui.Text("", 13, "Vos.TextSecondary", null, null, TextWrapping.Wrap);
        root.Children.Add(status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(Buttons.Accent(Ui.Text("Open V-OS", 14, "Vos.OnAccent", null, FontWeights.SemiBold), () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "VOS.exe"), "--shell") { UseShellExecute = false })));
        actions.Children.Add(Buttons.Filled(Ui.Text("Restore Windows desktop", 14), () =>
        {
            bool restored = DesktopMode.Restore();
            status.Text = restored ? "Windows desktop restored: taskbar and desktop icons are visible." : "The Windows desktop is already in its normal state.";
        }));
        root.Children.Add(actions);
        frame.Body.Content = root;
        frame.Activate();
    }
}
