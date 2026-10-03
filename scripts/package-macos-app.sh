#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$repo_root/scripts/build-macos-app.sh"

app="$repo_root/artifacts/macos/NexMUD.app"
tooling="$app/Contents/Resources/AutomationStudio/tooling"
identity="${CODESIGN_IDENTITY:--}"

# Sign nested executable code before the outer bundle, then refresh integrity hashes.
codesign --force --timestamp=none --sign "$identity" "$tooling/node/bin/node"
while IFS= read -r -d '' native_esbuild; do
  codesign --force --timestamp=none --sign "$identity" "$native_esbuild"
done < <(find "$tooling/packages/node_modules" -path '*/@esbuild/*/bin/esbuild' -type f -print0 2>/dev/null || true)

"$tooling/node/bin/node" "$repo_root/scripts/rehash-toolchain-manifest.cjs" "$tooling"
"$repo_root/scripts/verify-packaged-toolchain.sh" "$app"

codesign --force --timestamp=none --sign "$identity" "$app/Contents/MacOS/NexMUD"
codesign --force --timestamp=none --sign "$identity" "$app"
codesign --verify --deep --strict "$app"

printf 'Packaged and signed %s\n' "$app"
