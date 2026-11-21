# Roadmap

The goal is one complete, integrated, tested V-OS 1.0 release. The phases below are internal engineering steps, not separate deliveries.

Legend: ✅ done · 🔄 in progress · ⏸ waiting on the owner · ⬜ not started

_Last updated: 2026-11-21._

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

## Phase 2 — Foundations
- 🔄 VOS.Core: foundation services ✅ (paths, JSON, atomic files, logging, crash reports, formatting), settings model ✅; still to do: settings store, SQLite, IPC, quality resolver, weather, search parser, updates, notes, calendar, timers, clipboard, flows
- ⬜ VOS.Platform: Win32/COM/WinRT services (AOT-ready interop)
- ⬜ VOS.UI: "Instrument" design system (tokens, themes, typography, icons, controls, composition helpers, window helpers)

## Phase 3 — Shell
- ⬜ Top bar · V-Pill capsule · dock · desktop and ambient · widgets
- ⬜ Command and search · control center · activity center (notifications plus timeline)
- ⬜ Clipboard · drop shelf · switcher · layout zones · workspaces · focus · flows · lock presentation
- ⬜ Onboarding · companion and immersive modes · watchdog and recovery

## Phase 4 — Apps
- ⬜ Settings (including Privacy and Snapshots) · Files (including Archive) · Browser · Music · Weather · Monitor
- ⬜ Notes · Time (clocks, timers, stopwatch, calendar) · Capture · Store · Gaming
- ⬜ Assistant · Translate · Connect · Flow editor

## Phase 5 — Delivery
- ⬜ Bootstrapper `V-Launcher.exe` · WinUI installer and maintenance UI
- ⬜ Updater (signed feed) · rollback · recovery · uninstall
- ⬜ Reproducible `build/build.ps1` · GitHub Actions CI and release workflow (needs H1)

## Phase 6 — Quality
- ⬜ Unit, integration and UI automation tests · end-to-end install/update/rollback/uninstall (Windows Sandbox, H4)
- ⬜ Performance measurement against budgets · regression pass · documentation (user guide, limitations, privacy, security)

## Phase 7 — Release 1.0.0
- ⬜ Final build, verification, release notes, signed artifacts (when O2 is available)

## Next action
Owner approvals (Phase 0). Then spike S1 and continued development.
