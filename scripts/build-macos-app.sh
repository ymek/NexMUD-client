#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "$(uname -m)" in
  arm64) rid="osx-arm64" ;;
  x86_64) rid="osx-x64" ;;
  *) echo "Unsupported macOS architecture: $(uname -m)" >&2; exit 2 ;;
esac

configuration="${CONFIGURATION:-Release}"
artifacts="$repo_root/artifacts/macos"
publish="$artifacts/publish"
app="$artifacts/NexMUD.app"

rm -rf "$artifacts"
mkdir -p "$publish" "$app/Contents/MacOS" "$app/Contents/Resources"

dotnet publish src/NexMud.Gui/NexMud.Gui.csproj \
  -c "$configuration" \
  -r "$rid" \
  --self-contained false \
  -o "$publish"

cp -R "$publish"/. "$app/Contents/MacOS/"
cp src/NexMud.Gui/Assets/app-icon.icns "$app/Contents/Resources/NexMUD.icns"
chmod +x "$app/Contents/MacOS/NexMUD"

cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>NexMUD</string>
  <key>CFBundleDisplayName</key>
  <string>NexMUD</string>
  <key>CFBundleIdentifier</key>
  <string>ai.typesafe.nexmud</string>
  <key>CFBundleExecutable</key>
  <string>NexMUD</string>
  <key>CFBundleIconFile</key>
  <string>NexMUD.icns</string>
  <key>CFBundleIconName</key>
  <string>NexMUD</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>0.30.1</string>
  <key>CFBundleVersion</key>
  <string>30001</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
PLIST

printf 'Built %s\n' "$app"
printf 'Run with: open "%s"\n' "$app"
