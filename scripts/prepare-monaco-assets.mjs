#!/usr/bin/env node
import { cp, mkdir, rm, writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const MONACO_VERSION = '0.57.0';
const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '..');
const work = join(repo, '.artifacts', 'monaco');
const destination = join(repo, 'src', 'NexMud.Gui', 'Assets', 'Monaco', 'vs');

await rm(work, { recursive: true, force: true });
await mkdir(work, { recursive: true });
execFileSync('npm', [
  'install',
  '--ignore-scripts',
  '--no-audit',
  '--no-fund',
  '--package-lock=false',
  '--prefix', work,
  `monaco-editor@${MONACO_VERSION}`
], { cwd: repo, stdio: 'inherit' });

await rm(destination, { recursive: true, force: true });
await mkdir(destination, { recursive: true });
await cp(join(work, 'node_modules', 'monaco-editor', 'min', 'vs'), destination, { recursive: true });
await writeFile(join(repo, 'src', 'NexMud.Gui', 'Assets', 'Monaco', 'VERSION'), `${MONACO_VERSION}\n`, 'utf8');
console.log(`Bundled Monaco ${MONACO_VERSION} at ${destination}`);
