<#
.SYNOPSIS
  Runs spike S1 on the real desktop and measures startup, idle CPU, animation CPU and memory externally.
  Test windows appear briefly at the top centre, bottom centre and left edge of the primary screen.
#>
param([string]$Out = (Join-Path $PSScriptRoot 'results'), [int]$IdleSeconds = 30, [string]$ExePath, [switch]$MeasureOnly)
$ErrorActionPreference = 'Stop'
$exe = if ($ExePath) { Get-Item $ExePath } else { Get-ChildItem (Join-Path $PSScriptRoot 'bin') -Recurse -Filter 'S1.ShellSurfaces.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
if (-not $exe) { throw 'Build the spike first (dotnet build).' }
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force $Out | Out-Null
$cores = [Environment]::ProcessorCount

function Invoke-Mode([string]$mode) {
    $p = Start-Process $exe.FullName -ArgumentList $mode, "`"$Out`"" -PassThru
    if (-not $p.WaitForExit(90000)) { $p.Kill(); return "$mode TIMEOUT" }
    return "$mode exit=$($p.ExitCode)"
}

function Measure-Ambient([string]$mode, [int]$seconds) {
    $ready = Join-Path $Out ("{0}-ready.json" -f $mode.TrimStart('-').Replace('idle','idle').Replace('anim','anim'))
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process $exe.FullName -ArgumentList $mode, "`"$Out`"", ($seconds + 12) -PassThru
    while (-not (Test-Path $ready) -and $sw.ElapsedMilliseconds -lt 30000) { Start-Sleep -Milliseconds 15 }
    $launchToReadyMs = $sw.ElapsedMilliseconds
    Start-Sleep -Seconds 5                                   # let startup work settle
    $p.Refresh(); $cpu0 = $p.TotalProcessorTime; $t0 = Get-Date
    Start-Sleep -Seconds $seconds
    $p.Refresh(); $cpu1 = $p.TotalProcessorTime; $t1 = Get-Date
    $wall = ($t1 - $t0).TotalSeconds
    $cpuSec = ($cpu1 - $cpu0).TotalSeconds
    $res = [ordered]@{
        mode                       = $mode
        launchToReadyMs            = $launchToReadyMs
        appReported                = (Get-Content $ready -Raw | ConvertFrom-Json)
        measuredSeconds            = [math]::Round($wall, 1)
        cpuSeconds                 = [math]::Round($cpuSec, 3)
        cpuPercentOfMachine        = [math]::Round(100 * $cpuSec / ($wall * $cores), 3)
        cpuPercentOfOneCore        = [math]::Round(100 * $cpuSec / $wall, 2)
        privateMB                  = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        workingSetMB               = [math]::Round($p.WorkingSet64 / 1MB, 1)
        threads                    = $p.Threads.Count
        handles                    = $p.HandleCount
    }
    $p.WaitForExit(30000) | Out-Null
    if (-not $p.HasExited) { $p.Kill() }
    return $res
}

$log = @()
if (-not $MeasureOnly) {
    $log += Invoke-Mode '--pill'
    $log += Invoke-Mode '--dock'
    $log += Invoke-Mode '--appbar'
}
$idle = Measure-Ambient '--idle' $IdleSeconds
$anim = Measure-Ambient '--anim' 20
[ordered]@{ runs = $log; idle = $idle; animate = $anim; logicalCores = $cores } |
    ConvertTo-Json -Depth 6 | Set-Content (Join-Path $Out 'measurements.json') -Encoding UTF8
Get-Content (Join-Path $Out 'measurements.json')

