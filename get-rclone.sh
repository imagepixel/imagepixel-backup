#!/usr/bin/env bash
# Fetches rclone for Linux and puts it into bin/rclone-linux-amd64.
#
# Imagepixel Backup uses rclone as it is released, without changes, to show the
# storage read-only as it is: the encrypted files of the backup. This script
# downloads the pinned release from the rclone project and compares the file with
# the pinned SHA-256 checksum. macOS needs no rclone: it has no drive.
#
# To move to a newer rclone, change the version and take the checksum from the
# SHA256SUMS file of that release. get-rclone.ps1 pins the same version for Windows.
set -euo pipefail

version="v1.76.0"
sum_amd64="b00f00b3266c3764413afe599d3ec316f6f43b3e0ea8a0bc1d186d2133e46df7"

root="$(cd "$(dirname "$0")" && pwd)"
folder="$root/.build/rclone-$version"
mkdir -p "$folder" "$root/bin"

name="rclone-$version-linux-amd64"
archive="$folder/$name.zip"

if [ ! -f "$archive" ]; then
    echo "Fetching rclone $version ..."
    curl --fail --location --silent --show-error --output "$archive" \
        "https://github.com/rclone/rclone/releases/download/$version/$name.zip"
fi

actual="$(sha256sum "$archive" | cut -d ' ' -f 1)"
if [ "$actual" != "$sum_amd64" ]; then
    rm -f "$archive"
    echo "The checksum of $name.zip is $actual, expected was $sum_amd64. The file was removed." >&2
    exit 1
fi

if command -v unzip > /dev/null; then
    unzip -o -q "$archive" -d "$folder"
else
    bsdtar -xf "$archive" -C "$folder"
fi
cp "$folder/$name/rclone" "$root/bin/rclone-linux-amd64"
chmod +x "$root/bin/rclone-linux-amd64"
echo "Done: bin/rclone-linux-amd64"
