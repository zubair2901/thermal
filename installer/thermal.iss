; Inno Setup script for Thermal. Built by .github/workflows/release.yml:
;   iscc /DAppVersion=1.2.3 installer\thermal.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6C1E2F0B-6A1D-4F4B-9C55-0E7E3B8D2A41}
AppName=Thermal
AppVersion={#AppVersion}
AppPublisher=Thermal contributors
AppPublisherURL=https://github.com/zubair2901/thermal
DefaultDirName={autopf}\Thermal
DefaultGroupName=Thermal
DisableProgramGroupPage=yes
; Thermal reads fan and CPU sensors, which needs administrator rights anyway.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Thermal-Setup-{#AppVersion}
SetupIconFile=..\app\thermal.ico
UninstallDisplayIcon={app}\Thermal.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\LICENSE

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\Thermal.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Thermal"; Filename: "{app}\Thermal.exe"
Name: "{autodesktop}\Thermal"; Filename: "{app}\Thermal.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Thermal.exe"; Description: "Open Thermal"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the "Start with Windows" logon task if the user turned it on.
Filename: "schtasks.exe"; Parameters: "/Delete /F /TN Thermal"; Flags: runhidden; RunOnceId: "RemoveLogonTask"
Filename: "taskkill.exe"; Parameters: "/F /IM Thermal.exe"; Flags: runhidden; RunOnceId: "StopThermal"
