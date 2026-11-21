using System.Runtime.InteropServices;
using System.Text;
using VOS.Platform.Native;
using VOS.Platform.Shell;

namespace VOS.Platform.Sys;

public sealed record CapturableWindow(IntPtr Hwnd, string Title, Win32.RECT Bounds);

/// <summary>
/// Screen pixels via GDI (BitBlt with CAPTUREBLT from the composed desktop). Coordinates are physical pixels in
/// virtual-screen space. Protected content (DRM video) comes out black, as Windows intends.
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr l);
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    public static Win32.RECT VirtualScreen
    {
        get
        {
            int x = GetSystemMetrics(76), y = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
            return new Win32.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        }
    }

    /// <summary>Copies a rectangle of the screen. Pixels are opaque BGRA (top-down).</summary>
    public static PixelImage Capture(Win32.RECT r)
    {
        int w = Math.Max(1, r.Right - r.Left), h = Math.Max(1, r.Bottom - r.Top);
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, SRCCOPY | CAPTUREBLT);
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return new PixelImage(w, h, pixels);
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Visible top-level windows front to back, with their visible bounds (DWM extended frame, no shadow).</summary>
    public static List<CapturableWindow> Windows(IEnumerable<IntPtr>? exclude = null)
    {
        var skip = new HashSet<IntPtr>(exclude ?? []);
        var list = new List<CapturableWindow>();
        EnumWindows((h, _) =>
        {
            if (skip.Contains(h) || !Win32.IsWindowVisible(h) || Win32.IsIconic(h) || Win32.IsCloaked(h)) return true;
            if (Win32.DwmGetWindowAttribute(h, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT r, Marshal.SizeOf<Win32.RECT>()) != 0) Win32.GetWindowRect(h, out r);
            if (r.Right - r.Left < 40 || r.Bottom - r.Top < 30) return true;
            var title = Win32.GetText(h);
            var cls = Win32.GetClass(h);
            if (cls is "Progman" or "WorkerW") return true; // the desktop itself is "the screen"
            list.Add(new CapturableWindow(h, title, r));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
