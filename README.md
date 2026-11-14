# imagepixel-backup
Backup Desktop Software for your Data Windows and MAC

Imagepixel Backup is a simple desktop client for [restic](https://restic.net). You pick a
folder and it is backed up in the background, encrypted on your computer before it leaves it.
Backups can be opened as a read-only drive or restored into a folder.

## Status

Early development. The Windows version works end to end; macOS is planned and the user
interface is built with a toolkit that runs on both.

- Storage: Imagepixel Storage, or a folder such as a USB drive
- Back up folders hourly, daily at a set time, or on demand
- Back up OneDrive, Google Drive and iCloud Drive with one click; files that are only
  stored online are freed up again after they have been backed up
- Open all backups as a read-only drive (Windows, needs [WinFsp](https://winfsp.dev))
- Restore a folder from any earlier state

## Layout

| Path | Content |
|---|---|
| `src/ImagepixelBackup.Core` | Runs restic, stores settings and secrets, schedules backups |
| `src/ImagepixelBackup.App` | The desktop application (Avalonia) |
| `restic-overlay` | Files added to restic so that it can mount repositories on Windows |
| `tests` | Tests for the core and a walkthrough of the user interface |

## Building

Requirements: Git, Go, the .NET 10 SDK, and WinFsp to mount backups as a drive.
The setup program also needs Inno Setup 6.

```powershell
# builds bin\restic.exe: restic with the mount command for Windows
.\build-restic.ps1

# runs the application
dotnet run --project src/ImagepixelBackup.App

# runs the tests; pictures of every screen are saved to artifacts\screenshots
dotnet test

# builds artifacts\installer\ImagepixelBackup-Setup-<version>.exe
.\build-installer.ps1
```

The setup program brings everything a computer needs: the application with the .NET
runtime, restic, and the installer of WinFsp, which it runs if WinFsp is missing.

Upstream restic cannot mount repositories on Windows. `build-restic.ps1` fetches the pinned
restic release, copies the files from `restic-overlay` on top of it and builds the result.
The mounted volume rejects every operation that would change it, so a backup cannot be
damaged through the drive.

## License

Imagepixel Backup is licensed under the [BSD 2-Clause License](LICENSE), like restic.
The components it includes are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Backups are mounted with WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos,
<https://github.com/winfsp/winfsp>.
