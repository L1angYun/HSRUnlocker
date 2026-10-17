#ifndef AppVersion
  #error Pass AppVersion from build-installer.ps1.
#endif
#ifndef AppFileVersion
  #error Pass AppFileVersion from build-installer.ps1.
#endif
#ifndef PublishDir
  #error Pass PublishDir from build-installer.ps1.
#endif

[Setup]
AppId={{928328BA-7DAB-4F7F-9E3D-CC87061C2460}
AppName=HSRUnlocker
AppVersion={#AppVersion}
AppVerName=HSRUnlocker {#AppVersion}
AppPublisher=L1angYun
AppPublisherURL=https://github.com/L1angYun/HSRUnlocker
AppSupportURL=https://github.com/L1angYun/HSRUnlocker/issues
AppUpdatesURL=https://github.com/L1angYun/HSRUnlocker/releases
DefaultDirName={autopf}\HSRUnlocker
DefaultGroupName=HSRUnlocker
UsePreviousAppDir=yes
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22621
AppMutex=Global\HSRUnlocker.Running,HSRUnlocker
CloseApplications=no
RestartApplications=no
UninstallDisplayName=HSRUnlocker
UninstallDisplayIcon={app}\HSRUnlocker.exe
SetupIconFile={#PublishDir}\Assets\AppIcon.ico
LicenseFile={#PublishDir}\LICENSE
VersionInfoVersion={#AppFileVersion}
VersionInfoProductVersion={#AppFileVersion}
OutputBaseFilename=HSRUnlocker-{#AppVersion}-win-x64-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\HSRUnlocker"; Filename: "{app}\HSRUnlocker.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,HSRUnlocker}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\HSRUnlocker"; Filename: "{app}\HSRUnlocker.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\HSRUnlocker.exe"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser skipifdoesntexist; Check: IsUpdaterInstall

[Code]
function IsUpdaterInstall: Boolean;
begin
  Result := ExpandConstant('{param:HSRUPDATE|0}') = '1';
end;
