using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace S1;

/// <summary>Creates the Windows.System DispatcherQueue the OS compositor needs on the XAML thread.</summary>
internal static class SystemDispatcher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }
    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    private static IntPtr s_controller;

    public static void Ensure()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() != null) return;
        var o = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2, apartmentType = 2 };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(o, out s_controller));
    }
}

/// <summary>Fully transparent window background: DWM composes the XAML content over the desktop with per-pixel alpha.</summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? s_compositor;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        SystemDispatcher.Ensure();
        s_compositor ??= new Windows.UI.Composition.Compositor();
        connectedTarget.SystemBackdrop = s_compositor.CreateColorBrush(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 });
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}

/// <summary>Acrylic that keeps its active appearance on surfaces that never take focus (dock, bar, capsule panels).</summary>
internal sealed class ShellAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    public Windows.UI.Color Tint { get; init; } = new() { A = 255, R = 18, G = 20, B = 26 };
    public float TintOpacity { get; init; } = 0.45f;
    public float LuminosityOpacity { get; init; } = 0.70f;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _controller = new DesktopAcrylicController
        {
            TintColor = Tint,
            TintOpacity = TintOpacity,
            LuminosityOpacity = LuminosityOpacity,
            FallbackColor = Tint,
        };
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration { IsInputActive = true, Theme = SystemBackdropTheme.Dark });
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }
}

/// <summary>Borderless, topmost, non-activating, hidden-from-switchers surface window (the shell building block).</summary>
internal sealed class SurfaceWindow
{
    public Window Window { get; }
    public AppWindow AppWindow => Window.AppWindow;
    public IntPtr Hwnd { get; }
    public RectInt32 Bounds { get; private set; }

    public SurfaceWindow(UIElement content, RectInt32 bounds, SystemBackdrop? backdrop, bool topmost = true, bool noActivate = true)
    {
        Window = new Window { Content = content };
        if (backdrop != null) Window.SystemBackdrop = backdrop;
        Hwnd = Win32Interop.GetWindowFromWindowId(Window.AppWindow.Id);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = topmost;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        long style = (long)Native.GetWindowLongPtr(Hwnd, Native.GWL_STYLE);
        style &= ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
        style |= Native.WS_POPUP;
        Native.SetWindowLongPtr(Hwnd, Native.GWL_STYLE, (IntPtr)style);
        long ex = (long)Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE);
        ex |= Native.WS_EX_TOOLWINDOW;
        if (noActivate) ex |= Native.WS_EX_NOACTIVATE;
        ex &= ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE, (IntPtr)ex);
        int v = 1; Native.DwmSetWindowAttribute(Hwnd, 2, ref v, 4);           // DWMWA_NCRENDERING_POLICY = DISABLED (no shadow/border)
        v = 1; Native.DwmSetWindowAttribute(Hwnd, 33, ref v, 4);               // DWMWA_WINDOW_CORNER_PREFERENCE = DONOTROUND
        v = unchecked((int)0xFFFFFFFE); Native.DwmSetWindowAttribute(Hwnd, 34, ref v, 4); // DWMWA_BORDER_COLOR = NONE
        Native.SetWindowPos(Hwnd, topmost ? Native.HWND_TOPMOST : IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | (topmost ? 0 : Native.SWP_NOZORDER));
        // The top-level WinUI window is redirected: DWM honours its alpha only once the frame covers the client area.
        if (backdrop != null) Transparency.ExtendFrame(Hwnd);
        MoveAndResize(bounds);
    }

    public long ExStyle => (long)Native.GetWindowLongPtr(Hwnd, Native.GWL_EXSTYLE);
    public void MoveAndResize(RectInt32 r) { Bounds = r; AppWindow.MoveAndResize(r); }
    public void Show() => AppWindow.Show(false);
    public void Close() => Window.Close();
}

internal static class Palette
{
    public static Windows.UI.Color Rgb(byte r, byte g, byte b, byte a = 255) => ColorHelper.FromArgb(a, r, g, b);
    public static SolidColorBrush Brush(byte r, byte g, byte b, byte a = 255) => new(Rgb(r, g, b, a));
}

/// <summary>High-contrast stripe pattern placed under a test surface so transparency and blur are measurable.</summary>
internal sealed class PatternSurface
{
    public SurfaceWindow Surface { get; }

    public PatternSurface(RectInt32 bounds, Windows.UI.Color? solid = null)
    {
        var root = new Grid { Background = solid is { } s ? new SolidColorBrush(s) : Palette.Brush(232, 236, 244) };
        if (solid == null)
        {
            var stripes = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < bounds.Width / 16 + 1; i++)
                stripes.Children.Add(new Border { Width = 8, Margin = new Thickness(0, 0, 8, 0), Background = Palette.Brush(84, 108, 150) });
            root.Children.Add(stripes);
        }
        Surface = new SurfaceWindow(root, bounds, backdrop: null);
    }
}

/// <summary>V-Pill capsule: ink shape driven entirely by compositor springs and expressions.</summary>
internal sealed class PillSurface
{
    public const int W = 600, H = 220;
    public static readonly Vector2 Collapsed = new(184, 30), Expanded = new(520, 148);
    public const float Top = 6;
    public SurfaceWindow Surface { get; }
    public CompositionRoundedRectangleGeometry Geometry { get; }
    private readonly Compositor _c;
    private readonly TextBlock _small;
    private readonly StackPanel _large;

    public PillSurface(RectInt32 bounds)
    {
        var host = new Grid();
        _small = new TextBlock
        {
            Text = "21:47   4°  Cloudy",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 13,
            Foreground = Palette.Brush(236, 238, 242),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Top + 6, 0, 0),
        };
        _large = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Top + 30, 0, 0),
            Spacing = 4,
        };
        _large.Children.Add(new TextBlock { Text = "Now playing", FontSize = 12, Foreground = Palette.Brush(150, 156, 168) });
        _large.Children.Add(new TextBlock { Text = "Compositor spring morph", FontFamily = new FontFamily("Segoe UI Variable Display"), FontSize = 22, Foreground = Palette.Brush(240, 242, 246) });
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.Children.Add(host);
        root.Children.Add(_small);
        root.Children.Add(_large);
        Surface = new SurfaceWindow(root, bounds, new TransparentBackdrop());

        _c = ElementCompositionPreview.GetElementVisual(host).Compositor;
        Geometry = _c.CreateRoundedRectangleGeometry();
        Geometry.Size = Collapsed;
        Geometry.CornerRadius = new Vector2(Collapsed.Y / 2);
        var shape = _c.CreateSpriteShape(Geometry);
        shape.FillBrush = _c.CreateColorBrush(Palette.Rgb(11, 12, 15));
        var visual = _c.CreateShapeVisual();
        visual.Size = new Vector2(W, H);
        visual.Shapes.Add(shape);
        ElementCompositionPreview.SetElementChildVisual(host, visual);

        // Centre and corner radius follow the animated size on the compositor thread.
        var offset = _c.CreateExpressionAnimation("Vector2((W - g.Size.X) * 0.5, T)");
        offset.SetReferenceParameter("g", Geometry);
        offset.SetScalarParameter("W", W);
        offset.SetScalarParameter("T", Top);
        Geometry.StartAnimation("Offset", offset);
        var radius = _c.CreateExpressionAnimation("Vector2(Min(g.Size.Y * 0.5, 30.0), Min(g.Size.Y * 0.5, 30.0))");
        radius.SetReferenceParameter("g", Geometry);
        Geometry.StartAnimation("CornerRadius", radius);
        ElementCompositionPreview.GetElementVisual(_large).Opacity = 0;
    }

    public void Morph(bool expand)
    {
        var spring = _c.CreateSpringVector2Animation();
        spring.FinalValue = expand ? Expanded : Collapsed;
        spring.DampingRatio = 0.74f;
        spring.Period = TimeSpan.FromMilliseconds(48);
        Geometry.StartAnimation("Size", spring);
        Fade(_small, expand ? 0f : 1f, expand ? 90 : 200, expand ? 0 : 150);
        Fade(_large, expand ? 1f : 0f, expand ? 240 : 110, expand ? 120 : 0);
    }

    private void Fade(UIElement e, float to, int ms, int delay)
    {
        var a = _c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to);
        a.Duration = TimeSpan.FromMilliseconds(ms);
        a.DelayTime = TimeSpan.FromMilliseconds(delay);
        ElementCompositionPreview.GetElementVisual(e).StartAnimation("Opacity", a);
    }

    /// <summary>Click-through outside the capsule: region = capsule inflated by 2 px so anti-aliased edges stay visible.</summary>
    public void FitRegion(Vector2 size)
    {
        int w = (int)size.X + 4, h = (int)size.Y + 4, x = (W - w) / 2, y = (int)Top - 2;
        Native.SetRoundedRegion(Surface.Hwnd, (x, y, w, h, Math.Min(h / 2, 32)));
    }
}

/// <summary>V-Dock: real acrylic glass slab (SystemBackdropElement) with compositor-driven icon magnification.</summary>
internal sealed class DockSurface
{
    public const int W = 760, H = 170, SlabW = 600, SlabH = 72, Bottom = 10, Icon = 48, Gap = 14, Count = 7;
    public SurfaceWindow Surface { get; }
    public List<Border> Icons { get; } = [];
    public SystemBackdropElement Slab { get; }
    public SystemBackdropElement Chip { get; }
    private readonly Compositor _c;
    public static int SlabTop => H - Bottom - SlabH;
    public static int IconLeft(int i) => (W - (Count * Icon + (Count - 1) * Gap)) / 2 + i * (Icon + Gap);
    public static int IconTop => SlabTop + (SlabH - Icon) / 2;

    public DockSurface(RectInt32 bounds, bool shellAcrylic = true)
    {
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        Slab = new SystemBackdropElement
        {
            Width = SlabW, Height = SlabH,
            CornerRadius = new CornerRadius(24),
            SystemBackdrop = shellAcrylic ? new ShellAcrylicBackdrop() : new DesktopAcrylicBackdrop(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, Bottom),
        };
        root.Children.Add(Slab);
        root.Children.Add(new Border
        {
            Width = SlabW, Height = SlabH, CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(1),
            BorderBrush = Palette.Brush(255, 255, 255, 34),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, Bottom),
        });
        // Stock acrylic chip for comparison (shows the inactive-window fallback behaviour).
        Chip = new SystemBackdropElement
        {
            Width = 150, Height = 44, CornerRadius = new CornerRadius(22),
            SystemBackdrop = new DesktopAcrylicBackdrop(),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(14, 14, 0, 0),
        };
        root.Children.Add(Chip);
        var canvas = new Canvas();
        (byte r, byte g, byte b)[] hues = [(70, 120, 255), (40, 190, 160), (255, 96, 140), (110, 170, 255), (255, 190, 80), (150, 120, 255), (140, 150, 170)];
        for (int i = 0; i < Count; i++)
        {
            var icon = new Border
            {
                Width = Icon, Height = Icon, CornerRadius = new CornerRadius(12),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
                    GradientStops =
                    {
                        new GradientStop { Color = Palette.Rgb((byte)Math.Min(255, hues[i].r + 30), (byte)Math.Min(255, hues[i].g + 30), (byte)Math.Min(255, hues[i].b + 30)), Offset = 0 },
                        new GradientStop { Color = Palette.Rgb(hues[i].r, hues[i].g, hues[i].b), Offset = 1 },
                    },
                },
            };
            Canvas.SetLeft(icon, IconLeft(i));
            Canvas.SetTop(icon, IconTop);
            canvas.Children.Add(icon);
            Icons.Add(icon);
        }
        root.Children.Add(canvas);
        Surface = new SurfaceWindow(root, bounds, new TransparentBackdrop());
        _c = ElementCompositionPreview.GetElementVisual(root).Compositor;
    }

    public void Magnify(int index, float scale)
    {
        var v = ElementCompositionPreview.GetElementVisual(Icons[index]);
        v.CenterPoint = new Vector3(Icon / 2f, Icon, 0);
        var s = _c.CreateSpringVector3Animation();
        s.FinalValue = new Vector3(scale, scale, 1);
        s.DampingRatio = 0.7f;
        s.Period = TimeSpan.FromMilliseconds(40);
        v.StartAnimation("Scale", s);
    }

    public void FitRegion(int magnifiedIndex, float scale)
    {
        int ex = (int)(Icon * scale), ix = IconLeft(magnifiedIndex) - (ex - Icon) / 2, iy = IconTop + Icon - ex;
        Native.SetRoundedRegion(Surface.Hwnd,
            ((W - SlabW) / 2 - 2, SlabTop - 2, SlabW + 4, SlabH + 4, 26),
            (ix - 2, iy - 2, ex + 4, ex + 4, 14));
    }
}

/// <summary>Top bar: the whole window is glass via the custom always-active acrylic backdrop.</summary>
internal sealed class BarSurface
{
    public SurfaceWindow Surface { get; }

    public BarSurface(RectInt32 bounds)
    {
        var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        root.Children.Add(new TextBlock
        {
            Text = "V   Files      Personal",
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.Brush(230, 232, 238),
            FontSize = 13,
        });
        Surface = new SurfaceWindow(root, bounds, new ShellAcrylicBackdrop());
    }
}
