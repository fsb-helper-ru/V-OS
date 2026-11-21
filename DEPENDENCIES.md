# Dependencies

Each dependency has to justify its place. Policy:
- Prefer Windows and .NET built-ins.
- Every package below states why it is here.
- New packages are justified in this file. Ask the owner first when a package is large (over 20 MB), has a non-permissive license, or brings an external service or account.

_Last updated: 2026-11-21 (initial audit)._

## 1. Development machine (audited 2026-11-21)
| Item | State | Notes |
|---|---|---|
| OS | Windows 11 Pro 25H2, build 26200, x64, Russian locale | Hypervisor active. Windows Search running. |
| Hardware (reference machine) | i7-6700K (4C/8T), 16 GB RAM, GTX 970, 1680×1050 @ 59 Hz, 96 DPI, single monitor | used for performance budgets |
| .NET SDK | **10.0.404** (runtime 10.0.15, WindowsDesktop 10.0.15) | Installed **user-local** at `%LOCALAPPDATA%\Microsoft\dotnet` on 2026-11-21 with owner approval. SHA-512 verified, Microsoft Authenticode signature valid. Not on PATH; use `build/dev-env.ps1`. Telemetry is opted out via an environment variable. |
| .NET runtime (system) | Microsoft.NETCore.App 8.0.34 | pre-existing, unused |
| .NET Framework | 4.8.09221 | hosts the WinUI XAML compiler |
| Git | 2.56.1 + Git Credential Manager 2.9.1 | user identity configured |
| winget | 1.29.380 | — |
| WebView2 Runtime | 158.0.4370.33 (Evergreen) | — |
| Windows App Runtime (system packages) | 1.7, 1.8, 2 (CBS) | V-OS ships self-contained and does not rely on these |
| bsdtar / libarchive | 3.8.9 (`C:\Windows\System32\tar.exe`) | 7z, rar and tar.* extraction for V-Archive |
| Windows Performance Recorder | `wpr.exe` present | ETW traces |
| Fonts | Bahnschrift; Segoe UI Variable Display, Text and Small; Segoe Fluent Icons; Cascadia | design language |
| Project location | `C:\Dev\V-OS` on the Lexar THOR PRO 1 TB **NVMe** SSD (C:) | Chosen 2026-11-21 as the fastest drive. D: is a 5400 rpm HDD. Short path, so build redirection is not needed. |
| GitHub CLI | **2.105.0, portable, user-local** (`%LOCALAPPDATA%\Programs\GitHub CLI`) | Installed 2026-11-21. SHA-256 verified against the release checksums; Authenticode valid (GitHub, Inc.). **Not signed in** — authentication waits for the owner. Added to PATH by `dev-env.ps1`. |
| .NET diagnostics | dotnet-counters, dotnet-trace, dotnet-gcdump **10.0.750501** (user-level global tools) | Installed 2026-11-21 without admin |
| **Missing (deferred by owner)** | Visual Studio Build Tools / Windows SDK (signtool), Windows Sandbox | See INTEGRATIONS.md, "When deferred tools become necessary" |
| Not needed | PowerShell 7 | — |
| Long paths | **disabled** (`LongPathsEnabled=0`) | no longer relevant: the repository path is short (ADR-012) |

## 2. Product runtime prerequisites (on user machines)
| Prerequisite | Handling |
|---|---|
| WebView2 Runtime (Evergreen) | Preinstalled on Windows 11. The installer checks for it and, with consent, offers Microsoft's official bootstrapper. |
| Visual C++ 2015–2022 Redistributable | **Not required.** Spike S1 found that no self-contained binary imports it. The installer keeps a defensive check. |
| .NET 10 runtime | **Not required.** It is bundled self-contained. |
| Windows App SDK runtime | **Not required.** It is bundled self-contained. |

## 3. NuGet packages (pinned in `Directory.Packages.props`)
| Package | Version | License | Used by | Why |
|---|---|---|---|---|
| Microsoft.WindowsAppSDK.WinUI (component package; brings Foundation, InteractiveExperiences, Base) | 2.7.0 | Microsoft | UI, Shell, Apps, Launcher | WinUI 3, Composition, backdrops, windowing (ADR-001, ADR-016). Approved and restored 2026-11-21. |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2957 | Microsoft | build | MRT/PRI and WinMD tooling for WinUI builds |
| Microsoft.Graphics.Win2D | 1.4.0 | MIT | UI, Shell, Apps | GPU 2D drawing and effects: ambient scenes, visualizer, capture editor, blurred artwork. Approved and restored 2026-11-21. |
| CommunityToolkit.WinUI.Controls.SettingsControls / .Sizers | 8.2.251219 | MIT | Apps | Settings cards, grid splitters (dual pane, split view). Optional; decided in spike S1. |
| Microsoft.Web.WebView2 | 1.0.4370.26 | Microsoft | Apps (Browser), Files (PDF preview) | Chromium engine (ADR-007) |
| Microsoft.Data.Sqlite | 10.0.15 | MIT | Core | structured local storage (ADR-009) |
| System.Management | 10.0.15 | MIT | Platform (Monitor, Settings only) | WMI: panel brightness, battery details, thermal zones where exposed |
| System.Data.OleDb | 10.0.15 | MIT | Platform | Windows Search index queries (file search) |
| QRCoder | 1.9.0 | MIT | Apps (Connect) | pairing QR codes |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | 2.9.3 / 3.1.5 / 18.12.0 | Apache-2.0 / MIT | Tests | unit and integration tests |
| FlaUI.UIA3 | 5.0.0 | MIT | Tests | UI Automation tests of WinUI windows |
| ~~NAudio~~ | — | — | — | **Removed** (ADR-008): replaced by WinRT AudioGraph and source-generated WASAPI interop |

Not AOT-compatible: `System.Management`, `System.Data.OleDb`. They are confined to modules that will not be AOT-compiled, or are replaced later.

## 4. External services (runtime, all optional, consent-gated where personal data is involved)
| Service | Purpose | Data sent | Consent |
|---|---|---|---|
| Open-Meteo (forecast, geocoding, air quality) | weather | coordinates of saved locations | weather can be turned off. Commercial use needs a paid plan (ADR-011). |
| GitHub Releases (default update feed) | updates | none beyond the HTTP request | the update check can be turned off |
| Spotify Web API | music provider | OAuth (PKCE) with the user's own client ID | explicit sign-in |
| Claude API / local Ollama | assistant (optional) | only what the user sends | explicit opt-in per provider |
| MyMemory / DeepL / LibreTranslate | translation (optional) | the text being translated | explicit opt-in |
| winget sources | V-Store | search terms, package IDs | user-initiated |

## 5. Update policy
- Patch versions of the .NET 10 SDK and runtime, Windows App SDK 2.x and WebView2 are reviewed monthly via `/v-os-checkup`. They are applied after build and test pass.
- Major upgrades (for example .NET 11 STS, Windows App SDK 3.0) need an ADR.
