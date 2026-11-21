---
description: Read-only audit of the V-OS dev environment and project, with a prioritized setup report (installs nothing)
argument-hint: "[focus: all | tools | deps | security | github | perf | tests | integrations]"
---

# /v-os-checkup — V-OS environment and project audit

Focus: $ARGUMENTS (empty means a full audit).

Perform a fresh, **read-only** audit of the V-OS development environment and project, following CLAUDE.md.

## Hard rules for this command
- Do **not** do any of the following:
  - install, update, uninstall or purchase anything;
  - connect or authorize anything;
  - change system settings, accounts or git remotes.
- Do **not** modify repository files during the audit. Offer to record the results at the end, and write them only after the owner agrees.
- Use only:
  - read-only local commands;
  - anonymous GET or HEAD requests to official sources: api.nuget.org, builds.dotnet.microsoft.com release metadata, learn.microsoft.com, public api.github.com endpoints.

  Never send personal data (names, e-mail addresses, local paths) to external services.
- Windows here is Russian-localized. Parse tool output by structure (columns, separators), never by words.
- Start every PowerShell call with `. .\build\dev-env.ps1`. It provides the SDK path and opts out of .NET telemetry.

## Step 1 — Load context
Read CLAUDE.md, DEPENDENCIES.md, INTEGRATIONS.md, DECISIONS.md and ROADMAP.md. For the `perf` or `tests` focus, also read TESTING.md. Note their "Last audit" and "Last updated" dates so the report can say what changed since then.

## Step 2 — Environment inventory (read-only)
Collect with as few PowerShell calls as possible:
- **System:** OS edition, build and locale; CPU, RAM and GPU; monitors and DPI; free disk space on the repository drive.
- **.NET:**
  - `dotnet --info` (SDKs and runtimes).
  - Compare the `global.json` pin with the latest .NET 10 patch from `https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`.
  - Flag any newer LTS or STS channel from `releases-index.json`.
- **Native toolchain:**
  - Visual Studio or Build Tools (`vswhere`);
  - MSVC linker (Native AOT readiness);
  - Windows SDK and `signtool`.
- **Developer tools:**
  - `git` and Git Credential Manager;
  - `gh`, with `gh auth status` if present (report signed in yes/no and scopes only, never tokens);
  - `winget`, PowerShell 7, Windows Terminal;
  - `.NET` global tools (`dotnet tool list -g`);
  - WPR/WPA.
- **Windows features:** Windows Sandbox present, Developer Mode, `LongPathsEnabled`, hypervisor present.
- **Product runtimes:**
  - WebView2 Runtime version;
  - Windows App Runtime packages;
  - VC++ 2015–2022 Redistributable (`HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`).

## Step 3 — Project health
- **Git:** `git status --short`, current branch, remotes, date of the last commit.
- **Build:** `dotnet build V-OS.slnx -c Debug -nologo`. Time it and count warnings and errors. If the solution does not exist yet, say so and skip.
- **Tests:** `dotnet test` summary (passed, failed, skipped, duration) when test projects exist.
- **Packages:**
  - `dotnet list package --outdated` and `dotnet list package --vulnerable --include-transitive`.
  - Compare the pins in `Directory.Packages.props` with the latest stable versions (`https://api.nuget.org/v3-flatcontainer/<id>/index.json`).
  - For Windows App SDK, also check servicing status on learn.microsoft.com (release-channels page).
- **CI:** list `uses:` versions in `.github/workflows/*.yml` and compare them with the latest major tags of those actions.
- **Secrets hygiene:**
  - Run `git grep -nIE "(sk-ant-[A-Za-z0-9]|ghp_[A-Za-z0-9]{20}|AKIA[0-9A-Z]{16}|-----BEGIN (RSA |EC )?PRIVATE KEY-----)"`.
  - Confirm `.gitignore` covers `.keys/`, `*.pfx` and `*.pem`.
  - Report file:line only, never the secret.
- **Bottlenecks:** build and test duration, path-length risk (repository path length when long paths are off), slow restores, flaky tests, oversized artifacts.

## Step 4 — Integrations and new technology
- List the MCP servers and connectors in this session: available, connected, or awaiting authorization.
- When the registry tool is available, search the official connector registry for relevant ones: "microsoft learn", "github", "context7", "windows", "dotnet".
- Check for notable new stable releases relevant to V-OS:
  - Windows App SDK
  - WebView2 SDK
  - Win2D
  - .NET
  - CommunityToolkit.WinUI
  - Microsoft.Data.Sqlite

  Summarize only what matters to V-OS: security fixes, APIs we need, servicing end dates.

## Step 5 — Report (in Russian, concise)
1. **Summary:** 3–5 lines on overall health and what changed since the last audit.
2. **Recommendations:**
   - Group them as REQUIRED, HIGHLY RECOMMENDED, OPTIONAL and NOT NEEDED.
   - At most about 8 items in total, most valuable first.
   - For every item give the 10 facts:
     1. what
     2. why it matters for V-OS
     3. mandatory?
     4. free or paid
     5. account needed?
     6. installation?
     7. admin rights?
     8. external account or permissions
     9. official link or instructions
     10. what it unlocks
3. **Project findings:** build, test, dependency and security issues, each with file references and a proposed fix. Do not apply the fix.
4. **Next steps:** what you will do after approval.

Link only official sources: learn.microsoft.com, nuget.org, github.com (official organisations), cli.github.com, visualstudio.microsoft.com, and vendor sites.

## Step 6 — Ask, don't act
End by asking two things:
- which items the owner approves;
- whether to record this audit:
  - update the "Last audit" sections of DEPENDENCIES.md and INTEGRATIONS.md;
  - add build and test numbers to TESTING.md.

Make no changes until the owner answers.
