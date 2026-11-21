<#
.SYNOPSIS
  Prepares the current PowerShell session for building V-OS. Changes nothing outside this session.

.DESCRIPTION
  - Uses the user-local .NET SDK (%LOCALAPPDATA%\Microsoft\dotnet) when present, otherwise the dotnet on PATH.
  - Opts out of .NET CLI telemetry and first-run noise for this session.
  - When the repository path is long, redirects obj/bin to a short folder (Windows MAX_PATH safety, ADR-012).

.EXAMPLE
  . .\build\dev-env.ps1
#>
$ErrorActionPreference = 'Stop'

$userSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (Test-Path (Join-Path $userSdk 'dotnet.exe')) {
    $env:DOTNET_ROOT = $userSdk
    if (-not ($env:PATH -split ';' | Where-Object { $_ -eq $userSdk })) { $env:PATH = "$userSdk;$env:PATH" }
}

# User-level developer tools (no admin): .NET global tools and the portable GitHub CLI.
foreach ($p in @((Join-Path $env:USERPROFILE '.dotnet\tools'), (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\bin'))) {
    if ((Test-Path $p) -and -not ($env:PATH -split ';' | Where-Object { $_ -eq $p })) { $env:PATH = "$env:PATH;$p" }
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:GH_NO_UPDATE_NOTIFIER = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBUILDTERMINALLOGGER = 'off'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ($repoRoot.Length -gt 80 -and -not $env:VOS_BUILD_ROOT) {
    $env:VOS_BUILD_ROOT = Join-Path $env:TEMP 'vosb'
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnet) {
    Write-Host ("V-OS dev environment ready: .NET SDK {0} ({1})" -f (& dotnet --version), $dotnet.Source)
} else {
    Write-Warning '.NET SDK not found. Install the .NET 10 SDK (see DEPENDENCIES.md).'
}
if ($env:VOS_BUILD_ROOT) { Write-Host "Build output redirected to $env:VOS_BUILD_ROOT" }
