using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace S1;

/// <summary>Raw Win32 popup created with WS_EX_NOREDIRECTIONBITMAP, hosting XAML through DesktopWindowXamlSource.</summary>
internal sealed class IslandSurface
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private static readonly WndProc s_proc = (h, m, w, l) => DefWindowProcW(h, m, w, l);
    private static bool s_registered;
    private const string ClassName = "VOS.ShellSurface";

    public IntPtr Hwnd { get; }
    public DesktopWindowXamlSource Source { get; }

    public IslandSurface(UIElement content, RectInt32 b, SystemBackdrop? backdrop, bool noRedirection = true)
    {
        if (!s_registered)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_proc),
                hInstance = GetModuleHandleW(null),
                hCursor = LoadCursorW(IntPtr.Zero, 32512),
                lpszClassName = ClassName,
            };
            Transparency.T("register"); RegisterClassExW(ref wc); Transparency.T("registered");
            s_registered = true;
        }
        uint ex = 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x8 /*TOPMOST*/;
        if (noRedirection) ex |= 0x00200000;
        Transparency.T("create"); Hwnd = CreateWindowExW(ex, ClassName, "", 0x80000000 /*WS_POPUP*/, b.X, b.Y, b.Width, b.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        Transparency.T($"created {Hwnd}"); Source = new DesktopWindowXamlSource(); Transparency.T("source");
        Source.Initialize(Win32Interop.GetWindowIdFromWindow(Hwnd)); Transparency.T("initialized");
        if (backdrop != null) Source.SystemBackdrop = backdrop;
        Transparency.T("backdrop set"); Source.Content = content; Transparency.T("content");
        Source.SiteBridge.MoveAndResize(new RectInt32(0, 0, b.Width, b.Height));
        Source.SiteBridge.Show();
    }

    public void Show() => ShowWindow(Hwnd, 4 /*SW_SHOWNOACTIVATE*/);
    public void Close() { Source.Dispose(); DestroyWindow(Hwnd); }
}

internal static class Transparency
{
    [StructLayout(LayoutKind.Sequential)] private struct MARGINS { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);

    public static void ExtendFrame(IntPtr hwnd) { var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 }; DwmExtendFrameIntoClientArea(hwnd, ref m); }

    private static string Describe(IntPtr h)
    {
        var sb = new StringBuilder(256); GetClassNameW(h, sb, 256);
        long ex = (long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE), st = (long)Native.GetWindowLongPtr(h, Native.GWL_STYLE);
        return $"{sb} style=0x{st:X8} ex=0x{ex:X8} noRedirection={(ex & 0x00200000) != 0} layered={(ex & 0x80000) != 0}";
    }

    public static List<string> Tree(IntPtr root)
    {
        var list = new List<string> { "top: " + Describe(root) };
        EnumChildWindows(root, (h, _) => { list.Add("child: " + Describe(h)); return true; }, IntPtr.Zero);
        return list;
    }

    private static UIElement Cell(string label)
    {
        var g = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
        g.Children.Add(new Border
        {
            Width = 150, Height = 30, CornerRadius = new CornerRadius(15),
            Background = Palette.Brush(11, 12, 15),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = label, Foreground = Palette.Brush(236, 238, 242), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        return g;
    }

    private static string? s_trace;
    internal static void T(string step) { if (s_trace != null) File.AppendAllText(s_trace, $"{DateTime.Now:HH:mm:ss.fff} {step}{Environment.NewLine}"); }

    public static async Task<Dictionary<string, object?>> Run(string outDir)
    {
        var r = new Dictionary<string, object?>();
        s_trace = Path.Combine(outDir, "transparency.trace.txt"); File.WriteAllText(s_trace, "");
        const int cw = 220, ch = 110, n = 5;
        int sw = Native.GetSystemMetrics(0), x0 = (sw - cw * n) / 2, y0 = 60;
        var pattern = new PatternSurface(new RectInt32(x0, y0, cw * n, ch));
        pattern.Surface.Show();
        await Task.Delay(500);
        var background = Native.Capture(x0, y0, cw * n, ch);

        RectInt32 B(int i) => new(x0 + i * cw, y0, cw, ch);
        T("A"); var a = new SurfaceWindow(Cell("A backdrop"), B(0), new TransparentBackdrop());
        T("B"); var bWin = new SurfaceWindow(Cell("B backdrop+frame"), B(1), new TransparentBackdrop());
        ExtendFrame(bWin.Hwnd);
        T("C"); var c = new SurfaceWindow(Cell("C frame only"), B(2), null);
        ExtendFrame(c.Hwnd);
        T("D"); var d = new IslandSurface(Cell("D island"), B(3), null);
        T("E"); var e = new IslandSurface(Cell("E island+frame"), B(4), null);
        ExtendFrame(e.Hwnd);
        T("show"); a.Show(); bWin.Show(); c.Show(); d.Show(); e.Show(); T("shown");
        await Task.Delay(900);
        var shot = Native.Capture(x0, y0, cw * n, ch);
        shot.SavePng(Path.Combine(outDir, "transparency-strategies.png"));
        string[] names = ["A_winui_transparentBackdrop", "B_winui_backdrop_extendFrame", "C_winui_extendFrame", "D_island_noRedirection", "E_island_noRedirection_extendFrame"];
        for (int i = 0; i < n; i++)
        {
            // Left and right margins of each cell lie outside the capsule: they must show the pattern untouched.
            double left = shot.MatchRatio(background, i * cw + 4, 4, 28, ch - 8);
            double right = shot.MatchRatio(background, i * cw + cw - 32, 4, 28, ch - 8);
            double inkLuma = shot.LumaStats(i * cw + cw / 2 - 66, ch / 2 - 2, 6, 4).mean;
            r[names[i]] = new { transparentMarginMatch = Math.Round((left + right) / 2, 4), capsuleInkLuma = Math.Round(inkLuma, 1) };
        }
        T("measured"); r["tree.winuiWindow"] = Tree(a.Hwnd); T("tree a");
        r["tree.island"] = Tree(e.Hwnd); T("tree e");
        foreach (var w in new[] { a, bWin, c }) w.Close();
        T("close winui"); d.Close(); T("closed d"); e.Close(); T("closed e");
        pattern.Surface.Close();
        return r;
    }
}
