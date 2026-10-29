; Inno-Setup-Skript für FreeGuenther (früher „LM Chat“)
; Bauen:  ISCC.exe /DAppVersion=1.18.0 installer\FreeGuenther.iss
;         (optional /DSourceDir=<Ordner mit FreeGuenther.exe>, Standard: LmChat\publish)
; Erwartet die veröffentlichte Einzel-.exe (dotnet publish ...).
; Ergebnis: <SourceDir>\FreeGuenther-Setup.exe

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\LmChat\publish"
#endif

[Setup]
; Feste ID – nie ändern, sonst erkennt Windows Updates nicht als dieselbe App (gilt auch für Updates von „LM Chat“)
AppId={{B36621E8-1137-47C7-8AE3-B413C50181DF}
AppName=FreeGuenther
AppVersion={#AppVersion}
AppVerName=FreeGuenther {#AppVersion}
AppPublisher=ghaslbe
AppPublisherURL=https://github.com/ghaslbe/FreeGuenther
AppSupportURL=https://github.com/ghaslbe/FreeGuenther
AppUpdatesURL=https://github.com/ghaslbe/FreeGuenther/releases/latest
VersionInfoVersion={#AppVersion}
; Ohne Administratorrechte für den aktuellen Benutzer installieren (wahlweise für alle)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\FreeGuenther
DefaultGroupName=FreeGuenther
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\LmChat\app.ico
UninstallDisplayIcon={app}\FreeGuenther.exe
UninstallDisplayName=FreeGuenther
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Läuft FreeGuenther (oder noch das alte LM Chat), wird es zum Aktualisieren geschlossen
CloseApplications=yes
OutputDir={#SourceDir}
OutputBaseFilename=FreeGuenther-Setup

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Update von „LM Chat“: alte exe und alte Verknüpfungen entfernen
Type: files; Name: "{app}\LmChat.exe"
Type: files; Name: "{autoprograms}\LM Chat.lnk"
Type: files; Name: "{autodesktop}\LM Chat.lnk"

[Files]
Source: "{#SourceDir}\FreeGuenther.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\FreeGuenther"; Filename: "{app}\FreeGuenther.exe"
Name: "{autodesktop}\FreeGuenther"; Filename: "{app}\FreeGuenther.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\FreeGuenther.exe"; Description: "{cm:LaunchProgram,FreeGuenther}"; Flags: nowait postinstall skipifsilent

; Hinweis: Chats und Einstellungen (%LOCALAPPDATA%\FreeGuenther) bleiben bei der Deinstallation erhalten.
; Beim ersten Start übernimmt FreeGuenther den alten Ordner %LOCALAPPDATA%\LmChat.
