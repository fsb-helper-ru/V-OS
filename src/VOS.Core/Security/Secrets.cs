using System.Runtime.InteropServices;
using System.Text;

namespace VOS.Core.Security;

/// <summary>DPAPI (current-user scope) protection for secrets such as API keys stored in settings.json.</summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);

    public static bool IsProtected(string? value) => value?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var result = Transform(bytes, protect: true);
        return Prefix + Convert.ToBase64String(result);
    }

    /// <summary>Returns the plain value, or null when the value is missing or cannot be decrypted for this user.</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!IsProtected(stored)) return stored;
        try { return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false)); }
        catch { return null; }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var outBlob = new DATA_BLOB();
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
            bool ok = protect
                ? CryptProtectData(ref inBlob, "V-OS", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var output = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, output, 0, outBlob.cbData);
            return output;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
