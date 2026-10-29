import { spawnSync } from 'node:child_process';
import { readdir, readFile, mkdir, writeFile, rm } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { zipSync } from 'fflate';

const root = fileURLToPath(new URL('../', import.meta.url));
const output = path.join(root, 'dist', 'windows');
await rm(output, { recursive: true, force: true });
const result = spawnSync(process.env.PET_DOTNET_PATH || 'dotnet', ['publish', 'desktop/CodexPetPlus.Windows/CodexPetPlus.Windows.csproj', '-c', 'Release', '-p:PublishProfile=Portable', '-o', output, '--nologo', '-m:2'], { cwd: root, stdio: 'inherit' });
if (result.error) throw result.error;
if (result.status !== 0) process.exit(result.status ?? 1);
await writeFile(path.join(output, 'START-HERE.ko.md'), await readFile(path.join(root, 'desktop/START-HERE.ko.md')));
const files = {};
async function add(directory, relative = '') {
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const name = relative + entry.name;
    if (entry.isDirectory()) await add(path.join(directory, entry.name), name + '/');
    else files[name] = new Uint8Array(await readFile(path.join(directory, entry.name)));
  }
}
await add(output);
await mkdir(path.join(root, 'dist'), { recursive: true });
await writeFile(path.join(root, 'dist/CodexPetPlus-Windows-x64.zip'), zipSync(files, { level: 6 }));
console.log('Created dist/CodexPetPlus-Windows-x64.zip, including a self-contained Windows executable.');
