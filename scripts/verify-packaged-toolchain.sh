#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
app="${1:-$repo_root/artifacts/macos/NexMUD.app}"
tooling="$app/Contents/Resources/AutomationStudio/tooling"
node="$tooling/node/bin/node"
monaco="$app/Contents/MacOS/Assets/Monaco"

[[ -x "$node" ]] || { echo "Bundled Node.js is missing or not executable: $node" >&2; exit 1; }
[[ -f "$tooling/manifest.json" ]] || { echo "Toolchain manifest is missing." >&2; exit 1; }

component_path() {
  "$node" - "$tooling" "$1" <<'NODE'
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(process.argv[2]);
const name = process.argv[3];
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));
const component = (manifest.components ?? []).find(entry => entry.name === name);
if (!component) throw new Error(`Missing toolchain component in manifest: ${name}`);
const fullPath = path.resolve(root, ...String(component.relativePath).split('/'));
if (!fullPath.startsWith(`${root}${path.sep}`)) throw new Error(`Escaped component path: ${component.relativePath}`);
process.stdout.write(fullPath);
NODE
}

tsc="$(component_path typescript)"
pnpm="$(component_path pnpm)"
[[ -f "$tsc" ]] || { echo "Bundled TypeScript compiler is missing: $tsc" >&2; exit 1; }
[[ -f "$pnpm" ]] || { echo "Bundled pnpm is missing: $pnpm" >&2; exit 1; }
[[ -f "$monaco/index.html" ]] || { echo "Bundled Monaco index.html is missing." >&2; exit 1; }
[[ -f "$monaco/vs/loader.js" ]] || { echo "Bundled Monaco loader.js is missing." >&2; exit 1; }

clean_path="/usr/bin:/bin"
for command in node npm pnpm tsc; do
  if env -i PATH="$clean_path" /bin/sh -c "command -v '$command' >/dev/null 2>&1"; then
    echo "Clean-machine verification PATH unexpectedly exposes '$command'." >&2
    exit 1
  fi
done

clean_env=(env -i "HOME=${HOME:-/tmp}" "TMPDIR=${TMPDIR:-/tmp}" "PATH=$clean_path" "LANG=${LANG:-C}")
"${clean_env[@]}" "$node" "$tsc" --version >/dev/null
"${clean_env[@]}" "$node" "$pnpm" --version >/dev/null

"${clean_env[@]}" "$node" - "$tooling" <<'NODE'
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(process.argv[2]);
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'manifest.json'), 'utf8'));
const expectedPlatform = process.platform === 'darwin' ? 'darwin' : process.platform;
if (manifest.schemaVersion !== 1) throw new Error(`Unexpected toolchain schema: ${manifest.schemaVersion}`);
if (manifest.platform !== expectedPlatform || manifest.architecture !== process.arch) {
  throw new Error(`Toolchain target ${manifest.platform}/${manifest.architecture} does not match ${expectedPlatform}/${process.arch}`);
}
for (const component of manifest.components ?? []) {
  const fullPath = path.resolve(root, ...String(component.relativePath).split('/'));
  if (!fullPath.startsWith(`${root}${path.sep}`)) throw new Error(`Escaped component path: ${component.relativePath}`);
  const actual = crypto.createHash('sha256').update(fs.readFileSync(fullPath)).digest('hex');
  if (component.sha256 && actual !== component.sha256) throw new Error(`SHA-256 mismatch: ${component.name}`);
}
NODE

printf 'Verified packaged NexMUD scripting toolchain without system Node/npm/pnpm/tsc: %s\n' "$tooling"
