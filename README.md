# Eden Installer

Sets up the Switch emulator [Eden](https://eden-emu.dev) in one click, then sorts games, updates and DLCs into the right folders on its own.

**[Download: Eden-Installer.exe](../../releases/latest)**

The installer speaks German on German Windows and English everywhere else.

## Installation

Start the exe and click "Install". No admin rights needed.

1. The latest Eden version is downloaded and unpacked to `%LOCALAPPDATA%\Programs\Eden`. Only the "Eden" shortcut goes on the desktop.
2. Two folders are created in the Eden folder, `Games` and `Updates and DLCs` (`Updates und DLCs` on German Windows). Both are registered in Eden, as game folder and as external content.
3. Eden starts once, creates its folders and is closed again.
4. Keys and firmware are downloaded and placed in `%APPDATA%\eden`, exactly the way Eden would do it itself.

The folder can be changed beforehand with "Change".

### Always the latest versions

At startup the installer checks what is available: Eden on [git.eden-emu.dev](https://git.eden-emu.dev/eden-emu/eden/releases), the firmware at [THZoria/NX_Firmware](https://github.com/THZoria/NX_Firmware/releases), the keys on prodkeys.net. The versions it finds are shown at the top of the window.

Firmware and keys must have the same version, otherwise nothing starts. If a new firmware is out before matching keys exist, the installer takes the newest firmware that already has matching keys. As soon as the keys are there, it switches on its own.

If the lookup is not possible, for example without internet, it uses a built-in set that is known to match.

## Add game

Once Eden is set up, the installer opens straight on the home screen:

- **Add game**: pick ZIP, RAR, 7z, NSP, NSZ, XCI or XCZ files, several at once if you like. Multi-part archives (`.part1.rar`, `.part2.rar` and so on) are unpacked together. Base games go to `Games`, updates and DLCs to `Updates and DLCs`.
- **Start Eden**, **Open Games**, **Open Updates**
- **Reinstall**, in case something breaks or there is something new
- **Uninstall**: removes Eden and the desktop shortcut. Two checkboxes decide whether your games and your saves and settings stay, both are checked by default.

If there is a newer Eden version or a newer matching pair of firmware and keys, the home screen says so. "Reinstall" brings it in, your games stay where they are.

Files are recognized by their content or by the title ID in the file name. If neither works, the installer asks. Single NSP or XCI files are moved. Archives stay where they are, at the end the installer asks whether to move them to the recycle bin.

RAR and 7z, multi-part archives included, are unpacked with the 7-Zip that ships inside the installer. Nothing extra to install.

The installer does not download any games.

## Updating the installer

At startup the installer checks this repository for a newer release of itself. If there is one, a note with an "Update" button appears at the top. It downloads the new exe, replaces itself and restarts. Eden and your games are not touched.

## Requirements

Windows 10 or 11, 64 bit. The .NET Framework 4.8 it needs is already part of it.

## Troubleshooting

Everything the installer does is written to `%TEMP%\Eden-Installer.log`.

## Building

With the .NET SDK:

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

The result is `dist\Eden-Installer.exe`.

For testing without a real installation there is a test mode that redirects all paths into one folder:

```
Eden-Installer.exe --sandbox <folder> [--fake-eden <exe>] [--import <file>] [--uninstall keep|all] [--lang de|en]
```

## Third-party software

The exe contains [7-Zip](https://7-zip.org) 26.04 by Igor Pavlov, licensed under the GNU LGPL, some parts with the unRAR license restriction or BSD licenses. The full license text is in [7zip/License.txt](7zip/License.txt).
