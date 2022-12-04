#define AppName "SRT Host"
#define AppExeNamePrefix "SRTHost"
#define SuffixText32Bit "(32-bit)"
#define SuffixText64Bit "(64-bit)"
#define AppURL "https://www.SpeedRunTool.com/"
#define AppPublishDir "..\SRTHost\bin\Release\net7.0-windows\publish"
#define AppExe32Path AppPublishDir + "\" + AppExeNamePrefix + "32.exe"
#define AppExe64Path AppPublishDir + "\" + AppExeNamePrefix + "64.exe"

#ifndef AppCompany
#define AppCompany GetFileCompany(AppExe64Path)
#endif

#ifndef AppCopyright
#define AppCopyright GetFileCopyright(AppExe64Path)
#endif

#ifndef AppVersion
#define AppVersion GetFileProductVersion(AppExe64Path)
#endif

[Setup]
; NOTE: The value of AppId uniquely identifies this application. Do not use the same AppId value in installers for other applications.
; (To generate a new GUID, click Tools | Generate GUID inside the IDE.)
AppId={{B10F5521-C2F3-4A9D-AB05-1E0BF0A27AC1}
AppName={#AppName}
AppVersion={#AppVersion}
;AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppCompany}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
VersionInfoCompany={#AppCompany}
VersionInfoCopyright={#AppCopyright}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\{#AppCompany}\{#AppExeNamePrefix}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
CloseApplications=yes
RestartApplications=no
TimeStampsInUTC=yes
ArchitecturesAllowed=x86 x64
; Require Windows 7 SP1 or newer (minimum needed for .NET 7)
MinVersion=6.1sp1
OutputBaseFilename=SRTHostSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppExe32Path}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppExe64Path}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\appsettings.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#AppPublishDir}\appsettings.Production.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
Source: "{#AppPublishDir}\appsettings.Development.json"; DestDir: "{app}"; Flags: onlyifdoesntexist
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Dirs]
Name: "{app}\plugins"

[Icons]
Name: "{userprograms}\{#AppName} {#SuffixText32Bit}"; Filename: "{app}\{#AppExeNamePrefix}32.exe"
Name: "{userprograms}\{#AppName} {#SuffixText64Bit}"; Filename: "{app}\{#AppExeNamePrefix}64.exe"
Name: "{userdesktop}\{#AppName} {#SuffixText32Bit}"; Filename: "{app}\{#AppExeNamePrefix}32.exe"; Tasks: desktopicon
Name: "{userdesktop}\{#AppName} {#SuffixText64Bit}"; Filename: "{app}\{#AppExeNamePrefix}64.exe"; Tasks: desktopicon
