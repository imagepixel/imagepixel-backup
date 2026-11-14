# imagepixel-backup
Backup Desktop Software for your Data Windows and MAC

Imagepixel Backup is a desktop client for [restic](https://restic.net) that keeps backing
up simple. You choose what to back up and where to, and it runs in the background from
then on. Your data is encrypted on your computer before it leaves it.

## What it does

**Backing up**

- Pick folders, or OneDrive, Google Drive and iCloud Drive with one click
- Back up every hour, every day at a set time, or only on demand
- Store the backup in Imagepixel Storage, or in a folder such as a USB drive
- Files of a cloud service that are only stored online are downloaded for the backup and
  freed up again once they are saved, so the disk does not fill up. Files set to always
  stay on the computer are left alone.

**Getting data back**

- The first screen asks whether you want to back up or to restore. Restoring only needs
  the place of the backup and its password, for example on a newly set up computer.
- Restore a folder from any earlier state
- Open all backups as a read-only drive and copy single files out of it. Nothing can be
  changed through the drive, so a backup cannot be damaged by browsing it.

**Keeping an eye on it**

- With Imagepixel Storage the overview shows how much of the booked storage is used and
  warns from 80 % on. Backups keep running when the storage is full.
- The application stays in the notification area for scheduled backups.

## Your password

The backup is encrypted with a password that you choose. It is not sent to Imagepixel and
nobody else knows it. If you lose it, nobody can read the backup any more, Imagepixel
included, and it cannot be reset. Write it down and keep it somewhere safe.

## Status

Early development, version 0.1.0. The Windows version works end to end. macOS is planned,
and the user interface is built with a toolkit that runs on both. There is no signed
release yet; to try the application, build it as described below.

## Building

Requirements: Windows 10 or 11 (64 bit), Git, Go and the .NET 10 SDK.
[WinFsp](https://winfsp.dev) is needed to open backups as a drive, and Inno Setup 6 to
build the setup program.

```powershell
# builds bin\restic.exe: restic with the mount command for Windows
.\build-restic.ps1

# runs the application
dotnet run --project src/ImagepixelBackup.App

# runs the tests
dotnet test

# builds artifacts\installer\ImagepixelBackup-Setup-<version>.exe
.\build-installer.ps1
```

The setup program brings everything a computer needs: the application with the .NET
runtime, restic, and the installer of WinFsp, which it runs if WinFsp is missing.

The tests run restic against repositories in the temp folder and walk through the user
interface without showing a window. Pictures of every screen are saved to
`artifacts\screenshots`.

## Layout

| Path | Content |
|---|---|
| `src/ImagepixelBackup.Core` | Runs restic, stores settings and secrets, schedules backups, reads the storage status |
| `src/ImagepixelBackup.App` | The desktop application (Avalonia) |
| `tools/ImagepixelBackup.Admin` | `ipb-admin`: creates setup codes and writes the storage status file |
| `restic-overlay` | Files added to restic so that it can mount repositories on Windows |
| `installer` | The script of the setup program |
| `docs` | [How the application learns how much storage is booked and used](docs/storage-status.md) |
| `tests` | Tests for the core and a walkthrough of the user interface |

## Opening a backup as a drive

Upstream restic cannot mount repositories on Windows. `build-restic.ps1` fetches the pinned
restic release, copies the files from `restic-overlay` on top of it and builds the result.
The volume rejects every operation that would change it. When the application ends for
any reason, the drive is unmounted and its lock in the repository is released.

## Contributing

This repository does not take contributions from outside: pull requests are limited to
collaborators and issues are turned off. You are free to use and adapt the code under the
terms of the license.

## License

Imagepixel Backup is licensed under the [BSD 2-Clause License](LICENSE), like restic.
The components it includes are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Backups are mounted with WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos,
<https://github.com/winfsp/winfsp>.
