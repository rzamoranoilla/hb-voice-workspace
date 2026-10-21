# HB Voice Workspace

A Windows desktop app (WPF, .NET 10) that puts a voice transcriber, a text-to-speech reader and
work areas side by side:

- **Workspaces** — a folder each, of one kind: PowerShell terminal, SSH terminal, Markdown editor
  or LaTeX editor.
- **Markdown / LaTeX** — file browser, editor with auto-save, live preview (Markdown with MathJax
  equations and images; LaTeX compiled to PDF).
- **Transcriber** — dictation from the microphone, or playback of an audio file (mp3, wav, m4a,
  WhatsApp `.ogg`, …) transcribed while it plays, with Whisper running locally. Entries are saved
  to the workspace's notes file.
- **Read aloud** — speaks typed text with the Windows voices.
- Local user accounts; saved SSH passwords are encrypted with a key derived from the login password.
- Text selected with the mouse anywhere in the app is copied to the clipboard.

## Build

Requires the .NET 10 SDK and the WebView2 runtime (part of Windows 11 / Edge).

```powershell
dotnet build HB.csproj -c Release
```

The self-contained app is written to `bin\Release\net10.0-windows10.0.19041.0\win-x64\HB.exe`.
Whisper models are downloaded on first use into `%LOCALAPPDATA%\HB\models`.

## Main dependencies

Whisper.net (speech recognition), NAudio (audio capture and playback), Concentus (Opus decoding),
Markdig (Markdown), MathJax and xterm.js (bundled in `Assets`), Microsoft.Web.WebView2.
