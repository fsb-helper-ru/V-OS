using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using VOS.Core;
using VOS.Core.Setup;

namespace VOS.Launcher;

/// <summary>
/// Registers an installation with Windows, all per-user (ADR-005): the Apps entry (uninstall, modify), and Start-menu
/// shortcuts. Idempotent, so it also refreshes the shown version after updates and rollbacks. A redirected
/// installation (tests) registers nothing. Removal is shared with the bootstrapper: <see cref="InstallRegistration"/>.
/// </summary>
internal static class Registration
{
    public static bool CanRegister(InstallLayout layout) => !InstallLayout.IsRedirected && layout.IsActiveApp(AppContext.BaseDirectory);

    public static void RegisterEssentials(InstallLayout layout)
    {
        if (!CanRegister(layout)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(InstallRegistration.UninstallKeyPath, writable: true);
            var bootstrapper = layout.Bootstrapper;
            key.SetValue("DisplayName", AppInfo.ProductName);
            key.SetValue("DisplayVersion", AppInfo.Version);
            key.SetValue("Publisher", AppInfo.Publisher);
            key.SetValue("Comments", "Desktop environment for Windows");
            key.SetValue("DisplayIcon", $"\"{layout.AppExe}\",0");
            key.SetValue("InstallLocation", layout.Root);
            key.SetValue("UninstallString", $"\"{bootstrapper}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{bootstrapper}\" --uninstall --quiet");
            key.SetValue("ModifyPath", $"\"{layout.AppExe}\" --launcher");
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, InstallLayout.DirectorySize(layout.App) / 1024), RegistryValueKind.DWord);
            if (key.GetValue("InstallDate") == null) key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(InstallRegistration.StartMenu);
            CreateShortcut(InstallRegistration.ShellShortcut, layout.AppExe, "--shell", "Start V-OS", layout.App);
            CreateShortcut(InstallRegistration.LauncherShortcut, layout.AppExe, "--launcher", "Install updates, repair, roll back or uninstall V-OS", layout.App);
            var state = layout.ReadState();
            if (state != null && !state.Registered) { state.Registered = true; layout.WriteState(state); }
            Log.Info("Installation registered", new { AppInfo.Version });
        }
        catch (Exception ex) { Log.Warn("Could not register the installation", ex); }
    }

    public static bool HasDesktopShortcut => File.Exists(InstallRegistration.DesktopShortcut);

    public static void SetDesktopShortcut(InstallLayout layout, bool on)
    {
        if (!CanRegister(layout)) return;
        try
        {
            if (on) CreateShortcut(InstallRegistration.DesktopShortcut, layout.AppExe, "--shell", "Start V-OS", layout.App);
            else if (File.Exists(InstallRegistration.DesktopShortcut)) File.Delete(InstallRegistration.DesktopShortcut);
        }
        catch (Exception ex) { Log.Warn("Could not update the desktop shortcut", ex); }
    }

    private static void CreateShortcut(string path, string target, string args, string description, string workingDirectory)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(args);
            link.SetDescription(description);
            link.SetWorkingDirectory(workingDirectory);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
