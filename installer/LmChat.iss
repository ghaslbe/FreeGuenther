; Inno-Setup-Skript für LM Chat
; Bauen:  ISCC.exe /DAppVersion=1.10.0 installer\LmChat.iss
;         (optional /DSourceDir=<Ordner mit LmChat.exe>, Standard: LmChat\publish)
; Erwartet die veröffentlichte Einzel-.exe (dotnet publish ...).
; Ergebnis: <SourceDir>\LmChat-Setup.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\LmChat\publish"
#endif

[Setup]
; Feste ID – nie ändern, sonst erkennt Windows Updates nicht als dieselbe App
AppId={{B36621E8-1137-47C7-8AE3-B413C50181DF}
AppName=LM Chat
AppVersion={#AppVersion}
AppVerName=LM Chat {#AppVersion}
AppPublisher=ghaslbe
AppPublisherURL=https://github.com/ghaslbe/lmstudioChatWindows
AppSupportURL=https://github.com/ghaslbe/lmstudioChatWindows
AppUpdatesURL=https://github.com/ghaslbe/lmstudioChatWindows/releases/latest
VersionInfoVersion={#AppVersion}
; Ohne Administratorrechte für den aktuellen Benutzer installieren (wahlweise für alle)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\LM Chat
DefaultGroupName=LM Chat
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\LmChat\app.ico
UninstallDisplayIcon={app}\LmChat.exe
UninstallDisplayName=LM Chat
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Läuft LM Chat noch, wird es zum Aktualisieren geschlossen
CloseApplications=yes
OutputDir={#SourceDir}
OutputBaseFilename=LmChat-Setup

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\LmChat.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LM Chat"; Filename: "{app}\LmChat.exe"
Name: "{autodesktop}\LM Chat"; Filename: "{app}\LmChat.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LmChat.exe"; Description: "{cm:LaunchProgram,LM Chat}"; Flags: nowait postinstall skipifsilent

; Hinweis: Chats und Einstellungen (%LOCALAPPDATA%\LmChat) bleiben bei der Deinstallation erhalten.
