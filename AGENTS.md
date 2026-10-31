# Agent guide — Codex Voice Dictate

Keep `README.md` deliberately short and user-facing. Put engineering notes, architecture, caveats, and build instructions here.

## Product and scope

- Standalone Windows 10/11 x64 system-tray app for voice dictation into the Codex desktop composer. The app is independent of Codex login and uses a separately supplied OpenAI Platform API key.
- Built with C#/.NET 8 (`net8.0-windows`), Windows Forms, Windows UI Automation, and NAudio. Project: `src/CodexVoiceDictate/CodexVoiceDictate.csproj`.
- High-DPI is configured at process startup via generated `ApplicationConfiguration.Initialize()` and `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`. Keep Windows system fonts and the settings form's 96-DPI `AutoScaleDimensions`. Settings fields must use content-sized (`SizeType.AutoSize`) rows in a scrollable panel, not fixed pixel row heights; Save/Cancel live in an always-visible footer. Fixed rows clipped content at 4K / 150–250% scaling. Test on mixed-DPI displays as well as a single 4K monitor.
- Code map: `Program.cs` (entry), `TrayApplication.cs` (tray states and commands), `SettingsForm.cs` (preferences), `AppSettings.cs` (configuration and DPAPI key storage), `GlobalHotkeys.cs` (shortcuts), `WaveCapture.cs` (recording), `OpenAiSpeechClient.cs` (transcription and correction API), `CodexComposer.cs` (locating, binding, inserting, verifying, sending).

## User flow

- Right-click tray icon for controls, Settings, and Exit; double-click opens Settings.
- F8: start recording; press again to stop, transcribe, and insert text. F9: while recording, stop, transcribe, insert, and send. Escape: cancel. Ctrl+Alt+B: bind the focused composer via UI Automation. Insert/send shortcuts are user-configurable.
- Tray icon signals ready, recording, transcription, correction, insertion, success, error, and cancellation. Avoid implying insertion succeeded when it has not been verified.
- Default recognition languages are Russian and English; leaving the language selection empty enables automatic detection. Technical keyword hints are supported. Transcription uses `gpt-transcribe`; optional text correction defaults to `gpt-6-luna` and falls back to the raw transcript on failure.

## Security and behavior invariants

- User-supplied API key is encrypted with Windows DPAPI for the current user in `%LOCALAPPDATA%/CodexVoiceDictate/settings.json`; `OPENAI_API_KEY` is a fallback. Never read/extract Codex's credential cache or commit secrets.
- Recording is user-triggered, limited to 120 seconds, and buffered in memory. Do not save audio files. The OpenAI transcription endpoint receives microphone audio; optional correction sends recognized text and keywords with `store: false`.
- Use Windows UI Automation and an optional user-bound composer fingerprint to identify the correct input. Do not rely on fixed screen coordinates. Auto-send only after the inserted text has been verified. If insertion fails, preserve the transcript for user retry/copy.
- Codex UI can change and Windows foreground activation is not guaranteed; test focus and submission on a real Windows desktop. Do not claim that a CI build verifies the UI behavior.

## Build and distribution

On Windows with .NET 8 SDK:

```powershell
dotnet restore src/CodexVoiceDictate/CodexVoiceDictate.csproj
dotnet build src/CodexVoiceDictate/CodexVoiceDictate.csproj -c Release --no-restore
dotnet publish src/CodexVoiceDictate/CodexVoiceDictate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

`.github/workflows/build-windows.yml` builds on a GitHub-hosted Windows runner, stores a short-lived workflow artifact, and automatically publishes `publish/CodexVoiceDictate.exe` as an asset in a new GitHub Release for successful pushes to `main`. Pull requests only build; they never publish releases. The stable direct download link is `https://github.com/ilinic/Codex_Voice_Dictate/releases/latest/download/CodexVoiceDictate.exe`.

The executable is self-contained and currently unsigned, so Windows SmartScreen may warn. Before editing the distribution workflow, ensure the asset filename stays stable, the release is public, and the latest-download URL resolves.
