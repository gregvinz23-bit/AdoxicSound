; Adoxic Sound — Inno Setup script (per-user, no admin required)
; Compile with Inno Setup 6: iscc AdoxicSound.iss

#define MyAppName "Adoxic Sound"
#define MyAppVersion "1.6"
#define MyAppPublisher "Mantraix Software Solutions"
#define MyAppExeName "AdoxicSound.exe"

[Setup]
AppId={{8E2B4F1A-7C3D-4A9E-B5F6-1D2C3B4A5967}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\AdoxicSound
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
OutputBaseFilename=AdoxicSound-Setup-v{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile=LICENSE.txt
UninstallDisplayName={#MyAppName}

[CustomMessages]
WelcomeLabel1=Welcome to Adoxic Sound
WelcomeLabel2=Thanks for trying Adoxic Sound — a portable-minded studio for live internet radio, crafted by Greg Vincent for Mantraix Software Solutions.%n%nThis wizard installs the program on your PC in under a minute. Your stations and settings live in your own profile, and uninstalling removes everything cleanly.%n%nClick Next to continue.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; Flags: unchecked
Name: "startupicon"; Description: "Start with &Windows"; Flags: unchecked

[Files]
Source: "publish\AdoxicSound.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\libvlc\*"; DestDir: "{app}\libvlc"; Flags: ignoreversion recursesubdirs
Source: "publish\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "Instruction.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "Adoxic Sound"; ValueData: """{app}\{#MyAppExeName}"""; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Adoxic Sound"; \
  Flags: nowait postinstall skipifsilent
