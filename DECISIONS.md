# Architecture Decision Records

Each record states the context, the options compared, the decision, and its consequences.
Status values: **Accepted**, **Accepted — validation pending** (a spike must confirm stated assumptions before dependent work), **Superseded**.

---

## ADR-001 — UI foundation: WinUI 3 + Windows Composition, no WPF

**Status:** **Accepted — validated by spike S1** (2026-11-21, `docs/spikes/S1/README.md`). The owner approved the architecture on 2026-11-21.
**Supersedes:** the initial WPF plan of 2026-11-21, made before any UI code existed.
**Trigger:** owner directive `docs/brief/03-ui-framework-directive.md`.

### Context
V-OS's shell is the most demanding UI on the machine. It is always visible, animated, translucent, and stacked above every other application. It needs:
- per-pixel transparent, non-activating, topmost surfaces (dock, capsule, panels);
- real backdrop blur in custom shapes;
- fluid motion that stays smooth while the UI thread is busy;
- adaptive effects for weak hardware;
- first-class accessibility.

It also has to carry 20+ applications with one consistent design system.

### Verified facts (official docs and this machine, 2026-11-21)
| Fact | Source |
|---|---|
| Windows App SDK **2.7.0** is the current stable release (2026-11-17). 2.0 stable shipped 2026-04-29, and SemVer has applied since 2.0. | learn.microsoft.com, release-channels and 2.0 release notes; GitHub releases |
| Releases are serviced for about 12 months. **1.8 reached end of servicing on 2026-09-24**, so only 2.x is a valid target. | learn.microsoft.com, release-channels |
| Self-contained deployment is supported for all packaging options, including unpackaged apps. | learn.microsoft.com, deployment-architecture |
| **`SystemBackdropElement`** has been stable since 2.0.1. It places Mica or Acrylic anywhere in the XAML layout, with a `CornerRadius`. | 2.0 release notes |
| XAML compiler errors are now surfaced under `dotnet build` (fixed in 2.1.3). | 2.x release notes, GitHub #9813 |
| Hosting WinUI on more than one UI thread is supported. The fixes are in 2.5.1 and 2.7.0. | 2.x release notes |
| Unpackaged apps require the **Visual C++ Redistributable**. | deploy-unpackaged-apps |
| The docs do not state Native AOT support for WinUI 3. | — |
| .NET 10.0.404 SDK works here, and WebView2 Runtime 158 is present. | local audit |

### Options compared
| Criterion | **WinUI 3 + Composition** | WPF (.NET 10) | Win32 + Windows.UI.Composition, no XAML | Avalonia / Uno / Electron |
|---|---|---|---|---|
| Rendering pipeline | D3D11 and DirectComposition through the Windows compositor | Legacy milcore (D3D9-era). Transparent windows take a CPU read-back path (UpdateLayeredWindow). | DirectComposition | Own renderer (Skia) or Chromium |
| Motion off the UI thread | **Yes**: compositor springs, expression and implicit animations | No: the animation clock runs on the UI thread | Yes | Partial |
| Materials | MicaController and DesktopAcrylicController with built-in fallback policy (battery saver, transparency off, high contrast). **SystemBackdropElement** gives rounded glass regions. | DWM attribute interop, whole window only | Host backdrop brush (Windows 11), manual policy | Window-level hints only |
| Lighting and effects | CompositionLight, effect brushes (Win2D), GPU shadows | Pixel shaders that force software paths in layered windows | Same as WinUI, but manual | Varies |
| Text | DirectWrite, variable fonts | WPF's own text stack | DirectWrite, by hand | Skia + HarfBuzz |
| Accessibility (UI Automation) | Built in | Built in | Must be hand-built | Built in or partial |
| Velocity for 20+ apps | High (TabView, ItemsView, WebView2, MediaPlayerElement) | Highest | Very low: we would be writing a UI framework | Medium |
| Memory and startup | Medium (R2R now, AOT later) | Lower | Lowest | Electron: high |
| Support | Active. 2.x is serviced; WinUI is Microsoft's current native UI. | Maintenance mode. Supported with .NET 10 until 2028. | OS APIs | Third-party |
| Owner preference | Preferred | Rejected for the shell | — | — |

C++/WinRT with WinUI 3 was also considered. It has the best raw efficiency but much lower development velocity and higher defect risk. C# on .NET 10 with AOT-ready code captures most of the gain, so C++ was rejected.

### Decision
1. **All user-facing V-OS UI uses WinUI 3 on Windows App SDK 2.x (pinned to 2.7.0), with Microsoft.UI.Composition for motion, lighting and effects.** This covers the Shell, every app module, and the Launcher UI.
2. **Shell surfaces** (dock, capsule, top bar, panels) are borderless AppWindows. Each is transparent (custom transparent `SystemBackdrop` on a DirectComposition window, which has no redirection bitmap), non-activating where appropriate (`WS_EX_NOACTIVATE`), hidden from switchers (`IsShownInSwitchers = false`), and topmost.
   - Glass inside them comes from **`SystemBackdropElement`** with capsule radii.
   - Window regions make transparent margins click-through.
   - The top bar registers as an AppBar.
3. **Motion** is compositor-driven (natural-motion springs, implicit show/hide/offset animations, expression animations). The UI thread never ticks animations.
4. **The ambient desktop** uses composition gradient brushes and keyframes for zero-CPU motion. Win2D `CanvasAnimatedControl` handles particle weather on its own thread with an adaptive frame rate. Both pause when the desktop is covered.
5. **Win32, COM and WinRT** cover every system integration, isolated in `VOS.Platform` with no dependency on a UI framework.
6. **WPF is not used anywhere.** The only candidate was a single-file installer. It is solved without WPF: `V-Launcher.exe` is a UI-less, single-file bootstrapper that unpacks the payload and starts the WinUI installer (ADR-005).

### Consequences
- **Migration:** nothing to rewrite. No UI code existed. The two `VOS.Core` files (foundation services, settings model) are framework-agnostic and stay. The scaffolded `VOS.Design` (WPF) project becomes `VOS.UI` (WinUI 3 class library). `VOS.Platform` drops its WPF reference.
- **Dependencies:** Windows App SDK 2.7.0, Win2D 1.4.0, and possibly two CommunityToolkit.WinUI control packages. NAudio is dropped in favour of WinRT AudioGraph plus direct WASAPI interop (ADR-008).
- **Payload:** the self-contained Windows App SDK runtime adds tens of MB. The spike measures this and reduces it by referencing only the needed component packages instead of the full metapackage, which is about 280 MB as a NuGet download.
- **Runtime prerequisite:** none beyond Windows and the WebView2 Runtime. Spike S1 found that no self-contained binary imports the VC++ runtime (`vcruntime140`, `msvcp140`, `concrt140`). The documented "VC++ Redistributable" requirement applies to framework-dependent deployment. The installer still runs a defensive check.
- **Compatibility:**
  - Windows 11 22H2+ is the primary target with full effects.
  - Windows 10 22H2 is best effort with degraded materials. Microsoft supports the SDK only on Windows releases still in service.
  - x64 is primary; ARM64 builds are possible.
- **Performance strategy:** ReadyToRun and TieredPGO now. All code is AOT-ready (System.Text.Json source generation, `[GeneratedComInterface]` COM). Native AOT for the Shell and bootstrapper is evaluated once the MSVC toolchain is available, since AOT support for WinUI is not stated in the docs.

### Validation spike S1 (first development step after setup approval)
| # | Assumption to prove on this machine | Fallback if it fails |
|---|---|---|
| 1 | An unpackaged, self-contained WinUI 3 app builds and runs from `dotnet build` and `dotnet publish`. | Visual Studio Build Tools (MSBuild with the full XAML toolchain) |
| 2 | A transparent per-pixel borderless window renders anti-aliased capsule content. | Pure Windows.UI.Composition layer window for visuals, with XAML on top |
| 3 | `SystemBackdropElement` inside a transparent window gives capsule-shaped acrylic. | Composition host backdrop brush (`DWMWA_USE_HOSTBACKDROPBRUSH`, Windows 11), then tinted "smoked glass" |
| 4 | Click-through outside shapes via `SetWindowRgn`; non-activating topmost windows; AppBar registration | Tight window bounds animated per state |
| 5 | DWM thumbnails (`DwmRegisterThumbnail`) render in WinUI windows | Windows.Graphics.Capture previews |
| 6 | Idle CPU ≈ 0 with no animation running; compositor springs hold 60 fps | Reduce effect layers; profile with WPR |
| 7 | Memory and startup baseline per process; payload size; VC++ runtime imports | App-host process model (ADR-003); trim package components |

**Revisit triggers:** a spike assumption fails with no acceptable fallback, or Microsoft deprecates a relied-on API.

---

### Spike S1 outcome (2026-11-21)
All seven assumptions held on the reference machine, two of them with adjusted techniques (recorded in ADR-015). Measured numbers are in TESTING.md.

---

## ADR-015 — Shell surface technique (from spike S1)
**Status:** Accepted · 2026-11-21

Shell surfaces are standard WinUI `Window`s configured as follows:
1. **Presenter:** `OverlappedPresenter` without border or title bar, and with resize, minimize and maximize all off.
2. **Window styles and visibility:**
   - `WS_POPUP` style;
   - `WS_EX_TOOLWINDOW`, plus `WS_EX_NOACTIVATE` for passive surfaces;
   - `AppWindow.IsShownInSwitchers = false`;
   - topmost where needed;
   - shown with `AppWindow.Show(activateWindow: false)`.
3. **DWM attributes:** non-client rendering disabled, no corner rounding, no border colour.
4. **Transparency:** a `SystemBackdrop` that sets a transparent *system* composition brush, **plus `DwmExtendFrameIntoClientArea(-1)`**. Without the frame extension the redirected top-level window stays opaque.
5. **Glass:** `SystemBackdropElement` (stable since 2.0.1) with a custom `SystemBackdrop` that drives `DesktopAcrylicController` with `IsInputActive = true`. The stock `DesktopAcrylicBackdrop` falls back to solid grey on windows that never take focus. Tint, opacity and luminosity are design tokens, and the quality governor can swap glass for solid tint.
6. **Hit testing:** window regions follow visible shapes, inflated 2 px. They are widened before an expand animation and narrowed after a collapse.
7. **Motion:** composition geometry (`CompositionRoundedRectangleGeometry`) animated with `SpringVector2NaturalMotionAnimation`. Expression animations keep it centred and rounded, all on the compositor thread.

**Fallback:** a XAML island in a `WS_EX_NOREDIRECTIONBITMAP` popup is also transparent. Never assign a custom backdrop to a `DesktopWindowXamlSource`: it crashes in 2.7.

**Additions from the first live shell runs (2026-11-21):**
8. **Frameless client area.** WinUI re-applies `WS_DLGFRAME`/`WS_SYSMENU` whenever the presenter syncs, which brings back a 3 px classic frame: a light line under the dock, and top-bar content squeezed from 34 to 28 px. Every `SurfaceWindow` subclasses its HWND and answers `WM_NCCALCSIZE` (wParam = TRUE) with 0, so the client area is always the whole window. `dev-shell.ps1 geometry` reports any remaining frame.
9. **Panels are cloaked, not hidden.** Re-showing a hidden WinUI window can flash one black frame before XAML re-presents. A panel is shown once (DWM-cloaked, `DWMWA_CLOAK`) and stays shown. Closing it cloaks it and hands keyboard focus back. Opening it updates the content, waits for a rendered frame plus a DWM composition pass (`DwmFlush`), and only then uncloaks and plays the entrance. Cloaked windows are not hit-testable (verified with `WindowFromPoint`) and cost no idle CPU (0.000 % measured). The panels opened most often are pre-built during idle time 2 s after start.
10. **Composition shadows use a mask.** A `LayerVisual` drop shadow over a near-transparent shape sometimes rendered the shape solid black for a frame (6 of 15 opens flashed black, 0 of 15 without the shadow). Shadows are now a `SpriteVisual` + `DropShadow` whose `Mask` is an opaque rounded rectangle in a `CompositionVisualSurface` (0 black frames in 20 opens). The shadow sits inside the animated card, so it fades in with it.
11. **Shell icons come from one STA thread.** `IShellItemImageFactory` answers concurrent requests with `E_PENDING`, which left blank dock icons. `IconService` serves requests one at a time on a dedicated STA thread, retries briefly when pending, never caches a failure, and premultiplies the shell's straight-alpha pixels (WinUI composes premultiplied BGRA). Callers use `Images.LoadIcon`/`IconService.GetAsync`, never `Task.Run(IconService.Get)`.

## ADR-016 — Windows App SDK component package
**Status:** Accepted · 2026-11-21
- V-OS references `Microsoft.WindowsAppSDK.WinUI` 2.7.0, which brings WinUI, Foundation, InteractiveExperiences and Base. It does not use the `Microsoft.WindowsAppSDK` metapackage, which adds on-device AI/ML runtimes (onnxruntime, DirectML), semantic search, widgets, and 165 MB of MSIX installers that only framework-dependent deployment needs.
- Effect: self-contained output went from 281.6 MB to 216.9 MB with identical behaviour.
- Revisit if V-OS adopts a feature from those components.

## ADR-017 — Interop and JSON on the way to Native AOT
**Status:** Accepted · 2026-11-21
- **Today** the shell uses `[ComImport]` interfaces (shell items, property stores, Known Folders) and reflection-based `System.Text.Json`. Both work in the self-contained ReadyToRun build that ships first.
- **Before any Native AOT or trimmed build:**
  - convert every `[ComImport]` interface to `[GeneratedComInterface]` and every `DllImport` with non-blittable signatures to `LibraryImport`;
  - move `VosJson` to a `JsonSerializerContext` covering every persisted and IPC type.
  The code already avoids `dynamic` and reflection-based activation, except the app-module registry, which must become an explicit list.
- **Why later:** AOT for WinUI 3 in Windows App SDK 2.x is supported but still constrains XAML features and tooling. ReadyToRun already meets the startup budget (first frame 780 ms in spike S1). AOT is a size and startup optimisation, not a correctness requirement.
- **Trigger:** Phase 6 performance work, or a startup or memory budget miss that ReadyToRun cannot fix. Native AOT also needs the VS C++ build tools (see INTEGRATIONS.md, "When deferred tools become necessary").

## ADR-002 — Language and runtime: C# on .NET 10 LTS
**Status:** Accepted · 2026-11-21
- .NET 10 is the current LTS (patch 10.0.15, SDK 10.0.404, supported until November 2028). .NET 11 (released November 2026) is STS, so it is not used for a long-lived product.
- The SDK is pinned in `global.json` with `rollForward: latestFeature`. It is installed user-local for development. Shipped builds are self-contained, so users need no .NET installation.
- Coding rules that keep the code AOT- and trim-ready:
  - Use JSON source generators, not reflection.
  - Use `[GeneratedComInterface]` and `LibraryImport`, not `ComImport`.
  - Avoid `dynamic`.

## ADR-003 — Process model
**Status:** Accepted · 2026-11-21 · **Revised 2026-11-21:** one executable with modes

**Revision.** The roles below ship as a single self-contained `VOS.exe` selected by command-line mode, not as separate executables:
`--shell` (default), `--app <id> [args]` (app host; one process hosts every app window, the browser gets its own instance), `--launcher`, `--watchdog <pid>`, `--recover`, `--health-check`, `--glyph-test`.
One binary means one copy of the ~200 MB self-contained runtime instead of three, a single code-signing and update unit, and identical code in the watchdog and the shell it guards. Process isolation is unchanged: each role still runs as its own process.

Original plan, kept for reference (the roles still apply):

| Process | Role |
|---|---|
| `VOS.Shell.exe` | Always running. Shell surfaces plus background services: notifications, media sessions, weather, clipboard, timers, workspaces, focus, flows, and the adaptive-quality governor. Hosts the IPC server. |
| `VOS.Apps.exe` | App host started on demand. Every module window runs here on one UI thread with async discipline, which gives low memory and instant window opens. A crash in an app never takes down the desktop. |
| `VOS.Apps.exe --isolated browser` | The browser runs in its own instance. It is long-lived and memory-heavy, and WebView2 renderers are out of process anyway. |
| `VOS.Launcher.exe` | Setup, update, repair, rollback, recovery, diagnostics, uninstall, and the immersive-mode watchdog. |
| `V-Launcher.exe` | Download bootstrapper (ADR-005). |

## ADR-004 — Shell integration modes
**Status:** Accepted · 2026-11-21
- **Companion mode** (default) runs on top of Explorer. The top bar is an AppBar. Windows UI stays reachable.
  - *Revised 2026-11-21:* the dock also registers as a bottom AppBar (`Dock.ReserveSpace`, on by default), so maximised windows end above it instead of under it. Intellihide is used only when the dock does not reserve space.
  - The Windows taskbar is set to auto-hide while V-OS runs (`Shell.AutoHideTaskbar`, on by default), so the dock owns the bottom edge and the taskbar is one mouse move away. Auto-hide is a persisted Explorer setting, so the original value is recorded first and restored on exit, by the watchdog, at the next start, by the recovery hotkey, by Launcher → Recovery and by uninstall. Verified: a graceful stop restored the work area and auto-hide exactly.
  - Fullscreen apps (games, video, presentations) get the whole screen: on `ABN_FULLSCREENAPP` every V-OS surface steps aside.
- **Immersive mode** (opt-in) applies three reversible changes:
  1. Hides the Windows taskbar (auto-hide state plus `SW_HIDE`).
  2. Hides desktop icons.
  3. Routes the Win key to the V-OS launcher through a low-level hook on a dedicated thread.

  Each change records its prior state. A watchdog process, the recovery hotkey Ctrl+Alt+Shift+F12, and Launcher → Recovery all restore Windows UI. If V-OS stops, restarting Explorer also restores the taskbar.
- **Winlogon shell replacement is rejected.** A startup failure would leave the user without a working desktop, which conflicts with "never destroy the Windows shell" and "always preserve a reliable way back".

## ADR-005 — Installation and delivery
**Status:** Accepted · 2026-11-21
- Per-user install to `%LOCALAPPDATA%\Programs\V-OS`. No elevation is needed.
- User data lives separately in `%LOCALAPPDATA%\V-OS`.
- Install layout and updates:
  - `app\` holds the active version and `app.previous\` the rollback copy.
  - An update stages into `app.staging\` and then swaps atomically.
- Registration is all per-user: an HKCU uninstall entry, Start-menu shortcuts, and an HKCU `Run` entry when "start with Windows" is chosen.
- `V-Launcher.exe` is a single-file bootstrapper with no UI framework. It:
  1. verifies and unpacks the payload into `app.staging\`;
  2. starts the WinUI installer (`VOS.Launcher.exe --setup`), which handles compatibility checks, components, progress, and first launch.
- MSIX was not chosen: it needs a trusted signing certificate for sideloading and restricts shell-level integration.

## ADR-006 — Updates, integrity and rollback
**Status:** Accepted · 2026-11-21
- Feed: `vos-release.json` on GitHub Releases. The URL is configurable.
- The manifest is signed with **ECDSA P-256**. The public key is compiled into V-OS; the private key never enters the repository.
- Packages are verified by SHA-256 before extraction.
- After a swap, a health check runs (`--health-check`). On failure, `app.previous\` is restored automatically.
- User data is never touched by updates. A settings snapshot is taken before every update.
- Authenticode signing is added once a code-signing certificate is available (see INTEGRATIONS.md).

## ADR-007 — Browser engine: WebView2 (Evergreen)
**Status:** Accepted · 2026-11-21
- WebView2 was chosen over a bundled Chromium (CefSharp/CEF). Microsoft ships Chromium security patches to the shared runtime, the disk and memory footprint is smaller, and the WinUI 3 control is native.
- Known limitations, documented for users:
  - extensions load from unpacked folders only (no Web Store);
  - no Widevine DRM, so some streaming sites fail and say so.

## ADR-008 — Audio stack
**Status:** Accepted — validation pending (Music spike) · 2026-11-21
- WinRT **AudioGraph** handles local playback (MP3, AAC, FLAC, WAV, ALAC through Media Foundation), the built-in equalizer, output device selection, and frame output for the visualizer.
- System media transport controls are wired through the window interop.
- **WASAPI** COM interop, source-generated, covers:
  - loopback capture for music-reactive visuals (never the microphone);
  - endpoint volume;
  - per-app sessions;
  - default-device switching via `IPolicyConfig`, which is undocumented and degrades gracefully.
- This replaces NAudio: fewer dependencies, and AOT-compatible.

## ADR-009 — Storage
**Status:** Accepted · 2026-11-21
- `settings.json` is the single source of truth for preferences. Writes are atomic (temp file plus replace). A named mutex serializes read-modify-write across processes, and file watchers propagate changes live.
- Structured data uses SQLite via Microsoft.Data.Sqlite in WAL mode, with one database per module (`notes.db`, `music.db`, `browser.db`, `clipboard.db`, `metrics.db`, and so on). This makes per-module "clear data" trivial.
- Secrets (API keys) are protected with DPAPI under the current user scope.

## ADR-010 — Inter-process communication
**Status:** Accepted · 2026-11-21
- Named pipes with `PipeOptions.CurrentUserOnly`, carrying JSON-lines envelopes `{type, id, data}`.
- Message types: notify, live-activity update/end, open, command, ping.
- A per-app single-instance pipe forwards arguments to running app windows.

## ADR-011 — Weather provider: Open-Meteo
**Status:** Accepted · 2026-11-21
- Open-Meteo provides forecast, geocoding and air quality with no key, and is reliable.
- **Licensing caveat:** the free API is for non-commercial use. Commercial distribution needs an Open-Meteo API subscription or another provider. The endpoint and API key are configurable.
- Official alerts are not provided by Open-Meteo. V-OS shows forecast-derived advisories, clearly labelled as unofficial.

## ADR-012 — Build output redirection
**Status:** Accepted · 2026-11-21
- Windows long paths are disabled on the development machine, and the workspace path is 180 characters.
- When the `VOS_BUILD_ROOT` environment variable is set (by `build/dev-env.ps1`), `obj/` and `bin/` go to a short folder.
- The WinUI/WPF temporary-project name problem is handled with `_TargetAssemblyProjectName`.
- This is not needed once the repository lives at a short path.

## ADR-013 — Design language "Instrument"
**Status:** Accepted · 2026-11-21
- Graphite surfaces; accent rendered as *light* (composition lights, glows), not paint.
- Bahnschrift (DIN) for numerals and instruments; Segoe UI Variable (Display, Text, Small) for the interface.
- Radii follow hierarchy: windows 12, panels 16, cards 12, controls 8, capsules full.
- Spacing on a 4-px grid.
- One signature motion moment: the capsule. Motion responds to user actions; nothing animates without reason.
- Dynamic accent sources: preset, Windows, wallpaper, or album art.

## ADR-014 — No telemetry
**Status:** Accepted · 2026-11-21
- V-OS sends no usage data.
- Logs and crash reports stay local (`%LOCALAPPDATA%\V-OS\logs`, `crashes`) and are exported only by the user via Launcher → Diagnostics.
- Every network endpoint is listed in `docs/PRIVACY.md`.
