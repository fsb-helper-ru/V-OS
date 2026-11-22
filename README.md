# V-OS

A premium desktop environment and application ecosystem for Windows. It gives Windows a new everyday experience — top bar, interactive capsule, dock, command and search, quick controls, living desktop and an integrated app suite — while Windows stays the operating system underneath.

> **Status:** in development toward 1.0.0. See [ROADMAP.md](ROADMAP.md).

## For users
| Document | Purpose |
|---|---|
| [User guide](docs/USER-GUIDE.md) | Install, the desktop, apps, the Assistant, shortcuts, updates, troubleshooting |
| [Known limitations](docs/LIMITATIONS.md) | What V-OS 1.0 does not do yet |
| [Privacy](docs/PRIVACY.md) | What stays on your PC, and every service V-OS can contact |
| [Security](docs/SECURITY.md) | Security model, signed updates, reporting problems |
| [Third-party notices](THIRD-PARTY-NOTICES.md) | Open-source components and services |

Install by running `V-Launcher.exe`: per user, no administrator rights, signed releases, automatic rollback if an update does not start.

## For developers
| Document | Purpose |
|---|---|
| [CLAUDE.md](CLAUDE.md) | Permanent engineering instructions for AI-assisted development |
| [ARCHITECTURE.md](ARCHITECTURE.md) | System architecture: processes, layers, Windows integration, data, quality pipeline |
| [DECISIONS.md](DECISIONS.md) | Architecture decision records, including the WinUI 3 decision (ADR-001) |
| [ROADMAP.md](ROADMAP.md) | Development progress |
| [DEPENDENCIES.md](DEPENDENCIES.md) | Installed and required dependencies, and why each exists |
| [INTEGRATIONS.md](INTEGRATIONS.md) | Connected and recommended tools and integrations |
| [TESTING.md](TESTING.md) | Test strategy, performance budgets, measured results |
| [RELEASE_NOTES.md](RELEASE_NOTES.md) | Release history |
| [docs/brief/](docs/brief/) | The owner's original directives, verbatim |

## Technology
- **Language and runtime:** C# on .NET 10 LTS.
- **UI:** WinUI 3 (Windows App SDK 2.x) with Windows Composition.
- **Integration:** Win32, COM and WinRT.
- **Browser:** WebView2.
- **Storage:** SQLite.
- **Distribution:** self-contained, per-user, with no administrator rights needed.

## Building
```powershell
. .\build\dev-env.ps1
dotnet build V-OS.slnx -c Debug
dotnet test tests\VOS.Tests
.\build\build.ps1          # release: artifacts\release\<version>\
```
Release builds are signed with a key kept outside the repository (see CLAUDE.md, "Build and test").

## License
Copyright (c) 2026 V-OS Project. **All rights reserved.**
- The source is published to be read; using, copying, modifying or redistributing it needs written permission.
- Running a published release on your own computers is permitted.
- Details are in [LICENSE](LICENSE). Third-party components keep their own licenses ([THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).

## Development audit
Run `/v-os-checkup` in Claude Code inside this folder for a read-only audit of tools, SDKs, packages, integrations, GitHub, tests and security. It produces a prioritized report and changes nothing. You can add a focus: `/v-os-checkup security`, `/v-os-checkup perf`, `/v-os-checkup github` or `/v-os-checkup deps`.
