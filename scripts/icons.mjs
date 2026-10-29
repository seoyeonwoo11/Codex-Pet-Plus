import { deflateSync } from 'node:zlib';
import { mkdir, writeFile } from 'node:fs/promises';

const sprite = [
  '................', '................', '...oo......oo...', '...ofoo..oofo...',
  '...offooooffo...', '..oofffffff oo..'.replace(' ', ''), '..offfffffffo...', '..offfffffffo...',
  '..offbbffbbfo...', '..offbbffbbfo...', '..offffbbfffo...', '..offpffffpfo...',
  '...offbbbbfo....', '...offfffffo....', '....oooooo......', '................',
];
const palette = { '.': [0, 0, 0, 0], o: [130, 79, 51, 255], f: [246, 186, 130, 255], b: [54, 47, 40, 255], p: [238, 148, 127, 255] };
function crc(buffer) {
  let value = 0xffffffff;
  for (const byte of buffer) { value ^= byte; for (let k = 0; k < 8; k++) value = (value >>> 1) ^ ((value & 1) ? 0xedb88320 : 0); }
  return (value ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const body = Buffer.concat([Buffer.from(type), data]);
  const size = Buffer.alloc(4); size.writeUInt32BE(data.length);
  const sum = Buffer.alloc(4); sum.writeUInt32BE(crc(body));
  return Buffer.concat([size, body, sum]);
}
await mkdir(new URL('../extension/assets/', import.meta.url), { recursive: true });
for (const size of [16, 48, 128]) {
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const color = palette[sprite[Math.floor(y * 16 / size)][Math.floor(x * 16 / size)] ?? '.'];
    raw.set(color, y * (size * 4 + 1) + 1 + x * 4);
  }
  const header = Buffer.alloc(13); header.writeUInt32BE(size); header.writeUInt32BE(size, 4); header[8] = 8; header[9] = 6;
  const png = Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
  await writeFile(new URL(`../extension/assets/icon${size}.png`, import.meta.url), png);
}
console.log('Generated Chrome extension icons (16, 48, 128).');
