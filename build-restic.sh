#!/usr/bin/env bash
# Builds restic on macOS and Linux for the application, and bin/restic for this
# computer, which the tests use:
#
#   macOS: bin/restic-darwin-arm64 and bin/restic-darwin-amd64
#   Linux: bin/restic-linux-amd64
#
# Requirements: Git and Go on the PATH.
set -euo pipefail

RESTIC_VERSION="${RESTIC_VERSION:-v0.19.1}"

root="$(cd "$(dirname "$0")" && pwd)"
source="$root/.build/restic-$RESTIC_VERSION"

case "$(uname -s)" in
    Darwin) system="darwin"; architectures="arm64 amd64" ;;
    Linux) system="linux"; architectures="amd64" ;;
    *) echo "This script builds restic on macOS and Linux. On Windows use build-restic.ps1." >&2; exit 1 ;;
esac

if [ ! -f "$source/go.mod" ]; then
    echo "Fetching restic $RESTIC_VERSION ..."
    git clone --quiet --depth 1 --branch "$RESTIC_VERSION" -c advice.detachedHead=false \
        https://github.com/restic/restic.git "$source"
fi

# The overlay adds the mount command for Windows. It does not change restic for
# macOS and Linux, but applying it keeps one source tree for every platform.
echo "Applying overlay ..."
cp -R "$root/restic-overlay/." "$source/"

mkdir -p "$root/bin"
cd "$source"
for arch in $architectures; do
    echo "Building restic for $system ($arch) ..."
    CGO_ENABLED=0 GOOS="$system" GOARCH="$arch" \
        go build -trimpath -ldflags "-s -w" -o "$root/bin/restic-$system-$arch" ./cmd/restic
done

native="$(uname -m)"
case "$native" in
    x86_64) native="amd64" ;;
    aarch64) native="arm64" ;;
esac
if [ -f "$root/bin/restic-$system-$native" ]; then
    cp "$root/bin/restic-$system-$native" "$root/bin/restic"
fi

echo "Done: $root/bin"
