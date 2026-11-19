#!/usr/bin/env bash
# Installs the package on a fresh Ubuntu or CachyOS, as a customer would, and checks
# that the application runs: everything it needs came with the package, the restic
# inside starts, the application shows its window, keeps a secret in the keyring, and a
# second start gives way to the one that is running.
#
#   installer/linux/smoke-test.sh <package> [<folder for a picture of the screen>]
#
# Must run as root on a system that may be changed, such as a container:
#
#   docker run --rm -v "$PWD:/src:ro" ubuntu:rolling bash /src/installer/linux/smoke-test.sh \
#       /src/artifacts/linux/imagepixel-backup_0.1.9_amd64.deb
set -euo pipefail

package="$(realpath "$1")"
pictures="${2:-}"
root="$(cd "$(dirname "$0")/../.." && pwd)"

. /etc/os-release
echo "Testing $(basename "$package") on $PRETTY_NAME"

# what the test itself needs comes after the package, so that a missing dependency
# of the package is not covered up
if command -v apt-get > /dev/null; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q > /dev/null
    apt-get install -y -q "$package" > /dev/null
    apt-get install -y -q xvfb x11-utils dbus-daemon gnome-keyring imagemagick procps desktop-file-utils > /dev/null
else
    pacman -Syu --noconfirm > /dev/null
    pacman -U --noconfirm "$package" > /dev/null
    pacman -S --noconfirm --needed xorg-server-xvfb xorg-xwininfo dbus gnome-keyring imagemagick procps-ng \
        desktop-file-utils > /dev/null
fi

echo "Checking the installed files ..."
test -x /opt/imagepixel-backup/ImagepixelBackup
test "$(readlink -f "$(command -v imagepixel-backup)")" = /opt/imagepixel-backup/ImagepixelBackup
test -f /usr/share/icons/hicolor/256x256/apps/imagepixel-backup.png
desktop-file-validate /usr/share/applications/imagepixel-backup.desktop

echo "Starting the restic inside ..."
/opt/imagepixel-backup/restic version
echo "Starting the rclone inside ..."
/opt/imagepixel-backup/rclone version

work="$(mktemp -d)"
export IMAGEPIXEL_BACKUP_DATA="$work/settings"
export PICTURES="$pictures"
export SYSTEM="$ID"
export KEYRING="$root/tests/start-keyring.sh"

# shellcheck disable=SC2016
dbus-run-session -- bash -c '
    set -euo pipefail
    . "$KEYRING"

    echo "Keeping a secret in the keyring ..."
    echo -n "ipb1test" | secret-tool store --label "Imagepixel Backup (test)" service de.imagepixel.backup.test account test
    test "$(secret-tool lookup service de.imagepixel.backup.test account test)" = "ipb1test"
    secret-tool clear service de.imagepixel.backup.test account test

    Xvfb :99 -screen 0 1280x900x24 > /dev/null 2>&1 &
    display=$!
    export DISPLAY=:99
    sleep 2

    # The hard case for the start: no caches of fontconfig, so that the fonts are read
    # anew, which loads the HarfBuzz of the system next to the one of the application.
    rm -rf /var/cache/fontconfig ~/.cache/fontconfig

    echo "Starting the application ..."
    imagepixel-backup &
    first=$!
    stop() { kill "$first" "$display" 2> /dev/null || true; }
    trap stop EXIT
    sleep 20
    if ! kill -0 "$first" 2> /dev/null; then
        echo "The application did not stay up." >&2
        exit 1
    fi
    xwininfo -root -tree | grep -q "Imagepixel Backup"

    if [ -n "$PICTURES" ]; then
        mkdir -p "$PICTURES"
        import -window root "$PICTURES/linux-$SYSTEM.png" || echo "No picture of the screen could be taken."
    fi

    echo "Starting it a second time ..."
    imagepixel-backup --minimized &
    second=$!
    sleep 10
    if kill -0 "$second" 2> /dev/null; then
        kill "$second" 2> /dev/null || true
        echo "A second start kept running next to the first." >&2
        exit 1
    fi
    if ! kill -0 "$first" 2> /dev/null; then
        echo "The application ended when it was started a second time." >&2
        exit 1
    fi
'

rm -rf "$work"
echo "The application runs on $PRETTY_NAME."
