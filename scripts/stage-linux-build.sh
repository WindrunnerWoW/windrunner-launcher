#!/usr/bin/env bash
# Stage the Linux AppImage, SHA-256 files, and launcher-linux.json.
set -euo pipefail

: "${SOURCE:?SOURCE is required}"
: "${OUT:?OUT is required}"
: "${VERSION:?VERSION is required}"
: "${COMMIT:?COMMIT is required}"
: "${URL:?URL is required}"

native_aot="${NATIVE_AOT:-true}"
notes="${NOTES:-}"

if [[ ! -d "$SOURCE" ]]; then
  echo "Publish directory not found: $SOURCE" >&2
  exit 1
fi

filename="WindrunnerLauncher.AppImage"
bin="$SOURCE/$filename"
if [[ ! -f "$bin" ]]; then
  echo "$filename was not produced in $SOURCE" >&2
  exit 1
fi

rm -rf "$OUT"
mkdir -p "$OUT"
cp "$bin" "$OUT/$filename"
chmod +x "$OUT/$filename"

hash=$(sha256sum "$OUT/$filename" | awk '{print $1}')
size=$(stat -c%s "$OUT/$filename")
printf '%s  %s\n' "$hash" "$filename" > "$OUT/SHA256SUMS.txt"
printf '%s  %s\n' "$hash" "$filename" > "$OUT/$filename.sha256"

kind="AppImage (self-contained JIT fallback)"
if [[ "$native_aot" == "true" ]]; then
  kind="AppImage (NativeAOT)"
fi

cat > "$OUT/BUILD.txt" <<EOF
Windrunner Launcher
version:    $VERSION
commit:     $COMMIT
kind:       $kind
rid:        linux-x64
sha256:     $hash
size:       $size
EOF

LINUX_URL="$URL" LINUX_SHA="$hash" LINUX_SIZE="$size" LINUX_VERSION="$VERSION" LINUX_NOTES="$notes" \
  python3 - <<'PY'
import json, os
from pathlib import Path
doc = {
    "linuxUrl": os.environ["LINUX_URL"],
    "linuxSha256": os.environ["LINUX_SHA"],
    "linuxSize": int(os.environ["LINUX_SIZE"]),
    "version": os.environ["LINUX_VERSION"],
}
notes = os.environ.get("LINUX_NOTES", "")
if notes:
    doc["notes"] = notes
out = Path(os.environ["OUT"]) / "launcher-linux.json"
out.write_text(json.dumps(doc, indent=2) + "\n", encoding="utf-8")
PY

echo "Staged $OUT/$filename"
echo "SHA-256 $hash ($size bytes, $kind)"
