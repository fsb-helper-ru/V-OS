# Known limitations (V-OS 1.0)

V-OS aims to be honest about what it does not do yet. This list is kept current with each release.

## Platform
- Windows 10 version 2004 (build 19041) or later, or Windows 11. Release builds are for x64 PCs; ARM64 Windows 11 can run them through emulation, which has not been tested.
- V-OS runs **on top of** Windows. It does not replace the Windows shell (Explorer), the sign-in screen, the lock screen, UAC prompts, Windows Settings or Task Manager. Immersive mode auto-hides the taskbar and hides desktop icons; it does not remove them.
- The top bar, V-Pill and dock appear on the **primary display** only.
- Notifications from other apps keep appearing as Windows notifications; the V-OS activity center shows V-OS notifications.
- Locking shows the Windows lock screen; V-OS only adds its own presentation before it.

## Interface
- The interface is in **English**. Dates and numbers follow your Windows regional format, so some screens mix English text with local date formats. Localization (Russian first) is planned after 1.0.
- Keyboard shortcuts that another app already uses fall back to alternatives (Settings › Keyboard shortcuts shows the active ones).
- Accessibility: controls have names for screen readers and keyboard paths. A full screen-reader and high-contrast audit has not been done yet.

## Apps
- **Music** plays local files. There is no equalizer, visualizer or crossfade yet. Streaming services (Spotify and others) are controlled through Windows media controls, not integrated as libraries.
- **Capture** takes screen, window and region screenshots with an editor (pen, highlighter, rectangle, arrow). Screen recording is not available yet.
- **Store** needs the Windows Package Manager (winget, part of App Installer). Some installers ask for administrator rights through Windows.
- **Gaming** finds Steam, Epic Games and GOG games and games you add. Xbox and Microsoft Store games are not detected. Play time is counted while the Gaming app is open.
- **Browser** has no extensions; it uses Microsoft Edge WebView2.
- **Weather** comes from Open-Meteo: free for personal, non-commercial use. Weather alerts are derived from the forecast, not official warnings.
- **Translate** uses online services after your consent; there is no offline translation.
- **Connect** works only between devices on the same local network. Windows may ask to allow it through the firewall the first time.
- **Alarms and flows** run while the V-OS shell is running. They do not wake the PC from sleep.

## Assistant
- **On this PC** understands V-OS commands (apps, timers, settings, weather, calculations, unit conversion), not open questions.
- **Claude** needs your own Anthropic API key, and usage is billed to your Anthropic account. If Claude declines a request, another Claude model may answer, and the reply says so.
- **Ollama** answers questions but cannot run V-OS actions.
- Conversations are not saved.

## Installation and updates
- Online installs and update checks need a published update feed for your build.
- Updates download the full package (about 100 MB); there are no delta updates yet.
- The executables are not Authenticode-signed yet, so SmartScreen may warn on first run.

## Measured, still to measure
The development measurements (Debug build) are in TESTING.md: shell idle CPU about 0.01 %, shell memory 104–156 MB. Release measurements (start-up time, memory, 10-minute idle CPU) are taken for the final build and published there.
