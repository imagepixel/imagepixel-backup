#!/usr/bin/env bash
# Runs the tests on Linux the way a desktop session would: with a session bus and an
# unlocked keyring, so that the tests of the keyring run as well. The tests of the
# drive run where /dev/fuse exists.
#
#   tests/run-on-linux.sh [arguments for "dotnet test"]
#
# Requirements: the .NET 10 SDK, dbus, gnome-keyring and libsecret-tools (Arch: libsecret),
# fuse3 for the drive, and restic built by build-restic.sh.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

# shellcheck disable=SC2016
exec dbus-run-session -- bash -c '
    set -euo pipefail
    root="$1"
    shift
    echo -n "test" | gnome-keyring-daemon --unlock --components=secrets > /dev/null
    dotnet test "$root/ImagepixelBackup.slnx" --configuration Release "$@"
' run-on-linux "$root" "$@"
