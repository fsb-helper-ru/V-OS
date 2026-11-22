# Third-party notices

V-OS includes or uses the following third-party software and services. Each is used under its own license. This file ships with V-OS (Settings › About › Third-party notices).

## Included in the V-OS package

| Component | Version | License | Use in V-OS |
|---|---|---|---|
| .NET runtime and libraries (Microsoft) | 10.0 | MIT | Runtime, bundled self-contained |
| Windows App SDK: WinUI, Foundation, InteractiveExperiences, Base (Microsoft) | 2.7.0 | Microsoft Software License Terms for the Windows App SDK (redistributable) | UI framework, windowing, composition |
| Microsoft.Windows.SDK.NET (C#/WinRT projection of the Windows SDK) | 10.0.26100 | MIT | Windows APIs from C# |
| Win2D (Microsoft.Graphics.Win2D) | 1.4.0 | MIT source; package under Microsoft terms (<https://github.com/microsoft/Win2D>) | GPU drawing and effects |
| Microsoft Edge WebView2 SDK | 1.0.4370.26 | BSD-style license (Copyright (C) Microsoft Corporation) | Browser engine host (the WebView2 Runtime is part of Windows and is not redistributed) |
| Microsoft.Data.Sqlite | 10.0.15 | MIT | Local databases |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib.e_sqlite3) | 2.1.13 | Apache-2.0 | SQLite bindings |
| SQLite | 3.x (via e_sqlite3) | Public domain | Database engine |
| System.Management, System.Data.OleDb (Microsoft) | 10.0.15 | MIT | Hardware details; Windows Search queries |
| QRCoder (Raffael Herrmann and contributors) | 1.9.0 | MIT | QR codes in Connect |
| Anthropic C# SDK (Anthropic) | 13.12.0 | Apache-2.0 | Claude provider of the Assistant (optional) |
| Microsoft.Extensions.AI.Abstractions (Microsoft) | 10.5.1 | MIT | Dependency of the Anthropic SDK |

The full license texts are available from each project:
- .NET: <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> and <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>
- Windows App SDK: <https://github.com/microsoft/WindowsAppSDK> (license terms in the NuGet package `Microsoft.WindowsAppSDK.WinUI`)
- C#/WinRT: <https://github.com/microsoft/CsWinRT/blob/master/LICENSE>
- Win2D: <https://github.com/microsoft/Win2D/blob/winappsdk/main/LICENSE.txt>
- WebView2 SDK: license text in the NuGet package `Microsoft.Web.WebView2`
- Microsoft.Data.Sqlite: <https://github.com/dotnet/efcore/blob/main/LICENSE.txt>
- SQLitePCLRaw: <https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE.TXT>
- SQLite: <https://sqlite.org/copyright.html>
- QRCoder: <https://github.com/codebude/QRCoder/blob/master/LICENSE.txt>
- Anthropic C# SDK: <https://github.com/anthropics/anthropic-sdk-csharp/blob/main/LICENSE>

### MIT License (template)
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### Apache License 2.0
The Apache-2.0 components above are distributed under <https://www.apache.org/licenses/LICENSE-2.0>. No NOTICE files with additional attribution requirements are included in those packages.

## Used, not included

| Component or service | Terms |
|---|---|
| Windows fonts: Segoe UI Variable, Segoe Fluent Icons, Bahnschrift, Cascadia Mono | Part of Windows; used, not redistributed |
| Microsoft Edge WebView2 Runtime | Part of Windows (Evergreen) |
| winget (Windows Package Manager) | Part of Windows (App Installer); the Store app runs it |
| Open-Meteo weather data | CC BY 4.0, "Weather data by Open-Meteo.com". Free for non-commercial use; commercial use needs an Open-Meteo plan (ADR-011). |
| MyMemory, DeepL, LibreTranslate | Each service's own terms; used only after you allow online translation |
| Claude API (Anthropic) | Anthropic's commercial or consumer terms for your own API key; used only after your consent |
| Ollama | MIT; a server you install and run yourself |
