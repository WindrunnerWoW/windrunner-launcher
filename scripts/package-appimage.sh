#!/usr/bin/env bash
# Package the complete Linux publish tree with pinned, checksum-verified AppImage tools.
set -euo pipefail

: "${SOURCE:?SOURCE is required}"
: "${APPIMAGE_OUT:?APPIMAGE_OUT is required}"
: "${VERSION:?VERSION is required}"
test -s "$SOURCE/WindrunnerLauncher"

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
appdir="$work/WindrunnerLauncher.AppDir"
mkdir -p "$appdir/usr/bin" "$(dirname "$APPIMAGE_OUT")"
cp -a "$SOURCE/." "$appdir/usr/bin/"
install -m 755 "$repo/scripts/appimage/AppRun" "$appdir/AppRun"
install -m 644 "$repo/scripts/appimage/windrunner-launcher.desktop" "$appdir/windrunner-launcher.desktop"
install -m 644 "$repo/src/WindrunnerLauncher.App/Assets/icon.png" "$appdir/windrunner-launcher.png"
ln -s windrunner-launcher.png "$appdir/.DirIcon"
chmod +x "$appdir/usr/bin/WindrunnerLauncher"

curl --fail --location --retry 3 \
  https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage \
  -o "$work/appimagetool.AppImage"
curl --fail --location --retry 3 \
  https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64 \
  -o "$work/runtime-x86_64"
printf '%s  %s\n' \
  ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0 "$work/appimagetool.AppImage" \
  2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d "$work/runtime-x86_64" \
  | sha256sum --check
chmod +x "$work/appimagetool.AppImage"

# Extraction avoids requiring FUSE in the build environment.
ARCH=x86_64 VERSION="$VERSION" "$work/appimagetool.AppImage" --appimage-extract-and-run \
  --no-appstream --runtime-file "$work/runtime-x86_64" "$appdir" "$APPIMAGE_OUT"
chmod +x "$APPIMAGE_OUT"
test -s "$APPIMAGE_OUT"
echo "Packaged $APPIMAGE_OUT"
