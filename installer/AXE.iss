; AXE installer — builds release\AXE-Setup.exe (Inno Setup 6)
; Build with:  powershell -ExecutionPolicy Bypass -File build\build-release.ps1

#define AppName        "AXE"
#define AppVersion     "1.0.0"
#define AppPublisher   "Aniket Raj"
#define AppDescription "Private Floating Browser"
#define AppExe         "AXE.exe"
#define PublishDir     "..\artifacts\publish"
#define WebView2Setup  "redist\MicrosoftEdgeWebview2Setup.exe"
#define WebView2Guid   "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

[Setup]
AppId={{8C3F4E2A-6B1D-4F7A-9E25-5A0E7C9EA7E1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=© {#AppPublisher}
AppComments={#AppDescription}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=© {#AppPublisher}
VersionInfoDescription={#AppName} Setup — {#AppDescription}
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
; Windows 10 1809+ (capture exclusion itself needs Windows 10 2004 / build 19041+).
MinVersion=10.0.17763
OutputDir=..\release
OutputBaseFilename=AXE-Setup
SetupIconFile=..\src\AXE\Assets\axe.ico
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
WelcomeLabel2=This will install [name/ver] — a small, private floating browser — on your computer.%n%nIt is recommended that you close all other applications before continuing.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
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
           'AXE is installed and will show a link to download the runtime from Microsoft when it starts.',
           mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\AXE');
    if DirExists(DataDir) then
      if SuppressibleMsgBox('Also delete your AXE browsing data and settings?' + #13#10#13#10 +
                            '(cookies, site data, cache, settings and diagnostic logs in ' + DataDir + ')',
                            mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
