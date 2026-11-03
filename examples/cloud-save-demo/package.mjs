import { copyFile, readFile, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const project = dirname(fileURLToPath(import.meta.url));
const output = resolve(process.argv[2] ?? join(project, '../cloud-save-demo-build'));
const htmlPath = join(output, 'index.html');
const original = await readFile(htmlPath, 'utf8');
const script = '<script src="starport-bridge.v1.js"></script>';
if (!original.includes('</head>')) throw new Error('Godot export has no </head> element');
if (!original.includes(script)) {
  await writeFile(htmlPath, original.replace('</head>', `  ${script}\n</head>`));
}
await copyFile(
  resolve(project, '../../sdk/javascript/starport-bridge.v1.js'),
  join(output, 'starport-bridge.v1.js')
);
await writeFile(join(output, 'starport-game.json'), JSON.stringify({
  schemaVersion: 1,
  engine: 'godot',
  entrypoint: 'index.html',
  bridgeVersion: 1,
  saveSchemaVersions: [1],
}, null, 2) + '\n');
console.log(`Packaged Starport demo in ${output}`);
