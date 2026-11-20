# imagepixel-backup
Backup Desktop Software for your Data Windows and MAC

Imagepixel Backup is a desktop client for [restic](https://restic.net) that keeps backing
up simple. You choose what to back up and where to, and it runs in the background from
then on. Your data is encrypted on your computer before it leaves it.

## What it does

**Backing up**

- Pick folders, or on Windows and macOS OneDrive, Google Drive and iCloud Drive with one
  click
- Back up every hour, every day at a set time, or only on demand
- Store the backup in Imagepixel Storage, or in a folder such as a USB drive
- Connect to Imagepixel Storage with a setup code, or by
  [pairing the computer with the customer portal](docs/device-pairing.md): the
  application shows a code, the customer enters it in the portal, and the computer gets
  access data of its own, encrypted on the way for it alone and limited to its own folder
  in the storage
- Give each folder its own [destination](docs/storage-folders.md) inside the storage, so
  that several computers share one storage without their backups getting mixed up, and
  change it later. The backups at the previous destination are kept, or deleted after 7,
  30 or 90 days if you say so.
- On Windows, files of a cloud service that are only stored online are downloaded for the
  backup and freed up again once they are saved, so the disk does not fill up. Files set to
  always stay on the computer are left alone.

**Getting data back**

- The first screen asks whether you want to back up or to restore. Restoring only needs
  the place of the backup and its password, for example on a newly set up computer. The
  backups a storage holds are offered to choose from.
- Restore a folder from any earlier state
- On Windows and Linux, open all backups as a read-only drive and copy single files out of
  it. Nothing can be changed through the drive, so a backup cannot be damaged by browsing it.
- Open the storage the way it lies there, read-only: the encrypted files of the backup,
  to see for yourself that nothing in it can be read without your password (Windows and
  Linux, through [rclone](https://rclone.org))

**Keeping an eye on it**

- With Imagepixel Storage the overview shows how much of the booked storage is used and
  warns from 80 % on. Backups keep running when the storage is full.
- The application stays in the notification area, on macOS in the menu bar, for scheduled
  backups. On macOS and Linux it offers to start at login; on Windows the setup program
  takes care of that.

A guide for customers, in German, is in the
[wiki](https://github.com/imagepixel/imagepixel-backup/wiki).

## Your password

The backup is encrypted with a password that you choose. It is not sent to Imagepixel and
nobody else knows it. If you lose it, nobody can read the backup any more, Imagepixel
included, and it cannot be reset. Write it down and keep it somewhere safe.

## No warranty

Imagepixel Backup is free, open-source software and comes without warranty. You use it at
your own risk. Imagepixel is liable for defects of the program and for lost data only
where German law does not allow liability to be excluded, above all in cases of intent
and gross negligence. No storage is safe from every failure, the one of Imagepixel
included: do not rely on a single backup for important data, and try from time to time
whether files can be restored.

The full notes on use and liability, in German, are in
[NUTZUNGSHINWEISE.md](NUTZUNGSHINWEISE.md). The application shows a short form of them
once, at its first start, and asks to confirm it. They come with every download.

## Status

Early development, version 0.1.9. The
[pre-release](https://github.com/imagepixel/imagepixel-backup/releases) has downloads for
Windows, macOS and Linux. None of them is signed yet.

The Windows version works end to end and has a setup program.

The Linux version is new. It is packed for Ubuntu and for CachyOS, keeps its secrets in
the keyring of the desktop and opens backups as a drive. It does not offer OneDrive,
Google Drive and iCloud Drive as sources, because these services have no program for
Linux. It is tested by the
[workflow](.github/workflows/linux.yml) and in containers; nobody has worked with it on a
desktop yet.

The macOS version runs on macOS 12 and later, on Macs with an Apple chip and with an Intel
chip. The [workflow](.github/workflows/macos.yml) builds and tests it on a Mac of GitHub
with a current macOS; by hand it has been worked with on one Intel Mac with macOS 12 so
far. .NET and Avalonia, which the application is built on, are tested by their makers on
newer versions of macOS only, so macOS 12 and 13 are covered by that one test. It backs up
and restores like the Windows version and keeps its secrets in the keychain. Two things
are missing: it cannot open backups as a drive, and it does not free up files of a cloud
service again that were downloaded for the backup. Without a certificate of an Apple
developer account macOS asks for confirmation in the system settings before it opens the
application for the first time.

## Building

### Windows

Requirements: Windows 10 or 11 (64 bit), Git, Go and the .NET 10 SDK.
[WinFsp](https://winfsp.dev) is needed to open backups as a drive, and Inno Setup 6 to
build the setup program.

```powershell
# builds bin\restic.exe: restic with the mount command for Windows
.\build-restic.ps1

# fetches bin\rclone.exe, for the encrypted view of the storage
.\get-rclone.ps1

# runs the application
dotnet run --project src/ImagepixelBackup.App

# runs the tests
dotnet test

# builds artifacts\installer\ImagepixelBackup-Setup-<version>.exe
.\build-installer.ps1

# the same, offering pairing with the customer portal named in the file
.\build-installer.ps1 -PortalSettings <path>\portal.json
```

The setup program brings everything a computer needs: the application with the .NET
runtime, restic, rclone, and the installer of WinFsp, which it runs if WinFsp is missing.

### macOS

Requirements for building: macOS 14 or later, Git, Go and the .NET 10 SDK. The application
that comes out of it runs on macOS 12 and later.

```bash
# builds restic for Macs with an Apple chip and with an Intel chip
./build-restic.sh

# runs the application
dotnet run --project src/ImagepixelBackup.App

# runs the tests
dotnet test

# builds artifacts/macos/ImagepixelBackup-<version>-mac-arm64.dmg and -mac-x64.dmg
./build-macos.sh

# takes the application out of its disk image and checks that it starts
installer/macos/smoke-test.sh
```

A disk image holds the application with the .NET runtime and restic. The application
carries an ad-hoc signature only. The [workflow](.github/workflows/macos.yml) runs these
steps on a Mac of GitHub with every change and keeps the disk images.

### Linux

Requirements: a 64 bit Linux with Git, Go and the .NET 10 SDK. The tests of the keyring
need `dbus`, `gnome-keyring` and `secret-tool`, the tests of the drive `fuse3`. The
packages are built with `dpkg-deb`, `bsdtar` and `zstd`.

```bash
# builds restic
./build-restic.sh

# fetches rclone, for the encrypted view of the storage
./get-rclone.sh

# runs the application
dotnet run --project src/ImagepixelBackup.App

# runs the tests with a session bus and an unlocked keyring
tests/run-on-linux.sh

# builds artifacts/linux/imagepixel-backup_<version>_amd64.deb for Ubuntu
# and artifacts/linux/imagepixel-backup-<version>-1-x86_64.pkg.tar.zst for CachyOS
./build-linux.sh

# installs a package on a fresh system and checks that the application starts
docker run --rm --cap-add IPC_LOCK -v "$PWD:/src" ubuntu:rolling \
    bash /src/installer/linux/smoke-test.sh /src/artifacts/linux/imagepixel-backup_0.1.9_amd64.deb
```

Both packages put the application with the .NET runtime and restic into
`/opt/imagepixel-backup` and add an entry to the menu. They are installed with
`sudo apt install ./imagepixel-backup_<version>_amd64.deb` on Ubuntu and with
`sudo pacman -U imagepixel-backup-<version>-1-x86_64.pkg.tar.zst` on CachyOS, which also
fetches what the application needs from the system: `secret-tool` for the keyring and
FUSE for the drive. The [workflow](.github/workflows/linux.yml) runs these steps on
GitHub with every change.

### Tests

The tests run restic against repositories in the temp folder and walk through the user
interface without showing a window. Pictures of every screen are saved to
`artifacts/screenshots`.

## Layout

| Path | Content |
|---|---|
| `src/ImagepixelBackup.Core` | Runs restic, stores settings and secrets, schedules backups, reads the storage status, pairs with the customer portal |
| `src/ImagepixelBackup.App` | The desktop application (Avalonia) |
| `tools/ImagepixelBackup.Admin` | `ipb-admin`: creates setup codes and writes the storage status file |
| `restic-overlay` | Files added to restic so that it can mount repositories on Windows |
| `installer` | The script of the setup program for Windows, and what the disk image for macOS and the packages for Linux are made of |
| `docs` | [How the application learns how much storage is booked and used](docs/storage-status.md), [destinations in a storage](docs/storage-folders.md), [pairing with the customer portal](docs/device-pairing.md) |
| `tests` | Tests for the core and a walkthrough of the user interface |

## Opening a backup as a drive

Upstream restic cannot mount repositories on Windows. `build-restic.ps1` fetches the pinned
restic release, copies the files from `restic-overlay` on top of it and builds the result.
The volume rejects every operation that would change it. On Linux restic mounts through
FUSE as it comes. On both systems the drive is unmounted and its lock in the repository
is released when the application ends for any reason.

## Contributing

This repository does not take contributions from outside: pull requests are limited to
collaborators and issues are turned off. You are free to use and adapt the code under the
terms of the license.

## License

Imagepixel Backup is licensed under the [BSD 2-Clause License](LICENSE), like restic.
The components it includes are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

Backups are mounted with WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos,
<https://github.com/winfsp/winfsp>.
