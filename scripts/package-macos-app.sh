#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
"$repo_root/scripts/build-macos-app.sh"

app="$repo_root/artifacts/macos/NexMUD.app"
tooling="$app/Contents/Resources/AutomationStudio/tooling"
identity="${CODESIGN_IDENTITY:--}"

component_path() {
  "$tooling/node/bin/node" - "$tooling" "$1" <<'NODE'
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(process.argv[2]);
const name = process.argv[3];
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));
const component = (manifest.components ?? []).find(entry => entry.name === name);
if (!component) throw new Error(`Missing toolchain component in manifest: ${name}`);
process.stdout.write(path.resolve(root, ...String(component.relativePath).split('/')));
NODE
}

# Sign nested executable code before the outer bundle, then refresh integrity hashes.
codesign --force --timestamp=none --sign "$identity" "$tooling/node/bin/node"
pnpm="$(component_path pnpm)"
codesign --force --timestamp=none --sign "$identity" "$pnpm"
while IFS= read -r -d '' native_esbuild; do
  codesign --force --timestamp=none --sign "$identity" "$native_esbuild"
done < <(find "$tooling/packages/node_modules" -path '*/@esbuild/*/bin/esbuild' -type f -print0 2>/dev/null || true)

"$tooling/node/bin/node" "$repo_root/scripts/rehash-toolchain-manifest.cjs" "$tooling"
"$repo_root/scripts/verify-packaged-toolchain.sh" "$app"

codesign --force --timestamp=none --sign "$identity" "$app/Contents/MacOS/NexMUD"
codesign --force --timestamp=none --sign "$identity" "$app"
codesign --verify --deep --strict "$app"

printf 'Packaged and signed %s\n' "$app"
