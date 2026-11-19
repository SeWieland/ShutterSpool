import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { gzipSync } from 'node:zlib';
import manifest from '../node_modules/.cache/nuxt/.nuxt/dist/server/client.manifest.mjs';

const output = new URL('../.output/public/', import.meta.url);
const html = readFileSync(new URL('index.html', output), 'utf8');
assert(!/<link\b[^>]*\brel="prefetch"/.test(html), 'Static HTML must not prefetch inactive workspace chunks.');
const entry = Object.keys(manifest).find(key => manifest[key].isEntry);
assert(entry, 'Generated client manifest must identify its entry.');
const initial = new Set();
function collect(key) {
    if (initial.has(key)) return;
    assert(manifest[key], `Missing manifest dependency: ${key}`);
    initial.add(key);
    for (const dependency of manifest[key].imports ?? []) collect(dependency);
}
collect(entry);
for (const name of ['OperationalWorkspace', 'SourceWorkspace', 'DeliveryWorkspace', 'RetentionSettings', 'TrashWorkspace']) {
    const key = `components/${name}.vue`;
    assert(manifest[key]?.isDynamicEntry && !initial.has(key), `${name} must remain outside the initial static graph.`);
}
function sizes(files) {
    let bytes = 0, gzip = 0;
    for (const file of files) {
        const buffer = readFileSync(new URL(`_nuxt/${file}`, output));
        bytes += buffer.length;
        gzip += gzipSync(buffer).length;
    }
    return { bytes, gzip };
}
const javascript = sizes([...initial].map(key => manifest[key].file));
const styles = sizes(new Set([...initial].flatMap(key => manifest[key].css ?? [])));
// Measured initial graph: 484 KB / 161 KB gzip; CSS: 262 KB / 39 KB gzip. Leave bounded headroom.
assert(javascript.bytes <= 550_000 && javascript.gzip <= 185_000, `Initial JavaScript budget exceeded: ${JSON.stringify(javascript)}`);
assert(styles.bytes <= 280_000 && styles.gzip <= 42_000, `Initial CSS budget exceeded: ${JSON.stringify(styles)}`);
const chunks = readdirSync(new URL('_nuxt/', output)).filter(file => file.endsWith('.js'));
const largestChunk = Math.max(...chunks.map(file => sizes([file]).bytes));
assert(largestChunk <= 350_000, `JavaScript chunk budget exceeded: ${largestChunk} bytes`);
console.log(JSON.stringify({ initialJavaScript: javascript, initialCss: styles, largestChunk, lazySections: 5 }, null, 2));
