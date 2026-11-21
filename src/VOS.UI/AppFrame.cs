using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VOS.Core;
using VOS.Platform.Native;
using VOS.Platform.Shell;
using VOS.UI.Quality;
using VOS.UI.Theme;
using Windows.Graphics;

namespace VOS.UI;

/// <summary>
/// Window chrome shared by every V-OS app: custom title bar (icon, title, optional content), Windows caption buttons with
/// Snap Layouts, Mica or solid background by quality, minimum size, remembered placement, and a per-window app identity.
/// </summary>
public class AppFrame
{
    private sealed class Placement { public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; } public bool Maximized { get; set; } }

    public VosApp App { get; }
    public Window Window { get; }
    public AppWindow AppWindow => Window.AppWindow;
    public IntPtr Hwnd { get; }
    public Grid Root { get; }
    public Grid TitleBar { get; }
    public Grid TitleContent { get; }
    public ContentControl Body { get; }
    private readonly TextBlock _title;
    private readonly string _placementPath;

    public event Action? Closed;

    public AppFrame(VosApp app, string? title = null, double minWidth = 720, double minHeight = 480, double defaultWidth = 1120, double defaultHeight = 740)
    {
        App = app;
        Root = new Grid();
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ThemeService.Current.Track(Root);

        TitleBar = new Grid { Padding = new Thickness(14, 0, 0, 0), Background = new SolidColorBrush(Colors.Transparent) };
        TitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TitleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var icon = new AppIconView(app, 18) { VerticalAlignment = VerticalAlignment.Center };
        _title = Ui.Text(title ?? app.Name, 13, "Vos.TextSecondary", Tokens.Text);
        _title.VerticalAlignment = VerticalAlignment.Center;
        _title.Margin = new Thickness(10, 0, 16, 0);
        TitleContent = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(TitleContent, 2);
        TitleBar.Children.Add(icon);
        TitleBar.Children.Add(_title);
        TitleBar.Children.Add(TitleContent);
        Root.Children.Add(TitleBar);

        Body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        Grid.SetRow(Body, 1);
        Root.Children.Add(Body);

        Window = new Window { Content = Root, Title = title ?? app.Name, ExtendsContentIntoTitleBar = true };
        Window.SetTitleBar(TitleBar);
        Hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        ShellNative.SetWindowAppId(Hwnd, app.AppUserModelId);
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", app.Id + ".ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);

        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            double s = Win32.GetDpiForWindow(Hwnd) / 96.0;
            op.PreferredMinimumWidth = (int)(minWidth * s);
            op.PreferredMinimumHeight = (int)(minHeight * s);
        }

        _placementPath = Path.Combine(VosPaths.State, $"window-{app.Id}.json");
        RestorePlacement(defaultWidth, defaultHeight);
        ApplyLook();
        VisualQuality.Changed += ApplyLook;
        ThemeService.Current.Changed += ApplyLook;
        Window.Closed += (_, _) =>
        {
            VisualQuality.Changed -= ApplyLook;
            ThemeService.Current.Changed -= ApplyLook;
            SavePlacement();
            Closed?.Invoke();
        };
    }

    public string Title
    {
        get => _title.Text;
        set { _title.Text = value; Window.Title = value; }
    }

    private void ApplyLook()
    {
        bool mica = VisualQuality.Current.Mica;
        if (mica && Window.SystemBackdrop is not MicaBackdrop) Window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        if (!mica) Window.SystemBackdrop = null;
        Root.Background = mica ? new SolidColorBrush(Colors.Transparent) : ThemeService.Current.Brush("Vos.Base");
        var p = ThemeService.Current.Palette;
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = p.TextPrimary;
        tb.ButtonInactiveForegroundColor = p.TextTertiary;
        tb.ButtonHoverBackgroundColor = p.IsDark ? global::Windows.UI.Color.FromArgb(0x1A, 255, 255, 255) : global::Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        tb.ButtonHoverForegroundColor = p.TextPrimary;
        tb.ButtonPressedBackgroundColor = p.IsDark ? global::Windows.UI.Color.FromArgb(0x10, 255, 255, 255) : global::Windows.UI.Color.FromArgb(0x18, 0, 0, 0);
        int dark = p.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
    }

    private void RestorePlacement(double w, double h)
    {
        var saved = VosJson.TryDeserialize<Placement>(AtomicFile.TryReadAllText(_placementPath));
        var monitors = VOS.Platform.Windowing.Displays.All();
        if (saved != null && saved.W > 200 && saved.H > 150 &&
            monitors.Any(m => saved.X + 80 > m.WorkArea.Left && saved.X < m.WorkArea.Right - 80 && saved.Y >= m.WorkArea.Top - 10 && saved.Y < m.WorkArea.Bottom - 80))
        {
            AppWindow.MoveAndResize(new RectInt32(saved.X, saved.Y, saved.W, saved.H));
            if (saved.Maximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
            return;
        }
        var primary = VOS.Platform.Windowing.Displays.Primary;
        double s = primary.Scale;
        int pw = (int)Math.Min(w * s, primary.WorkArea.Width * 0.9), ph = (int)Math.Min(h * s, primary.WorkArea.Height * 0.9);
        AppWindow.MoveAndResize(new RectInt32(primary.WorkArea.Left + (primary.WorkArea.Width - pw) / 2, primary.WorkArea.Top + (primary.WorkArea.Height - ph) / 2, pw, ph));
    }

    private void SavePlacement()
    {
        try
        {
            bool max = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
            var p = new Placement { X = AppWindow.Position.X, Y = AppWindow.Position.Y, W = AppWindow.Size.Width, H = AppWindow.Size.Height, Maximized = max };
            AtomicFile.WriteAllText(_placementPath, VosJson.Serialize(p));
        }
        catch { }
    }

    public void Activate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } op) op.Restore();
        Window.Activate();
        Win32.Activate(Hwnd);
    }
}

/// <summary>Confirmation and message dialogs bound to a window's XamlRoot.</summary>
public static class Dialogs
{
    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm, string cancel = "Cancel", bool destructive = false)
    {
        var d = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = confirm,
            CloseButtonText = cancel,
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
            RequestedTheme = ThemeService.Current.ElementTheme,
        };
        if (!destructive) d.PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"];
        return await d.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task InfoAsync(XamlRoot root, string title, string message)
    {
        var d = new ContentDialog { XamlRoot = root, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 }, CloseButtonText = "OK", RequestedTheme = ThemeService.Current.ElementTheme };
        await d.ShowAsync();
    }
}
