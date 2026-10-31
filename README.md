# Codex Voice Dictate

[![Build Windows application](https://github.com/ilinic/Codex_Voice_Dictate/actions/workflows/build-windows.yml/badge.svg)](https://github.com/ilinic/Codex_Voice_Dictate/actions/workflows/build-windows.yml)
![Windows](https://img.shields.io/badge/Windows-10%2F11-blue)
![.NET 8](https://img.shields.io/badge/.NET-8-purple)

**Voice dictation for OpenAI Codex on Windows — including API-key authenticated sessions.**

Codex Voice Dictate is an independent Windows system-tray utility. It records a spoken coding instruction, transcribes it with the OpenAI Audio API, optionally corrects technical terms, and inserts it into the **Codex desktop composer**. The insert-and-send shortcut submits only after the inserted text has been verified through Windows accessibility APIs.

## Download and run

1. Open the [Windows build workflow](https://github.com/ilinic/Codex_Voice_Dictate/actions/workflows/build-windows.yml).
2. Open the most recent **successful** run and download the `CodexVoiceDictate-win-x64` artifact.
3. Extract the archive and run `CodexVoiceDictate.exe` on Windows 10/11 (64-bit).
4. Look for the colored circular icon in the Windows notification area. Right-click it to open **Settings**.

The GitHub Actions build publishes a **self-contained executable**; the user does not need to install the .NET runtime separately. Windows SmartScreen may show an unsigned-publisher warning because community builds are not code-signed.

## First-time setup

1. Open **Settings...** from the tray menu.
2. Enter your **OpenAI Platform API key**. It is protected with Windows DPAPI for the current user. Alternatively, leave it empty and set `OPENAI_API_KEY` in the app's environment.
3. Select one or more recognition languages; by default, **Russian and English** are selected. Uncheck all for automatic language detection.
4. Choose whether to correct speech recognition using a text model (default: **GPT-6 Luna**). Recognition itself uses **`gpt-transcribe`**.
5. Pick two shortcuts: one for inserting, another for sending. The defaults are **F8** and **F9**. You can capture any F1–F12 key alone or an alphanumeric key combined with Ctrl or Alt.
6. Optionally enter technical keywords such as class names or library names, separated by commas or newlines.

### Bind the composer for reliability

On your Windows installation, some Codex input fields may be exposed with generic accessibility labels. To bind the correct composer once:

1. Open the **Codex** view in the ChatGPT / Codex desktop app.
2. Click inside the message composer.
3. Press **Ctrl+Alt+B** (the bind shortcut).

The app remembers a fingerprint of the target accessibility control. Future transcriptions can be directed to the composer even when focus is currently elsewhere. If the UI structure changes after a Codex update, bind it again. Binding is optional if the composer already has a distinctive accessibility label.

## Dictation controls

| Action | Default shortcut | Behaviour |
| --- | --- | --- |
| Start / stop recording, insert only | **F8** | First press starts recording; second stops, transcribes, and leaves text in Codex. |
| Stop recording and send | **F9** | During recording, stops, transcribes, verifies insertion, and presses Enter. |
| Cancel | **Escape** | Discards a recording or cancels pending transcription/correction. |
| Bind the currently focused composer | **Ctrl+Alt+B** | Remembers the Codex input field using Windows UI Automation. |

**Important:** In Codex, Enter should be configured to send messages if you use the *Stop & send* shortcut. If your Codex settings require Ctrl+Enter, use insert-only mode and submit manually. The utility never automatically sends when it cannot verify the paste.

## Tray icon states

| Color / symbol | State |
| --- | --- |
| Green microphone | Ready |
| Red dot | Recording |
| Amber waveform | Transcribing |
| Purple wand | Correcting terminology |
| Blue arrow | Inserting into Codex |
| Green checkmark | Done |
| Orange exclamation | Error / unverified insertion |
| Gray cross | Cancelled |

Right-click the tray icon for **Start / Stop**, **Stop & send**, **Cancel**, **Retry inserting last transcript**, **Copy last transcript**, **Bind composer**, **Settings**, and **Exit**. Double-click opens Settings.

## How insertion works

- Locates the Windows Codex desktop window and an editable composer through **Windows UI Automation**.
- Uses semantic accessibility labels, an optional user-bound editor fingerprint, and relative layout hints to disambiguate inputs; it never uses fixed desktop coordinates.
- Activates the correct window, sets focus, then pastes via the Windows clipboard.
- Reads the editable field back through UI Automation where supported. **Auto-send requires confirmed insertion.**
- On a failure, keeps the transcript for retry and leaves it on the clipboard when a paste was attempted.

Windows does not guarantee that an external app can always force another window to the foreground, and accessibility trees vary between desktop releases. Therefore the utility **fails safely** instead of pretending an unverified paste succeeded. Real Windows testing is required for each substantially changed Codex UI.

## Privacy and security

- The microphone is activated only after a user shortcut or tray action, and recording is limited to two minutes.
- WAV audio is kept in memory and sent only to the OpenAI transcription endpoint. Audio is not saved to a file.
- The API key is configured separately; **the application does not extract credentials from Codex's auth cache**.
- Windows DPAPI encrypts the saved key per Windows user; the key never belongs in Git or source code.
- Optional technical correction sends the transcript and user-configured keywords to the OpenAI Responses API with `store: false`. When that optional step fails, the raw recognized text is retained.
- API usage is billed to the project owning the supplied key. Confirm with the key owner that audio and text-model usage is permitted.

## Build from source

Requires Windows and the .NET 8 SDK:

```powershell
git clone https://github.com/ilinic/Codex_Voice_Dictate.git
cd Codex_Voice_Dictate
dotnet restore src/CodexVoiceDictate/CodexVoiceDictate.csproj
dotnet publish src/CodexVoiceDictate/CodexVoiceDictate.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The app uses Windows Forms, Windows UI Automation, NAudio for microphone capture, and the official OpenAI HTTP endpoints. It does not need to attach to, patch, or inject code into Codex.

## Scope and current limitations

- This first version targets the **local Windows Codex desktop composer**. It does not send prompts to Codex Cloud or attempt to read conversation history.
- There is no automatic retrieval of the current Codex source-code context yet. A manually entered technical-word list is available in Settings.
- Accessibility-based insertion depends on the current desktop build and may require binding the composer.
- No code-signing certificate is provided; the executable is built and distributed by GitHub Actions.

This is an independent open-source community project, not affiliated with or endorsed by OpenAI.
