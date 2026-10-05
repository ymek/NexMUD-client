#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=/dev/null
source "$repo_root/scripts/scripting-toolchain.versions.env"

platform=""
arch=""
output=""
while (( $# > 0 )); do
  case "$1" in
    --platform) platform="${2:?missing --platform value}"; shift 2 ;;
    --arch) arch="${2:?missing --arch value}"; shift 2 ;;
    --output) output="${2:?missing --output value}"; shift 2 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$platform" ]]; then
  case "$(uname -s)" in
    Darwin) platform="darwin" ;;
    Linux) platform="linux" ;;
    *) echo "Unsupported tooling host platform: $(uname -s)" >&2; exit 2 ;;
  esac
fi
if [[ -z "$arch" ]]; then
  case "$(uname -m)" in
    arm64|aarch64) arch="arm64" ;;
    x86_64|amd64) arch="x64" ;;
    *) echo "Unsupported tooling architecture: $(uname -m)" >&2; exit 2 ;;
  esac
fi
if [[ -z "$output" ]]; then
  case "$platform-$arch" in
    darwin-arm64) rid="osx-arm64" ;;
    darwin-x64) rid="osx-x64" ;;
    linux-arm64) rid="linux-arm64" ;;
    linux-x64) rid="linux-x64" ;;
    *) echo "Unsupported tooling target: $platform/$arch" >&2; exit 2 ;;
  esac
  output="$repo_root/.artifacts/scripting-toolchain/$rid"
fi
output="$(mkdir -p "$(dirname "$output")" && cd "$(dirname "$output")" && pwd)/$(basename "$output")"

case "$platform" in
  darwin) archive_ext="tar.gz" ;;
  linux) archive_ext="tar.xz" ;;
  *) echo "prepare-scripting-toolchain.sh currently supports darwin and linux targets." >&2; exit 2 ;;
esac

fingerprint="layout=3;node=$NODE_VERSION;pnpm=$PNPM_VERSION;typescript=$TYPESCRIPT_VERSION;typescript-language-server=$TYPESCRIPT_LANGUAGE_SERVER_VERSION;esbuild=$ESBUILD_VERSION;vitest=$VITEST_VERSION;platform=$platform;arch=$arch"
if [[ -f "$output/.fingerprint" ]] && [[ "$(cat "$output/.fingerprint")" == "$fingerprint" ]] && \
   [[ -x "$output/node/bin/node" ]] && [[ -f "$output/manifest.json" ]] && \
   [[ -d "$output/pnpm" ]] && [[ -f "$output/packages/node_modules/typescript/lib/tsc.js" ]]; then
  printf 'Using cached NexMUD scripting toolchain: %s\n' "$output"
  exit 0
fi

work="$(mktemp -d "${TMPDIR:-/tmp}/nexmud-toolchain.XXXXXX")"
trap 'rm -rf "$work"' EXIT
rm -rf "$output"
mkdir -p "$output"

archive="node-v${NODE_VERSION}-${platform}-${arch}.${archive_ext}"
base_url="https://nodejs.org/dist/v${NODE_VERSION}"
printf 'Downloading Node.js %s for %s/%s...\n' "$NODE_VERSION" "$platform" "$arch"
curl --fail --location --retry 3 --silent --show-error "$base_url/$archive" -o "$work/$archive"
curl --fail --location --retry 3 --silent --show-error "$base_url/SHASUMS256.txt" -o "$work/SHASUMS256.txt"
expected="$(awk -v file="$archive" '$2 == file { print $1 }' "$work/SHASUMS256.txt")"
if [[ -z "$expected" ]]; then
  echo "Unable to find $archive in Node.js SHASUMS256.txt" >&2
  exit 1
fi
actual="$(shasum -a 256 "$work/$archive" | awk '{print $1}')"
if [[ "$actual" != "$expected" ]]; then
  echo "Node.js archive SHA-256 mismatch." >&2
  exit 1
fi

pnpm_archive="pnpm-${platform}-${arch}.tar.gz"
case "$platform-$arch" in
  darwin-arm64) pnpm_expected="8eeeae4cd714b2f1755750d303f5b1bcfe23d95ed7245536305d0c3877c5ce41" ;;
  darwin-x64) pnpm_expected="8cbc5a840b4bcd8c0da878e6616a832d88e98a8acbedf1736310b395467f4a13" ;;
  linux-arm64) pnpm_expected="75c5e34d3164fe3d3549580b1b1b59c9dd16543e558dec722b686193cdb7dfa1" ;;
  linux-x64) pnpm_expected="db3906881f63e41b655e3b97d473603dc26326c2470d0b1b8083689e1bcbd1e8" ;;
  *) echo "No pinned pnpm release digest for $platform/$arch." >&2; exit 2 ;;
esac
pnpm_url="https://github.com/pnpm/pnpm/releases/download/v${PNPM_VERSION}/${pnpm_archive}"
printf 'Downloading pnpm %s standalone binary for %s/%s...\n' "$PNPM_VERSION" "$platform" "$arch"
curl --fail --location --retry 3 --silent --show-error "$pnpm_url" -o "$work/$pnpm_archive"
pnpm_actual="$(shasum -a 256 "$work/$pnpm_archive" | awk '{print $1}')"
if [[ "$pnpm_actual" != "$pnpm_expected" ]]; then
  echo "pnpm release archive SHA-256 mismatch." >&2
  exit 1
fi

tar -xf "$work/$archive" -C "$work"
mv "$work/node-v${NODE_VERSION}-${platform}-${arch}" "$output/node"
node="$output/node/bin/node"
npm_cli="$output/node/lib/node_modules/npm/bin/npm-cli.js"

pnpm_release_root="$work/pnpm-release"
mkdir -p "$pnpm_release_root" "$output/pnpm"
tar -xzf "$work/$pnpm_archive" -C "$pnpm_release_root"
pnpm_source="$(find "$pnpm_release_root" -type f -name pnpm -print -quit)"
if [[ -z "$pnpm_source" ]]; then
  echo "pnpm release archive did not contain a pnpm executable." >&2
  exit 1
fi
cp -R "$pnpm_release_root"/. "$output/pnpm/"
pnpm_inside="${pnpm_source#${pnpm_release_root}/}"
pnpm_relative="pnpm/$pnpm_inside"
pnpm="$output/$pnpm_relative"
chmod +x "$pnpm"
if [[ "$("$pnpm" --version)" != "$PNPM_VERSION" ]]; then
  echo "Bundled pnpm version did not match $PNPM_VERSION." >&2
  exit 1
fi

mkdir -p "$output/packages"
cat > "$output/packages/package.json" <<JSON
{
  "private": true,
  "dependencies": {
    "typescript": "$TYPESCRIPT_VERSION",
    "typescript-language-server": "$TYPESCRIPT_LANGUAGE_SERVER_VERSION",
    "esbuild": "$ESBUILD_VERSION",
    "vitest": "$VITEST_VERSION"
  }
}
JSON

printf 'Installing pinned NexMUD authoring packages...\n'
"$node" "$npm_cli" install \
  --prefix "$output/packages" \
  --ignore-scripts \
  --no-audit \
  --no-fund \
  --package-lock=false

TOOLCHAIN_ROOT="$output" \
TOOLCHAIN_PLATFORM="$platform" \
TOOLCHAIN_ARCH="$arch" \
TOOLCHAIN_FINGERPRINT="$fingerprint" \
NODE_VERSION="$NODE_VERSION" \
PNPM_VERSION="$PNPM_VERSION" \
PNPM_RELATIVE_PATH="$pnpm_relative" \
TYPESCRIPT_VERSION="$TYPESCRIPT_VERSION" \
TYPESCRIPT_LANGUAGE_SERVER_VERSION="$TYPESCRIPT_LANGUAGE_SERVER_VERSION" \
ESBUILD_VERSION="$ESBUILD_VERSION" \
VITEST_VERSION="$VITEST_VERSION" \
"$node" - <<'NODE'
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(process.env.TOOLCHAIN_ROOT);

function packageBin(packageName, commandName) {
  const packageRoot = path.join(root, 'packages', 'node_modules', ...packageName.split('/'));
  const packageJsonPath = path.join(packageRoot, 'package.json');
  if (!fs.existsSync(packageJsonPath)) {
    throw new Error(`Missing prepared package metadata: ${packageName}/package.json`);
  }

  const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
  const bin = packageJson.bin;
  const target = typeof bin === 'string'
    ? bin
    : bin?.[commandName] ?? bin?.[packageName.split('/').at(-1)];
  if (typeof target !== 'string' || target.length === 0) {
    throw new Error(`Package ${packageName} does not expose the expected '${commandName}' binary.`);
  }

  const fullPath = path.resolve(packageRoot, target);
  if (fullPath !== packageRoot && !fullPath.startsWith(`${packageRoot}${path.sep}`)) {
    throw new Error(`Package binary escaped its package root: ${packageName} -> ${target}`);
  }
  if (!fs.existsSync(fullPath)) {
    throw new Error(`Missing prepared package binary: ${packageName} -> ${target}`);
  }
  return path.relative(root, fullPath).split(path.sep).join('/');
}

const entries = [
  ['node', process.env.NODE_VERSION, 'node/bin/node', true],
  ['pnpm', process.env.PNPM_VERSION, process.env.PNPM_RELATIVE_PATH, true],
  ['typescript', process.env.TYPESCRIPT_VERSION, packageBin('typescript', 'tsc'), false],
  ['typescript-language-server', process.env.TYPESCRIPT_LANGUAGE_SERVER_VERSION, packageBin('typescript-language-server', 'typescript-language-server'), false],
  ['esbuild', process.env.ESBUILD_VERSION, packageBin('esbuild', 'esbuild'), true],
  ['vitest', process.env.VITEST_VERSION, packageBin('vitest', 'vitest'), false]
];
const components = entries.map(([name, version, relativePath, executable]) => {
  const fullPath = path.join(root, ...relativePath.split('/'));
  if (!fs.existsSync(fullPath)) throw new Error(`Missing prepared toolchain component: ${relativePath}`);
  const sha256 = crypto.createHash('sha256').update(fs.readFileSync(fullPath)).digest('hex');
  return { name, version, relativePath, sha256, executable };
});
const manifest = {
  schemaVersion: 1,
  platform: process.env.TOOLCHAIN_PLATFORM,
  architecture: process.env.TOOLCHAIN_ARCH,
  fingerprint: process.env.TOOLCHAIN_FINGERPRINT,
  components
};
fs.writeFileSync(path.join(root, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
NODE

printf '%s' "$fingerprint" > "$output/.fingerprint"
printf 'Prepared NexMUD scripting toolchain: %s\n' "$output"
