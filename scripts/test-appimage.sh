#!/usr/bin/env bash
# Verify the package layout and keep the GUI running under a virtual X server.
set -euo pipefail
image=$(realpath "${1:?AppImage path is required}")
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

(cd "$work" && "$image" --appimage-extract >/dev/null)
test -x "$work/squashfs-root/AppRun"
test -x "$work/squashfs-root/usr/bin/WindrunnerLauncher"
test -s "$work/squashfs-root/windrunner-launcher.desktop"
test -s "$work/squashfs-root/windrunner-launcher.png"

status=0
XDG_DATA_HOME="$work/data" APPIMAGE_EXTRACT_AND_RUN=1 \
  timeout 15s xvfb-run -a "$image" || status=$?
if [ "$status" -ne 124 ]; then
  echo "AppImage exited during the startup smoke test (status $status)." >&2
  if [ -f "$work/data/windrunner-launcher/crash.log" ]; then
    cat "$work/data/windrunner-launcher/crash.log" >&2
  fi
  exit 1
fi
test ! -f "$work/data/windrunner-launcher/crash.log"
echo 'AppImage layout and GUI startup verified.'
