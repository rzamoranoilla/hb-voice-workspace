; Inno Setup script of the HB Voice Workspace installer.
;
; The installer carries the application itself (self-contained: no .NET to install) and
; downloads what the machine is missing:
;   - the Microsoft Edge WebView2 runtime (terminals and previews), when it is not installed;
;   - the Whisper speech recognition model, so the transcriber works from the first use;
;   - MiKTeX through winget, for the LaTeX workspaces (when no LaTeX is installed).
; It installs for the current user, without administrator rights.
;
; Build:  dotnet publish HB.csproj -c Release -r win-x64 --self-contained -o publish\win-x64
;         ISCC.exe /DAppVersion=1.0.0 installer\HB.iss
; The Setup exe is written to dist\.

#define AppName "HB Voice Workspace"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-x64"
#endif
#define Publisher "Zamoilla"
#define Site "https://raul.zamoilla.cl"
#define Repo "https://github.com/rzamoranoilla/hb-voice-workspace"
#define ExeName "HB.exe"
; Evergreen bootstrapper: a small program that downloads and installs the current WebView2 runtime
#define WebView2Url "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
; The same file Whisper.net downloads on first use
#define ModelUrl "https://huggingface.co/sandrohanea/whisper.net/resolve/v3/classic/ggml-base.bin"
#define ModelFile "ggml-base.bin"

[Setup]
AppId={{A3C1F2B4-7D5E-4C8A-9F0B-2E6D1C4B8A57}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#Site}
AppSupportURL={#Repo}/issues
AppUpdatesURL={#Repo}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoCopyright={#Publisher}
; Per-user install (no administrator prompt): {autopf} is then %LOCALAPPDATA%\Programs
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=HB-Voice-Workspace-Setup-{#AppVersion}
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.DownloadGroup=Download and install:
english.ModelTask=Speech recognition model (142 MB), so the transcriber works offline from the first use
english.LatexTask=MiKTeX (LaTeX compiler) through winget, needed by the LaTeX workspaces
english.WebView2Installing=Installing the Microsoft Edge WebView2 runtime...
english.LatexInstalling=Installing MiKTeX with winget (this can take several minutes)...
english.DownloadFailed=A download did not finish:%n%n%1%n%nThe installation continues. The program downloads the speech model by itself the first time the transcriber is used; the WebView2 runtime can be installed from https://developer.microsoft.com/microsoft-edge/webview2/ and MiKTeX from https://miktex.org/.
spanish.DownloadGroup=Descargar e instalar:
spanish.ModelTask=Modelo de reconocimiento de voz (142 MB), para que el transcriptor funcione sin conexión desde el primer uso
spanish.LatexTask=MiKTeX (compilador de LaTeX) mediante winget, necesario para los espacios de trabajo LaTeX
spanish.WebView2Installing=Instalando el entorno Microsoft Edge WebView2...
spanish.LatexInstalling=Instalando MiKTeX con winget (puede tardar varios minutos)...
spanish.DownloadFailed=Una descarga no terminó:%n%n%1%n%nLa instalación continúa. El programa descarga el modelo de voz por sí mismo la primera vez que se usa el transcriptor; el entorno WebView2 se puede instalar desde https://developer.microsoft.com/microsoft-edge/webview2/ y MiKTeX desde https://miktex.org/.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "model"; Description: "{cm:ModelTask}"; GroupDescription: "{cm:DownloadGroup}"; Check: not ModelInstalled
Name: "latex"; Description: "{cm:LatexTask}"; GroupDescription: "{cm:DownloadGroup}"; Check: not LatexInstalled

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Downloaded while installing (see [Code]); goes where the program looks for it
Source: "{tmp}\{#ModelFile}"; DestDir: "{localappdata}\HB\models"; Flags: external skipifsourcedoesntexist; Tasks: model

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{group}\{#Publisher}"; Filename: "{#Site}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "{cm:WebView2Installing}"; Check: WebView2Missing; Flags: waituntilterminated skipifdoesntexist
Filename: "{cmd}"; Parameters: "/C winget install --id MiKTeX.MiKTeX --exact --source winget --accept-package-agreements --accept-source-agreements"; StatusMsg: "{cm:LatexInstalling}"; Tasks: latex; Flags: waituntilterminated
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Browser cache and PDF copies of the program; accounts, settings and models in %LOCALAPPDATA%\HB are kept
Type: filesandordirs; Name: "{localappdata}\HB\WebView2"
Type: filesandordirs; Name: "{localappdata}\HB\preview"

[Code]
const
  { Where the WebView2 runtime registers its version (per machine, 32-bit view, or per user) }
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2KeyWow = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  DownloadPage: TDownloadWizardPage;

function WebView2Missing: Boolean;
var
  Version: String;
begin
  Result := True;
  if RegQueryStringValue(HKLM, WebView2KeyWow, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0') then Result := False
  else if RegQueryStringValue(HKLM, WebView2Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0') then Result := False
  else if RegQueryStringValue(HKCU, WebView2Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0') then Result := False;
end;

function ModelInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{localappdata}\HB\models\{#ModelFile}'));
end;

{ A LaTeX compiler the program can use: on the PATH, or MiKTeX in its usual per-user folder. }
function LatexInstalled: Boolean;
var
  Path, Folder: String;
  Position: Integer;
begin
  Result := FileExists(ExpandConstant('{localappdata}\Programs\MiKTeX\miktex\bin\x64\pdflatex.exe'));
  Path := GetEnv('PATH');
  while (not Result) and (Path <> '') do
  begin
    Position := Pos(';', Path);
    if Position = 0 then Position := Length(Path) + 1;
    Folder := Trim(Copy(Path, 1, Position - 1));
    Path := Copy(Path, Position + 1, Length(Path));
    if Folder = '' then Continue;
    Result := FileExists(AddBackslash(Folder) + 'pdflatex.exe') or FileExists(AddBackslash(Folder) + 'latexmk.exe')
      or FileExists(AddBackslash(Folder) + 'tectonic.exe');
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then Log(Format('Downloaded %s', [FileName]));
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

{ The downloads happen after the user confirmed the choices, right before the files are copied. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Anything: Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then Exit;

  DownloadPage.Clear;
  Anything := False;
  if WebView2Missing then
  begin
    DownloadPage.Add('{#WebView2Url}', 'MicrosoftEdgeWebview2Setup.exe', '');
    Anything := True;
  end;
  if WizardIsTaskSelected('model') then
  begin
    DownloadPage.Add('{#ModelUrl}', '{#ModelFile}', '');
    Anything := True;
  end;
  if not Anything then Exit;

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        Result := False
      else
        SuppressibleMsgBox(FmtMessage(CustomMessage('DownloadFailed'), [GetExceptionMessage]), mbInformation, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;
