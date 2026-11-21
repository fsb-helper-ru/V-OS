using System.Diagnostics;
using System.Runtime.InteropServices;
using VOS.Core;
using VOS.Platform.Native;

namespace VOS.Platform.Shell;

/// <summary>An installed app from the Windows Apps folder (desktop and Store apps alike).</summary>
public sealed record InstalledApp(string Name, string ParsingName, string? ExePath, bool IsPackaged)
{
    /// <summary>Parsing name usable with the shell ("shell:AppsFolder\..."), also the icon source.</summary>
    public string ShellPath => @"shell:AppsFolder\" + ParsingName;
    public string Key => (ExePath ?? ParsingName).ToLowerInvariant();
}

/// <summary>Enumerates shell:AppsFolder: the same list the Start menu uses, including Store apps.</summary>
public static class AppCatalog
{
    private static List<InstalledApp>? s_cache;
    private static DateTime s_loaded;
    private static readonly object s_gate = new();

    public static IReadOnlyList<InstalledApp> Get(bool refresh = false)
    {
        lock (s_gate)
        {
            if (!refresh && s_cache != null && DateTime.UtcNow - s_loaded < TimeSpan.FromMinutes(10)) return s_cache;
            var sw = Stopwatch.StartNew();
            s_cache = Enumerate();
            s_loaded = DateTime.UtcNow;
            Log.Info("App catalog loaded", new { count = s_cache.Count, ms = sw.ElapsedMilliseconds });
            return s_cache;
        }
    }

    private static List<InstalledApp> Enumerate()
    {
        var result = new List<InstalledApp>();
        IShellItem? folder = null;
        IntPtr enumPtr = IntPtr.Zero;
        try
        {
            var kf = ShellNative.FOLDERID_AppsFolder;
            var iidItem = ShellNative.IID_IShellItem;
            ShellNative.SHGetKnownFolderItem(ref kf, 0, IntPtr.Zero, ref iidItem, out folder);
            var bhid = ShellNative.BHID_EnumItems;
            var iidEnum = ShellNative.IID_IEnumShellItems;
            folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out enumPtr);
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
            try
            {
                while (items.Next(1, out var item, out uint fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        item.GetDisplayName(SIGDN.NORMALDISPLAY, out var name);
                        item.GetDisplayName(SIGDN.PARENTRELATIVEPARSING, out var parsing);
                        string? exe = null;
                        if (item is IShellItem2 item2)
                        {
                            var key = PROPERTYKEY.LinkTargetParsingPath;
                            if (item2.GetString(ref key, out var target) == 0 && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = target;
                        }
                        exe ??= ResolveKnownFolderPath(parsing);
                        bool packaged = parsing.Contains('!') && !parsing.Contains('\\');
                        if (string.IsNullOrWhiteSpace(name) || IsNoise(name, exe)) continue;
                        result.Add(new InstalledApp(name, parsing, exe, packaged));
                    }
                    catch (COMException) { }
                    finally { Marshal.ReleaseComObject(item); }
                }
            }
            finally { Marshal.ReleaseComObject(items); }
        }
        catch (Exception ex) { Log.Error("Could not enumerate installed apps", ex); }
        finally
        {
            if (enumPtr != IntPtr.Zero) Marshal.Release(enumPtr);
            if (folder != null) Marshal.ReleaseComObject(folder);
        }
        return result.GroupBy(a => a.Name + "|" + (a.ExePath ?? a.ParsingName), StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.First()).OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Desktop apps appear as "{KNOWNFOLDERID}\relative\path.exe"; expand to a real path when possible.</summary>
    private static string? ResolveKnownFolderPath(string parsing)
    {
        if (!parsing.StartsWith('{')) return parsing.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(parsing) ? parsing : null;
        int close = parsing.IndexOf('}');
        if (close < 0 || !Guid.TryParse(parsing[..(close + 1)], out var id)) return null;
        var root = ShellNative.KnownFolderPath(id);
        if (root == null) return null;
        var full = Path.Combine(root, parsing[(close + 1)..].TrimStart('\\'));
        return full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool IsNoise(string name, string? exe)
    {
        string[] noise = ["uninstall", "удалить", "readme", "help", "documentation", "release notes", "website"];
        if (noise.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return true;
        return exe != null && Path.GetFileName(exe).StartsWith("unins", StringComparison.OrdinalIgnoreCase);
    }

    public static InstalledApp? FindByExe(string exePath) =>
        Get().FirstOrDefault(a => a.ExePath != null && string.Equals(a.ExePath, exePath, StringComparison.OrdinalIgnoreCase));

    public static InstalledApp? FindByAppId(string appId) =>
        Get().FirstOrDefault(a => string.Equals(a.ParsingName, appId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Launching and shell operations. Uses ShellExecute so Windows applies its normal policies and prompts.</summary>
public static class ShellOps
{
    public static bool Launch(string target, string? arguments = null, string? workingDirectory = null, string verb = "open")
    {
        var info = new Win32.SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<Win32.SHELLEXECUTEINFOW>(),
            fMask = Win32.SEE_MASK_FLAG_NO_UI | Win32.SEE_MASK_ASYNCOK,
            lpVerb = verb,
            lpFile = target,
            lpParameters = arguments,
            lpDirectory = workingDirectory,
            nShow = Win32.SW_SHOWNORMAL,
        };
        bool ok = Win32.ShellExecuteExW(ref info);
        if (!ok) Log.Warn("Launch failed", null, new { target, error = Marshal.GetLastWin32Error() });
        return ok;
    }

    public static bool LaunchApp(InstalledApp app) => app.ExePath != null && File.Exists(app.ExePath)
        ? Launch(app.ExePath, null, Path.GetDirectoryName(app.ExePath))
        : Launch(app.ShellPath);

    public static bool OpenUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == "ms-settings" || u.Scheme == "mailto")
        && Launch(u.AbsoluteUri);

    public static void ShowInExplorer(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    public static void OpenProperties(string path) => Launch(path, verb: "properties");

    public static (long bytes, long items) RecycleBinInfo()
    {
        var info = new Win32.SHQUERYRBINFO { cbSize = Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
        return Win32.SHQueryRecycleBinW(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin. Callers must have obtained explicit user confirmation first.</summary>
    public static bool EmptyRecycleBin() => Win32.SHEmptyRecycleBinW(IntPtr.Zero, null, Win32.SHERB_NOCONFIRMATION | Win32.SHERB_NOSOUND) is 0 or unchecked((int)0x8000FFFF);

    public static void Lock() => Win32.LockWorkStation();
    public static void Sleep() => Win32.SetSuspendState(false, false, false);
    public static void SignOut() => Win32.ExitWindowsEx(0 /*EWX_LOGOFF*/, 0);
    public static void Restart() => Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
    public static void ShutDown() => Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });

    public static void OpenWindowsSettings(string page = "") => Launch("ms-settings:" + page);

    /// <summary>Newest file in Downloads (excluding partial downloads).</summary>
    public static FileInfo? LatestDownload()
    {
        try
        {
            var dir = new DirectoryInfo(ShellNative.Downloads);
            return dir.Exists
                ? dir.EnumerateFiles().Where(f => !f.Name.EndsWith(".crdownload", StringComparison.OrdinalIgnoreCase) && !f.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) && !f.Attributes.HasFlag(FileAttributes.Hidden))
                      .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                : null;
        }
        catch { return null; }
    }
}
