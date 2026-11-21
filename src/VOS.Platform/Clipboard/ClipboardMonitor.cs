using System.Runtime.InteropServices;
using System.Text;
using VOS.Core;
using VOS.Platform.Native;
using VOS.Platform.Shell;

namespace VOS.Platform.Clipboard;

public enum ClipKind { Text, Link, Files, Image }

public sealed record ClipContent(ClipKind Kind, string? Text, string[]? Files, PixelImage? Image, string? SourceExe);

/// <summary>
/// Clipboard history source. Honours the conventions password managers use to keep secrets out of history:
/// "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory" = 0 and "Clipboard Viewer Ignore".
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly MessageWindow _window;
    private readonly uint _excludeFmt, _canIncludeFmt, _viewerIgnoreFmt;
    private uint _lastSequence;
    private bool _suppressNext;

    public event Action<ClipContent>? Captured;
    public bool CaptureImages { get; set; } = true;
    public Func<string?, bool>? IsExcludedApp { get; set; }

    public ClipboardMonitor(MessageWindow window)
    {
        _window = window;
        _excludeFmt = Win32.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _canIncludeFmt = Win32.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        _viewerIgnoreFmt = Win32.RegisterClipboardFormatW("Clipboard Viewer Ignore");
    }

    public void Start()
    {
        _window.Message += OnMessage;
        Win32.AddClipboardFormatListener(_window.Handle);
    }

    /// <summary>Call before V-OS itself writes to the clipboard so the write is not recorded twice.</summary>
    public void SuppressNext() => _suppressNext = true;

    private bool OnMessage(uint msg, IntPtr w, IntPtr l)
    {
        if (msg != Win32.WM_CLIPBOARDUPDATE) return false;
        var seq = Win32.GetClipboardSequenceNumber();
        if (seq == _lastSequence) return true;
        _lastSequence = seq;
        if (_suppressNext) { _suppressNext = false; return true; }
        try
        {
            var content = Read();
            if (content != null) Captured?.Invoke(content);
        }
        catch (Exception ex) { Log.Debug("Clipboard read skipped: " + ex.Message); }
        return true;
    }

    private ClipContent? Read()
    {
        string? source = null;
        var owner = Win32.GetClipboardOwner();
        if (owner != IntPtr.Zero) { Win32.GetWindowThreadProcessId(owner, out uint pid); source = Win32.GetProcessPath(pid); }
        if (IsExcludedApp?.Invoke(source) == true) return null;

        if (!OpenWithRetry()) return null;
        try
        {
            if (Win32.IsClipboardFormatAvailable(_excludeFmt) || Win32.IsClipboardFormatAvailable(_viewerIgnoreFmt)) return null;
            if (Win32.IsClipboardFormatAvailable(_canIncludeFmt))
            {
                var h = Win32.GetClipboardData(_canIncludeFmt);
                var p = Win32.GlobalLock(h);
                try { if (p != IntPtr.Zero && Marshal.ReadInt32(p) == 0) return null; }
                finally { if (p != IntPtr.Zero) Win32.GlobalUnlock(h); }
            }
            if (Win32.IsClipboardFormatAvailable(Win32.CF_HDROP))
            {
                var hDrop = Win32.GetClipboardData(Win32.CF_HDROP);
                uint n = Win32.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                var files = new List<string>();
                for (uint i = 0; i < n && i < 500; i++)
                {
                    uint len = Win32.DragQueryFileW(hDrop, i, null, 0);
                    var sb = new StringBuilder((int)len + 1);
                    Win32.DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
                    files.Add(sb.ToString());
                }
                if (files.Count > 0) return new ClipContent(ClipKind.Files, null, files.ToArray(), null, source);
            }
            if (Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT))
            {
                var h = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
                var p = Win32.GlobalLock(h);
                try
                {
                    var text = p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null;
                    if (string.IsNullOrWhiteSpace(text)) return null;
                    if (text.Length > 200_000) text = text[..200_000];
                    bool link = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && !text.Trim().Contains(' ');
                    return new ClipContent(link ? ClipKind.Link : ClipKind.Text, text, null, null, source);
                }
                finally { if (p != IntPtr.Zero) Win32.GlobalUnlock(h); }
            }
            if (CaptureImages && Win32.IsClipboardFormatAvailable(Win32.CF_DIB))
            {
                var img = ReadDib(Win32.GetClipboardData(Win32.CF_DIB));
                if (img != null) return new ClipContent(ClipKind.Image, null, null, img, source);
            }
            return null;
        }
        finally { Win32.CloseClipboard(); }
    }

    private bool OpenWithRetry()
    {
        for (int i = 0; i < 5; i++)
        {
            if (Win32.OpenClipboard(_window.Handle)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>Converts a CF_DIB (BITMAPINFOHEADER + pixels) to premultiplied BGRA.</summary>
    private static PixelImage? ReadDib(IntPtr h)
    {
        var p = Win32.GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            int headerSize = Marshal.ReadInt32(p);
            int w = Marshal.ReadInt32(p, 4), hgt = Marshal.ReadInt32(p, 8);
            short bpp = Marshal.ReadInt16(p, 14);
            int compression = Marshal.ReadInt32(p, 16);
            if (w <= 0 || w > 16000 || Math.Abs(hgt) > 16000 || (bpp != 32 && bpp != 24)) return null;
            int masks = compression == 3 && headerSize == 40 ? 12 : 0;
            int stride = ((w * bpp + 31) / 32) * 4;
            int height = Math.Abs(hgt);
            var src = new byte[stride * height];
            Marshal.Copy(p + headerSize + masks, src, 0, src.Length);
            var dst = new byte[w * height * 4];
            bool bottomUp = hgt > 0;
            bool alphaUsed = false;
            for (int y = 0; y < height; y++)
            {
                int sy = bottomUp ? height - 1 - y : y;
                for (int x = 0; x < w; x++)
                {
                    int si = sy * stride + x * (bpp / 8), di = (y * w + x) * 4;
                    dst[di] = src[si]; dst[di + 1] = src[si + 1]; dst[di + 2] = src[si + 2];
                    dst[di + 3] = bpp == 32 ? src[si + 3] : (byte)255;
                    if (bpp == 32 && src[si + 3] != 0) alphaUsed = true;
                }
            }
            if (bpp == 32 && !alphaUsed) for (int i = 3; i < dst.Length; i += 4) dst[i] = 255;
            return new PixelImage(w, height, dst);
        }
        finally { Win32.GlobalUnlock(h); }
    }

    /// <summary>Puts text back on the clipboard (used when the user picks an item from history).</summary>
    public void SetText(string text)
    {
        SuppressNext();
        if (!OpenWithRetry()) return;
        try
        {
            Win32.EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var hMem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)bytes);
            var target = Win32.GlobalLock(hMem);
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
            Win32.GlobalUnlock(hMem);
            Win32.SetClipboardData(Win32.CF_UNICODETEXT, hMem);
        }
        finally { Win32.CloseClipboard(); }
    }

    public void Dispose()
    {
        Win32.RemoveClipboardFormatListener(_window.Handle);
        _window.Message -= OnMessage;
    }
}
