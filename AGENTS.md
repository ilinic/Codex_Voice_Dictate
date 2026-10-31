# Agent guide — Codex Voice Dictate

Keep `README.md` deliberately short and user-facing. Put engineering notes, architecture, caveats, and build instructions here.

## Product and scope

- Standalone Windows 10/11 x64 system-tray app for voice dictation into a user-bound text field in any Windows application (Codex, IntelliJ IDEA, etc.). The app is independent of Codex login and uses a separately supplied OpenAI Platform API key.
- Built with C#/.NET 8 (`net8.0-windows`), Windows Forms for the tray, WPF for the settings window, Windows UI Automation, and NAudio. Project: `src/CodexVoiceDictate/CodexVoiceDictate.csproj` (`UseWindowsForms` and `UseWPF` are both enabled).
- High-DPI is configured at startup via generated `ApplicationConfiguration.Initialize()` and `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`. Settings are a native WPF window (`SettingsWindow.xaml` plus code-behind) that uses device-independent units, a flexible WrapPanel for language choices, and a scrollable body with a fixed footer. At creation it caps its size to the active monitor's work area. Preserve system text preferences. Test on both 4K and small / mixed-DPI displays.
- Code map: `Program.cs` (entry), `TrayApplication.cs` (tray states and commands), `SettingsWindow.xaml` / `.xaml.cs` (preferences), `AppSettings.cs` (configuration and DPAPI key storage), `GlobalHotkeys.cs` (shortcuts), `WaveCapture.cs` (recording), `OpenAiSpeechClient.cs` (transcription and correction API), `CodexComposer.cs` (legacy filename: universal target binding, focusing, insertion, read-back verification, and conditional sending).

## User flow

- Right-click tray icon for controls, Settings, and Exit; double-click opens Settings.
- F8: start recording; press again to stop, transcribe, and insert text. F9: while recording, stop, transcribe, insert, and send only after text verification. Escape: cancel. Ctrl+Alt+B: bind the focused target field, including a focus-only fallback for custom UI controls. Insert/send shortcuts are user-configurable.
- Tray icon signals ready, recording, transcription, correction, insertion, success, error, and cancellation. Avoid implying insertion succeeded when it has not been verified.
- Default recognition languages are Russian and English; leaving the language selection empty enables automatic detection. Technical keyword hints are supported. Transcription uses `gpt-transcribe`; optional text correction defaults to `gpt-6-luna` and falls back to the raw transcript on failure.

## Security and behavior invariants

- User-supplied API key is encrypted with Windows DPAPI for the current user in `%LOCALAPPDATA%/CodexVoiceDictate/settings.json`; `OPENAI_API_KEY` is a fallback. Never read/extract Codex's credential cache or commit secrets.
- Recording is user-triggered, limited to 120 seconds, and buffered in memory. Do not save audio files. The OpenAI transcription endpoint receives microphone audio; optional correction sends recognized text and keywords with `store: false`.
- Use Windows UI Automation and an optional bound field fingerprint (process, window, element), with HWND focus checks for inaccessible custom editors such as Swing. Native focus HWNDs are session-local: after process restart, bind again if a semantic element cannot be found. Do not rely on fixed screen coordinates. Auto-send only after the inserted text has been verified via ValuePattern or TextPattern; paste attempts without read-back use `Unverified` status and never auto-send. If insertion fails, preserve the transcript for retry/copy.
- Accessibility providers may omit an editor's text or reject `SetFocus` (particularly Java Swing/JetBrains). Windows foreground activation is not guaranteed; test focus, insertion, and submission on a real Windows desktop. Do not claim that a CI build verifies the UI behavior.

## Build and distribution

On Windows with .NET 8 SDK:

```powershell
dotnet restore src/CodexVoiceDictate/CodexVoiceDictate.csproj
dotnet build src/CodexVoiceDictate/CodexVoiceDictate.csproj -c Release --no-restore
dotnet publish src/CodexVoiceDictate/CodexVoiceDictate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

`.github/workflows/build-windows.yml` builds on a GitHub-hosted Windows runner, stores a short-lived workflow artifact, and automatically publishes `publish/CodexVoiceDictate.exe` as an asset in a new GitHub Release for successful pushes to `main`. Pull requests only build; they never publish releases. The stable direct download link is `https://github.com/ilinic/Codex_Voice_Dictate/releases/latest/download/CodexVoiceDictate.exe`.

The executable is self-contained and currently unsigned, so Windows SmartScreen may warn. Before editing the distribution workflow, ensure the asset filename stays stable, the release is public, and the latest-download URL resolves.
