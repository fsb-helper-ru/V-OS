using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace VOS.Platform.Shell;

// Classic COM interop for the Windows Shell. Note (DECISIONS ADR-017): convert to [GeneratedComInterface]
// before enabling Native AOT.

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    void Compare(IShellItem psi, uint hint, out int piOrder);
}

[ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem2 : IShellItem
{
    new void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    new void GetParent(out IShellItem ppsi);
    new void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
    new void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    new void Compare(IShellItem psi, uint hint, out int piOrder);
    void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    void GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
    void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
    void GetPropertyDescriptionList(ref PROPERTYKEY keyType, ref Guid riid, out IntPtr ppv);
    void Update(IntPtr pbc);
    void GetProperty(ref PROPERTYKEY key, out PROPVARIANT ppropvar);
    void GetCLSID(ref PROPERTYKEY key, out Guid pclsid);
    void GetFileTime(ref PROPERTYKEY key, out FILETIME pft);
    void GetInt32(ref PROPERTYKEY key, out int pi);
    [PreserveSig] int GetString(ref PROPERTYKEY key, [MarshalAs(UnmanagedType.LPWStr)] out string ppsz);
    void GetUInt32(ref PROPERTYKEY key, out uint pui);
    void GetUInt64(ref PROPERTYKEY key, out ulong pull);
    void GetBool(ref PROPERTYKEY key, out int pf);
}

[ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumShellItems
{
    [PreserveSig] int Next(uint celt, [MarshalAs(UnmanagedType.Interface)] out IShellItem rgelt, out uint pceltFetched);
    void Skip(uint celt);
    void Reset();
    void Clone(out IEnumShellItems ppenum);
}

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPropertyStore
{
    void GetCount(out uint cProps);
    void GetAt(uint iProp, out PROPERTYKEY pkey);
    [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
    void Commit();
}

public enum SIGDN : uint
{
    NORMALDISPLAY = 0, PARENTRELATIVEPARSING = 0x80018001, DESKTOPABSOLUTEPARSING = 0x80028000,
    FILESYSPATH = 0x80058000, URL = 0x80068000,
}

[Flags]
public enum SIIGBF : uint { RESIZETOFIT = 0, BIGGERSIZEOK = 1, MEMORYONLY = 2, ICONONLY = 4, THUMBNAILONLY = 8, INCACHEONLY = 0x10, SCALEUP = 0x100 }

[StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PROPERTYKEY
{
    public Guid fmtid; public uint pid;
    public PROPERTYKEY(Guid g, uint p) { fmtid = g; pid = p; }
    public static readonly PROPERTYKEY AppUserModelId = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);
    public static readonly PROPERTYKEY LinkTargetParsingPath = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);
    public static readonly PROPERTYKEY DeviceFriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    public static readonly PROPERTYKEY DeviceInterfaceFriendlyName = new(new Guid("026E516E-B814-414B-83CD-856D6FEF4822"), 2);
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct PROPVARIANT
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointer;
    [FieldOffset(8)] public uint uintVal;

    public readonly string? AsString() => vt == 31 /*VT_LPWSTR*/ && pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pointer) : null;

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);
    public void Clear() => PropVariantClear(ref this);
}

public static class ShellNative
{
    public static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    public static readonly Guid IID_IShellItem2 = new("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
    public static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    public static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    public static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    public static readonly Guid FOLDERID_AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll", PreserveSig = false)]
    public static extern void SHGetKnownFolderItem(ref Guid kfid, uint flags, IntPtr token, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    [DllImport("shell32.dll")]
    public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    public static string? KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string Downloads => KnownFolderPath(FOLDERID_Downloads) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>AppUserModelID set on a window (packaged apps and apps that set it explicitly), or null.</summary>
    public static string? WindowAppId(IntPtr hwnd)
    {
        var iid = IID_IPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null) return null;
        try
        {
            var key = PROPERTYKEY.AppUserModelId;
            if (store.GetValue(ref key, out var pv) != 0) return null;
            var s = pv.AsString();
            pv.Clear();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        finally { Marshal.ReleaseComObject(store); }
    }
}
