import path from 'node:path';
import { mkdtemp, rm, cp, readFile } from 'node:fs/promises';
import os from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../', import.meta.url));
const tools = process.env.PET_MOD_TOOLS;
if (!tools) throw new Error('Set PET_MOD_TOOLS to a temporary npm prefix containing @electron/asar 4.3.1 and electron 45.1.0.');
const asar = await import(pathToFileURL(path.join(tools, 'node_modules/@electron/asar/lib/asar.js')));
const electron = path.join(tools, 'node_modules/electron/dist', process.platform === 'win32' ? 'electron.exe' : 'electron');
const directory = await mkdtemp(path.join(os.tmpdir(), 'pet-plus-electron-'));
try {
  const source = path.join(directory, 'source'); await cp(path.join(root, 'tests/electron-fixture'), source, { recursive: true });
  const original = path.join(directory, 'original.asar'), patched = path.join(directory, 'app.asar');
  await asar.createPackage(source, original);
  const runner = path.join(root, 'desktop/CodexPetPlus.Tests/bin/Release/net10.0/CodexPetPlus.Tests.dll');
  const patch = spawnSync(process.env.PET_DOTNET_PATH || 'dotnet', [runner, '--patch-asar', original, patched, path.join(root, 'mod/runtime.cjs')], { encoding: 'utf8', timeout: 30000 });
  if (patch.status !== 0) throw new Error(patch.stderr || patch.stdout || String(patch.error));
  assert.match(asar.extractFile(patched, 'main.cjs').toString(), /CODEX_PET_PLUS_MOD_0_4/);
  assert.equal(asar.extractFile(patched, 'codex-pet-plus-runtime.cjs').toString(), await readFile(path.join(root, 'mod/runtime.cjs'), 'utf8'));
  assert.equal(asar.extractFile(patched, 'index.html').toString(), await readFile(path.join(source, 'index.html'), 'utf8'));
  const flags = process.platform === 'linux' ? ['--no-sandbox', '--disable-gpu'] : [];
  const run = spawnSync(electron, [...flags, patched], { encoding: 'utf8', timeout: 30000, env: { ...process.env, PET_MOD_SMOKE_DIR: directory }, maxBuffer: 1024 * 1024 });
  if (run.status !== 0 || !run.stdout.includes('MOD_SMOKE_PASS')) throw new Error((run.stdout || '') + (run.stderr || '') + (run.error || ''));
  console.log(run.stdout.trim());
  if (process.env.PET_MOD_SCREENSHOT) await cp(path.join(directory, 'quota-panel.png'), process.env.PET_MOD_SCREENSHOT);
} finally {
  if (process.env.PET_MOD_KEEP_FIXTURE) console.log('Synthetic fixture: ' + directory);
  else await rm(directory, { recursive: true, force: true });
}
