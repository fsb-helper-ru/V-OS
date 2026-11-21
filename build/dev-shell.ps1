<#
.SYNOPSIS
  Developer helper: start, screenshot and gracefully stop the V-OS shell from a Debug build.
  Uses a separate data folder (VOS_DATA_ROOT) so development never touches real user data.
.EXAMPLE
  .\build\dev-shell.ps1 start
  .\build\dev-shell.ps1 shot first
  .\build\dev-shell.ps1 stop
#>
param([Parameter(Mandatory)][ValidateSet('start', 'shot', 'stop', 'send', 'app', 'geometry')][string]$Action, [string]$Name = 'shot', [string]$Message = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'src\VOS\bin\Debug\net10.0-windows10.0.26100.0\win-x64\VOS.exe'
$dataRoot = Join-Path $env:TEMP 'vos-dev-data'
$shots = Join-Path $env:TEMP 'vos-dev-shots'
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
# Every action (including --recover after a forced stop) must read the dev data root, where the desktop-state record lives.
$env:VOS_DATA_ROOT = $dataRoot
Add-Type -AssemblyName System.Drawing, System.Windows.Forms

function Send-Pipe([string]$pipe, [string]$json, [bool]$reply = $false) {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $c.Connect(1500)
        $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $w.WriteLine($json)
        if ($reply) { $r = New-Object System.IO.StreamReader($c); return $r.ReadLine() }
    } catch { return $null } finally { $c.Dispose() }
}

switch ($Action) {
    'start' {
        New-Item -ItemType Directory -Force $dataRoot | Out-Null
        $env:VOS_DATA_ROOT = $dataRoot
        $p = Start-Process $exe -ArgumentList '--shell', '--no-onboarding' -PassThru
        for ($i = 0; $i -lt 60; $i++) {
            if ((Send-Pipe "VOS.Shell.$sid" '{"type":"ping"}' $true) -match 'pong') { "Shell running (pid $($p.Id)) after $($i * 100) ms"; return }
            Start-Sleep -Milliseconds 100
            if ($p.HasExited) { "Shell exited with code $($p.ExitCode)"; return }
        }
        "Shell did not answer within 6 s (pid $($p.Id))"
    }
    'app' {
        $env:VOS_DATA_ROOT = $dataRoot
        # Test runs never write into the user's Pictures or overwrite their clipboard.
        $env:VOS_SCREENSHOTS_DIR = Join-Path $shots 'captures'
        $env:VOS_TEST_NO_CLIPBOARD = '1'
        $args2 = @('--app') + ($Name -split ' ')
        Start-Process $exe -ArgumentList $args2 | Out-Null
        "Opened app: $Name"
    }
    'send' { Send-Pipe "VOS.Shell.$sid" $Message $true }
    'geometry' {
        # Window, client and region rectangles of every top-level V-OS surface (physical pixels).
        Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class VosGeo {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public override string ToString() { return string.Format("({0},{1})-({2},{3}) {4}x{5}", L, T, R, B, R - L, B - T); } }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] static extern int GetWindowRgnBox(IntPtr h, out RECT r);
  public static List<string> Describe(uint pid) {
    var lines = new List<string>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p != pid) return true;
      var sb = new StringBuilder(256); GetWindowTextW(h, sb, 256);
      RECT w, c, g; GetWindowRect(h, out w); GetClientRect(h, out c);
      var o = new POINT(); ClientToScreen(h, ref o);
      int type = GetWindowRgnBox(h, out g);
      int frame = (o.X - w.L) + (o.Y - w.T) + (w.R - o.X - (c.R - c.L)) + (w.B - o.Y - (c.B - c.T));
      lines.Add(string.Format("{0,-22} {1,-7} window {2}  client {3}x{4}{5}  region {6}", sb.ToString(), IsWindowVisible(h) ? "visible" : "hidden",
        w, c.R - c.L, c.B - c.T, frame == 0 ? "" : "  FRAME " + frame + "px", type <= 1 ? "none" : g.ToString()));
      return true;
    }, IntPtr.Zero);
    return lines;
  }
}
'@
        $procs = @(Get-Process VOS -ErrorAction SilentlyContinue)
        if (-not $procs) { 'V-OS is not running'; return }
        foreach ($p in $procs) { [VosGeo]::Describe([uint32]$p.Id) | Where-Object { $_ -match '^V-OS' } }
    }
    'shot' {
        Add-Type -AssemblyName System.Drawing, System.Windows.Forms
        New-Item -ItemType Directory -Force $shots | Out-Null
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
        $full = Join-Path $shots "$Name-full.png"; $bmp.Save($full, [System.Drawing.Imaging.ImageFormat]::Png)
        function Crop($x, $y, $w, $h, $suffix) {
            $r = New-Object System.Drawing.Rectangle ([Math]::Max(0,$x)), ([Math]::Max(0,$y)), $w, $h
            $c = $bmp.Clone($r, $bmp.PixelFormat); $path = Join-Path $shots "$Name-$suffix.png"; $c.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $c.Dispose(); $path
        }
        Crop 0 0 $b.Width 60 'top'
        Crop ([int](($b.Width - 640) / 2)) 0 640 240 'pill'
        Crop ([int](($b.Width - 1100) / 2)) ($b.Height - 230) 1100 230 'bottom'
        $g.Dispose(); $bmp.Dispose()
        $full
    }
    'stop' {
        $r = Send-Pipe "VOS.Shell.$sid" '{"type":"shutdown"}' $true
        # Graceful exit restores the desktop and writes the clean-exit marker; give the shell (and its watchdog) up to 4 s.
        # App windows are separate processes that outlive the shell by design; they are closed afterwards.
        function Get-ShellProcs { Get-CimInstance Win32_Process -Filter "Name='VOS.exe'" | Where-Object { $_.CommandLine -notmatch '--app' } }
        for ($i = 0; $i -lt 20 -and (Get-ShellProcs); $i++) { Start-Sleep -Milliseconds 200 }
        $left = Get-ShellProcs
        if ($left) { $left | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }; Start-Sleep -Milliseconds 300; & $exe --recover | Out-Null; "Forced stop + desktop recovery" } else { "Stopped gracefully ($r)" }
        Get-CimInstance Win32_Process -Filter "Name='VOS.exe'" | Where-Object { $_.CommandLine -match '--app' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; "Closed app host $($_.ProcessId)" }
        $wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        "Work area: $wa"
        Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class VosAbm { [StructLayout(LayoutKind.Sequential)] public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cb; public uint edge; public int l, t, r, b; public IntPtr lParam; } [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint m, ref APPBARDATA d); public static bool AutoHide() { var d = new APPBARDATA(); d.cbSize = Marshal.SizeOf(d); return ((long)SHAppBarMessage(4, ref d) & 1) != 0; } }' -ErrorAction SilentlyContinue
        "Taskbar auto-hide: $([VosAbm]::AutoHide())"
    }
}
