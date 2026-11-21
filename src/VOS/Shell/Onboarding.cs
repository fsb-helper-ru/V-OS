using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using VOS.Core;
using VOS.Core.Perf;
using VOS.Core.Settings;
using VOS.Core.Weather;
using VOS.Platform.Windowing;
using VOS.UI;
using VOS.UI.Quality;
using VOS.UI.Surfaces;
using VOS.UI.Theme;
using Windows.Graphics;

namespace VOS.Shell;

/// <summary>First-run experience: appearance, location, performance and desktop mode, with sensible defaults throughout.</summary>
public sealed class Onboarding
{
    private readonly ShellHost _host;
    private readonly SurfaceWindow _window;
    private readonly Grid _stage = new() { Width = 600, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 48) };
    private int _step;
    private const int Steps = 6;
    private WeatherLocation? _location;

    public static void Show(ShellHost host) => new Onboarding(host).Start();

    private Onboarding(ShellHost host)
    {
        _host = host;
        _window = new SurfaceWindow("Welcome to V-OS", topmost: true, noActivate: false, backdrop: null);
        _window.Root.Background = Ui.B("Vos.Base");
        var glowHost = new Grid();
        _window.Root.Children.Add(glowHost);
        _window.Root.Children.Add(_stage);
        _window.Root.Children.Add(_dots);
        var m = Displays.Primary;
        _window.MoveAndResize(new RectInt32(m.Bounds.Left, m.Bounds.Top, m.Bounds.Width, m.Bounds.Height));
    }

    private void Start()
    {
        _window.Show(activate: true);
        Render();
    }

    private void Next() { _step = Math.Min(Steps - 1, _step + 1); Render(); }
    private void Back() { _step = Math.Max(0, _step - 1); Render(); }

    private void Render()
    {
        _stage.Children.Clear();
        _dots.Children.Clear();
        for (int i = 0; i < Steps; i++)
            _dots.Children.Add(new Border { Width = i == _step ? 22 : 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.B(i == _step ? "Vos.Accent" : "Vos.Stroke") });
        var page = _step switch
        {
            0 => Welcome(),
            1 => Appearance(),
            2 => Location(),
            3 => Performance(),
            4 => DesktopMode(),
            _ => Ready(),
        };
        _stage.Children.Add(page);
        Motion.Enter(page, new Vector3(0, 14, 0), 320);
    }

    private static TextBlock Title(string s) => new() { Text = s, FontFamily = Tokens.Display, FontWeight = FontWeights.SemiBold, FontSize = 30, Foreground = Ui.B("Vos.TextPrimary"), TextWrapping = TextWrapping.Wrap };
    private static TextBlock Lead(string s) => new() { Text = s, FontSize = 15, Foreground = Ui.B("Vos.TextSecondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 22), MaxWidth = 560, HorizontalAlignment = HorizontalAlignment.Left };

    private StackPanel Nav(string next = "Continue", Action? onNext = null, bool back = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 28, 0, 0) };
        if (back) row.Children.Add(Buttons.Subtle(Ui.Text("Back", 14), Back, 10, new Thickness(16, 9, 16, 9)));
        row.Children.Add(Buttons.Accent(Ui.Text(next, 14, "Vos.OnAccent", null, FontWeights.SemiBold), onNext ?? Next, 10, new Thickness(22, 9, 22, 9)));
        return row;
    }

    private UIElement Welcome()
    {
        var s = new StackPanel();
        s.Children.Add(new TextBlock { Text = "V-OS", FontFamily = Tokens.Instrument, FontWeight = FontWeights.Light, FontSize = 96, CharacterSpacing = -10, Foreground = Ui.B("Vos.TextPrimary") });
        s.Children.Add(Lead("A calmer desktop for Windows. Everything you use every day, rethought, with Windows still underneath, untouched and always one step away."));
        s.Children.Add(Nav("Get started", back: false));
        return s;
    }

    private UIElement Appearance()
    {
        var s = new StackPanel();
        s.Children.Add(Title("Choose a look"));
        s.Children.Add(Lead("You can change this any time in Settings."));
        var themes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var (mode, label) in new[] { (ThemeMode.Dark, "Dark"), (ThemeMode.Light, "Light"), (ThemeMode.Auto, "Match Windows") })
        {
            bool sel = _host.Settings.Current.Appearance.Theme == mode;
            var tile = new StackPanel { Spacing = 8 };
            var preview = new Border { Width = 160, Height = 96, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(sel ? 2 : 1), BorderBrush = Ui.B(sel ? "Vos.Accent" : "Vos.Stroke"),
                Background = new SolidColorBrush(mode == ThemeMode.Light ? Palette.Light.Base : mode == ThemeMode.Dark ? Palette.Dark.Base : Colors2.Mix(Palette.Light.Base, Palette.Dark.Base, 0.5)) };
            preview.Child = new Border { Width = 56, Height = 14, CornerRadius = new CornerRadius(7), Background = Ui.B("Vos.Ink"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) };
            tile.Children.Add(preview);
            tile.Children.Add(Ui.Text(label, 13));
            var b = Buttons.Subtle(tile, () => { _host.Settings.Update(x => x.Appearance.Theme = mode); Render(); }, 16, new Thickness(6));
            themes.Children.Add(b);
        }
        s.Children.Add(themes);
        s.Children.Add(new TextBlock { Text = "Accent", FontSize = 13, Foreground = Ui.B("Vos.TextSecondary"), Margin = new Thickness(0, 22, 0, 10) });
        var accents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var p in AccentPresets.All)
        {
            bool sel = _host.Settings.Current.Appearance.AccentSource == AccentSource.Preset && _host.Settings.Current.Appearance.AccentPreset == p.Id;
            var swatch = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(AccentPresets.Resolve(p.Id, ThemeService.Current.IsDark)), BorderThickness = new Thickness(sel ? 3 : 0), BorderBrush = Ui.B("Vos.TextPrimary") };
            var b = Buttons.Subtle(swatch, () => { _host.Settings.Update(x => { x.Appearance.AccentSource = AccentSource.Preset; x.Appearance.AccentPreset = p.Id; }); Render(); }, 20, new Thickness(2));
            ToolTipService.SetToolTip(b, p.Name);
            Buttons.AutomationName(b, p.Name);
            accents.Children.Add(b);
        }
        var windowsAccent = Buttons.Subtle(Ui.Text("Windows accent", 12.5), () => { _host.Settings.Update(x => x.Appearance.AccentSource = AccentSource.Windows); Render(); }, 16, new Thickness(12, 8, 12, 8));
        accents.Children.Add(windowsAccent);
        s.Children.Add(accents);
        s.Children.Add(Nav());
        return s;
    }

    private UIElement Location()
    {
        var s = new StackPanel();
        s.Children.Add(Title("Where are you?"));
        s.Children.Add(Lead("V-OS shows weather in the capsule, on the desktop and in the Weather app. Searching sends the city name to Open-Meteo; nothing else leaves your PC. You can skip this."));
        var box = new AutoSuggestBox { PlaceholderText = "Search for a city", QueryIcon = new SymbolIcon(Symbol.Find), Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var status = Ui.Text(_location != null ? $"Selected: {_location.Display}, {_location.Country}" : "", 13, "Vos.AccentText");
        status.Margin = new Thickness(0, 10, 0, 0);
        List<WeatherLocation> results = [];
        box.TextChanged += async (sender, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            try
            {
                results = await WeatherService.Shared.SearchAsync(sender.Text);
                sender.ItemsSource = results.Select(r => $"{r.Display}, {r.Country}").ToList();
            }
            catch { sender.ItemsSource = new[] { "Search is unavailable offline" }; }
        };
        box.SuggestionChosen += (_, args) =>
        {
            var idx = (box.ItemsSource as List<string>)?.IndexOf(args.SelectedItem as string ?? "") ?? -1;
            if (idx >= 0 && idx < results.Count) { _location = results[idx]; status.Text = $"Selected: {_location.Display}, {_location.Country}"; }
        };
        s.Children.Add(box);
        s.Children.Add(status);
        var units = new ComboBox { Header = "Temperature", ItemsSource = new[] { "Celsius", "Fahrenheit" }, SelectedIndex = _host.Settings.Current.Weather.TemperatureUnit == TemperatureUnit.Celsius ? 0 : 1, Margin = new Thickness(0, 16, 0, 0) };
        units.SelectionChanged += (_, _) => _host.Settings.Update(x => x.Weather.TemperatureUnit = units.SelectedIndex == 0 ? TemperatureUnit.Celsius : TemperatureUnit.Fahrenheit);
        s.Children.Add(units);
        s.Children.Add(Nav(_location == null ? "Skip" : "Continue", () =>
        {
            if (_location != null)
                _host.Settings.Update(x => { x.Weather.Locations = [_location]; x.Weather.SelectedIndex = 0; x.Privacy.WeatherNetworkConsent = true; });
            _ = _host.Weather.RefreshAsync(true);
            Next();
        }));
        return s;
    }

    private UIElement Performance()
    {
        var hw = _host.Quality.Hardware;
        var tier = QualityResolver.Classify(hw);
        var s = new StackPanel();
        s.Children.Add(Title("Performance"));
        s.Children.Add(Lead($"Detected: {hw.GpuName}, {hw.LogicalCores} threads, {Math.Round(hw.RamGb)} GB memory. Adaptive picks the best effects this PC can run smoothly, and steps down on battery or in games."));
        var list = new StackPanel { Spacing = 8 };
        foreach (var (p, text) in new[]
        {
            (PerformanceProfile.Adaptive, $"Adaptive · recommended ({(tier == HardwareTier.High ? "full effects" : tier == HardwareTier.Medium ? "balanced effects" : "light effects")} on this PC)"),
            (PerformanceProfile.Quality, "Quality · every effect, always"),
            (PerformanceProfile.Balanced, "Balanced · rich look, moderate use of resources"),
            (PerformanceProfile.Performance, "Performance · fewer effects, more headroom"),
            (PerformanceProfile.Minimal, "Minimal · lightest desktop"),
        })
        {
            var rb = new RadioButton { Content = text, GroupName = "perf", IsChecked = _host.Settings.Current.Performance.Profile == p, FontSize = 14 };
            rb.Checked += (_, _) => _host.Settings.Update(x => x.Performance.Profile = p);
            list.Children.Add(rb);
        }
        s.Children.Add(list);
        s.Children.Add(Nav());
        return s;
    }

    private UIElement DesktopMode()
    {
        var s = new StackPanel();
        s.Children.Add(Title("Set up your desktop"));
        s.Children.Add(Lead("Companion runs V-OS alongside the Windows taskbar. Immersive replaces the taskbar and desktop icons with the V-OS desktop. Both are reversible at any time; Ctrl+Alt+Shift+F12 always brings the Windows desktop back."));
        var companion = new RadioButton { Content = "Companion · recommended to start", GroupName = "mode", IsChecked = _host.Settings.Current.Shell.Mode == ShellMode.Companion, FontSize = 14 };
        var immersive = new RadioButton { Content = "Immersive · the full V-OS experience", GroupName = "mode", IsChecked = _host.Settings.Current.Shell.Mode == ShellMode.Immersive, FontSize = 14 };
        s.Children.Add(companion);
        s.Children.Add(immersive);
        var startup = new ToggleSwitch { Header = "Start V-OS when I sign in", IsOn = _host.Settings.Current.Shell.StartWithWindows, Margin = new Thickness(0, 18, 0, 0) };
        s.Children.Add(startup);
        s.Children.Add(Nav(onNext: () =>
        {
            _host.Settings.Update(x => { x.Shell.Mode = immersive.IsChecked == true ? ShellMode.Immersive : ShellMode.Companion; x.Shell.StartWithWindows = startup.IsOn; });
            Startup.Apply(startup.IsOn);
            Next();
        }));
        return s;
    }

    private UIElement Ready()
    {
        var s = new StackPanel();
        s.Children.Add(Title("You’re ready"));
        s.Children.Add(Lead("Press Alt+Space to search and run commands. Click the capsule for music, timers and alerts. Open the V menu for settings and power."));
        s.Children.Add(Nav("Enter V-OS", () =>
        {
            _host.Settings.Update(x => x.OnboardingCompleted = true);
            Motion.Fade(_window.Root, 0, 300);
            UiThread.Timer(TimeSpan.FromMilliseconds(Motion.Enabled ? 320 : 1), _window.Close, repeating: false).Start();
        }));
        return s;
    }
}

/// <summary>Per-user sign-in startup entry (HKCU Run). No elevation, removed cleanly on uninstall.</summary>
public static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "V-OS";

    public static void Apply(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k == null) return;
            if (enabled) k.SetValue(Name, $"\"{CommandRunner.VosExe}\" --shell");
            else if (k.GetValue(Name) != null) k.DeleteValue(Name);
        }
        catch (Exception ex) { Log.Warn("Could not update the sign-in startup entry", ex); }
    }
}
