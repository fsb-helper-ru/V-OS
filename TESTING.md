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
| — | Product tests | Begin with the foundations (Phase 2) |

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
