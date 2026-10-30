# FreeGuenther für den Mac

Native macOS-App (AppKit) für Apple Silicon. Sie nutzt dieselbe Logik wie die Windows-Version: `Providers`, `ChatStore`, `Plugins`, `PromptGuard`, `DocumentReader`, `AutomationJob`, `MailJob`, `LlmSettings` und `L` sind aus `../LmChat` verlinkt, ebenso Sprachdateien und Plugins. Neu ist nur die Oberfläche in `UI/` (Aufteilung wie `MainWindow.*.cs` unter Windows) und `Core/`.

## Bauen

Benötigt werden Xcode, das offizielle .NET SDK und der passende macOS-Workload. Das SDK von Homebrew kennt den Workload nicht.

```
curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
bash dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet --architecture arm64 --no-path
DOTNET_ROOT=~/.dotnet ~/.dotnet/dotnet workload install macos --version 10.0.401
./build.sh
```

Ergebnis: `bin/Release/net10.0-macos/osx-arm64/FreeGuenther.app`, die .NET-Laufzeit steckt in der App. Auf dem Zielrechner muss nichts installiert sein.

Workload-Stand `10.0.401` enthält das macOS-26.5-SDK und passt zu Xcode 26.5/26.6. Neuere Workload-Stände verlangen Xcode 27.

## Unterschiede zur Windows-Version

| Thema | Mac |
|---|---|
| Daten | `~/Library/Application Support/FreeGuenther/chats.db` |
| Geheimnisse | AES-verschlüsselt in der Datenbank, Schlüssel im Schlüsselbund. Aus Windows übernommene Werte müssen neu eingetragen werden. |
| Tasten | ⌘V statt Strg+V, ⌘N neuer Chat, ⌘, Einstellungen |
| Chat löschen | Rechtsklick, Entf-Taste oder Papierkorb oben rechts im Chat |
| Datei-Automatik | macOS sperrt Dateien beim Schreiben nicht; „fertig geschrieben“ heißt hier: seit 2 Sekunden unverändert |

Ohne Signatur und Notarisierung blockiert macOS heruntergeladene Apps beim ersten Start.

## Release

GitHub Actions baut die App bei jedem Push auf `main` auf dem Runner `macos-26` und legt `FreeGuenther-mac-arm64.zip` ins Release `v<Version>`. Darin stehen `FreeGuenther.app` und die mehrsprachige Anleitung `Installation.txt` im Ordner `FreeGuenther`. Ändert sich die Anleitung, auch das Kapitel im Haupt-README anpassen. Die Versionen von Xcode, SDK und Workload sind in `.github/workflows/release.yml` festgelegt und müssen zueinander passen.

## Testen

Screenshots fürs README: Im Debug-Build speichert `FG_SHOT_DIR=<Ordner>` Seitenleiste und Seiten einzeln, `tools/mac-screenshots.py` setzt sie zusammen.

Im Debug-Build fährt `FG_TEST_DIR=<Ordner>` alle Seiten ab, sendet eine Nachricht und speichert Bilder der Fenster dorthin (`UI/MainWindow.Test.cs`). Mit `FREEGUENTHER_DB=<Datei>` läuft die App gegen eine Testdatenbank. Bilder der Seitenleiste und scrollender Seiten fehlen im Gesamtbild, weil macOS 26 sie beim Rendern außerhalb des Bildschirms auslässt; die Seiten werden deshalb zusätzlich einzeln gespeichert.
