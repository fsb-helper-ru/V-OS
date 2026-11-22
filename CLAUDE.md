# V-OS — Engineering Instructions

V-OS is a premium desktop environment and application ecosystem on top of Windows. Windows remains the operating system.

**The goal:** one complete, integrated, tested, installable release. Not a mockup, not milestones handed to the owner to assemble.

## Read before significant work
The owner's directives in `docs/brief/` are kept on this machine only (git-ignored, not published).
- `docs/brief/01-master-directive.md`: product requirements (owner's words, verbatim)
- `docs/brief/02-engineering-directive.md`: permanent engineering rules (verbatim)
- `docs/brief/03-ui-framework-directive.md`: UI framework directive (verbatim)
- `ARCHITECTURE.md` (current architecture) · `DECISIONS.md` (ADRs) · `ROADMAP.md` (status)

## Working agreement
- **Act as a full team:** architecture, Windows systems, .NET, UI/UX, graphics and performance, security, QA, release engineering.
- **Decide technical questions yourself.** Record significant ones as ADRs in DECISIONS.md (context, options compared, decision, consequences). Before major choices, check current official documentation and supported versions. Never pick a technology because it is installed or easier.
- **Ask the owner only for** preferences, account authorization, payments, security approvals, or significant changes to their Windows environment.
- **Never without explicit approval:**
  - install software or tools;
  - connect services or MCP servers;
  - create accounts;
  - purchase anything;
  - change system settings;
  - push to remote repositories.

  NuGet packages already listed in DEPENDENCIES.md are approved. A new package is justified there first; ask when it is large (over 20 MB), copyleft, or brings an external service.
- **Recommending tools:** group them as REQUIRED / HIGHLY RECOMMENDED / OPTIONAL / NOT NEEDED. Give each the 10 facts: what, why, mandatory, cost, account, install, admin, permissions, official link, gain. Keep the list short and prioritized.
- **No fake completion.** Every visible control works or is disabled with an explanation. Genuine limitations go into `docs/LIMITATIONS.md` and are reported plainly.
- **Communication:** the owner writes in Russian and pastes specifications in English. Reply in chat in **Russian**. Code, comments, commit messages and documentation are in **English**.

## Environment (this machine)
- The .NET SDK 10.0.404 is user-local at `%LOCALAPPDATA%\Microsoft\dotnet` and **not on PATH**. Every shell session starts with:
  ```powershell
  . .\build\dev-env.ps1
  ```
  The script sets `DOTNET_ROOT` and `PATH`, opts out of .NET CLI telemetry, and redirects `obj`/`bin` to a short folder when the repository path is long (ADR-012).
- Windows long paths are disabled. Keep generated paths short.
- Windows PowerShell 5.1 only (no `pwsh`). The OS locale is Russian, so parse tool output by structure, never by localized words.
- **Text encoding pitfalls** (sources contain literal Unicode: icon glyphs, `·`, `›`, `°`, Russian strings):
  - Never round-trip a source file through PowerShell 5.1 `Get-Content`/`Set-Content` without `-Encoding UTF8`: it re-encodes through cp1251 and silently corrupts glyphs (this once turned every top-bar icon into `□`).
  - For scripted edits use `[IO.File]::ReadAllText(path, [Text.Encoding]::UTF8)` and `WriteAllText(path, text, (New-Object Text.UTF8Encoding($false)))` with **absolute** paths (.NET's working directory is not PowerShell's location).
  - Icon glyphs are referenced by code point (`""`) in `VOS.UI/Identity.cs` (`Glyphs`). Check new glyphs with `VOS.exe --glyph-test` before using them; Segoe Fluent Icons has no weather set (use `WeatherIcon`).
- Native shell crashes (stowed exceptions, `0xC000027B`) leave no managed log: read the Application event log (event 1000 / WER) and `%LOCALAPPDATA%\V-OS\logs`.

## Architecture rules (see ARCHITECTURE.md)
- **UI:** WinUI 3 on Windows App SDK 2.x with Microsoft.UI.Composition. **No WPF** (ADR-001).
- **Layering:**
  - `VOS.Core` (no UI) → `VOS.Platform` (OS interop, no UI framework) → `VOS.UI` (design system) → `VOS.Shell` / `VOS.Apps` / `VOS.Launcher`.
  - Never reference upward. Modules communicate via Core services and IPC.
- **OS access and styling:**
  - All OS access goes through `VOS.Platform`.
  - All colours, typography, spacing and motion come from `VOS.UI` tokens. Views never hard-code colours.
- **AOT-ready code:**
  - JSON source generation;
  - `[GeneratedComInterface]` / `LibraryImport` for interop;
  - no `dynamic`, no reflection-based activation.
- **Efficiency:**
  - Event-driven, not polling: WinEvent hooks, file watchers, WinRT events. Never block the UI thread.
  - Animations run on the compositor.
  - Everything visual respects `EffectiveQuality` (performance profiles must change real behaviour).
- **Safety:**
  - Per-user install with no elevation at runtime.
  - Never disable Defender, UAC, Firewall, Windows Update or Secure Boot.
  - No unsupported registry edits; never replace system files.
  - Every change to Windows UI state (taskbar, desktop icons, wallpaper) is recorded and restorable (watchdog, recovery hotkey, Launcher → Recovery).
- **Privacy:**
  - No telemetry.
  - Network calls only to endpoints listed in `docs/PRIVACY.md`, and only after consent where personal data is involved.
  - Never activate the microphone without an explicit user action.

## Performance budgets (reference: i7-6700K / GTX 970 / 16 GB)
- Shell idle CPU under 0.5 % average.
- Shell private memory under 150 MB.
- App window open: under 300 ms warm, under 1 s cold.
- Shell animations at the display refresh rate with no dropped frames.
- Report only measured numbers (TESTING.md).

## Definition of done (per feature)
A feature is done when it is:
- implemented end-to-end and integrated with related modules (capsule, search, activity, workspaces);
- handling errors and offline cases;
- respecting adaptive quality;
- accessible (keyboard, UIA names, high contrast);
- tested (unit, integration or UI);
- measured, if performance-sensitive;
- documented.

## Keep the knowledge base current
Update in the same change whenever relevant:

| File | Update with |
|---|---|
| ARCHITECTURE.md | architecture |
| ROADMAP.md | status |
| DEPENDENCIES.md | every new or updated dependency |
| INTEGRATIONS.md | connected or recommended tools |
| DECISIONS.md | ADRs |
| TESTING.md | strategy and measured results |
| RELEASE_NOTES.md | user-visible changes |

## Slash commands
- `/v-os-checkup [focus]`: read-only audit of the development environment and project. Produces a prioritized report with official links. Installs or changes nothing. Definition: `.claude/commands/v-os-checkup.md`.

## Build and test
```powershell
. .\build\dev-env.ps1
dotnet build V-OS.slnx -c Debug        # everything; .\build\dev-build.ps1 builds VOS.exe and prints only issues
dotnet test tests\VOS.Tests
.\build\build.ps1                      # release: artifacts\release\<version>\ (V-Launcher.exe, package zip, signed vos-release.json)
```
- `build.ps1` signs with `%USERPROFILE%\.vos-release\release-key.pem` (never in the repo). `-Version`, `-Channel`, `-Feed <url>` (baked into V-Launcher.exe), `-SkipTests`.
- Install tests never touch the real installation: set `VOS_INSTALL_ROOT` (and `VOS_DATA_ROOT`) to temp folders. A redirected installation registers nothing with Windows.
- Task dialogs of V-Launcher.exe can be driven by sending `TDM_CLICK_BUTTON` (0x466) with the button id to the `#32770` window: 100 Install, 101 Open, 102 Repair, 103 Uninstall, 2 Cancel, 8 Close.
- `build\tools\make-icon.ps1` regenerates `assets\icons\vos.ico`.
