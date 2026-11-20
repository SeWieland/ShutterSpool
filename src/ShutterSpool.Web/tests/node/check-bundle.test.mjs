import assert from 'node:assert/strict';
import { mkdirSync, mkdtempDisposableSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, sep } from 'node:path';
import { pathToFileURL } from 'node:url';
import { gzipSync } from 'node:zlib';
import { onTestFinished, test } from 'vitest';
import { checkBundle } from '../../scripts/check-bundle.mjs';

const files = {
    'entry.js': 'void "entry";',
    'page.js': 'void "initial page";',
    'shared.js': 'void "shared dependency";',
    'app.css': 'body{color:white}',
    'page.css': 'main{margin:auto}',
};
const workspaceChunk = 'export const inactiveWorkspace = "lazy";';

function fixture(extraHtml = '') {
    const { path, remove } = mkdtempDisposableSync(join(tmpdir(), 'shutterspool-bundle-'));
    onTestFinished(remove);
    const assets = join(path, '_nuxt');
    mkdirSync(assets);
    for (const [file, text] of Object.entries(files)) writeFileSync(join(assets, file), text);

    const manifest = {
        entry: { file: '_nuxt/entry.js', css: ['_nuxt/app.css'], isEntry: true },
        page: { file: '_nuxt/page.js', imports: ['shared'] },
        shared: { file: '_nuxt/shared.js' },
    };
    for (const name of ['OperationalWorkspace', 'SourceWorkspace', 'DeliveryWorkspace', 'RetentionSettings', 'TrashWorkspace']) {
        manifest[`components/${name}.vue`] = { file: `_nuxt/${name}.js`, isDynamicEntry: true };
        writeFileSync(join(assets, `${name}.js`), workspaceChunk);
    }
    writeFileSync(join(path, 'manifest.json'), JSON.stringify(manifest));
    writeFileSync(join(path, 'index.html'), `
        <script type="module" src="/_nuxt/entry.js"></script>
        <link rel="modulepreload" href="/_nuxt/page.js">
        <link rel="stylesheet" href="/_nuxt/page.css">
        ${extraHtml}
    `);
    return pathToFileURL(path + sep);
}

function size(...contents) {
    return {
        bytes: Buffer.byteLength(contents.join('')),
        gzip: contents.reduce((total, text) => total + gzipSync(text).length, 0),
    };
}

test('bundle check includes HTML preloads, their imports and initial styles', () => {
    assert.deepEqual(checkBundle(fixture()), {
        initialJavaScript: size(files['entry.js'], files['page.js'], files['shared.js']),
        initialCss: size(files['app.css'], files['page.css']),
        largestChunk: Buffer.byteLength(workspaceChunk),
        lazySections: 5,
    });
});

test('bundle check rejects a TrashWorkspace module preload by name', () => {
    const output = fixture('<link rel="modulepreload" href="/_nuxt/TrashWorkspace.js">');
    assert.throws(() => checkBundle(output), {
        message: 'TrashWorkspace must remain outside the initial static graph.',
    });
});
