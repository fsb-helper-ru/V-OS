# V-OS user guide

V-OS is a desktop environment that runs on top of Windows. It adds a top bar, the V-Pill, a dock, a command bar and its own apps, and it keeps Windows and your Windows apps fully available.

## Install
1. Run **V-Launcher.exe**. It installs V-OS for your account in `%LOCALAPPDATA%\Programs\V-OS`. No administrator rights are needed.
2. V-Launcher checks the release signature and the package before anything is installed. **Show details** lists the source and the checksum.
3. When installation finishes, the **V-OS Launcher** opens. Choose **Start V-OS**.

The first time V-OS starts, it asks how to run:
- **Companion**: V-OS sits alongside the Windows taskbar and desktop.
- **Immersive**: the Windows taskbar auto-hides and the desktop icons are hidden, so V-OS owns the screen.

It also asks whether V-OS should start when you sign in. You can change both later in Settings › General.

To put Windows back exactly as it was at any moment, press **Ctrl+Alt+Shift+F12**.

## The desktop
| Part | What it does |
|---|---|
| **Top bar** | V menu, workspaces, the active app, clock and date, weather, quick status. Click the clock for the activity center, the status icons for the control center. |
| **V-Pill** | The capsule in the middle of the top bar. It shows what is happening now (music, timers, recordings, transfers) and expands for controls. |
| **Dock** | Pinned and running apps at the bottom. Drag to reorder; right-click for options. |
| **Command bar** (Alt+Space) | Type to open apps, find files and settings, calculate (`12*7`), convert (`5 km in miles`), or give commands: `timer 10 min`, `dark mode`, `volume 30`, `workspace work`, `focus on`, `weather in Paris`, `lock`. |
| **Control center** (Win+Alt+C) | Wi-Fi, Bluetooth, Focus, dark theme, performance profile, volume and output device, brightness (where the display supports it). |
| **Activity center** (Win+Alt+N) | V-OS notifications and today's timeline. |
| **Clipboard** (Win+Alt+V) | Clipboard history with pinning. Content from password managers is never saved. |
| **Drop shelf** (Win+Alt+D) | A shelf for files you are moving between places. |
| **Switcher** (Alt+Q) | Windows with live previews. |
| **Layout zones** (Win+Alt+Z) | Snap the active window into a layout. |

**Workspaces** switch your whole context at once: theme, accent, wallpaper, dock, performance profile, Focus, and the apps you had open. The built-in workspaces are Personal, Work, Study, Gaming, Media and Minimal; edit them in Settings › Workspaces.

**Performance profiles** change what V-OS draws, not just a label:
- **Adaptive** picks a profile from your hardware, power source and full-screen apps.
- **Quality** keeps every effect on.
- **Balanced**.
- **Performance** reduces blur, shadows and animations.
- **Minimal** is the lightest.

## Apps
| App | Highlights |
|---|---|
| Files | Browse and search, previews, copy, move and rename, Recycle Bin, ZIP compress and extract, properties |
| Browser | Vertical tabs, bookmarks, history, downloads, private windows (Microsoft Edge WebView2) |
| Music | Your local library, playlists, now playing in the V-Pill |
| Weather | Current conditions, 48-hour and 14-day forecast, air quality, forecast-based alerts (Open-Meteo) |
| Notes | Markdown and rich-text notes with search |
| Time | World clocks, alarms, timers, stopwatch, calendar (ICS import) |
| Monitor | CPU, memory, disk, network, GPU and processes |
| Capture | Screen, window and region screenshots (Ctrl+Alt+P, Ctrl+Alt+W, Ctrl+Alt+S) with an editor |
| Store | Search, install and update apps with winget |
| Gaming | Your Steam, Epic Games and GOG library, the Gaming workspace and play time |
| Translate | Text and clipboard translation (Win+Alt+T) after you allow online translation |
| Connect | Share files with a phone or computer on your network through a QR code |
| Flow | Automations: "at 22:00 turn on Focus", "when Steam opens, switch to the Gaming workspace" |
| Assistant | Help and actions, offline or with Claude or Ollama (below) |
| Settings | Everything above, privacy, snapshots and updates |

## Assistant
Open it from the dock or the command bar. Choose a provider in Settings › Apps › Assistant:
- **On this PC** (default): works offline and runs V-OS commands such as `open files`, `timer 10 min`, `weather in London`, `12 * 7`. Nothing leaves your PC except weather lookups.
- **Claude**: answers questions and can act for you (open apps, start timers, change the theme and more), using your own Anthropic API key. A consent screen explains what is sent before the first message. Each action appears in the conversation with its result. Sleep, restart, shut down and sign out always ask first.
- **Ollama**: answers with a model running on your own Ollama server.

Press **Enter** to send and **Shift+Enter** for a new line. **Esc** stops an answer, and **New chat** starts over. Conversations are not saved.

## Keyboard shortcuts
| Shortcut | Action |
|---|---|
| Alt+Space | Command bar |
| Win+Alt+V | Clipboard history |
| Alt+Q | Switcher |
| Win+Alt+C | Control center |
| Win+Alt+N | Activity center |
| Ctrl+Alt+S / Ctrl+Alt+P / Ctrl+Alt+W | Capture region / screen / window |
| Win+Alt+Z | Layout zones |
| Win+Alt+D | Drop shelf |
| Win+Alt+T | Translate the clipboard |
| Win+Alt+L | Lock |
| Ctrl+Alt+Shift+F12 | Restore the Windows desktop |

If another app already uses a shortcut, V-OS uses an alternative; Settings › Keyboard shortcuts shows and changes them.

## Updates, repair and uninstall
Open **V-OS Launcher** from the Start menu or Settings › Updates.
- **Updates.** V-OS checks once a day if allowed (Settings › Updates and Settings › Privacy) and tells you when a version is available. Installing verifies the signature and checksum, saves a snapshot of your settings, closes V-OS, installs, checks that it starts, and opens again. If the new version does not start, V-OS returns to the previous one by itself.
- **Roll back.** Updates › Roll back returns to the version you had before.
- **Repair** replaces the program files with a verified copy and keeps your settings.
- **Recovery** restores the Windows desktop, restores a snapshot, or clears cached data.
- **Diagnostics** shows system information and exports a zip file for support, with keys removed.
- **Uninstall** (also in Windows Settings › Apps) closes V-OS, restores the Windows desktop, removes the program and lets you keep or delete your data.

## Where your data is
Settings and data: `%LOCALAPPDATA%\V-OS`. The program: `%LOCALAPPDATA%\Programs\V-OS`. Details are in [PRIVACY.md](PRIVACY.md).

## Troubleshooting
| Problem | Try |
|---|---|
| The taskbar or desktop icons are missing after V-OS closed | Press Ctrl+Alt+Shift+F12, or V-OS Launcher › Repair and recovery › Restore the Windows desktop. Running `VOS.exe --recover` does the same. |
| V-OS feels slow on this PC | Choose the Performance or Minimal profile in the control center. |
| A shortcut does nothing | Another app has it; see Settings › Keyboard shortcuts. |
| Weather or translation shows nothing | Check that it is allowed in Settings › Privacy and that you are online. |
| Something else is wrong | Use V-OS Launcher › Repair. Export diagnostics if you report the problem. |

Known limitations are listed in [LIMITATIONS.md](LIMITATIONS.md).
