using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VOS.Platform.Native;
using VOS.UI.Theme;
using Windows.Graphics;

namespace VOS.UI.Surfaces;

/// <summary>Creates the Windows.System DispatcherQueue the OS compositor needs on the XAML thread.</summary>
public static class SystemDispatcher
{
    [StructLayout(LayoutKind.Sequential)] private struct Options { public int dwSize, threadType, apartmentType; }
    [DllImport("CoreMessaging.dll")] private static extern int CreateDispatcherQueueController(Options o, out IntPtr controller);
    private static IntPtr s_controller;

    public static void Ensure()
    {
        if (global::Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        var o = new Options { dwSize = Marshal.SizeOf<Options>(), threadType = 2, apartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(o, out s_controller));
    }
}

/// <summary>Fully transparent window background (ADR-015). Pair with DwmExtendFrameIntoClientArea.</summary>
public sealed class TransparentBackdrop : SystemBackdrop
{
    private static global::Windows.UI.Composition.Compositor? s_compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        SystemDispatcher.Ensure();
        s_compositor ??= new global::Windows.UI.Composition.Compositor();
        connectedTarget.SystemBackdrop = s_compositor.CreateColorBrush(new global::Windows.UI.Color());
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}

/// <summary>
/// Acrylic that stays "active" on surfaces that never take focus (ADR-015) and follows the V-OS palette.
/// Tint and luminosity come from the current <see cref="Palette"/>; theme changes update live.
/// </summary>
public sealed class ShellAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _config;
    private readonly bool _thin;

    public ShellAcrylicBackdrop(bool thin = false)
    {
        _thin = thin;
        ThemeService.Current.Changed += Retint;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _config = new SystemBackdropConfiguration { IsInputActive = true };
        _controller = new DesktopAcrylicController { Kind = _thin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base };
        Retint();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_config);
    }

    private void Retint()
    {
        if (_controller == null || _config == null) return;
        var p = ThemeService.Current.Palette;
        _config.Theme = p.IsDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _controller.TintColor = p.GlassTint;
        _controller.TintOpacity = (float)p.GlassTintOpacity;
        _controller.LuminosityOpacity = (float)p.GlassLuminosity;
        _controller.FallbackColor = p.IsDark ? Colors2.Hex("#1A1B20") : Colors2.Hex("#F7F8FA");
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}

/// <summary>
/// A shell surface: borderless, transparent, optionally non-activating/topmost, hidden from Alt+Tab (ADR-015).
/// Bounds are in physical pixels; content is laid out in logical pixels (DPI-aware via <see cref="Scale"/>).
/// </summary>
public class SurfaceWindow
{
    public Window Window { get; }
    public AppWindow AppWindow => Window.AppWindow;
    public IntPtr Hwnd { get; }
    public Grid Root { get; }
    public RectInt32 Bounds { get; private set; }
    public bool IsVisible { get; private set; }
    private readonly WindowSubclass _frameless;
    public double Scale => Root.XamlRoot?.RasterizationScale ?? (Win32.GetDpiForWindow(Hwnd) / 96.0);

    public SurfaceWindow(string title, bool topmost = true, bool noActivate = true, SystemBackdrop? backdrop = null)
    {
        Root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        Window = new Window { Content = Root, Title = title, SystemBackdrop = backdrop ?? new TransparentBackdrop() };
        Hwnd = Win32Interop.GetWindowFromWindowId(Window.AppWindow.Id);
        ThemeService.Current.Track(Root);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = topmost;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        long style = Win32.Style(Hwnd);
        style &= ~(Win32.WS_CAPTION | Win32.WS_THICKFRAME | Win32.WS_SYSMENU | Win32.WS_MINIMIZEBOX | Win32.WS_MAXIMIZEBOX);
        style |= Win32.WS_POPUP;
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_STYLE, (IntPtr)style);
        long ex = Win32.ExStyle(Hwnd) | Win32.WS_EX_TOOLWINDOW;
        if (noActivate) ex |= Win32.WS_EX_NOACTIVATE;
        ex &= ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE, (IntPtr)ex);
        // WinUI re-applies frame styles (WS_DLGFRAME, WS_SYSMENU) whenever the presenter syncs, which brings back a
        // 3 px classic frame around the content (visible as a light line, and it shrinks the content). Claiming the
        // whole window as client area makes a frame impossible, whatever the styles say.
        _frameless = new WindowSubclass(Hwnd, (msg, w, _) => msg == 0x0083 /*WM_NCCALCSIZE*/ && w != IntPtr.Zero ? IntPtr.Zero : null);
        int v = 1; Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_NCRENDERING_POLICY, ref v, 4);
        v = 1; Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
        v = unchecked((int)0xFFFFFFFE); Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_BORDER_COLOR, ref v, 4);
        var m = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(Hwnd, ref m);
        Win32.SetWindowPos(Hwnd, topmost ? Win32.HWND_TOPMOST : IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED | (topmost ? 0 : Win32.SWP_NOZORDER));
    }

    public void MoveAndResize(RectInt32 r)
    {
        Bounds = r;
        AppWindow.MoveAndResize(r);
    }

    public void Show(bool activate = false)
    {
        AppWindow.Show(activate);
        IsVisible = true;
        if (activate) Win32.Activate(Hwnd);
    }

    public void Hide()
    {
        AppWindow.Hide();
        IsVisible = false;
    }

    /// <summary>DWM cloaking: the window stays shown (it lays out and renders) but nothing of it reaches the screen.</summary>
    public void SetCloaked(bool cloaked)
    {
        int v = cloaked ? 1 : 0;
        Win32.DwmSetWindowAttribute(Hwnd, 13 /*DWMWA_CLOAK*/, ref v, 4);
    }

    /// <summary>
    /// Completes once content rendered after <see cref="Show"/> has reached the screen: XAML renders a frame, commits it,
    /// and DWM composes it on its next pass. Uncloaking earlier shows the window's (black) redirection surface.
    /// </summary>
    public async Task PresentedAsync()
    {
        await UiThread.NextFrameAsync(2);
        await Task.Run(() => { Win32.DwmFlush(); Win32.DwmFlush(); });
    }

    public void SetTopmost(bool topmost) =>
        Win32.SetWindowPos(Hwnd, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

    /// <summary>Click-through outside the given shapes (logical px). Pass nothing to clear the region.</summary>
    public void SetHitRegion(params (double x, double y, double w, double h, double r)[] shapes)
    {
        if (shapes.Length == 0) { Win32.SetWindowRgn(Hwnd, IntPtr.Zero, true); return; }
        double s = Scale;
        Win32.SetRoundedRegion(Hwnd, shapes.Select(z => ((int)Math.Floor(z.x * s) - 2, (int)Math.Floor(z.y * s) - 2,
            (int)Math.Ceiling(z.w * s) + 4, (int)Math.Ceiling(z.h * s) + 4, (int)Math.Round(z.r * s) + 2)).ToArray());
    }

    public void Close() => Window.Close();
}

/// <summary>Glass card: real acrylic (SystemBackdropElement) when quality allows, tinted solid otherwise, with a hairline rim.</summary>
public sealed class GlassSurface : Grid
{
    private readonly SystemBackdropElement _glass;
    private readonly Border _tint;

    public GlassSurface(double radius, bool thin = false)
    {
        _glass = new SystemBackdropElement { CornerRadius = new CornerRadius(radius), SystemBackdrop = new ShellAcrylicBackdrop(thin) };
        _tint = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1) };
        Children.Add(_glass);
        Children.Add(_tint);
        Radius = radius;
        Quality.VisualQuality.Changed += Apply;
        ThemeService.Current.Changed += Apply;
        Apply();
    }

    public double Radius { get; }

    private void Apply()
    {
        bool glass = Quality.VisualQuality.Current.Glass;
        _glass.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        _tint.Background = glass ? null : ThemeService.Current.Brush("Vos.GlassSolid");
        _tint.BorderBrush = ThemeService.Current.Brush("Vos.GlassRim");
    }
}
