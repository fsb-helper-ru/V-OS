using System.Runtime.InteropServices;
using VOS.Core;
using VOS.Platform.Native;

namespace VOS.Platform.Sys;

public sealed class ProcessInfo
{
    public int Id { get; init; }
    public int ParentId { get; init; }
    public string Name { get; init; } = "";
    public int SessionId { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public long WorkingSet { get; init; }
    public long PrivateBytes { get; init; }
    /// <summary>User + kernel CPU time in 100 ns units.</summary>
    public long CpuTime { get; init; }
    public DateTime Started { get; init; }
    /// <summary>Share of the whole machine since the previous snapshot (0–100).</summary>
    public double CpuPercent { get; set; }
    public string? Path { get; set; }
}

/// <summary>
/// Process snapshots from a single NtQuerySystemInformation(SystemProcessInformation) call: CPU time, memory, threads
/// and handles for every process, including protected ones, without opening a handle to each (unlike
/// System.Diagnostics.Process). CPU percentages come from the difference between two snapshots.
/// </summary>
public sealed class ProcessSampler
{
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returned);
    private const int SystemProcessInformation = 5, STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    private Dictionary<int, (long cpu, DateTime started)> _previous = [];
    private long _previousTick;
    private readonly Dictionary<int, string?> _paths = [];
    private int _bufferSize = 512 * 1024;

    public IReadOnlyList<ProcessInfo> Sample()
    {
        var list = Read();
        long tick = DateTime.UtcNow.Ticks;
        double elapsed = _previousTick == 0 ? 0 : tick - _previousTick;
        int cores = Environment.ProcessorCount;
        var current = new Dictionary<int, (long, DateTime)>(list.Count);
        foreach (var p in list)
        {
            current[p.Id] = (p.CpuTime, p.Started);
            if (elapsed > 0 && _previous.TryGetValue(p.Id, out var prev) && prev.started == p.Started)
                p.CpuPercent = Math.Clamp((p.CpuTime - prev.cpu) / elapsed / cores * 100, 0, 100);
            if (!_paths.TryGetValue(p.Id, out var path)) { path = p.Id > 4 ? Win32.GetProcessPath((uint)p.Id) : null; _paths[p.Id] = path; }
            p.Path = path;
        }
        foreach (var gone in _paths.Keys.Where(k => !current.ContainsKey(k)).ToList()) _paths.Remove(gone);
        _previous = current;
        _previousTick = tick;
        return list;
    }

    private unsafe List<ProcessInfo> Read()
    {
        while (true)
        {
            var buffer = Marshal.AllocHGlobal(_bufferSize);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, _bufferSize, out int needed);
                if (status == STATUS_INFO_LENGTH_MISMATCH) { _bufferSize = Math.Max(needed, _bufferSize * 2) + 64 * 1024; continue; }
                if (status != 0) { Log.Warn($"NtQuerySystemInformation failed (0x{status:X8})"); return []; }
                var list = new List<ProcessInfo>(400);
                byte* p = (byte*)buffer;
                while (true)
                {
                    // SYSTEM_PROCESS_INFORMATION (x64 layout).
                    uint next = *(uint*)p;
                    int threads = *(int*)(p + 4);
                    long create = *(long*)(p + 32), user = *(long*)(p + 40), kernel = *(long*)(p + 48);
                    ushort nameBytes = *(ushort*)(p + 56);
                    IntPtr nameBuffer = *(IntPtr*)(p + 64);
                    int pid = (int)*(long*)(p + 80);
                    int parent = (int)*(long*)(p + 88);
                    int handles = *(int*)(p + 96);
                    int session = *(int*)(p + 100);
                    long workingSet = *(long*)(p + 144);
                    long privateBytes = *(long*)(p + 184);
                    string name = pid == 0 ? "System Idle Process" : nameBytes > 0 && nameBuffer != IntPtr.Zero ? new string((char*)nameBuffer, 0, nameBytes / 2) : "System";
                    list.Add(new ProcessInfo
                    {
                        Id = pid, ParentId = parent, Name = name, SessionId = session, Threads = threads, Handles = handles,
                        WorkingSet = workingSet, PrivateBytes = privateBytes, CpuTime = pid == 0 ? 0 : user + kernel,
                        Started = create > 0 ? DateTime.FromFileTimeUtc(create) : DateTime.MinValue,
                    });
                    if (next == 0) break;
                    p += next;
                }
                return list;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    /// <summary>Processes whose termination crashes or destabilises Windows. V-OS never offers to end them.</summary>
    public static bool IsCritical(ProcessInfo p) => p.Id <= 4 || p.Name.ToLowerInvariant() is
        "csrss.exe" or "wininit.exe" or "winlogon.exe" or "smss.exe" or "services.exe" or "lsass.exe" or "lsaiso.exe" or
        "dwm.exe" or "fontdrvhost.exe" or "registry" or "memory compression" or "secure system" or "system" or "svchost.exe" or "sihost.exe";

    /// <summary>Ends a process the user owns. Returns an error message, or null on success.</summary>
    public static string? End(ProcessInfo p)
    {
        if (IsCritical(p)) return $"{p.Name} is part of Windows and cannot be ended safely.";
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(p.Id);
            // Same PID, different process (the original exited and the ID was reused): never end the wrong one.
            if (p.Started != DateTime.MinValue && Math.Abs((proc.StartTime.ToUniversalTime() - p.Started).TotalSeconds) > 1) return "The process has already exited.";
            proc.Kill(entireProcessTree: false);
            return null;
        }
        catch (ArgumentException) { return "The process has already exited."; }
        catch (System.ComponentModel.Win32Exception) { return $"Windows did not allow ending {p.Name}. It may belong to another user or run as administrator."; }
        catch (InvalidOperationException) { return "The process has already exited."; }
    }
}
