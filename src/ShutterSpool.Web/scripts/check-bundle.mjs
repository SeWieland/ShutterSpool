import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { gzipSync } from 'node:zlib';

export function checkBundle(output) {
    const manifest = JSON.parse(readFileSync(new URL('manifest.json', output), 'utf8'));
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
    const stylesheets = new Set();
    // The prerendered HTML can preload page chunks outside the app entry's static imports.
    for (const [, file] of html.matchAll(/<(?:script|link)\b[^>]*\b(?:src|href)=["']\/(_nuxt\/[^"'?]+\.(?:js|css))["']/g)) {
        if (file.endsWith('.css')) {
            stylesheets.add(file);
            continue;
        }
        const key = Object.keys(manifest).find(key => manifest[key].file === file);
        assert(key, `Missing manifest entry for HTML asset: ${file}`);
        collect(key);
    }
    const scripts = new Set([...initial].map(key => manifest[key].file));
    for (const name of ['OperationalWorkspace', 'SourceWorkspace', 'DeliveryWorkspace', 'RetentionSettings', 'TrashWorkspace']) {
        const chunk = manifest[`components/${name}.vue`];
        assert(chunk?.isDynamicEntry && !scripts.has(chunk.file), `${name} must remain outside the initial static graph.`);
    }
    function sizes(files) {
        let bytes = 0, gzip = 0;
        for (const file of files) {
            const buffer = readFileSync(new URL(file, output));
            bytes += buffer.length;
            gzip += gzipSync(buffer).length;
        }
        return { bytes, gzip };
    }
    for (const key of initial)
        for (const file of manifest[key].css ?? []) stylesheets.add(file);
    const javascript = sizes(scripts);
    const styles = sizes(stylesheets);
    assert(javascript.bytes <= 550_000 && javascript.gzip <= 185_000, `Initial JavaScript budget exceeded: ${JSON.stringify(javascript)}`);
    assert(styles.bytes <= 280_000 && styles.gzip <= 42_000, `Initial CSS budget exceeded: ${JSON.stringify(styles)}`);
    const chunks = readdirSync(new URL('_nuxt/', output)).filter(file => file.endsWith('.js')).map(file => `_nuxt/${file}`);
    const largestChunk = Math.max(...chunks.map(file => sizes([file]).bytes));
    assert(largestChunk <= 350_000, `JavaScript chunk budget exceeded: ${largestChunk} bytes`);
    return { initialJavaScript: javascript, initialCss: styles, largestChunk, lazySections: 5 };
}

if (import.meta.main) {
    const output = new URL('../.output/public/', import.meta.url);
    console.log(JSON.stringify(checkBundle(output), null, 2));
}
