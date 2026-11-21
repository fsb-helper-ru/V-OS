<#
.SYNOPSIS
  Builds a V-OS release into artifacts\release\<version>:
    V-Launcher.exe                 single-file installer, updater and uninstaller (ADR-005)
    V-OS-<version>-win-x64.zip     the app: Release, ReadyToRun, self-contained, win-x64
    vos-release.json               signed release manifest (ADR-006)
    symbols-<version>.zip          debug symbols (not shipped), SHA256SUMS.txt
.DESCRIPTION
  Reproducible from a commit: timestamps come from the last commit, the package is written in a fixed order, and the
  compilers run in deterministic CI mode. The private signing key is read from outside the repository.
  Put V-Launcher.exe, the zip and vos-release.json in one folder for an offline installer, or publish the zip and the
  manifest at the feed URL baked into V-Launcher.exe for online installs and updates.
.EXAMPLE
  .\build\build.ps1
  .\build\build.ps1 -Version 1.0.1 -Feed https://github.com/OWNER/V-OS/releases/latest/download/vos-release.json
#>
param(
    [string]$Version = '',
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [string]$Feed = '',
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.vos-release\release-key.pem'),
    [string]$NotesFile = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dev-env.ps1') | Out-Null
$sw = [Diagnostics.Stopwatch]::StartNew()

if (-not $Version) {
    $props = [IO.File]::ReadAllText((Join-Path $repo 'Directory.Build.props'), [Text.Encoding]::UTF8)
    $Version = [regex]::Match($props, '<VosVersion[^>]*>([^<]+)</VosVersion>').Groups[1].Value
}
if (-not $NotesFile) { $NotesFile = Join-Path $repo 'RELEASE_NOTES.md' }
$epoch = (& git -C $repo log -1 --format=%ct).Trim()
$commit = (& git -C $repo rev-parse --short HEAD).Trim()
$dirty = (& git -C $repo status --porcelain) -ne $null
$out = Join-Path $repo "artifacts\release\$Version"
$stage = Join-Path $out 'app'
$boot = Join-Path $out 'bootstrap'
"V-OS $Version ($Channel) from commit $commit$(if ($dirty) { ' + uncommitted changes (not reproducible from the commit)' })"

function Invoke-Dotnet([string[]]$arguments, [string]$what) {
    $log = & dotnet @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { $log | Select-Object -Last 30; throw "$what failed" }
    $issues = $log | Select-String -Pattern ': (error|warning) ' | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique
    if ($issues) { $issues | Select-Object -First 15 }
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $SkipTests) {
    'Running tests...'
    Invoke-Dotnet @('test', (Join-Path $repo 'tests\VOS.Tests\VOS.Tests.csproj'), '-c', 'Release', '-nologo', '-v', 'q') 'Tests'
}

$common = @('-c', 'Release', '-nologo', '-v', 'q', '-clp:NoSummary', "-p:VosVersion=$Version", "-p:VosUpdateFeed=$Feed", '-p:ContinuousIntegrationBuild=true')
'Publishing VOS (ReadyToRun, self-contained)...'
Invoke-Dotnet (@('publish', (Join-Path $repo 'src\VOS\VOS.csproj'), '-r', 'win-x64', '--self-contained', '-o', $stage) + $common) 'Publishing VOS'
'Publishing V-Launcher.exe (single file, trimmed)...'
Invoke-Dotnet (@('publish', (Join-Path $repo 'src\VOS.Bootstrap\VOS.Bootstrap.csproj'), '-o', $boot) + $common) 'Publishing V-Launcher'

# Not shipped: API documentation of referenced libraries and debug symbols (kept separately for crash analysis).
$symbols = Join-Path $out 'symbols'
New-Item -ItemType Directory -Force $symbols | Out-Null
Get-ChildItem $stage -Recurse -Filter *.pdb | ForEach-Object { Move-Item $_.FullName (Join-Path $symbols $_.Name) -Force }
$docs = Get-ChildItem $stage -Recurse -Filter *.xml | Where-Object { Test-Path ([IO.Path]::ChangeExtension($_.FullName, '.dll')) }
$docBytes = ($docs | Measure-Object Length -Sum).Sum
$docs | Remove-Item -Force
Copy-Item (Join-Path $boot 'V-Launcher.exe') (Join-Path $stage 'V-Launcher.exe')
Copy-Item (Join-Path $boot 'V-Launcher.exe') (Join-Path $out 'V-Launcher.exe')
"Removed $($docs.Count) documentation files ($([math]::Round($docBytes / 1MB, 1)) MB) and moved symbols aside"

'Packaging and signing...'
$tool = Join-Path $repo 'build\VOS.BuildTool\VOS.BuildTool.csproj'
$toolOut = & dotnet run --project $tool -c Release -- release --app $stage --out $out --version $Version --channel $Channel --time $epoch --key $KeyPath --notes $NotesFile 2>&1
if ($LASTEXITCODE -ne 0) { $toolOut; throw 'Packaging failed' }
$toolOut | Where-Object { $_ -match '^(package|sha256|manifest)' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$symZip = Join-Path $out "symbols-$Version.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($symbols, $symZip)
Remove-Item -Recurse -Force $symbols, $boot

$sums = Get-ChildItem $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), [string[]]$sums, (New-Object Text.UTF8Encoding($false)))

$appMb = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
''
"Release $Version ready in $out ($([math]::Round($sw.Elapsed.TotalSeconds)) s)"
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object { '  {0,-34} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
"  app folder (unpacked)              $appMb MB"
