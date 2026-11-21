<#
.SYNOPSIS
  Developer helper: drive V-OS windows through UI Automation and capture them, even when other windows cover them.
.EXAMPLE
  .\build\dev-ui.ps1 invoke -Window Settings -Name Appearance
  .\build\dev-ui.ps1 capture -Window Settings -Out settings-appearance
  .\build\dev-ui.ps1 tree -Window Settings
  .\build\dev-ui.ps1 type -Window Settings -Name "Find a setting" -Text "dock"
#>
param(
    [Parameter(Mandatory)][ValidateSet('invoke', 'capture', 'tree', 'type', 'close', 'wait')][string]$Action,
    [Parameter(Mandatory)][string]$Window,
    [string]$Name = '',
    [string]$Text = '',
    [string]$Out = 'ui',
    [int]$Depth = 400
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class VosCap {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
}
'@ -ErrorAction SilentlyContinue
$shots = Join-Path $env:TEMP 'vos-dev-shots'
New-Item -ItemType Directory -Force $shots | Out-Null
$root = [System.Windows.Automation.AutomationElement]::RootElement
$nameProp = [System.Windows.Automation.AutomationElement]::NameProperty

function Find-Window([string]$title) {
    for ($i = 0; $i -lt 40; $i++) {
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($nameProp, $title)))
        if ($w) { return $w }
        Start-Sleep -Milliseconds 150
    }
    throw "Window '$title' not found"
}
function Find-Element($w, [string]$name) {
    $e = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($nameProp, $name)))
    if (-not $e) { throw "Element '$name' not found in '$Window'" }
    return $e
}

switch ($Action) {
    'wait' { $null = Find-Window $Window; "found" }
    'invoke' {
        $w = Find-Window $Window
        $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($nameProp, $Name)))
        if ($all.Count -eq 0) { throw "Element '$Name' not found in '$Window'" }
        foreach ($e in $all) {
            $p = $null
            if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); "invoked $Name"; return }
            if ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { $p.Toggle(); "toggled $Name"; return }
            if ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$p)) { $p.Select(); "selected $Name"; return }
        }
        throw "Element '$Name' has no invoke, toggle or selection pattern"
    }
    'type' {
        $e = Find-Element (Find-Window $Window) $Name
        $p = $null
        if (-not $e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$p)) { throw "Element '$Name' does not accept text" }
        $p.SetValue($Text)
        "typed into $Name"
    }
    'close' {
        $w = Find-Window $Window
        $p = $null
        if ($w.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$p)) { $p.Close(); "closed" }
    }
    'capture' {
        $w = Find-Window $Window
        $h = [IntPtr]$w.Current.NativeWindowHandle
        $r = New-Object VosCap+RECT
        # Extended frame bounds = the visible window without the invisible resize borders.
        if ([VosCap]::DwmGetWindowAttribute($h, 9, [ref]$r, 16) -ne 0) { [void][VosCap]::GetWindowRect($h, [ref]$r) }
        $wr = New-Object VosCap+RECT; [void][VosCap]::GetWindowRect($h, [ref]$wr)
        $bmp = New-Object System.Drawing.Bitmap ($wr.R - $wr.L), ($wr.B - $wr.T)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $hdc = $g.GetHdc()
        [void][VosCap]::PrintWindow($h, $hdc, 2) # PW_RENDERFULLCONTENT: includes DirectComposition content
        $g.ReleaseHdc($hdc); $g.Dispose()
        $crop = New-Object System.Drawing.Rectangle ($r.L - $wr.L), ($r.T - $wr.T), ($r.R - $r.L), ($r.B - $r.T)
        $final = $bmp.Clone($crop, $bmp.PixelFormat); $bmp.Dispose()
        $path = Join-Path $shots "$Out.png"
        $final.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $final.Dispose()
        $path
    }
    'tree' {
        $w = Find-Window $Window
        $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
        $n = 0
        foreach ($e in $all) {
            if ($n++ -ge $Depth) { break }
            $nm = $e.Current.Name
            if ($nm) { "{0,-12} {1}" -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), ($nm -replace '\s+', ' ').Substring(0, [Math]::Min(90, $nm.Length)) }
        }
    }
}
