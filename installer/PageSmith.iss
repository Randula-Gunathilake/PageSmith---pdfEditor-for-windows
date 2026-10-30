; Inno Setup 6 script for the PageSmith installer.
; Build it with scripts\build-installer.ps1, which publishes the app first and passes the version,
; runtime and folders below. See TODO.md, section 5.5.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Runtime
  #define Runtime "win-x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\" + Runtime
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#if Runtime == "win-x64"
  #define Arch "x64compatible"
#elif Runtime == "win-arm64"
  #define Arch "arm64"
#elif Runtime == "win-x86"
  #define Arch "x86compatible"
#else
  #error Runtime must be win-x64, win-arm64 or win-x86
#endif

#define AppExe "PageSmith.exe"
#define ProgId "PageSmith.Document"
#define RepoUrl "https://github.com/Randula-Gunathilake/PageSmith---pdfEditor-for-windows"

[Setup]
; The AppId ties upgrades and the uninstaller together; never change it.
AppId={{97DF6453-A6C2-45F2-9420-A7AF34600865}
AppName=PageSmith
AppVersion={#AppVersion}
AppPublisher=PageSmith contributors
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
AppCopyright=Copyright (C) 2026 PageSmith contributors
VersionInfoVersion={#AppVersion}
VersionInfoDescription=PageSmith Setup
; "Only for me" needs no admin rights and installs to %LOCALAPPDATA%\Programs; "for all users" goes to Program Files.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\PageSmith
DisableProgramGroupPage=yes
ArchitecturesAllowed={#Arch}
#if Runtime != "win-x86"
ArchitecturesInstallIn64BitMode={#Arch}
#endif
MinVersion=10.0.19041
WizardStyle=modern dynamic windows11
WizardImageFile=WizardImage.png
WizardSmallImageFile=WizardSmallImage.png
SetupIconFile=..\src\PageSmith.App\Assets\PageSmith.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=PageSmith
; Lets Explorer pick up the "Open with" entry straight away.
ChangesAssociations=yes
; Multithreaded lzma2/max compiles in under a minute; ultra64 took 19 minutes for a 2% smaller setup.
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=8
OutputDir={#OutputDir}
OutputBaseFilename=PageSmith-Setup-{#AppVersion}-{#Runtime}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.OpenWithGroup=Opening PDFs:
english.OpenWithPdf=Add PageSmith to "Open with" for PDF files
english.RemoveUserData=Also delete your PageSmith settings, saved API keys and workflows?%n%nChoose No to keep them for a later install.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "openwith"; Description: "{cm:OpenWithPdf}"; GroupDescription: "{cm:OpenWithGroup}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PageSmith"; Filename: "{app}\{#AppExe}"; Comment: "Read, edit, convert, sign and protect PDFs"
Name: "{autodesktop}\PageSmith"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Lets Win+R and other programs start "PageSmith".
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey
; The same keys Settings > Opening PDFs writes (PdfAppRegistration), so PageSmith shows up in
; "Open with" and Settings > Apps > Default apps. Windows leaves choosing the default to the user.
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: ""; ValueData: "PDF Document"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "PDF Document"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\Application"; ValueType: string; ValueName: "ApplicationName"; ValueData: "PageSmith"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\{#ProgId}\Application"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\.pdf\OpenWithProgids"; ValueType: string; ValueName: "{#ProgId}"; ValueData: ""; Flags: uninsdeletevalue; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "PageSmith"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: openwith
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".pdf"; ValueData: ""; Tasks: openwith
Root: HKA; Subkey: "Software\PageSmith"; Flags: uninsdeletekeyifempty; Tasks: openwith
Root: HKA; Subkey: "Software\PageSmith\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "PageSmith"; Flags: uninsdeletekey; Tasks: openwith
Root: HKA; Subkey: "Software\PageSmith\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Read, edit, convert, sign and protect PDFs on your PC, offline."; Tasks: openwith
Root: HKA; Subkey: "Software\PageSmith\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: """{app}\{#AppExe}"",0"; Tasks: openwith
Root: HKA; Subkey: "Software\PageSmith\Capabilities\FileAssociations"; ValueType: string; ValueName: ".pdf"; ValueData: "{#ProgId}"; Tasks: openwith
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "PageSmith"; ValueData: "Software\PageSmith\Capabilities"; Flags: uninsdeletevalue; Tasks: openwith

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,PageSmith}"; Flags: nowait postinstall skipifsilent

[Code]
// Settings > Opening PDFs can register this copy for the current user after setup; undo that too,
// but only when it points here, so another PageSmith copy's registration stays.
procedure RemoveUserOpenWith;
var
  Command: String;
begin
  if not RegQueryStringValue(HKCU, 'Software\Classes\{#ProgId}\shell\open\command', '', Command) then Exit;
  if CompareText(Command, '"' + ExpandConstant('{app}\{#AppExe}') + '" "%1"') <> 0 then Exit;
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\{#ProgId}');
  RegDeleteValue(HKCU, 'Software\Classes\.pdf\OpenWithProgids', '{#ProgId}');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\Classes\Applications\{#AppExe}');
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\PageSmith\Capabilities');
  RegDeleteKeyIfEmpty(HKCU, 'Software\PageSmith');
  RegDeleteValue(HKCU, 'Software\RegisteredApplications', 'PageSmith');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  RemoveUserOpenWith;
  // Settings, workflows and the HTML to PDF browser profile belong to the user; ask before deleting them.
  if not UninstallSilent and (SuppressibleMsgBox(CustomMessage('RemoveUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
  begin
    DelTree(ExpandConstant('{userappdata}\PageSmith'), True, True, True);
    DelTree(ExpandConstant('{localappdata}\PageSmith'), True, True, True);
  end;
end;
