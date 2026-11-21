using Windows.Storage.Pickers;

namespace VOS.UI;

/// <summary>System file and folder pickers bound to a window (unpackaged apps must pass their HWND).</summary>
public static class Pickers
{
    public static async Task<string?> FolderAsync(IntPtr hwnd)
    {
        var p = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, hwnd);
        var f = await p.PickSingleFolderAsync();
        return f?.Path;
    }

    public static async Task<string?> FileAsync(IntPtr hwnd, PickerLocationId start, params string[] extensions)
    {
        var p = new FileOpenPicker { SuggestedStartLocation = start, ViewMode = PickerViewMode.Thumbnail };
        foreach (var e in extensions.Length == 0 ? ["*"] : extensions) p.FileTypeFilter.Add(e);
        WinRT.Interop.InitializeWithWindow.Initialize(p, hwnd);
        var f = await p.PickSingleFileAsync();
        return f?.Path;
    }

    public static async Task<IReadOnlyList<string>> FilesAsync(IntPtr hwnd, PickerLocationId start, params string[] extensions)
    {
        var p = new FileOpenPicker { SuggestedStartLocation = start };
        foreach (var e in extensions.Length == 0 ? ["*"] : extensions) p.FileTypeFilter.Add(e);
        WinRT.Interop.InitializeWithWindow.Initialize(p, hwnd);
        var files = await p.PickMultipleFilesAsync();
        return files.Select(f => f.Path).ToList();
    }

    public static async Task<string?> SaveAsync(IntPtr hwnd, string suggestedName, string typeLabel, params string[] extensions)
    {
        var p = new FileSavePicker { SuggestedFileName = suggestedName, SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        p.FileTypeChoices.Add(typeLabel, extensions.ToList());
        WinRT.Interop.InitializeWithWindow.Initialize(p, hwnd);
        var f = await p.PickSaveFileAsync();
        return f?.Path;
    }
}
