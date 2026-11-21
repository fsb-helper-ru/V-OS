# V-OS Architecture

> **Status:** target architecture per ADR-001 (WinUI 3 + Composition). Implementation is in the foundations phase. See ROADMAP.md for what exists today.
> Decisions and their reasoning live in DECISIONS.md. This file describes *what* the system is and *how* the parts fit.

## 1. What V-OS is
V-OS is a desktop environment and app ecosystem layered on Windows 10/11.

**What it replaces:** the everyday surfaces you touch.
- Top bar
- Interactive capsule (V-Pill)
- Dock
- Command and search
- Quick controls
- Activity and notifications
- Living desktop

It also ships an integrated suite of apps.

**What stays Windows:**
- the kernel, drivers and security (Defender, UAC, Firewall, Secure Boot, Windows Hello sign-in);
- app compatibility;
- Explorer, which keeps running underneath and is always recoverable.

## 2. Processes
```
 user session
 ┌──────────────────────────────────────────────────────────────────────────┐
 │ VOS.Shell.exe  (WinUI 3 · always running · no elevation)                │
 │   surfaces : top bar · V-Pill · dock · desktop/ambient · widgets ·      │
 │              command & search · control center · activity center ·     │
 │              switcher · layout zones · onboarding · lock presentation   │
 │   services : notifications · live activities · media sessions ·         │
 │              weather · clipboard · timers · workspaces · focus ·        │
 │              flows · window tracker · adaptive-quality governor          │
 │   IPC      : \\.\pipe\VOS.Shell.<user-SID>   (CurrentUserOnly)          │
 │                   ▲ JSON lines: notify · activity · open · command      │
 │ VOS.Apps.exe   (WinUI 3 app host · on demand)                           │
 │   Files · Music · Weather · Settings · Monitor · Notes · Time ·         │
 │   Capture · Store · Gaming · Assistant · Translate · Connect · Flow     │
 │ VOS.Apps.exe --isolated browser   (+ WebView2 renderer processes)       │
 │ VOS.Launcher.exe (WinUI 3) setup · update · repair · rollback ·         │
 │   recovery · diagnostics · uninstall · immersive-mode watchdog          │
 └──────────────────────────────────────────────────────────────────────────┘
 V-Launcher.exe — single-file download bootstrapper: verify → unpack → start VOS.Launcher.exe --setup
```

## 3. Layers (projects)
| Project | Kind | Responsibility | May reference |
|---|---|---|---|
| `VOS.Core` | class library, no UI | settings, storage (SQLite), logging, crash reports, IPC, models, providers (weather, translate, assistant), update verification, search parsing | — |
| `VOS.Platform` | class library, no UI framework | all Win32/COM/WinRT access: windows, AppBar, hooks, hotkeys, shell items, icons, file operations, audio, radios, power, display, capture, metrics, Windows Search | Core |
| `VOS.UI` | WinUI 3 class library | design system "Instrument": tokens, themes, typography, icons, controls, composition helpers (glass, light, springs), window helpers (transparent backdrop, non-activating, click-through, AppBar binding), quality governor bindings | Core, Platform |
| `VOS.Shell` | WinUI 3 exe | shell surfaces and services | Core, Platform, UI |
| `VOS.Apps` | WinUI 3 exe | app modules (one folder per module) | Core, Platform, UI |
| `VOS.Launcher` | WinUI 3 exe | installer and maintenance UI, watchdog | Core, Platform, UI |
| `VOS.Bootstrap` | .NET single-file, no UI framework | `V-Launcher.exe` download bootstrapper | Core |
| `VOS.Tests` | xUnit + FlaUI | unit, integration and UI automation tests | all |

Lower layers never reference higher ones. Modules talk to each other through Core services and IPC, never by referencing another module's types.

## 4. Shell surfaces: how they are built
| Surface | Window | Rendering | Notes |
|---|---|---|---|
| Top bar | borderless AppWindow, AppBar-registered (reserves space), topmost, non-activating | `SystemBackdropElement` acrylic strip | one per monitor |
| V-Pill (capsule) | transparent topmost window over the bar centre | ink capsule; composition springs animate size and corner radius on the compositor thread | window region follows the shape → clicks pass through outside it |
| Dock | transparent topmost window | capsule glass via `SystemBackdropElement`; icon magnification by composition scale animations; implicit reorder animations | optional AppBar reservation; auto-hide with an edge trigger |
| Panels (command, control, activity, clipboard, switcher) | transient topmost windows | acrylic plus content transitions | dismissed on deactivate or Esc |
| Desktop (immersive mode) | full-screen window kept at the bottom of the z-order | composition gradients (zero CPU) + Win2D particle layer (own thread, adaptive FPS) | paused when covered, locked, or fullscreen |
| Widgets (companion mode) | small bottom-most windows | XAML + composition | — |

## 5. Platform integration map
| Capability | Windows API |
|---|---|
| Reserve screen space | `SHAppBarMessage` (AppBar) |
| Running windows and apps | `EnumWindows`; `SetWinEventHook` (create/destroy/show/hide/foreground/minimize, event-driven, no polling); `DWMWA_CLOAKED`; window AUMID via `SHGetPropertyStoreForWindow` |
| App catalog and icons | Shell Apps folder (`FOLDERID_AppsFolder`, `IShellItem`), `IShellItemImageFactory` |
| Launch | `ShellExecuteEx`; `shell:AppsFolder\<AUMID>` for packaged apps |
| Window previews | `DwmRegisterThumbnail` |
| Layout zones and snapping | `EVENT_SYSTEM_MOVESIZESTART/END` hooks, `SetWindowPlacement` |
| Hotkeys | `RegisterHotKey`; `WH_KEYBOARD_LL` only for the Win key in immersive mode, on a dedicated thread |
| Media capsule | `GlobalSystemMediaTransportControlsSessionManager` |
| Volume, devices, per-app volume | Core Audio: `IMMDeviceEnumerator`, `IAudioEndpointVolume`, `IAudioSessionManager2` |
| Music playback and equalizer | WinRT `AudioGraph`; system media controls via `SystemMediaTransportControlsInterop` |
| Music-reactive visuals | WASAPI loopback (`AUDCLNT_STREAMFLAGS_LOOPBACK`), never the microphone |
| Wi-Fi and Bluetooth | `Windows.Devices.Radios`; Native Wifi (`wlanapi`) |
| Brightness | WMI `WmiMonitorBrightnessMethods` (internal panels); DDC/CI via `dxva2` (external monitors) |
| Power and battery | `GetSystemPowerStatus`, `PowerSetActiveScheme`, `Windows.System.Power` |
| Clipboard history | `AddClipboardFormatListener`. Honours `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory`. |
| File operations | `IFileOperation`: preserves metadata and ACLs, uses the Recycle Bin, supports undo |
| Native context menu | `IContextMenu` / `IContextMenu3` |
| File search | Windows Search (OLE DB `Search.CollatorDSO`), with an enumeration fallback |
| Previews and thumbnails | `IShellItemImageFactory`, MediaPlayerElement, WebView2 (PDF) |
| Archives | `System.IO.Compression` (zip); Windows' built-in libarchive `tar.exe` (7z, rar, tar.*) |
| Screenshots and recording | `Windows.Graphics.Capture` + Win2D; Media Foundation (`MediaTranscoder` / `MediaStreamSource`) H.264/AAC |
| Location (consent-gated, one-shot) | `Windows.Devices.Geolocation` |
| Browser | WebView2 (Evergreen) |
| Immersive mode | taskbar auto-hide (`ABM_SETSTATE`) + `ShowWindow`; desktop icons toggle. All reversible and restored by the watchdog. |
| Lock | `LockWorkStation`. Windows secure sign-in is never altered. |
| Store | `winget` CLI |
| Game library | Steam (`libraryfolders.vdf`, `appmanifest_*.acf`), Epic (manifests), GOG (registry), manual entries. No injection, ever. |
| Metrics | PDH using English counter names (locale-safe), DXGI adapter info, `GlobalMemoryStatusEx` |
| Startup and uninstall registration | `HKCU\...\Run`, `HKCU\...\Uninstall\V-OS` |

## 6. Data
```
%LOCALAPPDATA%\Programs\V-OS\     binaries (per-user, no admin)
    app\  app.previous\  app.staging\  VOS.Launcher.exe (maintenance)
%LOCALAPPDATA%\V-OS\              user data (never touched by updates)
    settings.json                 preferences (atomic writes + cross-process mutex)
    data\*.db                     SQLite per module (WAL)
    cache\                        weather, artwork, icons, thumbnails
    logs\  crashes\               local structured logs (JSON lines) and crash reports
    snapshots\                    settings restore points (V-Snapshots)
    state\                        immersive-mode restore records, session state
```

## 7. Adaptive quality pipeline
Inputs, all read once and then re-read only on change events:
- hardware tier (CPU threads, RAM, GPU memory and feature level via DXGI);
- `CompositionCapabilities.AreEffectsFast`;
- power source and battery saver;
- remote session;
- Windows animation and transparency settings, high contrast;
- fullscreen app state (`SHQueryUserNotificationState`);
- the user's profile and per-effect overrides.

These resolve to an `EffectiveQuality` record (`VOS.Core.Perf`). The record drives:
- backdrop materials;
- animation scale and spring use;
- ambient scene complexity and FPS;
- particles, lights and shadows;
- live thumbnails;
- the audio visualizer;
- background refresh intervals.

Profiles: Adaptive · Quality · Balanced · Performance · Minimal. Each changes real behaviour; none is a label only.

## 8. Security model
- No elevation at runtime. Admin is requested only by explicit, consented setup steps (for example the VC++ runtime install).
- IPC is limited to the current user. Settings secrets use DPAPI.
- Network access only to documented endpoints and only after consent where personal data could be involved (location, cloud AI, online translation).
- Updates: an ECDSA-signed manifest plus SHA-256 package hashes, with automatic rollback.
- Never touched: Defender, UAC, Firewall, Windows Update, Secure Boot, system files, unsupported registry areas.

## 9. Compatibility
- **Windows 11 22H2+ (x64):** primary, full effects.
- **Windows 10 22H2:** best effort with degraded materials.
- **ARM64:** buildable.
- Per-monitor DPI v2 and multiple monitors throughout.
- **Runtime prerequisites:** WebView2 Runtime (preinstalled on Windows 11), VC++ 2015–2022 Redistributable. Both are checked by the installer.
