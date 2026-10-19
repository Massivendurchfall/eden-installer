# Eden Installer

Richtet den Switch Emulator [Eden](https://eden-emu.dev) mit einem Klick ein und sortiert danach Spiele, Updates und DLCs von selbst in die richtigen Ordner.

**[Download: Eden-Installer.exe](../../releases/latest)**

## Installation

Einfach die exe starten und auf „Installieren“ klicken. Adminrechte braucht es nicht.

1. Eden v0.2.1 wird heruntergeladen und nach `%LOCALAPPDATA%\Programs\Eden` entpackt. Auf den Desktop kommt nur die Verknüpfung „Eden“.
2. Im Eden-Ordner entstehen `Games` und `Updates und DLCs`. Beide werden in Eden eingetragen, als Spieleverzeichnis und als externer Inhalt.
3. Eden startet einmal, legt seine Ordner an und wird wieder geschlossen.
4. Keys und Firmware 22.5.0 werden heruntergeladen und nach `%APPDATA%\eden` gelegt, genau so, wie Eden es selbst machen würde.

Der Ordner lässt sich vorher über „Ändern“ anpassen.

## Spiel hinzufügen

Ist Eden schon eingerichtet, öffnet der Installer direkt die Startansicht:

- **Spiel hinzufügen**: ZIP, RAR, 7z, NSP, NSZ, XCI oder XCZ auswählen, gern mehrere auf einmal. Mehrteilige Archive (`.part1.rar`, `.part2.rar` und so weiter) werden zusammen entpackt. Basisspiele kommen nach `Games`, Updates und DLCs nach `Updates und DLCs`.
- **Eden starten**, **Games öffnen**, **Updates öffnen**
- **Neu installieren**, falls mal etwas kaputt ist

Erkannt wird am Inhalt der Datei oder an der Title-ID im Dateinamen. Klappt beides nicht, fragt der Installer kurz nach. Einzelne NSP- oder XCI-Dateien werden verschoben. Archive bleiben liegen, am Ende fragt der Installer, ob sie in den Papierkorb sollen.

RAR und 7z entpackt der Installer mit 7-Zip, falls es installiert ist, sonst mit dem tar aus Windows. Für mehrteilige RAR-Archive sollte 7-Zip installiert sein ([7-zip.org](https://7-zip.org)).

Der Installer lädt keine Spiele herunter.

## Voraussetzungen

Windows 10 oder 11, 64 Bit. Das nötige .NET Framework 4.8 ist dort schon drin.

## Bei Problemen

Alles, was der Installer macht, steht in `%TEMP%\Eden-Installer.log`.

## Selbst bauen

Mit dem .NET SDK:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Das Ergebnis liegt in `dist\Eden-Installer.exe`.

Zum Testen ohne echte Installation gibt es einen Testmodus, der alle Pfade in einen Ordner umleitet:

```
Eden-Installer.exe --sandbox <ordner> [--fake-eden <exe>] [--import <datei>]
```
