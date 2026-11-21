<#
.SYNOPSIS
  Developer build: stops running V-OS app-host processes (they lock the output DLLs), builds VOS.exe and prints only
  errors and warnings. Exit code 1 when the build has issues.
.EXAMPLE
  .\build\dev-build.ps1
  .\build\dev-build.ps1 -KeepApps
#>
param([switch]$KeepApps)
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dev-env.ps1') | Out-Null
if (-not $KeepApps) {
    Get-CimInstance Win32_Process -Filter "Name='VOS.exe'" | Where-Object { $_.CommandLine -match '--app' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 300
}
$project = Join-Path $repo 'src\VOS\VOS.csproj'
$out = cmd /c "dotnet build `"$project`" -nologo -v q -clp:NoSummary 2>&1"
$prefix = [regex]::Escape((Join-Path $repo 'src') + '\')
$issues = $out | Select-String -Pattern ': (error|warning) ' | ForEach-Object { ($_.Line -replace $prefix, '') -replace '\s*\[[^\]]+\]\s*$', '' } | Select-Object -Unique
if ($LASTEXITCODE -ne 0 -and -not $issues) { $issues = $out | Select-Object -Last 15 }
if ($issues) { $issues; exit 1 }
"Build OK"
