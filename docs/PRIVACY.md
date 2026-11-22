# V-OS privacy

**In short:** V-OS runs on your PC and keeps your data on your PC. It has no account, no telemetry, no advertising and no analytics (ADR-014). It connects to an online service only for a feature you use. Where personal content is involved, it asks first.

## What V-OS stores on your PC
Everything is in your user profile, readable only by your Windows account:

| Location | Contents |
|---|---|
| `%LOCALAPPDATA%\V-OS\settings.json` | All settings. API keys are encrypted with Windows data protection (DPAPI) for your account. |
| `%LOCALAPPDATA%\V-OS\data\` | Notes, clipboard history, calendar, alarms, world clocks, timers, flows, notification history, music library index, browser history and bookmarks (`browser.db`) and the browser profile (`browser\`: cookies, site data), game play time |
| `%LOCALAPPDATA%\V-OS\state\` | Window positions, session state, the record of desktop changes that V-OS must undo on exit |
| `%LOCALAPPDATA%\V-OS\cache\` | App icons, thumbnails, the last weather forecast. Safe to delete (V-OS Launcher › Repair and recovery). |
| `%LOCALAPPDATA%\V-OS\snapshots\` | Snapshots of your setup that you saved, plus one before every update |
| `%LOCALAPPDATA%\V-OS\logs\`, `crashes\` | Technical logs and crash reports: events, errors and sometimes file or folder paths. They contain no note, message or file content. |
| `%LOCALAPPDATA%\Programs\V-OS\` | The program and `install.json` (version information) |

- Clipboard history respects apps that mark content as private (password managers) and can be turned off or cleared at any time.
- Private browser windows keep no history, and their cookies and site data are cleared when they close.
- Assistant conversations are kept in memory only and are gone when the window closes.

## When V-OS connects to the internet

| Feature | Service | What is sent | When |
|---|---|---|---|
| Weather (Weather app, widgets, top bar) | Open-Meteo: `api.open-meteo.com`, `geocoding-api.open-meteo.com`, `air-quality-api.open-meteo.com` | Coordinates of the places you added; the place names you search for | Only while weather is on (Settings › Privacy). No account, no identifier. |
| Online translation (Translate app, clipboard translation) | Your choice: MyMemory (`api.mymemory.translated.net`), DeepL (`api.deepl.com` / `api-free.deepl.com`, your key) or LibreTranslate (`libretranslate.com` or your server) | The text you translate | Only after you allow online translation |
| Assistant with Claude | Anthropic (`api.anthropic.com`) with your own API key | What you type in the Assistant, and the results of actions it runs for you (for example the weather it looked up) | Only after the consent screen in the Assistant; never in the background |
| Assistant with Ollama | The Ollama server you set (default `localhost`, which stays on your PC) | What you type in the Assistant | A server on another machine needs your consent first |
| Updates | The update feed of your V-OS build (shown in Settings › Updates and V-OS Launcher › Updates) | A normal web request for the release information and the package | When you check, or once a day if automatic checks are on. No identifier is sent. |
| Browser | The websites you visit | What any browser sends | When you browse. The browser engine is Microsoft Edge WebView2, which follows its own Microsoft privacy settings. |
| Web search from the command bar or the Assistant | Your search engine (DuckDuckGo by default; Google, Bing, Brave, Startpage or Ecosia) | Your search, in the browser | When you search |
| Store | winget sources (Microsoft) through the Windows Package Manager | Search terms and package names | When you search, install or update |

V-OS never uploads files, screenshots, documents, clipboard content or system information on its own.

## Local network
**Connect** shares files with a phone or computer on your network. It runs only while you have it open and sharing. It listens only on your private network address. Every link carries a random token, and nothing is reachable from the internet. Windows may ask you to allow it through the firewall the first time.

## Microphone, camera, location
V-OS 1.0 does not use the microphone, the camera or Windows location. Weather places are only those you add yourself. Music visuals planned after 1.0 will use system audio loopback, never the microphone.

## Diagnostics you share yourself
V-OS Launcher › Diagnostics › Export creates a zip file on your PC with logs, crash reports, install information and your settings with every key, token and password removed. It is never sent anywhere; you decide whether to share it.

## Removing your data
- Turn features off in Settings › Privacy, clear the clipboard history, or delete notes and history in their apps.
- Uninstall (Windows Settings › Apps, or V-OS Launcher › Uninstall) offers to delete all V-OS data. If you keep it, delete `%LOCALAPPDATA%\V-OS` yourself at any time.
- Data held by the online services above is subject to their own policies.
