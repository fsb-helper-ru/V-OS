using System.IO.Compression;
using System.Runtime.InteropServices;

namespace S1;

/// <summary>Win32 interop used by the spike: window styles, regions, hit testing, screen capture, AppBar, DWM thumbnails.</summary>
internal static class Native
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_POPUP = 0x80000000L, WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L, WS_SYSMENU = 0x00080000L,
        WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L;
    public const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8, WS_EX_APPWINDOW = 0x40000,
        WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_LAYERED = 0x80000;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    public static IntPtr RootAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2 /*GA_ROOT*/);

    /// <summary>Region = union of rounded rectangles (client coordinates). The system owns the region after SetWindowRgn.</summary>
    public static void SetRoundedRegion(IntPtr hwnd, params (int x, int y, int w, int h, int r)[] rects)
    {
        IntPtr combined = CreateRectRgn(0, 0, 0, 0);
        foreach (var (x, y, w, h, r) in rects)
        {
            IntPtr part = CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, r * 2, r * 2);
            CombineRgn(combined, combined, part, 2 /*RGN_OR*/);
            DeleteObject(part);
        }
        SetWindowRgn(hwnd, combined, true);
    }

    // ---- screen capture (composed desktop, includes DirectComposition windows) ----
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);

    public static Frame Capture(int x, int y, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(mem, dib);
        BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020 | 0x40000000 /*SRCCOPY|CAPTUREBLT*/);
        var data = new byte[w * h * 4];
        Marshal.Copy(bits, data, 0, data.Length);
        SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        return new Frame(data, w, h);
    }

    // ---- AppBar ----
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
    [DllImport("shell32.dll")] public static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref RECT rect, uint winIni);
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_TOP = 1;
    public static RECT WorkArea() { var r = new RECT(); SystemParametersInfo(0x30 /*SPI_GETWORKAREA*/, 0, ref r, 0); return r; }

    // ---- DWM thumbnails ----
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES { public uint dwFlags; public RECT rcDestination; public RECT rcSource; public byte opacity; public int fVisible; public int fSourceClientAreaOnly; }
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
}

/// <summary>A captured BGRA frame with the measurements the spike needs.</summary>
internal sealed record Frame(byte[] Bgra, int Width, int Height)
{
    public (byte b, byte g, byte r) At(int x, int y) { int i = (y * Width + x) * 4; return (Bgra[i], Bgra[i + 1], Bgra[i + 2]); }
    public double Luma(int x, int y) { var (b, g, r) = At(x, y); return 0.2126 * r + 0.7152 * g + 0.0722 * b; }

    /// <summary>Fraction of pixels in the rectangle that equal <paramref name="other"/> within a tolerance.</summary>
    public double MatchRatio(Frame other, int x0, int y0, int w, int h, int tol = 6)
    {
        long same = 0, total = 0;
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                var (b1, g1, r1) = At(x, y); var (b2, g2, r2) = other.At(x, y);
                total++;
                if (Math.Abs(b1 - b2) <= tol && Math.Abs(g1 - g2) <= tol && Math.Abs(r1 - r2) <= tol) same++;
            }
        return total == 0 ? 0 : (double)same / total;
    }

    public (double mean, double std) LumaStats(int x0, int y0, int w, int h)
    {
        double sum = 0, sq = 0; int n = 0;
        for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) { double l = Luma(x, y); sum += l; sq += l * l; n++; }
        double mean = sum / n; return (mean, Math.Sqrt(Math.Max(0, sq / n - mean * mean)));
    }

    /// <summary>Width of the dark run (luma below threshold) crossing the given row, measured outward from x.</summary>
    public int DarkRunWidth(int row, int threshold = 45)
    {
        int first = -1, last = -1;
        for (int x = 0; x < Width; x++) if (Luma(x, row) < threshold) { if (first < 0) first = x; last = x; }
        return first < 0 ? 0 : last - first + 1;
    }

    public void SavePng(string path)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, Width); WriteBE(ihdr, 4, Height); ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(fs, "IHDR", ihdr);
        var raw = new byte[Height * (1 + Width * 3)];
        for (int y = 0, o = 0; y < Height; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < Width; x++) { int i = (y * Width + x) * 4; raw[o++] = Bgra[i + 2]; raw[o++] = Bgra[i + 1]; raw[o++] = Bgra[i]; }
        }
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw);
        Chunk(fs, "IDAT", ms.ToArray());
        Chunk(fs, "IEND", []);
    }

    private static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length]; System.Text.Encoding.ASCII.GetBytes(type, 0, 4, td, 0); data.CopyTo(td, 4);
        s.Write(td);
        var crc = new byte[4]; WriteBE(crc, 0, (int)Crc32(td)); s.Write(crc);
    }
    private static uint[]? s_table;
    private static uint Crc32(byte[] d)
    {
        s_table ??= Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
        uint crc = 0xFFFFFFFF; foreach (var b in d) crc = s_table[(crc ^ b) & 0xFF] ^ (crc >> 8); return crc ^ 0xFFFFFFFF;
    }
}
