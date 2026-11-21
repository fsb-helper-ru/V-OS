using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VOS.Bootstrap;

internal enum TaskIcon { App, Information, Warning, Error }

/// <summary>One task dialog: text, buttons, an optional progress bar and checkbox, and callbacks.</summary>
internal sealed class TaskPage
{
    public string Title { get; init; } = "V-OS";
    public string Heading { get; init; } = "";
    public string Content { get; init; } = "";
    public string? Details { get; init; }
    public string? Footer { get; init; }
    public TaskIcon Icon { get; init; } = TaskIcon.App;
    public IReadOnlyList<(int Id, string Text)> Buttons { get; init; } = [];
    public int DefaultButton { get; init; }
    /// <summary>TDCBF_* flags: 1 OK, 8 Cancel, 0x10 Retry, 0x20 Close.</summary>
    public uint CommonButtons { get; init; }
    public string? Checkbox { get; init; }
    public bool Progress { get; init; }
    public bool Marquee { get; init; }
    /// <summary>Runs about every 100 ms while the dialog is open (progress pages poll their work here).</summary>
    public Action<TaskDialogHandle>? Tick { get; init; }
    /// <summary>Return false to keep the dialog open after a button press.</summary>
    public Func<int, bool>? Clicked { get; init; }
}

/// <summary>Controls a task dialog from inside its callbacks.</summary>
internal readonly struct TaskDialogHandle(IntPtr hwnd)
{
    public void SetProgress(double fraction) => Native.SendMessage(hwnd, Native.TDM_SET_PROGRESS_BAR_POS, (nuint)Math.Clamp((int)(fraction * 1000), 0, 1000), 0);

    public void SetMarquee(bool on)
    {
        Native.SendMessage(hwnd, Native.TDM_SET_MARQUEE_PROGRESS_BAR, on ? 1u : 0u, 0);
        if (on) Native.SendMessage(hwnd, Native.TDM_SET_PROGRESS_BAR_MARQUEE, 1, 30);
        else Native.SendMessage(hwnd, Native.TDM_SET_PROGRESS_BAR_RANGE, 0, 1000 << 16);
    }

    public void SetContent(string text) => SetText(Native.TDE_CONTENT, text);
    public void SetHeading(string text) => SetText(Native.TDE_MAIN_INSTRUCTION, text);
    public void EnableButton(int id, bool enabled) => Native.SendMessage(hwnd, Native.TDM_ENABLE_BUTTON, (nuint)id, enabled ? 1 : 0);
    public void Click(int id) => Native.SendMessage(hwnd, Native.TDM_CLICK_BUTTON, (nuint)id, 0);

    private void SetText(int element, string text)
    {
        var p = Marshal.StringToHGlobalUni(text);
        try { Native.SendMessage(hwnd, Native.TDM_SET_ELEMENT_TEXT, (nuint)element, p); }
        finally { Marshal.FreeHGlobal(p); }
    }
}

/// <summary>The Windows task dialog (Common Controls 6) through P/Invoke: native, DPI-aware, no UI framework.</summary>
internal static unsafe class TaskDialog
{
    public const int IdOk = 1, IdCancel = 2, IdRetry = 4, IdClose = 8;
    public const uint OkButton = 1, CancelButton = 8, RetryButton = 0x10, CloseButton = 0x20;

    private sealed class State(TaskPage page) { public TaskPage Page = page; }

    public static (int Button, bool Checked) Show(TaskPage page)
    {
        var strings = new List<IntPtr>();
        IntPtr Str(string? s) { if (s == null) return IntPtr.Zero; var p = Marshal.StringToHGlobalUni(s); strings.Add(p); return p; }
        var buttons = new Native.TASKDIALOG_BUTTON[page.Buttons.Count];
        for (int i = 0; i < buttons.Length; i++) buttons[i] = new Native.TASKDIALOG_BUTTON { nButtonID = page.Buttons[i].Id, pszButtonText = Str(page.Buttons[i].Text) };
        var state = GCHandle.Alloc(new State(page));
        var icon = page.Icon == TaskIcon.App ? Native.AppIcon() : IntPtr.Zero;
        try
        {
            fixed (Native.TASKDIALOG_BUTTON* pButtons = buttons)
            {
                uint flags = Native.TDF_ALLOW_DIALOG_CANCELLATION | Native.TDF_CALLBACK_TIMER | Native.TDF_SIZE_TO_CONTENT | Native.TDF_ENABLE_HYPERLINKS;
                if (page.Progress) flags |= Native.TDF_SHOW_PROGRESS_BAR;
                if (page.Marquee) flags |= Native.TDF_SHOW_MARQUEE_PROGRESS_BAR;
                if (icon != IntPtr.Zero) flags |= Native.TDF_USE_HICON_MAIN;
                var config = new Native.TASKDIALOGCONFIG
                {
                    cbSize = (uint)sizeof(Native.TASKDIALOGCONFIG),
                    dwFlags = flags,
                    dwCommonButtons = page.CommonButtons,
                    pszWindowTitle = Str(page.Title),
                    hMainIcon = icon != IntPtr.Zero ? icon : page.Icon switch
                    {
                        TaskIcon.Warning => (IntPtr)0xFFFF, TaskIcon.Error => (IntPtr)0xFFFE, _ => (IntPtr)0xFFFD,
                    },
                    pszMainInstruction = Str(page.Heading),
                    pszContent = Str(page.Content),
                    cButtons = (uint)buttons.Length,
                    pButtons = (IntPtr)pButtons,
                    nDefaultButton = page.DefaultButton,
                    pszVerificationText = Str(page.Checkbox),
                    pszExpandedInformation = Str(page.Details),
                    pszExpandedControlText = page.Details != null ? Str("Hide details") : IntPtr.Zero,
                    pszCollapsedControlText = page.Details != null ? Str("Show details") : IntPtr.Zero,
                    pszFooter = Str(page.Footer),
                    pfCallback = &Callback,
                    lpCallbackData = GCHandle.ToIntPtr(state),
                };
                int button = 0, radio = 0, verification = 0;
                int hr = Native.TaskDialogIndirect(&config, &button, &radio, &verification);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                return (button, verification != 0);
            }
        }
        finally
        {
            state.Free();
            strings.ForEach(Marshal.FreeHGlobal);
            if (icon != IntPtr.Zero) Native.DestroyIcon(icon);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Callback(IntPtr hwnd, uint msg, nuint wParam, nint lParam, nint data)
    {
        try
        {
            var page = ((State)GCHandle.FromIntPtr(data).Target!).Page;
            var handle = new TaskDialogHandle(hwnd);
            switch (msg)
            {
                case Native.TDN_CREATED:
                    if (page.Progress && !page.Marquee) Native.SendMessage(hwnd, Native.TDM_SET_PROGRESS_BAR_RANGE, 0, 1000 << 16);
                    if (page.Marquee) Native.SendMessage(hwnd, Native.TDM_SET_PROGRESS_BAR_MARQUEE, 1, 30);
                    page.Tick?.Invoke(handle);
                    break;
                case Native.TDN_TIMER:
                    page.Tick?.Invoke(handle);
                    break;
                case Native.TDN_BUTTON_CLICKED:
                    return page.Clicked?.Invoke((int)wParam) == false ? 1 : 0;
                case Native.TDN_HYPERLINK_CLICKED:
                    var url = Marshal.PtrToStringUni(lParam);
                    if (url != null && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception) { }
        return 0;
    }
}

internal static unsafe partial class Native
{
    public const uint TDF_ENABLE_HYPERLINKS = 0x1, TDF_USE_HICON_MAIN = 0x2, TDF_ALLOW_DIALOG_CANCELLATION = 0x8,
        TDF_SHOW_PROGRESS_BAR = 0x200, TDF_SHOW_MARQUEE_PROGRESS_BAR = 0x400, TDF_CALLBACK_TIMER = 0x800, TDF_SIZE_TO_CONTENT = 0x1000000;
    public const uint TDN_CREATED = 0, TDN_BUTTON_CLICKED = 2, TDN_HYPERLINK_CLICKED = 3, TDN_TIMER = 4;
    private const uint WM_USER = 0x400;
    public const uint TDM_CLICK_BUTTON = WM_USER + 102, TDM_SET_MARQUEE_PROGRESS_BAR = WM_USER + 103, TDM_SET_PROGRESS_BAR_RANGE = WM_USER + 105,
        TDM_SET_PROGRESS_BAR_POS = WM_USER + 106, TDM_SET_PROGRESS_BAR_MARQUEE = WM_USER + 107, TDM_SET_ELEMENT_TEXT = WM_USER + 108, TDM_ENABLE_BUTTON = WM_USER + 111;
    public const int TDE_CONTENT = 0, TDE_MAIN_INSTRUCTION = 3;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TASKDIALOG_BUTTON { public int nButtonID; public IntPtr pszButtonText; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TASKDIALOGCONFIG
    {
        public uint cbSize;
        public IntPtr hwndParent;
        public IntPtr hInstance;
        public uint dwFlags;
        public uint dwCommonButtons;
        public IntPtr pszWindowTitle;
        public IntPtr hMainIcon;
        public IntPtr pszMainInstruction;
        public IntPtr pszContent;
        public uint cButtons;
        public IntPtr pButtons;
        public int nDefaultButton;
        public uint cRadioButtons;
        public IntPtr pRadioButtons;
        public int nDefaultRadioButton;
        public IntPtr pszVerificationText;
        public IntPtr pszExpandedInformation;
        public IntPtr pszExpandedControlText;
        public IntPtr pszCollapsedControlText;
        public IntPtr hFooterIcon;
        public IntPtr pszFooter;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nint, nint, int> pfCallback;
        public nint lpCallbackData;
        public uint cxWidth;
    }

    [LibraryImport("comctl32.dll")]
    public static partial int TaskDialogIndirect(TASKDIALOGCONFIG* config, int* button, int* radio, int* verification);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial nint SendMessage(IntPtr hwnd, uint msg, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "PrivateExtractIconsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr* icons, uint* ids, uint count, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(IntPtr icon);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForSystem();

    /// <summary>The V-OS icon from this executable at the dialog's icon size, or zero.</summary>
    public static IntPtr AppIcon()
    {
        try
        {
            int size = (int)(32 * GetDpiForSystem() / 96);
            IntPtr icon = IntPtr.Zero; uint id = 0;
            return PrivateExtractIcons(Environment.ProcessPath!, 0, size, size, &icon, &id, 1, 0) == 1 ? icon : IntPtr.Zero;
        }
        catch (Exception) { return IntPtr.Zero; }
    }
}
