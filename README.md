# Codex Voice Dictate

**Voice dictation for OpenAI Codex on Windows — including API-key authenticated sessions.**

Codex Voice Dictate is a planned lightweight Windows system-tray application that turns speech into coding prompts for the **OpenAI Codex desktop app**. It is designed for developers who want hands-free, push-to-talk input while working with Codex through an OpenAI API key.

> **Project status:** Early development. This repository currently documents the intended functionality; a usable Windows release is not available yet. This is an independent community project, not an official OpenAI product.

## Why this project?

Codex supports API-key authentication for local coding workflows, but built-in voice dictation is not currently available with that sign-in method. Codex Voice Dictate aims to provide a dedicated speech-to-text workflow without changing how Codex authenticates or which repositories you work on.

## Planned features

- **Windows system tray:** a small background application with visible states for idle, recording, transcription, insertion, and errors.
- **Push-to-talk hotkeys:** configurable keyboard shortcuts to start and stop recording.
- **Insert or send:** leave the recognized prompt in the Codex composer for review, or send it after successful insertion.
- **OpenAI speech-to-text:** transcribe recorded audio using an API key configured by the user.
- **Reliable targeting:** locate the Codex desktop window and its active conversation composer using Windows UI Automation, rather than fixed mouse coordinates or assumptions about keyboard focus.
- **Verified insertion:** check whether a prompt reached its intended destination; preserve the text and report a failure if insertion cannot be confirmed.
- **Multilingual dictation:** support natural-language prompts, including technical and programming terminology, subject to the transcription model's capabilities.
- **Secure credential handling:** keep API keys out of source code and Git history.

These are development goals, **not a claim that they are already implemented**. Some details depend on testing the accessibility interface exposed by current Codex Windows builds.

## Intended workflow

1. Press a configured shortcut to begin recording.
2. Speak a coding task or instruction.
3. Press the shortcut again to finish.
4. The app transcribes the recording through the OpenAI API.
5. The app locates the currently selected Codex conversation, activates its composer, and inserts the text.
6. Depending on the chosen shortcut, review the text or send it.

The intended implementation will avoid silently sending text to another application or to the wrong Codex field.

## API key and billing

The application is intended to accept a separately configured OpenAI API key. You can configure the same authorized API key that you use with Codex, provided the key has access to the required audio endpoint and its use is permitted by the account or project owner.

**The application will not extract credentials from Codex's authentication cache.** Audio transcription incurs API usage charges separately from Codex's model requests.

## Compatibility

- **Platform:** Microsoft Windows
- **Target:** OpenAI Codex desktop app (ChatGPT desktop's Codex view)
- **Input:** microphone / configurable global keyboard shortcuts
- **Speech recognition:** OpenAI Audio API
- **Composer integration:** Windows UI Automation, subject to accessibility support and testing

## Roadmap

- [ ] Windows tray UI and application lifecycle
- [ ] Microphone capture and recording status
- [ ] Configurable global shortcuts
- [ ] OpenAI transcription integration
- [ ] Secure API-key configuration
- [ ] Codex window and composer discovery
- [ ] Focus-independent insertion and success verification
- [ ] Insert-only and insert-and-send modes
- [ ] Windows packaging and portable distribution
- [ ] Integration tests against current Codex releases

## Search terms

Codex voice dictation · Codex speech-to-text · OpenAI Codex voice input · Codex API key dictation · Windows voice typing · push-to-talk for Codex.

---

**Codex Voice Dictate** is an independent utility project and is not affiliated with or endorsed by OpenAI.
