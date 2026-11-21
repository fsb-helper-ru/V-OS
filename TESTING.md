# Testing

## 1. Strategy
| Level | Tooling | Scope |
|---|---|---|
| Unit | xUnit | Core logic: settings normalisation and migration, atomic files, quality resolver, search and command parsing, calculator, unit conversion, ICS parsing, update manifest signatures and version comparison, weather parsing, batch-rename patterns, folder comparison, flow rules |
| Integration | xUnit on Windows | Platform services against the real OS: window enumeration, Apps-folder catalog, icon extraction, PDH counters, audio endpoints, Windows Search query, file operations in a temp sandbox folder |
| UI automation | FlaUI (UIA3) | Every app window opens, navigates, and exposes accessible names. Shell panels open and close. Keyboard paths work. |
| Visual | screenshots of real windows | design review against the "Instrument" language; light, dark and high contrast; DPI 100/125/150/200% |
| End-to-end | Windows Sandbox (`.wsb` scripts) | Install → first launch → update → forced failure → automatic rollback → recovery → uninstall, on a clean Windows |
| Performance | `dotnet-counters`, `dotnet-trace`, WPR/ETW, in-app timing | Cold and warm start, idle CPU over 10 minutes, private memory, frame pacing of shell animations, behaviour per performance profile |
| Resilience | fault injection | killed shell restores the Windows UI (watchdog); corrupt settings recovered; offline network; denied permissions (location, Wi-Fi scan) |

Rules:
- A feature is not done until its tests exist and pass.
- Regressions get a test before the fix.
- Performance claims are only those measured here, with hardware and conditions stated.

## 2. Performance budgets (reference machine: i7-6700K, GTX 970, 16 GB)
| Metric | Budget |
|---|---|
| Shell idle CPU (10 min, no animation) | < 0.5 % average |
| Shell private memory | < 150 MB |
| App host with 3 apps open (excluding WebView2) | < 200 MB |
| App window open (warm host) | < 300 ms |
| App window open (cold) | < 1 s |
| Capsule and dock animations | display refresh rate, no dropped frames |

## 3. Results
| Date | Test | Result |
|---|---|---|
| 2026-11-21 | Toolchain sanity: .NET 10.0.404 desktop project builds through the short build root | ✅ Build succeeded in 4.8 s |
| 2026-11-21 | WinUI 3 (Windows App SDK 2.7.0) unpackaged, self-contained build with the `dotnet` CLI, no Visual Studio | ✅ 0 errors, 0 warnings. First restore and build took 115 s. |
| 2026-11-21 | **Spike S1 — shell surfaces** (`docs/spikes/S1/README.md`) | ✅ **Passed** (details below) |
| 2026-11-21 | Unit and integration tests (`tests/VOS.Tests`) | ✅ 95 passed, 0 failed |
| 2026-11-21 | **Live shell run** (Debug build, real desktop, `build/dev-shell.ps1`) | ✅ details below |
| 2026-11-22 | Unit and integration tests (`tests/VOS.Tests`), including 25 assistant tests: action validation, Markdown, Ollama stream parsing, and the Claude conversation through the real SDK against a fake HTTP handler | ✅ 128 passed, 0 failed |
| 2026-11-22 | **Live app verification** (Debug build, dev data root, screenshots of real windows) | ✅ details below |

### Live shell verification (Debug build, reference machine, 2026-11-21)
How: `dev-shell.ps1 start | shot | geometry | send | stop` with an isolated data root, screenshots via the screen DC, UI Automation for element trees, `WindowFromPoint` for hit-testing, and the shell's `diag` IPC message for memory.

| Check | Result |
|---|---|
| Top bar, V-Pill, dock render; glyphs correct | ✅ after fixing cp1251 corruption and wrong code points (`--glyph-test`) |
| Shell ready (process start → all surfaces) | 1.5 s Debug (Release measurement pending, Phase 6) |
| Adaptive quality on GTX 970 | ✅ "Quality" (was wrongly "Performance": GPU probe ran before any compositor existed) |
| Hotkeys | ✅ all 13 registered; 4 preferred chords were taken by other apps, so fallbacks were used (`state/hotkeys-active.json`) |
| Dock reserves space (AppBar); maximised windows end above it | ✅ work area 1680×928 from Y=34 |
| Taskbar auto-hide applied, then restored by a graceful stop | ✅ work area and auto-hide returned to the recorded values |
| Dock icons of running apps (incl. packaged apps) | ✅ after serialising extraction on an STA thread (was `E_PENDING` → blank icons) |
| Surface windows have no non-client frame | ✅ client = window for every surface (was a 3 px frame: light line under the dock) |
| Panel open: black frames in 20 consecutive opens | ✅ 0 (was up to 6 in 15 because of the LayerVisual shadow) |
| Closed panel passes clicks through and returns focus | ✅ `WindowFromPoint` hits the app below; foreground returns to the previous app |
| Control Center fits its content, never shows half-built | ✅ cloaked until device queries finish; height follows content |
| Idle CPU (30 s, nothing animating) | ✅ 0.007 % of the machine (16 ms CPU); 0.000 % with pre-built panels |
| Private memory: start / 2 panels pre-built / all 7 panels opened | 104 MB / 138–147 MB / 156 MB (Debug; managed heap only 4.5–5.6 MB, the rest is native XAML and composition). Release figures pending (Phase 6). |

### Live app verification (Debug build, dev data root, 2026-11-21 → 22)
Test runs avoid side effects: screenshots go to a temp folder, clipboard writes are disabled, music uses generated tones, no winget installs, no LAN listening, neutral text for online translation.

| App | Verified |
|---|---|
| Settings | all 15 pages render; live theme and accent changes reach open apps and the shell |
| Weather | place search and forecast from Open-Meteo |
| Monitor · Notes · Time | live counters and process list; notes create and search; clocks, alarms, timers, calendar |
| Files · Browser · Music | navigation and previews; example.com in WebView2; playback of generated tones |
| Capture · Store · Gaming | screen and window capture with the selection overlay; real winget search and upgrade lists (no installs); 5 Steam games found |
| Translate · Connect · Flow | MyMemory EN→RU; Connect start page (share server covered by a loopback test); flow list and editor |
| Assistant, on this PC | `12 * 7`, `5 km in miles`, help text in Markdown; `weather in London` from Open-Meteo; `timer 10 min` ran through the shell's new `run` message and appeared in the V-Pill; `open notes` opened Notes in-process |
| Assistant, Claude | consent screen and key screen appear and react to settings at once; with a deliberately invalid key the real API returned 401 and the reply showed “The Claude API key was not accepted”; changing provider mid-chat starts a new conversation (divider shown). A successful Claude answer needs the owner's own key and has not been run. |
| Assistant, Ollama | with no server running: “Could not reach Ollama at http://localhost:11434” |

### Spike S1 transparency strategies (first run, before the fix was adopted)
Margin pixels that must equal the background:

| Strategy | Margin match | Result |
|---|---|---|
| A. WinUI `Window` + transparent backdrop | 0.00 | opaque |
| **B. A + `DwmExtendFrameIntoClientArea(-1)`** | **1.00** | **adopted** |
| C. Frame extension only, no backdrop | 0.00 | opaque |
| D. XAML island in a `WS_EX_NOREDIRECTIONBITMAP` popup | 1.00 | fallback |
| E. D + frame extension | 1.00 | — |

### Spike S1 measurements (Release, ReadyToRun, self-contained, reference machine)
| Metric | Measured | Budget |
|---|---|---|
| Idle CPU: bar + capsule + dock, 30 s | 0.059 % of the machine | < 0.5 % ✅ |
| CPU while animating every second | 1.19 % of the machine | — |
| Private memory | 98 MB | < 150 MB ✅ |
| Process start → first frame | 780 ms | < 1 s ✅ |
| Animation independent of the UI thread | 327 px of movement during a 350 ms UI freeze | ✅ |
| GPU 3D engine during morphs | 0.76 % | — |
| Self-contained output | 216.9 MB (452 files) | measured; compressed size will be measured in Phase 5 |
