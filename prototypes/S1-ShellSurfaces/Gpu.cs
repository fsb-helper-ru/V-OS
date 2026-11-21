using System.Runtime.InteropServices;

namespace S1;

/// <summary>GPU evidence: the default DXGI adapter (what WinUI renders on) and per-process GPU engine utilization (PDH).</summary>
internal static unsafe class Gpu
{
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    public static (string Name, long DedicatedMB) DefaultAdapter()
    {
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref iid, out IntPtr factory) < 0) return ("unavailable", 0);
        try
        {
            var vtbl = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[12];
            IntPtr adapter;
            if (enumAdapters1(factory, 0, &adapter) < 0) return ("none", 0);
            try
            {
                var avtbl = *(IntPtr**)adapter;
                var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)avtbl[10];
                byte* desc = stackalloc byte[512];
                getDesc1(adapter, desc);
                return (new string((char*)desc), (long)(*(ulong*)(desc + 272) / (1024 * 1024)));
            }
            finally { Marshal.Release(adapter); }
        }
        finally { Marshal.Release(factory); }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? src, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>Samples "\GPU Engine(*)\Utilization Percentage" (locale-independent English path) for one process.</summary>
    public sealed class EngineSampler : IDisposable
    {
        private readonly IntPtr _query, _counter;

        public EngineSampler()
        {
            PdhOpenQueryW(null, IntPtr.Zero, out _query);
            PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter);
            PdhCollectQueryData(_query);
        }

        public Dictionary<string, double> Read(int pid)
        {
            PdhCollectQueryData(_query);
            uint size = 0;
            PdhGetFormattedCounterArrayW(_counter, 0x200 /*PDH_FMT_DOUBLE*/, ref size, out uint count, IntPtr.Zero);
            var result = new Dictionary<string, double>();
            if (size == 0) return result;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_counter, 0x200, ref size, out count, buf) != 0) return result;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buf + i * 24;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                    if (!name.StartsWith($"pid_{pid}_", StringComparison.Ordinal)) continue;
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    int at = name.IndexOf("engtype_", StringComparison.Ordinal);
                    string engine = at >= 0 ? name[(at + 8)..] : name;
                    result[engine] = Math.Round(result.GetValueOrDefault(engine) + value, 2);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }

        public void Dispose() => PdhCloseQuery(_query);
    }
}
