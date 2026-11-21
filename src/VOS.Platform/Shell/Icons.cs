using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VOS.Core;

namespace VOS.Platform.Shell;

/// <summary>Premultiplied BGRA pixels (what WinUI's WriteableBitmap expects).</summary>
public sealed record PixelImage(int Width, int Height, byte[] Bgra)
{
    /// <summary>Encodes as PNG (straight alpha) for the disk cache.</summary>
    public byte[] ToPng() => PngCodec.Encode(this);
}

/// <summary>
/// Shell icons and thumbnails via IShellItemImageFactory, with memory and disk caches.
/// All extraction runs on one dedicated STA thread: the shell's image factories are apartment-sensitive and answer
/// concurrent requests with E_PENDING, so requests are queued, served one at a time, and retried briefly when pending.
/// </summary>
public static class IconService
{
    private const int E_PENDING = unchecked((int)0x8000000A);
    private sealed record Request(string ParsingName, int Size, bool Thumbnail, bool Only, TaskCompletionSource<PixelImage?> Done);

    private static readonly ConcurrentDictionary<string, PixelImage> s_memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockingCollection<Request> s_queue = [];
    private static readonly object s_gate = new();
    private static Thread? s_worker;
    private static string CacheDir => Directory.CreateDirectory(Path.Combine(VosPaths.Cache, "icons")).FullName;

    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [DllImport("gdi32.dll")] private static extern int GetObjectW(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);

    private static string Key(string parsingName, int size, bool thumbnail, bool only = false) => $"{parsingName}|{size}|{thumbnail}{(only ? "|only" : "")}";

    /// <summary>
    /// Icon for a parsing name (file path, "shell:AppsFolder\..." item, folder). Completes synchronously from the memory
    /// cache, otherwise on the icon thread. Never throws; completes with null when no icon is available.
    /// </summary>
    public static Task<PixelImage?> GetAsync(string parsingName, int size, bool thumbnail = false) => Enqueue(parsingName, size, thumbnail, false);

    /// <summary>A real thumbnail (album art, photo, video frame) or null: never falls back to the file-type icon.</summary>
    public static Task<PixelImage?> GetThumbnailOnlyAsync(string parsingName, int size) => Enqueue(parsingName, size, true, true);

    private static Task<PixelImage?> Enqueue(string parsingName, int size, bool thumbnail, bool only)
    {
        if (s_memory.TryGetValue(Key(parsingName, size, thumbnail, only), out var hit)) return Task.FromResult<PixelImage?>(hit);
        EnsureWorker();
        var done = new TaskCompletionSource<PixelImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_queue.Add(new Request(parsingName, size, thumbnail, only, done));
        return done.Task;
    }

    /// <summary>Memory-cache lookup only (never blocks, never extracts).</summary>
    public static PixelImage? TryGetCached(string parsingName, int size, bool thumbnail = false) =>
        s_memory.TryGetValue(Key(parsingName, size, thumbnail), out var hit) ? hit : null;

    /// <summary>Blocking variant for background threads. Never call it on a UI thread.</summary>
    public static PixelImage? Get(string parsingName, int size, bool thumbnail = false) =>
        Thread.CurrentThread == s_worker ? Load(parsingName, size, thumbnail) : GetAsync(parsingName, size, thumbnail).GetAwaiter().GetResult();

    private static void EnsureWorker()
    {
        if (s_worker != null) return;
        lock (s_gate)
        {
            if (s_worker != null) return;
            var t = new Thread(Work) { IsBackground = true, Name = "V-OS shell icons", Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            s_worker = t;
        }
    }

    private static void Work()
    {
        // Blocking waits on an STA thread pump COM messages, so shell callbacks into this apartment still run.
        foreach (var r in s_queue.GetConsumingEnumerable())
        {
            try { r.Done.TrySetResult(Load(r.ParsingName, r.Size, r.Thumbnail, r.Only)); }
            catch (Exception ex) { Log.Warn("Icon request failed", ex); r.Done.TrySetResult(null); }
        }
    }

    private static PixelImage? Load(string parsingName, int size, bool thumbnail, bool only = false)
    {
        var key = Key(parsingName, size, thumbnail, only);
        if (s_memory.TryGetValue(key, out var cached)) return cached;
        // v2: pixels are premultiplied before caching (v1 cached straight alpha as if it were premultiplied).
        var disk = Path.Combine(CacheDir, Hash("v2|" + key) + ".png");
        PixelImage? img = null;
        try
        {
            if (!thumbnail && File.Exists(disk) && File.GetLastWriteTimeUtc(disk) > DateTime.UtcNow.AddDays(-14))
                img = PngCodec.Decode(File.ReadAllBytes(disk));
        }
        catch { img = null; }
        img ??= Extract(parsingName, size, thumbnail, only);
        if (img == null) return null; // never remember a failure: the next request retries
        if (!thumbnail) { try { File.WriteAllBytes(disk, img.ToPng()); } catch { } }
        if (s_memory.Count > 800) s_memory.Clear();
        s_memory[key] = img;
        return img;
    }

    /// <summary>Extracts directly (no cache), retrying while the shell reports the image as pending.</summary>
    public static PixelImage? Extract(string parsingName, int size, bool thumbnail, bool only = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            var img = ExtractOnce(parsingName, size, thumbnail, only, out int hr);
            if (img != null || hr != E_PENDING || attempt == 4) return img;
            Thread.Sleep(30 << attempt); // 30, 60, 120, 240 ms
        }
    }

    private static PixelImage? ExtractOnce(string parsingName, int size, bool thumbnail, bool only, out int hr)
    {
        hr = 0;
        IShellItem? item = null;
        try
        {
            var iid = ShellNative.IID_IShellItem;
            ShellNative.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out item);
            if (item is not IShellItemImageFactory factory) return null;
            var flags = only ? SIIGBF.THUMBNAILONLY | SIIGBF.BIGGERSIZEOK : thumbnail ? SIIGBF.BIGGERSIZEOK : SIIGBF.ICONONLY | SIIGBF.BIGGERSIZEOK;
            hr = factory.GetImage(new SIZE(size, size), flags, out var hbm);
            if (hr != 0 || hbm == IntPtr.Zero)
            {
                if (hr != E_PENDING && !only) Log.Info("Icon extraction failed", new { parsingName, size, hr = $"0x{hr:X8}" });
                return null;
            }
            try
            {
                var img = FromHBitmap(hbm);
                if (img == null) Log.Info("Icon bitmap unreadable", new { parsingName, size });
                return img;
            }
            finally { DeleteObject(hbm); }
        }
        catch (Exception ex)
        {
            hr = ex.HResult;
            if (ex is not (FileNotFoundException or DirectoryNotFoundException))
                Log.Info("Icon extraction failed", new { parsingName, size, error = ex.GetType().Name + ": " + ex.Message });
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }
    private static PixelImage? FromHBitmap(IntPtr hbm)
    {
        if (GetObjectW(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight), stride = bm.bmWidthBytes;
        var src = new byte[stride * h];
        Marshal.Copy(bm.bmBits, src, 0, src.Length);
        var dst = new byte[w * h * 4];
        bool bottomUp = bm.bmHeight > 0;
        bool anyAlpha = false;
        for (int i = 3; i < src.Length; i += 4) if (src[i] != 0) { anyAlpha = true; break; }
        for (int y = 0; y < h; y++)
        {
            int sy = bottomUp ? h - 1 - y : y;
            Buffer.BlockCopy(src, sy * stride, dst, y * w * 4, w * 4);
        }
        if (!anyAlpha) { for (int i = 3; i < dst.Length; i += 4) dst[i] = 255; return new PixelImage(w, h, dst); } // legacy icons without alpha
        // The shell usually hands back straight (non-premultiplied) alpha; WinUI composes premultiplied pixels.
        // A colour channel above its alpha can only occur in straight alpha, so convert when one is seen.
        bool straight = false;
        for (int i = 0; i < dst.Length && !straight; i += 4)
        {
            byte a = dst[i + 3];
            straight = dst[i] > a || dst[i + 1] > a || dst[i + 2] > a;
        }
        if (straight)
            for (int i = 0; i < dst.Length; i += 4)
            {
                int a = dst[i + 3];
                dst[i] = (byte)((dst[i] * a + 127) / 255);
                dst[i + 1] = (byte)((dst[i + 1] * a + 127) / 255);
                dst[i + 2] = (byte)((dst[i + 2] * a + 127) / 255);
            }
        return new PixelImage(w, h, dst);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..20];
}

/// <summary>Minimal PNG encoder/decoder for icon caching (8-bit RGBA, no interlace).</summary>
public static class PngCodec
{
    public static byte[] Encode(PixelImage img)
    {
        int w = img.Width, h = img.Height;
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BE(ihdr, 0, w); BE(ihdr, 4, h); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(ms, "IHDR", ihdr);
        var raw = new byte[h * (1 + w * 4)];
        for (int y = 0, o = 0; y < h; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte a = img.Bgra[i + 3];
                // un-premultiply
                raw[o++] = a == 0 ? (byte)0 : (byte)Math.Min(255, img.Bgra[i + 2] * 255 / a);
                raw[o++] = a == 0 ? (byte)0 : (byte)Math.Min(255, img.Bgra[i + 1] * 255 / a);
                raw[o++] = a == 0 ? (byte)0 : (byte)Math.Min(255, img.Bgra[i] * 255 / a);
                raw[o++] = a;
            }
        }
        using (var zms = new MemoryStream())
        {
            using (var z = new ZLibStream(zms, CompressionLevel.Optimal, true)) z.Write(raw);
            Chunk(ms, "IDAT", zms.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>Decodes PNGs written by <see cref="Encode"/> (RGBA8, filter 0..4). Returns premultiplied BGRA.</summary>
    public static PixelImage? Decode(byte[] png)
    {
        if (png.Length < 33 || png[1] != 0x50) return null;
        int pos = 8, w = 0, h = 0;
        using var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            var type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IHDR") { w = RBE(png, pos + 8); h = RBE(png, pos + 12); if (png[pos + 16] != 8 || png[pos + 17] != 6) return null; }
            else if (type == "IDAT") idat.Write(png, pos + 8, len);
            else if (type == "IEND") break;
            pos += 12 + len;
        }
        if (w <= 0 || h <= 0) return null;
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        int stride = w * 4;
        var raw = new byte[h * (stride + 1)];
        int read = 0, n;
        while (read < raw.Length && (n = z.Read(raw, read, raw.Length - read)) > 0) read += n;
        var outp = new byte[w * h * 4];
        var prev = new byte[stride];
        var cur = new byte[stride];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, cur, 0, stride);
            for (int x = 0; x < stride; x++)
            {
                int a = x >= 4 ? cur[x - 4] : 0, b = prev[x], c = x >= 4 ? prev[x - 4] : 0;
                cur[x] = f switch
                {
                    1 => (byte)(cur[x] + a), 2 => (byte)(cur[x] + b), 3 => (byte)(cur[x] + (a + b) / 2),
                    4 => (byte)(cur[x] + Paeth(a, b, c)), _ => cur[x],
                };
            }
            for (int x = 0; x < w; x++)
            {
                int i = x * 4, o = (y * w + x) * 4;
                byte al = cur[i + 3];
                outp[o] = (byte)(cur[i + 2] * al / 255); outp[o + 1] = (byte)(cur[i + 1] * al / 255); outp[o + 2] = (byte)(cur[i] * al / 255); outp[o + 3] = al;
            }
            (prev, cur) = (cur, prev);
        }
        return new PixelImage(w, h, outp);
    }

    private static int Paeth(int a, int b, int c) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static void BE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
    private static int RBE(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, data.Length); s.Write(len);
        var td = new byte[4 + data.Length]; Encoding.ASCII.GetBytes(type, 0, 4, td, 0); data.CopyTo(td, 4);
        s.Write(td);
        var crc = new byte[4]; BE(crc, 0, (int)Crc(td)); s.Write(crc);
    }
    private static readonly uint[] s_table = Enumerable.Range(0, 256).Select(n => { uint c = (uint)n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; return c; }).ToArray();
    private static uint Crc(byte[] d) { uint c = 0xFFFFFFFF; foreach (var b in d) c = s_table[(c ^ b) & 0xFF] ^ (c >> 8); return c ^ 0xFFFFFFFF; }
}
