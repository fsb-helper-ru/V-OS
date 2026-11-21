using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using VOS.Core;
using VOS.Platform.Native;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace VOS.Platform.Sys;

public sealed record BatteryState(bool Present, int Percent, bool Charging, bool OnAc, bool Saver, TimeSpan? Remaining);

public sealed record NetworkState(bool Connected, bool Internet, bool IsWifi, string Name, int SignalBars, bool IsEthernet);

/// <summary>Battery, power, network and radio state. Everything is event-driven; nothing polls.</summary>
public sealed class SystemStatusService : IDisposable
{
    public event Action<BatteryState>? BatteryChanged;
    public event Action<NetworkState>? NetworkChanged;

    public BatteryState Battery { get; private set; } = ReadBattery();
    public NetworkState Network { get; private set; } = ReadNetwork();

    public void Start()
    {
        try
        {
            PowerManager.BatteryStatusChanged += OnPower;
            PowerManager.RemainingChargePercentChanged += OnPower;
            PowerManager.PowerSupplyStatusChanged += OnPower;
            PowerManager.EnergySaverStatusChanged += OnPower;
        }
        catch (Exception ex) { Log.Warn("Power notifications unavailable", ex); }
        NetworkInformation.NetworkStatusChanged += OnNetwork;
    }

    private void OnPower(object? sender, object e) { Battery = ReadBattery(); BatteryChanged?.Invoke(Battery); }
    private void OnNetwork(object? sender) { Network = ReadNetwork(); NetworkChanged?.Invoke(Network); }

    public static BatteryState ReadBattery()
    {
        Win32.GetSystemPowerStatus(out var s);
        bool present = s.BatteryFlag != 128 && s.BatteryFlag != 255;
        bool saver = false;
        try { saver = PowerManager.EnergySaverStatus == EnergySaverStatus.On; } catch { }
        TimeSpan? remaining = s.BatteryLifeTime > 0 ? TimeSpan.FromSeconds(s.BatteryLifeTime) : null;
        return new BatteryState(present, s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 100, (s.BatteryFlag & 8) != 0, s.ACLineStatus == 1, saver, remaining);
    }

    public static NetworkState ReadNetwork()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p == null) return new NetworkState(false, false, false, "Not connected", 0, false);
            var level = p.GetNetworkConnectivityLevel();
            bool wifi = p.IsWlanConnectionProfile;
            string name = wifi ? p.WlanConnectionProfileDetails?.GetConnectedSsid() ?? p.ProfileName : p.ProfileName;
            int bars = p.GetSignalBars() ?? (wifi ? 3 : 5);
            bool ethernet = p.NetworkAdapter?.IanaInterfaceType == 6;
            return new NetworkState(level != NetworkConnectivityLevel.None, level == NetworkConnectivityLevel.InternetAccess, wifi, name, bars, ethernet);
        }
        catch { return new NetworkState(NetworkInterface.GetIsNetworkAvailable(), NetworkInterface.GetIsNetworkAvailable(), false, "Network", 3, false); }
    }

    public void Dispose()
    {
        try
        {
            PowerManager.BatteryStatusChanged -= OnPower;
            PowerManager.RemainingChargePercentChanged -= OnPower;
            PowerManager.PowerSupplyStatusChanged -= OnPower;
            PowerManager.EnergySaverStatusChanged -= OnPower;
        }
        catch { }
        NetworkInformation.NetworkStatusChanged -= OnNetwork;
    }
}

/// <summary>Wi-Fi and Bluetooth radios (Windows.Devices.Radios). Only radios that exist are exposed.</summary>
public static class Radios
{
    public sealed record RadioInfo(string Name, RadioKind Kind, bool On);

    public static async Task<bool> RequestAccessAsync()
    {
        try { return await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed; }
        catch { return false; }
    }

    public static async Task<List<RadioInfo>> ListAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            return radios.Where(r => r.Kind is RadioKind.WiFi or RadioKind.Bluetooth)
                         .Select(r => new RadioInfo(r.Name, r.Kind, r.State == RadioState.On)).ToList();
        }
        catch { return []; }
    }

    public static async Task<bool> SetAsync(RadioKind kind, bool on)
    {
        try
        {
            if (!await RequestAccessAsync()) return false;
            var radio = (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == kind);
            if (radio == null) return false;
            return await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch (Exception ex) { Log.Warn($"Could not switch {kind}", ex); return false; }
    }
}

/// <summary>Display brightness: WMI for built-in panels, DDC/CI for external monitors that support it.</summary>
public static class Brightness
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR { public IntPtr hPhysicalMonitor; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription; }
    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hmon, out uint n);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hmon, uint n, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] private static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetMonitorBrightness(IntPtr h, uint value);

    /// <summary>Current brightness 0..100, or null when no display exposes brightness control.</summary>
    public static int? Get()
    {
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"));
            foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        int? ddc = null;
        ForEachPhysical(h => { if (ddc == null && GetMonitorBrightness(h, out var min, out var cur, out var max) && max > min) ddc = (int)Math.Round(100.0 * (cur - min) / (max - min)); });
        return ddc;
    }

    public static bool Set(int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        bool ok = false;
        try
        {
            using var s = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\wmi"), new ObjectQuery("SELECT * FROM WmiMonitorBrightnessMethods"));
            foreach (ManagementObject o in s.Get()) { o.InvokeMethod("WmiSetBrightness", [1u, (byte)percent]); ok = true; }
        }
        catch { }
        if (ok) return true;
        ForEachPhysical(h =>
        {
            if (GetMonitorBrightness(h, out var min, out _, out var max) && max > min)
                ok |= SetMonitorBrightness(h, (uint)(min + (max - min) * percent / 100.0));
        });
        return ok;
    }

    private static void ForEachPhysical(Action<IntPtr> action)
    {
        foreach (var m in VOS.Platform.Windowing.Displays.All())
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m.Handle, out uint n) || n == 0) continue;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(m.Handle, n, arr)) continue;
            try { foreach (var p in arr) action(p.hPhysicalMonitor); }
            finally { DestroyPhysicalMonitors(n, arr); }
        }
    }
}
