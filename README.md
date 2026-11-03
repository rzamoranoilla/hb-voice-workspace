# HB Voice Workspace

<img src="Assets/logo.png" alt="HB Voice Workspace logo" width="96" align="right" />

A Windows desktop program that puts, side by side, the places where you work and the voice tools
you work with:

- **Workspaces** (left column): each one is a folder on your disk, opened as a set of PowerShell
  terminals, an SSH terminal, a Markdown editor or a LaTeX editor.
- **The work area** (middle column): the terminals, or the editor with its file browser and live
  preview.
- **Transcriber and read-aloud** (right column): dictate into the microphone or play an audio
  file, and the text appears as it is spoken, recognised on your own computer; and have any text
  read out loud with the Windows voices.

Everything runs locally. No account, no server and no internet connection are needed after the
installation, except to connect to your own devices over SSH.

Powered by [Zamoilla](https://raul.zamoilla.cl).

---

## Install

1. Download **HB-Voice-Workspace-Setup-x.y.z.exe** from the
   [latest release](https://github.com/rzamoranoilla/hb-voice-workspace/releases/latest).
2. Run it. It installs for your own user, so no administrator password is asked.
3. If Windows SmartScreen shows "Windows protected your PC", click **More info** and then
   **Run anyway**. The installer is not signed with a paid certificate; that is all the warning
   means.

The installer carries the program itself (which already includes .NET, so nothing has to be
installed for it) and downloads what your computer is missing:

| Component | Needed for | What the installer does |
|---|---|---|
| Microsoft Edge WebView2 runtime | terminals and previews | Installed silently if it is missing (it is part of Windows 11 and of most Windows 10 machines). |
| Whisper speech model `base` (142 MB) | the transcriber | Downloaded if you leave the option ticked; otherwise the program downloads it the first time you dictate. |
| MiKTeX (LaTeX) | LaTeX workspaces only | Installed through `winget` if you leave the option ticked and no LaTeX is found. Untick it if you do not write LaTeX. |

Requirements: Windows 10 version 2004 or later, or Windows 11, 64-bit. About 450 MB of disk
space with the speech model, more with MiKTeX.

A portable zip (`HB-Voice-Workspace-x.y.z-win-x64.zip`) is published next to the installer for
machines where nothing may be installed: unzip it anywhere and run `HB.exe`. The WebView2 runtime
has to be present on such a machine.

To remove the program use *Settings > Apps* or the uninstaller in the Start menu. Your accounts,
settings and workspaces are kept (see [Where things are stored](#where-things-are-stored)).

## First start

A start-up screen shows what is being loaded (the browser engine, the voices, the audio
engine). Then the login window asks for a user. There are no users at first: type a name and a
password and press **Create new user**.

Users are local to the computer. Each user has their own list of workspaces, saved SSH
connections and preferences. The password also protects the SSH passwords you choose to save:
they are encrypted with a key derived from it, so nobody can read them without logging in as you.

## How the program works

### Workspaces

A workspace is a folder. Press **＋** to open one, choose a folder and a kind:

| Kind | What the middle column shows |
|---|---|
| **Default** | PowerShell tabs opened in the folder, and the transcriber on the right. |
| **SSH** | The same, plus a connection bar above the terminal to log in to another device over SSH. |
| **Markdown** | A Markdown editor with a file browser and a live preview, instead of terminals. |
| **LaTeX** | A LaTeX editor with a file browser and the compiled PDF, instead of terminals. |

Clicking a workspace in the list only selects it. Nothing is loaded until you press **▶ Start**
(or the ▶ next to its name). **⏹** stops it again, closing its terminals and releasing its
preview; the files are untouched. This keeps the program light when you have many workspaces.
The **✕** closes the workspace, that is, removes it from the list; the folder stays on disk.

Press **Ctrl+F** while the workspace list has the focus to filter it by name.

### Terminals

The Default and SSH workspaces open PowerShell tabs in the workspace folder. **＋** adds a tab;
double-click or right-click a tab to rename it; the tab names are remembered per workspace.
Text selected with the mouse is copied to the clipboard at once, in the terminals and everywhere
else in the program.

In an SSH workspace the connection bar takes a host, port, user and password. **Connect** runs
`ssh` in the current tab. **Save** keeps the connection under a name, in the list of saved
connections that every SSH workspace shares; the password is stored encrypted, as explained
above. The Windows OpenSSH client (part of Windows) is what connects.

### Markdown and LaTeX editors

The middle column becomes a writing desk:

- **File browser** at the top: the files of the workspace folder. **New file…** creates one,
  **Open other…** opens a file outside the folder, **Refresh** reads the folder again. The
  browser can be collapsed to give the editor the whole height. Press **Ctrl+F** with the
  browser focused to filter files by name, through all subfolders.
- **Editor** in the middle. Changes are saved automatically shortly after you stop typing
  (Ctrl+S saves at once, and compiles a LaTeX document).
- **Preview** at the bottom, or in its own window with **Pop out** so it can live on another
  screen. The preview keeps your reading position when the document is rebuilt after a change.
- **Terminal** opens a PowerShell in the document folder, under the transcriber, for `git`,
  scripts or anything else.

**Markdown** is rendered as you type. Equations use MathJax: `$a^2 + b^2 = c^2$` inline and
`$$ … $$` on its own line for a display equation. Images use the usual syntax and can point to
files next to the document or anywhere on the disk:

```markdown
![Figure 1](figures/plot.png)
![A photo](../photos/my photo.jpg)
![Absolute path](C:/Users/me/Pictures/diagram.svg)
```

**LaTeX** is compiled to PDF after each change (and with **Compile**). The program uses the
first compiler it finds among `tectonic`, `latexmk` and `pdflatex`, so MiKTeX, TeX Live and
Tectonic all work; with MiKTeX, missing packages are installed on the fly. The auxiliary files
go to a `.latex-build` folder next to the document, and the finished PDF is also copied next to
the `.tex` file. Compilation errors appear between the editor and the preview. If a project has
several `.tex` files, the one you compile first is remembered as the main file of the workspace.

**Grammarly switch.** The header of the editor has a **Grammarly** switch. While it is off, the
editor is hidden from Grammarly for Windows and other writing assistants, so you can write
without suggestions. Turn it on when you want the text checked.

**Find.** **Ctrl+F** opens a find bar over the editor. Every match is highlighted in yellow and
the current one in orange; Enter or F3 goes to the next, Shift+Enter or Shift+F3 to the previous,
Esc closes the bar.

### Transcriber

The right column transcribes speech with [Whisper](https://github.com/ggerganov/whisper.cpp),
running on your computer through Whisper.net. Nothing is sent anywhere.

- **🎤 Start dictation** listens to the microphone. The text appears while you speak and is
  committed sentence by sentence at the pauses.
- **📂 Transcribe an audio file…** plays a recording (mp3, wav, m4a, wma, flac, WhatsApp `.ogg`
  and anything else Windows can play) through the speakers and transcribes it as it plays.
- **Language**: pick the language spoken, or *Auto-detect*. **Model**: *Fast* (the 142 MB
  `base` model) or *More accurate* (the 466 MB `small` model, downloaded on first use).

Each dictation becomes an entry with its time and language. The entries are appended to a
Markdown file named after the workspace, in the workspace folder (`<workspace name>.md`), so they
are plain text you can open anywhere. Click an entry to copy it, or select part of its text.

### Read aloud

Paste or type text in the **Read aloud** box, choose a voice and press **🔊 Read aloud**. The
voices are those installed in Windows (*Settings > Time & language > Speech* adds more
languages).

### Keyboard shortcuts

| Keys | Where | Action |
|---|---|---|
| Ctrl+F | workspace list | Filter the workspaces by name |
| Ctrl+F | file browser | Filter the files by name |
| Ctrl+F | editor | Find in the text (Enter/F3 next, Shift+F3 previous, Esc close) |
| Ctrl+S | editor | Save now (and compile LaTeX) |
| Ctrl+wheel, Ctrl+0, Ctrl+± | PDF preview | Zoom |
| Enter / Esc | filter boxes | Open the first match / clear and close |

Text selected with the mouse anywhere in the program is copied to the clipboard.

## Where things are stored

| What | Where |
|---|---|
| Program files | `%LOCALAPPDATA%\Programs\HB Voice Workspace` |
| Users, settings, speech models, browser cache | `%LOCALAPPDATA%\HB` |
| Transcriptions | `<workspace folder>\<workspace name>.md` |
| LaTeX build files | `<document folder>\.latex-build` |

Set the environment variable `HB_DATA_DIR` to keep the data somewhere else (for instance on a
USB drive with the portable version).

## Troubleshooting

- **"The preview could not start" or an empty terminal.** The WebView2 runtime is missing or
  broken. Install it from
  [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/) and start the program
  again.
- **"No LaTeX compiler was found".** Install [MiKTeX](https://miktex.org/) (or TeX Live) and
  restart the program so it sees the new `PATH`.
- **The transcriber says it is downloading the model.** The first dictation downloads the
  speech model (142 MB) if the installer did not; it happens once.
- **Nothing is transcribed.** Check that the microphone is allowed for desktop apps in
  *Settings > Privacy & security > Microphone*.
- **The program does not start after an update of Windows.** Run the installer again; it keeps
  your data.

## Build from source

Requirements: Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and, for the
installer, [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
git clone https://github.com/rzamoranoilla/hb-voice-workspace.git
cd hb-voice-workspace
dotnet build HB.csproj -c Release          # bin\Release\net10.0-windows10.0.19041.0\win-x64\HB.exe
```

To build the installer:

```powershell
dotnet publish HB.csproj -c Release -r win-x64 --self-contained -o publish\win-x64
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=1.0.0 installer\HB.iss
# -> dist\HB-Voice-Workspace-Setup-1.0.0.exe
```

### Releasing

Pushing a tag such as `v1.1.0` runs the [Release workflow](.github/workflows/release.yml) on
GitHub Actions, which publishes the app, builds the installer and the portable zip, and attaches
both to a GitHub release of that tag:

```powershell
git tag v1.1.0
git push origin v1.1.0
```

The version shown in the program comes from `<Version>` in `HB.csproj`; the workflow overrides it
with the tag's number.

## Project layout

| Folder / file | Contents |
|---|---|
| `App.xaml.cs`, `SplashWindow`, `LoginWindow`, `MainWindow` | Start-up, warm-up screen, accounts, the three-column window |
| `Models.cs`, `Accounts.cs` | Settings, workspaces, templates, local user accounts |
| `Terminal/` | ConPTY pseudo-console and the xterm.js terminal view (WebView2) |
| `Documents/` | Markdown/LaTeX pane, preview, LaTeX compiler, find highlighter, Grammarly-aware editor |
| `Speech/` | Whisper transcriber, Ogg Opus reader, Windows text-to-speech, transcript file |
| `Assets/` | xterm.js, MathJax, pdf.js and the HTML pages they run in; logo and icon |
| `installer/HB.iss` | Inno Setup script of the installer |
| `.github/workflows/release.yml` | Builds and publishes a release from a version tag |

## Built with

[Whisper.net](https://github.com/sandrohanea/whisper.net) (speech recognition),
[NAudio](https://github.com/naudio/NAudio) and [Concentus](https://github.com/lostromb/concentus)
(audio), [Markdig](https://github.com/xoofx/markdig) (Markdown),
[MathJax](https://www.mathjax.org/), [xterm.js](https://xtermjs.org/),
[pdf.js](https://mozilla.github.io/pdf.js/) and
[Microsoft Edge WebView2](https://developer.microsoft.com/microsoft-edge/webview2/).

© 2026 [Zamoilla](https://raul.zamoilla.cl)
