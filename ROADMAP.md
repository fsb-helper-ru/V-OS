# Roadmap

The goal is one complete, integrated, tested V-OS 1.0 release. The phases below are internal engineering steps, not separate deliveries.

Legend: ✅ done · 🔄 in progress · ⏸ waiting on the owner · ⬜ not started

_Last updated: 2026-11-22._

## Phase 0 — Environment and decisions
- ✅ .NET 10.0.404 SDK installed user-local (owner-approved), verified
- ✅ Toolchain smoke build (.NET 10 desktop build through the short build root)
- ✅ Capability and environment audit (INTEGRATIONS.md, DEPENDENCIES.md)
- ✅ Framework reassessment → ADR-001: WinUI 3 + Composition, no WPF
- ✅ Knowledge base (CLAUDE.md and the docs listed in it) and `/v-os-checkup`
- ✅ Owner approvals. R1: project moved to `C:\Dev\V-OS` and verified. R2: packages restored. H3: diagnostics installed. H1: GitHub prepared without sign-in. H2: connector offered. H4/O1: deferred by the owner.

## Phase 1 — Architecture validation spike S1 (ADR-001) ✅
- ✅ Unpackaged, self-contained WinUI 3 build and publish from the CLI
- ✅ Transparent capsule window, `SystemBackdropElement` glass, click-through regions, AppBar, non-activating topmost (ADR-015)
- ✅ DWM thumbnails in WinUI windows; compositor springs independent of the UI thread; idle CPU 0.06 %
- ✅ Memory 98 MB, first frame 780 ms, payload 217 MB; no VC++ runtime dependency; component packages (ADR-016)

## Phase 2 — Foundations ✅
- ✅ VOS.Core: paths, JSON, atomic files, logging, crash reports, settings store and migration, SQLite, IPC, quality resolver, weather, search and command parser, unit conversion, update manifests, notes, calendar (ICS), timers and alarms, flows, translation, assistant actions, Markdown subset
- ✅ VOS.Platform: Win32/COM/WinRT services (windows, shell items and icons, file operations, audio endpoints and sessions, media sessions, power, network, processes, screen capture, winget, game libraries, local share server)
- ✅ VOS.UI: "Instrument" design system (tokens, live themes and accent retint, typography, icons, controls, app kit, virtualised lists, Markdown view, composition helpers, surfaces)

## Phase 3 — Shell ✅
- ✅ Top bar · V-Pill capsule · dock · desktop and ambient · widgets
- ✅ Command and search · control center (including output device and per-app volume) · activity center (notifications plus timeline)
- ✅ Clipboard · drop shelf · switcher · layout zones · workspaces · focus · flows · alarms · lock presentation
- ✅ Onboarding · companion and immersive modes · watchdog and recovery

## Phase 4 — Apps ✅
- ✅ Settings (including Privacy and Snapshots) · Files (including Archive) · Browser · Music · Weather · Monitor
- ✅ Notes · Time (clocks, alarms, timers, stopwatch, calendar) · Capture · Store · Gaming
- ✅ Translate · Connect · Flow editor · Assistant (on this PC, Claude, Ollama; ADR-018)

## Phase 5 — Delivery
- ✅ Bootstrapper `V-Launcher.exe` (single file, task dialogs): install, repair, apply update with automatic rollback, rollback, uninstall
- ✅ WinUI launcher (`--launcher`): setup completion and registration, signed updates, rollback, repair, recovery, diagnostics export, uninstall
- ✅ Reproducible `build/build.ps1` with `VOS.BuildTool`: deterministic package, signed manifest, verification; V-OS icon
- ✅ End-to-end delivery test on real artifacts (redirected install): install, repair, update, rollback, automatic rollback, uninstall
- ⏸ Real-installation test (Apps entry, Start menu) and hosted HTTPS feed: Windows Sandbox (H4) or the owner's go-ahead; GitHub release (H1)
- ⬜ GitHub Actions CI and release workflow (needs H1)

## Phase 6 — Quality
- 🔄 Unit and integration tests (128 passing) · live verification of every app with screenshots
- ⬜ FlaUI UI automation suite · end-to-end install/update/rollback/uninstall (Windows Sandbox, H4)
- ⬜ Release measurements against budgets (start, memory, 10-minute idle CPU) · documentation (user guide, limitations, privacy, security, third-party notices)

## Phase 7 — Release 1.0.0
- ⬜ Final build, verification, release notes, signed artifacts (when O2 is available)

## After 1.0 (tracked, not in the 1.0 scope)
- Interface localization (English and Russian first). Today the interface is English while dates and numbers follow the regional format.
- Music: equalizer, visualizer, crossfade. Capture: screen recording.
- Native AOT and trimming (ADR-017).

## Next action
Phase 6: user documentation (guide, limitations, privacy, security, third-party notices), then the final release build with measurements.
