const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(process.argv[2] ?? '');
if (!root || !fs.existsSync(root)) throw new Error('Usage: node rehash-toolchain-manifest.cjs <toolchain-root>');
const manifestPath = path.join(root, 'manifest.json');
const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
for (const component of manifest.components ?? []) {
  const fullPath = path.resolve(root, ...String(component.relativePath).split('/'));
  if (!fullPath.startsWith(`${root}${path.sep}`) || !fs.existsSync(fullPath)) {
    throw new Error(`Invalid toolchain component path: ${component.relativePath}`);
  }
  component.sha256 = crypto.createHash('sha256').update(fs.readFileSync(fullPath)).digest('hex');
}
fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, 'utf8');
