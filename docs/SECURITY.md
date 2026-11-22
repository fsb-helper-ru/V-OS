# V-OS security

V-OS is a desktop environment that runs as an ordinary app for your Windows account. This page describes what it can and cannot do, and how it protects your PC and data.

## Principles
- **No elevation.** V-OS installs per user in `%LOCALAPPDATA%\Programs\V-OS` and never asks for administrator rights. Installers started from the Store app may ask for them through the normal Windows UAC prompt.
- **Windows protections stay on.** V-OS never changes Microsoft Defender, UAC, Windows Firewall, Windows Update, Secure Boot, system files, services or unsupported registry areas.
- **Everything V-OS changes in Windows is reversible.** That is taskbar auto-hide, hidden desktop icons in immersive mode and the reserved screen space. V-OS records the original state before changing it. The state is restored on exit, by the watchdog if the shell stops unexpectedly, at the next start, with **Ctrl+Alt+Shift+F12**, from V-OS Launcher › Repair and recovery, and by uninstall.
- **No telemetry.** Nothing about your use of V-OS is sent anywhere (see [PRIVACY.md](PRIVACY.md)).

## Integrity of installs and updates
- **Signed releases.** Every release manifest (`vos-release.json`) is signed with ECDSA P-256. V-Launcher.exe and V-OS verify the signature with a public key compiled into them, and they reject unsigned or wrongly signed releases. The private key never enters the source repository.
- **Package integrity.** The package's size and SHA-256 hash must match the signed manifest before anything is unpacked. Package entries that would write outside the install folder are rejected.
- **HTTPS only** for online installs. Update feeds can also be local files.
- **Safe updates.**
  - The new version is unpacked beside the current one.
  - It is swapped in with directory moves and has to pass a start-up health check.
  - If the check fails, the previous version is restored automatically.
  - The previous version stays available for a manual rollback.
  - Your settings are saved in a snapshot before every update.
- **Code signing.** Authenticode signing of the executables is not available yet; it needs a code-signing certificate. Until then, Windows SmartScreen may warn when V-Launcher.exe is run for the first time.

## Data protection
- **API keys** (Claude, DeepL and others) are stored with Windows data protection (DPAPI). Only your Windows account on this PC can decrypt them.
- **Inter-process communication** between V-OS parts uses named pipes restricted to your user account (`PipeOptions.CurrentUserOnly`).
- **Diagnostics exports** remove every key, token, secret and password from the settings they contain.

## The Assistant
- With Claude, the model can act only through a fixed set of V-OS actions: open apps, timers, theme, volume, media, Focus, workspace, performance profile, settings page, website, web search, note, weather, and lock or power.
- It cannot run commands, scripts or programs of its choice, read files, or see the screen.
- Every action input is validated before it runs. Malformed input and actions from a declined or cut-off answer are never run.
- Only http and https web addresses can be opened.
- Sleep, restart, shut down and sign out always ask for your confirmation in V-OS itself.
- Conversations are not stored.
- The on-device mode uses a deterministic command parser; no model is involved.

## Connect (sharing on the local network)
- It runs only while you share from the Connect app.
- It listens on your private network address, never on public interfaces.
- Each session has a random token in every link.
- Uploaded file names are stripped of any path, so files land only in the chosen folder.

## Browser
The Browser uses Microsoft Edge WebView2, which keeps the Chromium sandbox and security updates through Windows. It runs in its own process, separate from the other V-OS apps. Private windows keep no history and clear their cookies and site data when they close.

## Games
The Gaming app reads game libraries (Steam, Epic Games, GOG) and starts games normally. It never injects into games, touches anti-cheat systems or changes game files.

## Reporting a security problem
Until the project has a public repository, report security issues privately to the project owner, and do not publish details until a fix is available. Include the V-OS version (Settings › About) and, if possible, a diagnostics export (V-OS Launcher › Diagnostics).
