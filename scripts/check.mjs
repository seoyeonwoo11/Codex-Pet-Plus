import { readdir, readFile, access } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../', import.meta.url));
async function files(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  return (await Promise.all(entries.map(entry => entry.isDirectory() ? files(path.join(directory, entry.name)) : path.join(directory, entry.name)))).flat();
}
let count = 0;
for (const directory of ['extension', 'scripts', 'tests']) for (const file of await files(path.join(root, directory))) {
  if (!/\.(m?js)$/.test(file)) continue;
  const result = spawnSync(process.execPath, ['--check', file], { stdio: 'inherit' });
  if (result.status !== 0) process.exit(result.status ?? 1);
  count++;
}
const extension = path.join(root, 'extension');
const manifest = JSON.parse(await readFile(path.join(extension, 'manifest.json'), 'utf8'));
for (const file of [manifest.background.service_worker, manifest.action.default_popup, manifest.side_panel.default_path, manifest.options_page, ...Object.values(manifest.icons)]) await access(path.join(extension, file));
if (manifest.manifest_version !== 3 || JSON.stringify(manifest.host_permissions) !== '["https://chatgpt.com/*"]') throw new Error('Unexpected extension permissions');
console.log(`Syntax checked ${count} JavaScript modules; Manifest V3 resources and site permissions verified.`);
