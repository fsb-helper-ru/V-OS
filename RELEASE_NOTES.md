# Release Notes

## Unreleased — 1.0.0 (in development)
No build has been released yet.

**Done so far:**
- Development environment set up: .NET 10 LTS.
- Architecture decided: WinUI 3 + Windows Composition (see DECISIONS.md, ADR-001).
- Project knowledge base created.
- Shell: top bar, V-Pill capsule, dock, desktop, command and search, control center, activity center, clipboard, drop shelf, switcher, layout zones, workspaces, focus, alarms, flows, lock presentation, onboarding, companion and immersive modes with watchdog recovery.
- Apps: Settings, Files, Browser, Music, Weather, Monitor, Notes, Time, Capture, Store, Gaming, Translate, Connect, Flow and Assistant.
- Assistant: works offline on this PC with V-OS commands. It can also use Claude (your own API key; consent first; actions as validated tools; another model answers if Claude declines) or a local Ollama server.
- Fixed: the “note …” command opened Notes without the note text.
- Removed: the Music “Visualizer” and “Crossfade” switches, which had no effect yet (planned after 1.0).

**Known limitations so far:** the interface is English while dates and numbers follow the regional format; Music has no equalizer, visualizer or crossfade; Capture has no screen recording; Store needs winget; alarms and flows need the shell running; Ollama answers but cannot run actions.

**Planned for 1.0.0:** the complete scope in `docs/brief/01-master-directive.md`, tracked in ROADMAP.md.

Release entries will list user-visible changes, known limitations, and verified test results for each version.
