# Integrations

Covers what is connected, what is available, and what is recommended. Every recommendation uses the owner's format:
1. what
2. why
3. mandatory?
4. cost
5. account
6. install
7. admin
8. permissions
9. official instructions
10. gain

Nothing on this page is installed or connected without the owner's approval.

_Last audit: 2026-11-21 (initial audit). Re-run with `/v-os-checkup`._

## 0. Setup status (owner approvals of 2026-11-21)
| Item | Status |
|---|---|
| R1 Permanent folder | ✅ `C:\Dev\V-OS` (NVMe). All 25 files copied and SHA-256-verified before the temporary workspace was cleared. |
| R2 Windows App SDK 2.7.0 + Win2D 1.4.0 | ✅ restored and verified by spike S1 |
| H1 GitHub | 🔶 prepared: `gh` 2.105.0 installed (portable, verified); local git repository; CI workflows written. **Not signed in, no remote repository.** Both wait for the owner's explicit go-ahead. |
| H2 Microsoft Learn MCP | 🔶 connector card offered in the Claude app. Connecting is the owner's click. |
| H3 .NET diagnostics tools | ✅ installed, user-level |
| H4 Windows Sandbox | ⏸ deferred by the owner (see below) |
| O1 Visual Studio Build Tools | ⏸ deferred by the owner (see below) |

### When deferred tools become necessary
**Status 2026-11-22:**
- The install → update → forced failure → rollback → uninstall cycle has passed on real artifacts. It ran in a redirected folder (`VOS_INSTALL_ROOT`), so nothing was registered with Windows.
- What is still unproven needs either Windows Sandbox or the owner's go-ahead for a real per-user install on this PC:
  - registration with Windows: the Apps entry, Start-menu shortcuts and the sign-in entry;
  - a truly clean machine without the .NET SDK or developer tools.
- The VS C++ build tools are now the path to the cold-start budget: app windows open in 1.05–1.92 s cold against a 1 s budget, and Native AOT is the lever (details below).

- **Windows Sandbox (H4).** Needed at the start of **Phase 6 (end-to-end QA)**. Until then, end-to-end testing waits for the owner's decision.
  - First, the install → update → forced failure → rollback → uninstall cycle must be proven on a *clean* Windows. Your PC already has every prerequisite, so it cannot prove a clean-machine install.
  - Second, immersive mode hides the taskbar and desktop icons and hooks the Win key. Its recovery paths should be tested away from your real desktop.
  - Alternatives: run those tests on this PC (reversible, but visibly intrusive), or on another PC or a VM you provide.
- **Visual Studio Build Tools (O1).** No longer needed for the VC++ runtime: spike S1 showed there is no dependency. It is needed for three things:
  1. **Native AOT** compilation of the shell and the download bootstrapper. This is a Phase 6 optimisation, expected to cut startup time and memory further. Without it V-OS ships with ReadyToRun, which is what the spike measured.
  2. **`signtool`**, once you have a code-signing certificate (O2).
  3. **Native crash-dump analysis**, if a native crash appears that the managed logs cannot explain.

## 1. Current state

### Available in Claude Code sessions for this project (no action needed)
| Integration | Status | Use for V-OS |
|---|---|---|
| Built-in browser pane | available | reading docs; testing local pages, e.g. the browser's new-tab page |
| Computer use (desktop control) | available; asks per app | final visual verification of shell surfaces |
| Microsoft Learn docs via the `web-fetch` agent | available | official docs (used for the Windows App SDK 2.x facts in ADR-001) |
| Scheduled tasks | available | optional: periodic `/v-os-checkup` |
| Project slash command `/v-os-checkup` | **created** (`.claude/commands/v-os-checkup.md`) | read-only environment and project audit |

### Installed but not authorized (need the owner's sign-in via `/mcp`)
| Integration | Relevance |
|---|---|
| `context7` plugin | optional (see O3) |
| GitHub plugin (`engineering:github`) | optional (see O4) |
| Figma, Slack, Linear, Notion, Asana, Atlassian, Datadog, PagerDuty, Intercom, SpriteCook | not needed |

### Not connected; found in the official connector registry
| Integration | Relevance |
|---|---|
| **Microsoft Learn MCP** (`https://learn.microsoft.com/api/mcp`) | highly recommended (H2) |
| Context7 (`https://mcp.context7.com/mcp`) | optional (O3) |

### GitHub connectivity
| Item | Status |
|---|---|
| Git | 2.56.1. This repository commits as `fsb-helper-ru` with the GitHub noreply address (repository-local config); the history was rewritten to that identity before publication (2026-11-22). |
| Git Credential Manager | 2.9.1, configured as the system helper. Browser sign-in happens on first push. |
| `api.github.com` | reachable |
| GitHub CLI (`gh`) | 2.105.0, installed and signed in by the owner (account `fsb-helper-ru`, scopes repo, workflow, gist, read:org). Not on the PATH of shells started before the install: use `C:Program FilesGitHub CLIgh.exe`. |
| Remote repository | `https://github.com/fsb-helper-ru/V-OS` (public; created 2026-11-22 with the owner's approval). Site: GitHub Pages from `/docs`. Release feed: `releases/latest/download/vos-release.json`. Pushes and releases only with the owner's explicit approval. |
| GitHub MCP connector (plugin) | Not usable: GitHub's remote MCP server does not support OAuth sign-in from Claude Desktop (needs a registered GitHub App). The CLI covers everything, including release uploads. |

---

## 2. Recommendations

### REQUIRED

#### R1. A permanent project folder with a short path
1. **What:** move the V-OS repository from this session's temporary folder to a permanent folder with a short path, for example `D:\Dev\V-OS`.
2. **Why:** the current folder is **deleted together with this session**. That would delete the code, `CLAUDE.md` and `/v-os-checkup`. A short path also avoids Windows' 260-character limit; long paths are disabled on this PC.
3. **Mandatory:** yes.
4. **Cost:** free.
5. **Account:** no.
6. **Install:** no.
7. **Admin:** no.
8. **Permissions:** Claude gets read/write access to that folder only.
9. **How:** name the folder, or pick it in the folder dialog. Claude moves the session there.
10. **Gain:** permanent repository. Future sessions load `CLAUDE.md` and `/v-os-checkup` automatically.

#### R2. Windows App SDK 2.7.0 + Win2D 1.4.0 (NuGet packages)
1. **What:**
   - `Microsoft.WindowsAppSDK` 2.7.0 (ideally only the needed component packages);
   - `Microsoft.Graphics.Win2D` 1.4.0;
   - possibly `CommunityToolkit.WinUI.Controls.SettingsControls` and `.Sizers` 8.2.
2. **Why:** this is the UI foundation chosen in ADR-001: WinUI 3, Composition, Mica/Acrylic, `SystemBackdropElement`, and the WebView2 control. Win2D adds GPU drawing and effects (ambient scenes, visualizer, capture editor).
3. **Mandatory:** yes, for the approved architecture.
4. **Cost:** free (Microsoft and MIT licenses).
5. **Account:** no.
6. **Install:** NuGet download into the user's package cache (`%USERPROFILE%\.nuget\packages`). It is about 280 MB with the full metapackage; component packages should be smaller. Nothing is installed into Windows.
7. **Admin:** no.
8. **Permissions:** none.
9. **Links:**
   - https://learn.microsoft.com/windows/apps/windows-app-sdk/
   - https://www.nuget.org/packages/Microsoft.WindowsAppSDK
   - https://github.com/microsoft/Win2D
10. **Gain:** the modern GPU-composited UI stack the owner asked for.

### HIGHLY RECOMMENDED

#### H1. GitHub: a repository, the GitHub CLI, and sign-in
1. **What:**
   - a GitHub repository for V-OS (private recommended);
   - GitHub CLI `gh`;
   - `gh auth login`.
2. **Why:**
   - CI on every push (GitHub Actions build and test on Windows runners);
   - GitHub Releases as the V-OS update feed (ADR-006);
   - Dependabot security alerts;
   - off-machine backup.
3. **Mandatory:** no, but required before releases and auto-updates can work against a real feed.
4. **Cost:**
   - Public repositories: free, with unlimited Actions minutes.
   - Private repositories on GitHub Free: 2,000 Actions minutes per month. Windows runners count at a 2× rate.
5. **Account:** a GitHub account is required.
6. **Install:**
   - `winget install --id GitHub.cli` (version 2.105.0, MSI) — needs admin;
   - or the portable zip from the official site — no admin.
7. **Admin:** only for the MSI variant.
8. **Permissions:** `gh auth login` runs in your browser. Grant the `repo` and `workflow` scopes only. You sign in yourself; Claude never sees the token.
9. **Links:**
   - https://cli.github.com/
   - https://cli.github.com/manual/gh_auth_login
10. **Gain:** Claude can create the repo, push, run and watch CI, publish signed releases, and connect the updater to real releases.

#### H2. Microsoft Learn MCP server (official Microsoft documentation)
1. **What:** Microsoft's documentation MCP server (search, fetch, code samples).
2. **Why:** the Windows App SDK 2.x, WebView2 and .NET 10 APIs used by V-OS are newer than Claude's training data. Live official docs prevent wrong API use and guesswork.
3. **Mandatory:** no.
4. **Cost:** free.
5. **Account:** no.
6. **Install:** no. It is a remote HTTP connector.
7. **Admin:** no.
8. **Permissions:** read-only public documentation. No access to your data.
9. **How:**
   - In the Claude app, add the "Microsoft Learn" connector.
   - Or in Claude Code: `claude mcp add --transport http microsoft-learn https://learn.microsoft.com/api/mcp`
10. **Gain:** faster and more correct WinUI, Win32 and WinRT work; verified answers instead of assumptions.

#### H3. .NET diagnostics tools
1. **What:** `dotnet-counters`, `dotnet-trace`, `dotnet-gcdump`.
2. **Why:** the brief demands *measured* startup, idle CPU, memory and leak checks. These tools give exact numbers. Windows Performance Recorder (`wpr.exe`) is already present for ETW traces.
3. **Mandatory:** no.
4. **Cost:** free (MIT).
5. **Account:** no.
6. **Install:** `dotnet tool install -g dotnet-counters` (and the same for `dotnet-trace` and `dotnet-gcdump`). They go into `%USERPROFILE%\.dotnet\tools`, a few MB each, from nuget.org.
7. **Admin:** no.
8. **Permissions:** none.
9. **Link:** https://learn.microsoft.com/dotnet/core/diagnostics/
10. **Gain:** reproducible performance numbers for TESTING.md; GC and allocation profiling; memory-leak detection.

#### H4. Windows Sandbox
1. **What:** the built-in Windows Sandbox feature (Windows 11 Pro).
2. **Why:** it provides a clean, disposable Windows for testing the installer, updates, rollback, uninstall and **immersive mode**, which hides the taskbar. Your real desktop is never touched. It also proves clean-machine prerequisites (VC++ runtime, WebView2).
3. **Mandatory:** no. It is strongly advised before testing immersive mode and install flows.
4. **Cost:** free.
5. **Account:** no.
6. **Install:** enables an optional Windows feature (`Containers-DisposableClientVM`). The hypervisor is already active on this PC.
7. **Admin:** yes, plus **one reboot**.
8. **Permissions:** none.
9. **How:**
   - Settings → System → Optional features → More Windows features → "Windows Sandbox";
   - or, in an admin PowerShell: `Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All`
   - Docs: https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/
10. **Gain:** automated, repeatable end-to-end install tests through a `.wsb` script with mapped folders, at zero risk to your system.

### OPTIONAL

#### O1. Visual Studio Build Tools (MSVC + Windows SDK)
1. **What:** Build Tools for Visual Studio with the "Desktop development with C++" workload and the Windows 11 SDK.
2. **Why:**
   - Native AOT, for a smaller and faster Shell and bootstrapper;
   - `signtool` for code signing;
   - official app-local VC++ runtime files, so V-OS can ship without the separate redistributable;
   - crash-dump analysis tools.
3. **Mandatory:** no for now. Highly recommended before the 1.0 release.
4. **Cost:** free for individuals (Visual Studio Community license terms).
5. **Account:** no.
6. **Install:** yes, about 6–8 GB.
7. **Admin:** yes.
8. **Permissions:** none.
9. **Links:**
   - https://visualstudio.microsoft.com/downloads/ ("Tools for Visual Studio" → Build Tools)
   - https://learn.microsoft.com/dotnet/core/deploying/native-aot/#prerequisites
10. **Gain:** AOT builds, signing tooling, self-contained VC++ runtime, debugging tools.

#### O2. Code-signing certificate
1. **What:** an Authenticode code-signing identity, either a cloud signing service (Microsoft's Trusted/Artifact Signing service) or an OV certificate from a public certificate authority.
2. **Why:** without it, Windows SmartScreen shows "Unknown publisher" for `V-Launcher.exe`. V-OS's own ECDSA update signatures protect updates either way (ADR-006).
3. **Mandatory:** no for development. Needed before public distribution.
4. **Cost:** **paid**. Prices vary by provider; check current pricing on the provider's site.
5. **Account:** yes, with identity validation. Eligibility depends on country and entity type.
6. **Install:** no, or a hardware token for OV certificates.
7. **Admin:** no.
8. **Permissions:** the signing credentials stay with you or in GitHub encrypted secrets.
9. **Link:** https://learn.microsoft.com/azure/trusted-signing/
10. **Gain:** trusted publisher name, fewer warnings, tamper evidence on every binary.

#### O3. Context7 MCP
1. **What:** an up-to-date documentation index for third-party libraries (Win2D, CommunityToolkit, SQLite).
2. **Why:** it complements Microsoft Learn.
3. **Mandatory:** no.
4. **Cost:** free tier.
5. **Account:** it asks you to sign in.
6. **Install:** none; the plugin is already present.
7. **Admin:** no.
8. **Permissions:** authorize the `context7` plugin via `/mcp`.
9. **Link:** https://context7.com
10. **Gain:** library docs for non-Microsoft dependencies.

#### O4. GitHub MCP plugin
1. **What:** Claude's GitHub connector (`engineering:github` plugin).
2. **Why:** browse issues and PRs and review code inside Claude. Largely overlaps with `gh`.
3. **Mandatory:** no.
4. **Cost:** free.
5. **Account:** yes, GitHub.
6. **Install:** no.
7. **Admin:** no.
8. **Permissions:** an OAuth grant to your GitHub account; limit it to the V-OS repository.
9. **How:** `/mcp` → authorize.
10. **Gain:** convenience only if `gh` is already set up.

### NOT NEEDED (would add complexity without value)
- **Full Visual Studio IDE.** Claude works through the CLI. Install it only if you want to debug interactively yourself.
- **VS Code, Node.js, Python, PowerShell 7.** Scripts target Windows PowerShell 5.1, and the CI runners already have `pwsh`.
- **WiX, Inno Setup, MSIX tooling.** `V-Launcher.exe` is V-OS's own installer (ADR-005).
- **Enabling Windows long paths** (an admin registry change). It is unnecessary once the repository lives at a short path, and builds already redirect output.
- **Figma, Slack, Linear, Notion, Asana, Atlassian, Datadog, PagerDuty, Intercom, SpriteCook connectors.** They bring no benefit to this project.
- **Other UI frameworks** (Avalonia, Uno, Electron, Flutter). Evaluated in ADR-001.

## 3. How to use `/v-os-checkup`
- Type `/v-os-checkup` in a Claude Code session opened in the V-OS folder.
- To narrow the scope, add an optional focus: `/v-os-checkup security`, `/v-os-checkup github`, `/v-os-checkup perf`, `/v-os-checkup deps`.

The command is **read-only**. It inspects tools, SDKs, packages, connectors, GitHub, tests and security, then prints a prioritized report (REQUIRED, HIGHLY RECOMMENDED, OPTIONAL, NOT NEEDED) with official links. It ends by asking whether to record the results in DEPENDENCIES.md and INTEGRATIONS.md. It never installs, connects, purchases or changes anything.

The command definition lives in `.claude/commands/v-os-checkup.md` and is versioned with the repository.
