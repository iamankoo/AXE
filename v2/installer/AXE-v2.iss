; AXE v2 installer - builds release-v2\AXE-v2-Setup.exe (Inno Setup 6)
; Separate from AXE v1: its own AppId, install directory, Start Menu entry and data directory. Installing or
; uninstalling it never touches v1.
; Build with:  powershell -ExecutionPolicy Bypass -File build\build-release-v2.ps1 -ServerConfig <production server.json>

#define AppName        "AXE v2"
#define AppVersion     "2.0.0"
#define AppPublisher   "Aniket Raj"
#define AppDescription "Private Floating Browser"
#define AppExe         "AXE-v2.exe"
#define PublishDir     "..\artifacts-v2\publish"
#define WebView2Setup  "redist\MicrosoftEdgeWebview2Setup.exe"
#define WebView2Guid   "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

[Setup]
; A different AppId from AXE v1 ({8C3F4E2A-...}) so the two never upgrade or uninstall each other.
AppId={{5D2B7C14-93E8-4A6F-B1C0-7E4A2F9D6B38}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=(c) {#AppPublisher}
AppComments={#AppDescription}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=(c) {#AppPublisher}
VersionInfoDescription={#AppName} Setup - {#AppDescription}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
; Per-user install by default (no admin prompt); the user may choose an all-users install.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\release-v2
OutputBaseFilename=AXE-v2-Setup
SetupIconFile=..\src\AXEv2\Assets\axe.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=assets\wizard-large.bmp,assets\wizard-large@2x.bmp
WizardSmallImageFile=assets\wizard-small.bmp,assets\wizard-small@2x.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] - a small, private floating browser - on your computer. It is installed separately from AXE v1 and does not touch it.%n%nIt is recommended that you close all other applications before continuing.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Only the published application. No server secrets, no development configuration, no per-PC identity:
; every installation creates its own device key the first time it runs.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#WebView2Setup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppDescription}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{#AppDescription}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
    StatusMsg: "Installing Microsoft Edge WebView2 Runtime (required to display websites)..."; \
    Flags: waituntilterminated; Check: NeedsWebView2
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{#WebView2Guid}', 'pv', Version)
      and (Version <> '') and (Version <> '0.0.0.0'))
    or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{#WebView2Guid}', 'pv', Version)
      and (Version <> '') and (Version <> '0.0.0.0'));
end;

function NeedsWebView2(): Boolean;
begin
  Result := not WebView2Installed();
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and NeedsWebView2() then
    MsgBox('The Microsoft Edge WebView2 Runtime could not be installed automatically (an internet connection is required).' + #13#10#13#10 +
           'AXE v2 is installed and will show a link to download the runtime from Microsoft when it starts.',
           mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // Upgrades keep this folder, so the saved authorization and device identity survive. Only an explicit "Yes"
    // here removes them. AXE v1 data ({localappdata}\AXE) is never touched.
    DataDir := ExpandConstant('{localappdata}\AXE v2');
    if DirExists(DataDir) then
      if SuppressibleMsgBox('Also delete your AXE v2 browsing data, settings, saved authorization and this PC''s device identity?' + #13#10#13#10 +
                            '(in ' + DataDir + ')',
                            mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
