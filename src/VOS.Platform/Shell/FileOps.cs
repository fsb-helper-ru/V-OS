using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using VOS.Core;

namespace VOS.Platform.Shell;

/// <summary>
/// File operations through the Windows shell where it matters: copy, move and delete use SHFileOperation, so people
/// get Windows' own progress, conflict and permission prompts, and Ctrl+Z in Explorer undoes them. Deleting always
/// goes to the Recycle Bin.
/// </summary>
public static class FileOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT { public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags; public int fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize; public uint fMask; public IntPtr hwnd; public string? lpVerb; public string lpFile; public string? lpParameters; public string? lpDirectory;
        public int nShow; public IntPtr hInstApp; public IntPtr lpIDList; public string? lpClass; public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon; public IntPtr hProcess;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO info);

    private const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMMKDIR = 0x200;

    private static string Multi(IEnumerable<string> paths) => string.Join('\0', paths.Select(p => p.TrimEnd('\\'))) + "\0\0";

    /// <summary>Moves items to the Recycle Bin (recoverable).</summary>
    public static bool Recycle(IEnumerable<string> paths, IntPtr owner = default)
    {
        var op = new SHFILEOPSTRUCT { hwnd = owner, wFunc = FO_DELETE, pFrom = Multi(paths), fFlags = FOF_ALLOWUNDO | FOF_NOERRORUI };
        return SHFileOperationW(ref op) == 0 && op.fAnyOperationsAborted == 0;
    }

    /// <summary>Copies into a folder. Items copied into their own folder get "- Copy" names instead of conflicts.</summary>
    public static Task<bool> CopyAsync(IReadOnlyList<string> sources, string destinationFolder, IntPtr owner) =>
        RunAsync(FO_COPY, sources, destinationFolder, owner,
            sources.All(s => string.Equals(Path.GetDirectoryName(s.TrimEnd('\\')), destinationFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) ? FOF_RENAMEONCOLLISION : (ushort)0);

    public static Task<bool> MoveAsync(IReadOnlyList<string> sources, string destinationFolder, IntPtr owner) =>
        RunAsync(FO_MOVE, sources, destinationFolder, owner, 0);

    public static Task<bool> RecycleAsync(IReadOnlyList<string> paths, IntPtr owner) => RunAsync(FO_DELETE, paths, null, owner, 0);

    /// <summary>Runs a shell file operation on its own STA thread (it shows modal progress and prompts).</summary>
    private static Task<bool> RunAsync(uint func, IReadOnlyList<string> sources, string? destination, IntPtr owner, ushort extra)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = owner, wFunc = func, pFrom = Multi(sources), pTo = destination != null ? destination.TrimEnd('\\') + "\0\0" : null,
                    fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR | extra),
                };
                int r = SHFileOperationW(ref op);
                if (r != 0) Log.Info("Shell file operation ended", new { func, result = $"0x{r:X}" });
                done.TrySetResult(r == 0 && op.fAnyOperationsAborted == 0);
            }
            catch (Exception ex) { Log.Warn("Shell file operation failed", ex); done.TrySetResult(false); }
        }) { IsBackground = true, Name = "V-OS file operation" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return done.Task;
    }

    /// <summary>Opens the Windows Properties dialog for a file or folder.</summary>
    public static void ShowProperties(string path, IntPtr owner = default)
    {
        var info = new SHELLEXECUTEINFO { cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(), fMask = 0x0000000C /*SEE_MASK_INVOKEIDLIST*/, hwnd = owner, lpVerb = "properties", lpFile = path, nShow = 1 };
        if (!ShellExecuteExW(ref info)) Log.Warn("Properties dialog failed", null, new { error = Marshal.GetLastWin32Error() });
    }

    /// <summary>Shows Windows' "Open with" chooser.</summary>
    public static void OpenWith(string path)
    {
        try { Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{path}\"") { UseShellExecute = false }); }
        catch (Exception ex) { Log.Warn("Open with failed", ex); }
    }

    /// <summary>"New folder", "New folder (2)", … — the first name not taken in <paramref name="folder"/>.</summary>
    public static string UniqueName(string folder, string baseName, string extension = "")
    {
        string Candidate(int n) => Path.Combine(folder, n == 1 ? baseName + extension : $"{baseName} ({n}){extension}");
        for (int n = 1; n < 10000; n++)
        {
            var c = Candidate(n);
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
        return Candidate(Environment.TickCount);
    }

    /// <summary>Renames in place. Returns an error message, or null on success.</summary>
    public static string? Rename(string path, string newName)
    {
        newName = newName.Trim();
        if (newName.Length == 0) return "The name cannot be empty.";
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A name cannot contain any of these characters: \\ / : * ? \" < > |";
        if (newName is "." or ".." || newName.EndsWith('.') || newName.EndsWith(' ')) return "A name cannot end with a dot or a space.";
        var target = Path.Combine(Path.GetDirectoryName(path.TrimEnd('\\'))!, newName);
        if (string.Equals(target, path, StringComparison.Ordinal)) return null;
        bool caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(target) || Directory.Exists(target))) return $"“{newName}” already exists here.";
        try
        {
            if (Directory.Exists(path))
            {
                if (caseOnly) { var tmp = path + ".vos-rename"; Directory.Move(path, tmp); Directory.Move(tmp, target); }
                else Directory.Move(path, target);
            }
            else File.Move(path, target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
    }

    /// <summary>Creates a ZIP of files and folders (folders keep their structure). Deletes the partial archive on failure or cancel.</summary>
    public static async Task CompressAsync(IReadOnlyList<string> items, string zipPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            var files = new List<(string full, string entry)>();
            foreach (var item in items)
            {
                if (Directory.Exists(item))
                {
                    var root = Path.GetDirectoryName(item.TrimEnd('\\'))!;
                    foreach (var f in Directory.EnumerateFiles(item, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
                        files.Add((f, Path.GetRelativePath(root, f).Replace('\\', '/')));
                }
                else if (File.Exists(item)) files.Add((item, Path.GetFileName(item)));
            }
            long total = Math.Max(1, files.Sum(f => new FileInfo(f.full).Length)), doneBytes = 0;
            try
            {
                using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
                foreach (var (full, entry) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try { zip.CreateEntryFromFile(full, entry, CompressionLevel.Optimal); }
                    catch (IOException ex) { Log.Warn("Skipped a file while compressing", ex, new { full }); }
                    doneBytes += new FileInfo(full).Length;
                    progress?.Report((double)doneBytes / total);
                }
            }
            catch
            {
                try { File.Delete(zipPath); } catch { }
                throw;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Extracts a ZIP into a new folder (never overwrites; entries escaping the folder are rejected by .NET).</summary>
    public static async Task<string> ExtractAsync(string zipPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var folder = UniqueName(Path.GetDirectoryName(zipPath)!, Path.GetFileNameWithoutExtension(zipPath));
        await Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                int i = 0, n = Math.Max(1, zip.Entries.Count);
                var root = Path.GetFullPath(folder + Path.DirectorySeparatorChar);
                foreach (var e in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var dest = Path.GetFullPath(Path.Combine(folder, e.FullName));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Log.Warn("Rejected an archive entry outside the target folder", null, new { e.FullName }); continue; }
                    if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) Directory.CreateDirectory(dest);
                    else { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); e.ExtractToFile(dest, overwrite: false); }
                    progress?.Report(++i / (double)n);
                }
            }
            catch (OperationCanceledException) { try { Directory.Delete(folder, true); } catch { } throw; }
        }, ct).ConfigureAwait(false);
        return folder;
    }
}
