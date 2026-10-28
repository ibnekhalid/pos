; Inno Setup 6 script for the Restaurant POS Windows desktop app (the unpackaged MAUI publish folder).
; Build it with installer\build-installer.ps1, which publishes the app and passes the defines below:
;
;   ISCC.exe /DAppVersion=0.3 /DPublishDir=..\installer\publish installer\Restaurant.Desktop.iss
;
; Output: installer\output\Restaurant-POS-Setup-<version>.exe. Upload it to /root/downloads/restaurant on
; the VM and set the RP_DESKTOP_* repo variables so running apps show the update notice
; (docs/DEPLOYMENT.md, "Desktop app releases").
;
; Installs per user by default (no admin prompt for cashiers updating the till); the first page offers
; "all users" for a shared counter PC. App data (session, device token, preferences) lives in the user's
; AppData and is never touched by an update or an uninstall.

#ifndef AppVersion
  #error AppVersion is required, e.g. /DAppVersion=0.3 (build-installer.ps1 reads it from the .csproj)
#endif
#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "publish"
#endif

#define AppName "Restaurant POS"
#define AppExe "Restaurant.Desktop.exe"
; Microsoft Edge WebView2 Runtime (the Blazor WebView needs it). Windows 11 has it; older Windows 10
; may not. Put the Evergreen bootstrapper here to install it silently when missing.
; (Its licence allows redistribution; it isn't committed: download it from Microsoft's WebView2 page.)
#define WebView2Bootstrapper AddBackslash(SourcePath) + "redist\MicrosoftEdgeWebview2Setup.exe"
#ifexist WebView2Bootstrapper
  #define WebView2Bundled
#endif

[Setup]
; Never change AppId: it is how an update finds the installed copy.
AppId={{0F329E2C-A86E-4C39-AF31-A0F5DDEBC4B3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809, the app's TargetPlatformMinVersion.
MinVersion=10.0.17763
OutputDir=output
OutputBaseFilename=Restaurant-POS-Setup-{#AppVersion}
#ifexist PublishDir + "\appicon.ico"
SetupIconFile={#PublishDir}\appicon.ico
#endif
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; An update closes the running app (Restart Manager) instead of failing on locked files.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Static web assets are fingerprinted, so old copies would pile up across updates.
Type: filesandordirs; Name: "{app}\wwwroot"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef WebView2Bundled
Source: "{#WebView2Bootstrapper}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not WebView2Installed
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
#ifdef WebView2Bundled
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2 Runtime..."; Check: not WebView2Installed
#endif
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  WebView2ClientKey = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

{ The runtime registers its version ("pv") per machine or per user; "0.0.0.0" means removed. }
function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\' + WebView2ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKLM, 'SOFTWARE\' + WebView2ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\' + WebView2ClientKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

procedure CurPageChanged(CurPageID: Integer);
begin
#ifndef WebView2Bundled
  if (CurPageID = wpReady) and not WebView2Installed then
    MsgBox('This PC does not have the Microsoft Edge WebView2 Runtime, which Restaurant POS needs.' + #13#10 +
      'Install it from https://developer.microsoft.com/microsoft-edge/webview2/ (Evergreen) before starting the app.',
      mbInformation, MB_OK);
#endif
end;
