# Agent guide — Codex Voice Dictate

Keep `README.md` deliberately short and user-facing. Put engineering notes, architecture, caveats, and build instructions here.

## Product and scope

- Standalone Windows 10/11 x64 system-tray app for voice dictation into Windows text fields (Codex, IntelliJ IDEA, etc.). At recording start it automatically captures the currently focused editor, or captures only the focused window for opt-in clipboard-only mode. No manual binding shortcut or persisted target is needed. The app is independent of Codex login and uses a separately supplied OpenAI Platform API key.
- Built with C#/.NET 8 (`net8.0-windows`), Windows Forms for the tray, WPF for the settings window, Windows UI Automation, NAudio for microphone capture, and Concentus for Ogg/Opus encoding. Project: `src/CodexVoiceDictate/CodexVoiceDictate.csproj` (`UseWindowsForms` and `UseWPF` are both enabled).
- High-DPI is configured at startup via generated `ApplicationConfiguration.Initialize()` and `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`. Settings are a native WPF window (`SettingsWindow.xaml` plus code-behind) that uses device-independent units, a flexible WrapPanel for language choices, and a scrollable body with a fixed footer. At creation it caps its size to the active monitor's work area. Preserve system text preferences. Test on both 4K and small / mixed-DPI displays.
- Code map: `Program.cs` (entry), `TrayApplication.cs` (tray states and hotkey commands), `SettingsWindow.xaml` / `.xaml.cs` (preferences), `AppSettings.cs` (configuration and DPAPI key storage), `GlobalHotkeys.cs` (shortcuts), `WaveCapture.cs` (real-time Ogg/Opus recording), `OpenAiSpeechClient.cs` (transcription and correction API), `CodexComposer.cs` (automatic editor capture, focusing, insertion, read-back verification, and conditional sending).

## User flow

- Right-click tray icon for one status/version row, Settings, and Exit; double-click opens Settings. Status details are in the status row tooltip, while the glyph color reflects state. Do not add duplicate start/stop or binding items to the menu.
- The WinForms `ContextMenuStrip` is shown manually on `NotifyIcon.MouseUp` after foregrounding the hidden hotkey-owner HWND and followed by `PostMessage(WM_NULL)`; this matches the Win32 tray-popup dismissal requirement. Do not assign it to `NotifyIcon.ContextMenuStrip`, or outside-click dismissal can regress.
- F8: start recording; press again to stop, transcribe, and insert text. F9: while recording, stop, transcribe, insert, and send. Both modes capture their target when recording starts. Automatic editor mode attempts to reactivate the captured editor and requires UIA text verification before submitting; clipboard-only mode F9 is an explicit request to paste and submit without read-back verification, but it requires the originally focused window/control to remain focused. Sending uses Ctrl+Enter in Codex and Enter elsewhere. Escape: cancel. Insert/send shortcuts are user-configurable. No Ctrl+Alt+B binding shortcut is registered.
- Tray icon signals ready, recording, transcription, correction, insertion, success, error, and cancellation. Avoid implying insertion succeeded when it has not been verified.
- Default recognition languages are Russian and English; leaving the language selection empty enables automatic detection. The optional user-entered `RecognitionContext` is free-form, defaults to empty, and supplies exact project terminology via the `gpt-transcribe` prompt and optional text-correction input. Never auto-invent project names or populate this field. Transcription uses `gpt-transcribe`; optional text correction defaults to `gpt-6-luna` and falls back to the raw transcript on failure.

## Security and behavior invariants

- User-supplied API key is encrypted with Windows DPAPI for the current user in `%LOCALAPPDATA%/CodexVoiceDictate/settings.json`; `OPENAI_API_KEY` is a fallback. Never read/extract Codex's credential cache or commit secrets.
- Recording is user-triggered and compressed *as microphone samples arrive* to an in-memory 24-kbit/s mono Ogg/Opus stream; raw audio is never accumulated or saved to disk. There is no 120-second time limit. When encoded size nears 24,000,000 bytes, stop accepting further samples and automatically finish/transcribe. Reserve room for the final Ogg page and stay below OpenAI's 25 MB transcription file limit. The OpenAI transcription endpoint receives microphone audio and optional user-written context; optional correction sends recognized text and the same context with `store: false`. Keep the contextual hint bounded (currently 1200 characters) and treat it as spelling reference, not instructions or required words.
- Automatic editor mode uses Windows UI Automation and HWND/native-focus snapshots captured at recording start (not saved between recordings). It only reactivates the captured window with the same process id, and verifies the captured editor has focus before paste. Auto-send requires verified text via ValuePattern or TextPattern, otherwise never submit. The opt-in direct-paste mode uses only Win32 foreground/native focus and clipboard + SendInput (no UIA or read-back); it pastes/sends only if original foreground HWND, process, and available native focus still match. Do not rely on fixed screen coordinates; never send after an unverified automatic insertion. Report direct-paste operations as `Unverified`. Preserve the transcript on clipboard if insertion fails.
- If transcription succeeds but insertion fails or throws (for instance because focus changed), automatically copy the available transcript to the clipboard and report this in the tray status, preserving it for retry/copy. Unverified insertion pathways already populate the clipboard before attempting paste. Accessibility providers may omit an editor's text or reject `SetFocus` (particularly Java Swing/JetBrains). Windows foreground activation is not guaranteed; test focus, insertion, and submission on a real Windows desktop. Do not claim that a CI build verifies the UI behavior.

## Build and distribution

On Windows with .NET 8 SDK:

```powershell
dotnet restore src/CodexVoiceDictate/CodexVoiceDictate.csproj
dotnet build src/CodexVoiceDictate/CodexVoiceDictate.csproj -c Release --no-restore
dotnet publish src/CodexVoiceDictate/CodexVoiceDictate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

`.github/workflows/build-windows.yml` builds on a GitHub-hosted Windows runner using `0.1.${{ github.run_number }}` as the assembly version, stores a short-lived workflow artifact, and automatically publishes `publish/CodexVoiceDictate.exe` as an asset in a new GitHub Release for successful pushes to `main`. The tray status shows the resulting assembly version. Pull requests only build; they never publish releases. The stable direct download link is `https://github.com/ilinic/Codex_Voice_Dictate/releases/latest/download/CodexVoiceDictate.exe`.

The executable is self-contained and currently unsigned, so Windows SmartScreen may warn. Before editing the distribution workflow, ensure the asset filename stays stable, the release is public, and the latest-download URL resolves.
