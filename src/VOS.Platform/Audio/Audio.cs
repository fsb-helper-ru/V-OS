using System.Diagnostics;
using System.Runtime.InteropServices;
using VOS.Core;
using VOS.Platform.Shell;

namespace VOS.Platform.Audio;

#region Core Audio COM declarations (vtable order matters)

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
    void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
    void OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
    void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float db);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
    [PreserveSig] int VolumeStepUp(ref Guid ctx);
    [PreserveSig] int VolumeStepDown(ref Guid ctx);
    [PreserveSig] int QueryHardwareSupport(out uint mask);
    [PreserveSig] int GetVolumeRange(out float min, out float max, out float inc);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr data); }

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr guid, uint flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, uint flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator e);
    [PreserveSig] int RegisterSessionNotification(IntPtr n);
    [PreserveSig] int UnregisterSessionNotification(IntPtr n);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr n);
    [PreserveSig] int UnregisterDuckNotification(IntPtr n);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
    [PreserveSig] int GetGroupingParam(out Guid g);
    [PreserveSig] int SetGroupingParam(ref Guid g, ref Guid ctx);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>Undocumented but long-stable interface used by Windows' own sound settings to change the default device.</summary>
[ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(string id, IntPtr fmt);
    [PreserveSig] int GetDeviceFormat(string id, int def, IntPtr fmt);
    [PreserveSig] int ResetDeviceFormat(string id);
    [PreserveSig] int SetDeviceFormat(string id, IntPtr a, IntPtr b);
    [PreserveSig] int GetProcessingPeriod(string id, int def, IntPtr a, IntPtr b);
    [PreserveSig] int SetProcessingPeriod(string id, IntPtr a);
    [PreserveSig] int GetShareMode(string id, IntPtr mode);
    [PreserveSig] int SetShareMode(string id, IntPtr mode);
    [PreserveSig] int GetPropertyValue(string id, IntPtr key, IntPtr pv);
    [PreserveSig] int SetPropertyValue(string id, IntPtr key, IntPtr pv);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    [PreserveSig] int SetEndpointVisibility(string id, int visible);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] internal class MMDeviceEnumeratorCom { }
[ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")] internal class PolicyConfigCom { }

#endregion

public sealed record AudioDevice(string Id, string Name, bool IsDefault);
public sealed record AudioSessionInfo(uint ProcessId, string Name, string? ExePath, float Volume, bool Muted);

/// <summary>Master volume, mute, output devices and per-app volume for the default render endpoint.</summary>
public sealed class AudioService : IDisposable
{
    private readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
    private IAudioEndpointVolume? _volume;
    private readonly VolumeCallback _callback;
    private readonly DeviceNotifier _notifier;
    private static Guid s_ctx = Guid.NewGuid();

    public event Action<float, bool>? VolumeChanged;
    public event Action<AudioDevice?>? DefaultDeviceChanged;

    public AudioService()
    {
        _callback = new VolumeCallback(this);
        _notifier = new DeviceNotifier(this);
        _enumerator.RegisterEndpointNotificationCallback(_notifier);
        AttachDefault();
    }

    private void AttachDefault()
    {
        try
        {
            if (_volume != null) { _volume.UnregisterControlChangeNotify(_callback); Marshal.ReleaseComObject(_volume); _volume = null; }
            if (_enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 1 /*eMultimedia*/, out var device) != 0 || device == null) return;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 1 /*CLSCTX_INPROC*/, IntPtr.Zero, out var obj) == 0)
            {
                _volume = (IAudioEndpointVolume)obj;
                _volume.RegisterControlChangeNotify(_callback);
            }
            Marshal.ReleaseComObject(device);
        }
        catch (Exception ex) { Log.Warn("Audio endpoint unavailable", ex); }
    }

    public bool Available => _volume != null;

    public float Volume
    {
        get => _volume != null && _volume.GetMasterVolumeLevelScalar(out var v) == 0 ? v : 0;
        set => _volume?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 1), ref s_ctx);
    }

    public bool Muted
    {
        get => _volume != null && _volume.GetMute(out var m) == 0 && m;
        set => _volume?.SetMute(value, ref s_ctx);
    }

    public AudioDevice? DefaultDevice => Devices().FirstOrDefault(d => d.IsDefault);

    public List<AudioDevice> Devices()
    {
        var list = new List<AudioDevice>();
        try
        {
            string? defaultId = null;
            if (_enumerator.GetDefaultAudioEndpoint(0, 1, out var def) == 0 && def != null) { def.GetId(out defaultId); Marshal.ReleaseComObject(def); }
            if (_enumerator.EnumAudioEndpoints(0, 1 /*ACTIVE*/, out var coll) != 0) return list;
            coll.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (coll.Item(i, out var d) != 0) continue;
                d.GetId(out var id);
                list.Add(new AudioDevice(id, FriendlyName(d) ?? "Speakers", id == defaultId));
                Marshal.ReleaseComObject(d);
            }
            Marshal.ReleaseComObject(coll);
        }
        catch (Exception ex) { Log.Warn("Audio device enumeration failed", ex); }
        return list;
    }

    private static string? FriendlyName(IMMDevice d)
    {
        if (d.OpenPropertyStore(0, out var store) != 0) return null;
        try
        {
            var key = PROPERTYKEY.DeviceFriendlyName;
            if (store.GetValue(ref key, out var pv) != 0) return null;
            var s = pv.AsString();
            pv.Clear();
            return s;
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    /// <summary>Sets the default output for all roles. Uses an undocumented interface; returns false if unsupported.</summary>
    public bool SetDefaultDevice(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            bool ok = true;
            for (int role = 0; role < 3; role++) ok &= policy.SetDefaultEndpoint(id, role) == 0;
            Marshal.ReleaseComObject(policy);
            return ok;
        }
        catch (Exception ex) { Log.Warn("Switching the default audio device is not supported on this system", ex); return false; }
    }

    public List<AudioSessionInfo> Sessions()
    {
        var list = new List<AudioSessionInfo>();
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) return list;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 1, IntPtr.Zero, out var obj) != 0) return list;
            var mgr = (IAudioSessionManager2)obj;
            mgr.GetSessionEnumerator(out var e);
            e.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                if (e.GetSession(i, out var s) != 0) continue;
                s.GetProcessId(out uint pid);
                s.GetState(out int state);
                if (pid != 0 && state != 2 /*expired*/ && s.IsSystemSoundsSession() != 0 && s is ISimpleAudioVolume v)
                {
                    v.GetMasterVolume(out float vol); v.GetMute(out bool mute);
                    string? exe = Native.Win32.GetProcessPath(pid);
                    s.GetDisplayName(out var display);
                    string name = !string.IsNullOrWhiteSpace(display) && !display.StartsWith('@') ? display : ProcessDescription(exe) ?? $"Process {pid}";
                    list.Add(new AudioSessionInfo(pid, name, exe, vol, mute));
                }
                Marshal.ReleaseComObject(s);
            }
            Marshal.ReleaseComObject(e);
            Marshal.ReleaseComObject(mgr);
            Marshal.ReleaseComObject(device);
        }
        catch (Exception ex) { Log.Warn("Audio sessions unavailable", ex); }
        return list.GroupBy(s => s.ProcessId).Select(g => g.First()).ToList();
    }

    public void SetSessionVolume(uint pid, float? volume, bool? mute)
    {
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) return;
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 1, IntPtr.Zero, out var obj) != 0) return;
            var mgr = (IAudioSessionManager2)obj;
            mgr.GetSessionEnumerator(out var e);
            e.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                if (e.GetSession(i, out var s) != 0) continue;
                s.GetProcessId(out uint p);
                if (p == pid && s is ISimpleAudioVolume v)
                {
                    if (volume is float f) v.SetMasterVolume(Math.Clamp(f, 0, 1), ref s_ctx);
                    if (mute is bool m) v.SetMute(m, ref s_ctx);
                }
                Marshal.ReleaseComObject(s);
            }
        }
        catch (Exception ex) { Log.Warn("Could not change app volume", ex); }
    }

    private static string? ProcessDescription(string? exe)
    {
        if (exe == null) return null;
        try { var d = FileVersionInfo.GetVersionInfo(exe).FileDescription; return string.IsNullOrWhiteSpace(d) ? Path.GetFileNameWithoutExtension(exe) : d; }
        catch { return Path.GetFileNameWithoutExtension(exe); }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_notifier); } catch { }
        if (_volume != null) { try { _volume.UnregisterControlChangeNotify(_callback); } catch { } }
    }

    [ComVisible(true)]
    private sealed class VolumeCallback(AudioService owner) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            // AUDIO_VOLUME_NOTIFICATION_DATA: GUID (16) | BOOL muted (4) | float master (4) | ...
            bool muted = Marshal.ReadInt32(data, 16) != 0;
            float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            ThreadPool.QueueUserWorkItem(_ => owner.VolumeChanged?.Invoke(level, muted));
            return 0;
        }
    }

    [ComVisible(true)]
    private sealed class DeviceNotifier(AudioService owner) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string id, int state) { }
        public void OnDeviceAdded(string id) { }
        public void OnDeviceRemoved(string id) { }
        public void OnDefaultDeviceChanged(int flow, int role, string? id)
        {
            if (flow != 0 || role != 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                owner.AttachDefault();
                owner.DefaultDeviceChanged?.Invoke(owner.DefaultDevice);
            });
        }
        public void OnPropertyValueChanged(string id, PROPERTYKEY key) { }
    }
}
