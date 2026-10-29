import { readdir, readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { zipSync } from 'fflate';
const root = fileURLToPath(new URL('../', import.meta.url));
const files = {};
async function add(relative) {
  const absolute = path.join(root, relative);
  const entries = await readdir(absolute, { withFileTypes: true });
  for (const entry of entries) {
    const child = `${relative}/${entry.name}`;
    if (entry.isDirectory()) await add(child);
    else if (entry.isFile()) files[`codex-pet-plus/${child}`] = new Uint8Array(await readFile(path.join(root, child)));
  }
}
await add('extension');
for (const file of ['README.md', 'PRIVACY.md']) files[`codex-pet-plus/${file}`] = new Uint8Array(await readFile(path.join(root, file)));
await mkdir(path.join(root, 'dist'), { recursive: true });
await writeFile(path.join(root, 'dist/codex-pet-plus.zip'), zipSync(files));
console.log(`Created dist/codex-pet-plus.zip (${Object.keys(files).length} files). No dependency install or build is needed to load the extension.`);
