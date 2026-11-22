<#
.SYNOPSIS
  Release verification on real binaries: opens every app of a built V-OS and checks that its window appears and stays
  alive (catches missing resources such as VOS.pri), then measures the shell: time to ready, private memory, idle CPU
  over N minutes, and a graceful stop that restores the Windows desktop.
.DESCRIPTION
  Uses its own data folder (never your real V-OS data). Screenshots go to %TEMP%\vos-release-check.
.EXAMPLE
  .\build\release-check.ps1 -App C:\Dev\V-OS\artifacts\release\1.0.0\app
  .\build\release-check.ps1 -App <dir> -IdleMinutes 10 -SkipApps
#>
param(
    [Parameter(Mandatory)][string]$App,
    [string]$DataRoot = (Join-Path $env:TEMP 'vos-release-check\data'),
    [int]$IdleMinutes = 10,
    [switch]$SkipApps,
    [switch]$SkipShell
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $App 'VOS.exe'
if (-not (Test-Path $exe)) { throw "VOS.exe not found in $App" }
$out = Join-Path $env:TEMP 'vos-release-check'
New-Item -ItemType Directory -Force $out, $DataRoot | Out-Null
$env:VOS_DATA_ROOT = $DataRoot
$env:VOS_SCREENSHOTS_DIR = Join-Path $out 'captures'
$env:VOS_TEST_NO_CLIPBOARD = '1'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$root = [System.Windows.Automation.AutomationElement]::RootElement
$nameProp = [System.Windows.Automation.AutomationElement]::NameProperty
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$version = (Get-Item $exe).VersionInfo.ProductVersion
"V-OS $version from $App"

function Find-Window([string]$title, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
        $w = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($nameProp, $title)))
        if ($w) { return @{ Window = $w; Ms = $sw.ElapsedMilliseconds } }
        Start-Sleep -Milliseconds 100
    }
    return $null
}
function Get-VosProcesses { Get-CimInstance Win32_Process -Filter "Name='VOS.exe'" | Where-Object { $_.ExecutablePath -like "$App*" } }
function Stop-VosApps { Get-VosProcesses | Where-Object { $_.CommandLine -match '--app|--launcher' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } }
function Send-Pipe([string]$pipe, [string]$json) {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
    try { $c.Connect(1500); $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true; $w.WriteLine($json); $r = New-Object System.IO.StreamReader($c); return $r.ReadLine() } catch { return $null } finally { $c.Dispose() }
}

if (-not $SkipApps) {
    ''
    'Apps (window must appear and the process must survive 4 s; screenshot of each):'
    $apps = @(
        @('settings', 'Settings'), @('weather', 'Weather'), @('monitor', 'Monitor'), @('notes', 'Notes'), @('time', 'Time'),
        @('files', 'Files'), @('music', 'Music'), @('store', 'Store'), @('gaming', 'Gaming'), @('translate', 'Translate'),
        @('connect', 'Connect'), @('flow', 'Flow'), @('assistant', 'Assistant'), @('browser', 'Browser'))
    $results = foreach ($a in $apps) {
        Stop-VosApps
        Start-Sleep -Milliseconds 300
        $p = Start-Process $exe -ArgumentList '--app', $a[0] -PassThru
        $found = Find-Window $a[1] 15000
        Start-Sleep -Seconds 4
        $alive = @(Get-VosProcesses | Where-Object { $_.CommandLine -match "--app" }).Count -gt 0
        $still = if ($found) { [bool]($root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($nameProp, $a[1])))) } else { $false }
        $ok = $found -and $alive -and $still
        if ($ok) {
            $r = $found.Window.Current.BoundingRectangle
            if ($r.Width -gt 0) {
                $bmp = New-Object System.Drawing.Bitmap([int]$r.Width, [int]$r.Height)
                $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size); $g.Dispose()
                $bmp.Save((Join-Path $out "app-$($a[0]).png")); $bmp.Dispose()
            }
        }
        [pscustomobject]@{ App = $a[1]; Window = if ($found) { "$($found.Ms) ms" } else { 'missing' }; Result = if ($ok) { 'OK' } else { 'FAILED' } }
    }
    Stop-VosApps
    $results | Format-Table -AutoSize | Out-String -Width 120
    $failed = @($results | Where-Object Result -ne 'OK').Count
    "Apps: $($results.Count - $failed) of $($results.Count) OK"
}

if (-not $SkipShell) {
    ''
    'Shell:'
    $wa0 = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $t0 = Get-Date
    $shell = Start-Process $exe -ArgumentList '--shell', '--no-onboarding' -PassThru
    $ready = $null
    for ($i = 0; $i -lt 150; $i++) {
        if ((Send-Pipe "VOS.Shell.$sid" '{"type":"ping"}') -match 'pong') { $ready = (Get-Date) - $t0; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'The shell did not answer within 15 s' }
    $log = Get-ChildItem (Join-Path $DataRoot 'logs') -Filter 'shell-*.log' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Start-Sleep -Seconds 2
    $readyLine = if ($log) { Get-Content $log.FullName -Encoding UTF8 | Select-String '"Shell ready"' | Select-Object -Last 1 } else { $null }
    $readyMs = if ($readyLine -and $readyLine.Line -match '"ms":(\d+)') { [int]$Matches[1] } else { $null }
    "  answering IPC after {0:N0} ms; shell reports ready after {1} ms (process start to all surfaces shown)" -f $ready.TotalMilliseconds, $readyMs
    Start-Sleep -Seconds 28
    $proc = Get-Process -Id $shell.Id
    "  private memory after 30 s: {0:N0} MB (working set {1:N0} MB)" -f ($proc.PrivateMemorySize64 / 1MB), ($proc.WorkingSet64 / 1MB)
    $cpu0 = $proc.TotalProcessorTime; $w0 = Get-Date
    "  measuring idle CPU for $IdleMinutes min (do not use the PC meanwhile)..."
    Start-Sleep -Seconds ($IdleMinutes * 60)
    $proc.Refresh()
    $cpu = ($proc.TotalProcessorTime - $cpu0).TotalSeconds; $wall = ((Get-Date) - $w0).TotalSeconds
    "  idle CPU: {0:N3} % of the machine ({1:N2} s CPU in {2:N0} s, {3} logical processors); private memory {4:N0} MB" -f (100 * $cpu / $wall / [Environment]::ProcessorCount), $cpu, $wall, [Environment]::ProcessorCount, ($proc.PrivateMemorySize64 / 1MB)
    $reply = Send-Pipe "VOS.Shell.$sid" '{"type":"shutdown"}'
    for ($i = 0; $i -lt 40 -and -not $shell.HasExited; $i++) { Start-Sleep -Milliseconds 200 }
    Get-VosProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    $wa1 = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    "  graceful stop: {0}; work area before {1}, after {2}: {3}" -f $(if ($shell.HasExited) { 'yes' } else { 'no (forced)' }), $wa0, $wa1, $(if ($wa0 -eq $wa1) { 'restored' } else { 'DIFFERENT' })
}
"Screenshots: $out"
