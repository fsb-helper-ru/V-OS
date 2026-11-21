using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using VOS.Core;
using VOS.Core.Perf;
using VOS.Platform.Native;

namespace VOS.Platform.Sys;

public sealed record SystemSample(
    DateTimeOffset Time, double CpuPercent, double MemoryUsedGb, double MemoryTotalGb, double MemoryPercent,
    double GpuPercent, double DiskActivePercent, double DiskReadBps, double DiskWriteBps, double NetDownBps, double NetUpBps);

/// <summary>
/// Samples system load on demand (callers decide the interval; nothing runs in the background by itself).
/// CPU from GetSystemTimes, memory from GlobalMemoryStatusEx, GPU/disk from PDH (English counter paths, locale-safe).
/// </summary>
public sealed class MetricsSampler : IDisposable
{
    private long _idle, _kernel, _user;
    private long _rx, _tx;
    private DateTime _netAt;
    private readonly Pdh? _pdh;

    public MetricsSampler(bool includeGpuAndDisk = true)
    {
        Win32.GetSystemTimes(out _idle, out _kernel, out _user);
        (_rx, _tx) = NetTotals();
        _netAt = DateTime.UtcNow;
        if (includeGpuAndDisk)
        {
            try { _pdh = new Pdh(); } catch (Exception ex) { Log.Warn("PDH counters unavailable", ex); }
        }
    }

    public SystemSample Sample()
    {
        Win32.GetSystemTimes(out long idle, out long kernel, out long user);
        long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
        (_idle, _kernel, _user) = (idle, kernel, user);
        double total = dk + du;
        double cpu = total > 0 ? Math.Clamp((total - di) * 100.0 / total, 0, 100) : 0;

        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        Win32.GlobalMemoryStatusEx(ref mem);
        double totalGb = mem.ullTotalPhys / 1073741824.0, usedGb = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;

        var (rx, tx) = NetTotals();
        var now = DateTime.UtcNow;
        double secs = Math.Max(0.001, (now - _netAt).TotalSeconds);
        double down = Math.Max(0, (rx - _rx) / secs), up = Math.Max(0, (tx - _tx) / secs);
        (_rx, _tx, _netAt) = (rx, tx, now);

        var (gpu, disk, read, write) = _pdh?.Read() ?? (0, 0, 0, 0);
        return new SystemSample(DateTimeOffset.Now, cpu, usedGb, totalGb, mem.dwMemoryLoad, gpu, disk, read, write, down, up);
    }

    private static (long rx, long tx) NetTotals()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = ni.GetIPStatistics();
                rx += s.BytesReceived; tx += s.BytesSent;
            }
        }
        catch { }
        return (rx, tx);
    }

    public void Dispose() => _pdh?.Dispose();

    private sealed class Pdh : IDisposable
    {
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? src, IntPtr user, out IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr c);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr q);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);
        [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr c, uint fmt, out uint type, out PDH_FMT_COUNTERVALUE v);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr q);
        [StructLayout(LayoutKind.Explicit)] private struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint status; [FieldOffset(8)] public double value; }

        private readonly IntPtr _q, _gpu, _disk, _read, _write;

        public Pdh()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) throw new InvalidOperationException("PDH unavailable");
            PdhAddEnglishCounterW(_q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpu);
            PdhAddEnglishCounterW(_q, @"\PhysicalDisk(_Total)\% Idle Time", IntPtr.Zero, out _disk);
            PdhAddEnglishCounterW(_q, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", IntPtr.Zero, out _read);
            PdhAddEnglishCounterW(_q, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", IntPtr.Zero, out _write);
            PdhCollectQueryData(_q);
        }

        public (double gpu, double disk, double read, double write) Read()
        {
            PdhCollectQueryData(_q);
            double Value(IntPtr c) => c != IntPtr.Zero && PdhGetFormattedCounterValue(c, 0x200 | 0x8000 /*NOCAP100*/, out _, out var v) == 0 ? v.value : 0;
            double idle = Value(_disk);
            return (GpuMax(), Math.Clamp(100 - idle, 0, 100), Value(_read), Value(_write));
        }

        /// <summary>GPU load the way Task Manager shows it: the busiest engine type, summed across processes.</summary>
        private double GpuMax()
        {
            if (_gpu == IntPtr.Zero) return 0;
            uint size = 0;
            PdhGetFormattedCounterArrayW(_gpu, 0x200, ref size, out uint count, IntPtr.Zero);
            if (size == 0) return 0;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_gpu, 0x200, ref size, out count, buf) != 0) return 0;
                var perEngine = new Dictionary<string, double>();
                for (int i = 0; i < count; i++)
                {
                    var item = buf + i * 24;
                    var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    int at = name.IndexOf("engtype_", StringComparison.Ordinal);
                    var engine = at >= 0 ? name[(at + 8)..] : "other";
                    perEngine[engine] = perEngine.GetValueOrDefault(engine) + v;
                }
                return perEngine.Count == 0 ? 0 : Math.Clamp(perEngine.Values.Max(), 0, 100);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        public void Dispose() => PdhCloseQuery(_q);
    }
}

/// <summary>Builds the <see cref="HardwareSnapshot"/> consumed by the adaptive-quality resolver.</summary>
public static unsafe class HardwareProbe
{
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
    [StructLayout(LayoutKind.Sequential)] private struct HIGHCONTRAST { public int cbSize; public int dwFlags; public IntPtr lpszDefaultScheme; }
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint param, ref HIGHCONTRAST hc, uint winIni);

    public static HardwareSnapshot Probe(bool effectsSupported = true, bool effectsFast = true)
    {
        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        Win32.GlobalMemoryStatusEx(ref mem);
        var (gpu, vram, software) = Adapter();
        var battery = SystemStatusService.ReadBattery();
        var monitors = Windowing.Displays.All();
        Win32.SystemParametersInfoW(Win32.SPI_GETCLIENTAREAANIMATION, 0, out int anim, 0);
        var hc = new HIGHCONTRAST { cbSize = Marshal.SizeOf<HIGHCONTRAST>() };
        SystemParametersInfoW(Win32.SPI_GETHIGHCONTRAST, (uint)hc.cbSize, ref hc, 0);
        return new HardwareSnapshot
        {
            LogicalCores = Environment.ProcessorCount,
            RamGb = mem.ullTotalPhys / 1073741824.0,
            GpuName = gpu,
            GpuDedicatedMb = vram,
            GpuIsSoftware = software,
            EffectsSupported = effectsSupported,
            EffectsFast = effectsFast,
            HasBattery = battery.Present,
            OnBattery = battery.Present && !battery.OnAc,
            BatterySaver = battery.Saver,
            RemoteSession = Win32.GetSystemMetrics(0x1000) != 0,
            MonitorCount = Math.Max(1, monitors.Count),
            TotalPixels = monitors.Sum(m => (long)m.Bounds.Width * m.Bounds.Height),
            WindowsAnimations = anim != 0,
            WindowsTransparency = WindowsTheme.TransparencyEnabled,
            HighContrast = (hc.dwFlags & 1) != 0,
        };
    }

    public static (string name, long vramMb, bool software) Adapter()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref iid, out IntPtr factory) < 0) return ("Unknown", 0, false);
        try
        {
            var vtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[12];
            IntPtr adapter;
            if (enumAdapters1(factory, 0, &adapter) < 0) return ("None", 0, true);
            try
            {
                var avtbl = *(IntPtr**)adapter;
                var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)avtbl[10];
                byte* desc = stackalloc byte[512];
                getDesc1(adapter, desc);
                var name = new string((char*)desc);
                long vram = (long)(*(ulong*)(desc + 272) / (1024 * 1024));
                uint flags = *(uint*)(desc + 304);
                bool software = (flags & 2) != 0 || name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase);
                return (name, vram, software);
            }
            finally { Marshal.Release(adapter); }
        }
        finally { Marshal.Release(factory); }
    }
}

/// <summary>Windows personalization state (read-only unless the user opts in to syncing).</summary>
public static class WindowsTheme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool AppsUseLightTheme => ReadDword("AppsUseLightTheme", 0) != 0;
    public static bool TransparencyEnabled => ReadDword("EnableTransparency", 1) != 0;

    /// <summary>Windows accent color as #RRGGBB.</summary>
    public static string AccentHex()
    {
        try
        {
            var c = new global::Windows.UI.ViewManagement.UISettings().GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        catch { return "#4C8DFF"; }
    }

    /// <summary>Opt-in only (Settings › Appearance › "Also switch Windows apps"): writes the per-user theme preference.</summary>
    public static void SetAppsTheme(bool light)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Personalize, writable: true);
        k?.SetValue("AppsUseLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
        k?.SetValue("SystemUsesLightTheme", light ? 1 : 0, RegistryValueKind.DWord);
    }

    private static int ReadDword(string name, int fallback)
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(Personalize); return k?.GetValue(name) is int v ? v : fallback; }
        catch { return fallback; }
    }
}
