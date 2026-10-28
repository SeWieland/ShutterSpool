import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const sourceDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const MAX_METADATA_BYTES = 2 * 1024 * 1024;
const DIGEST = /^sha256:[a-f0-9]{64}$/;
const REVISION = /^[a-f0-9]{40}$/;
const LABELS = {
  version: 'org.opencontainers.image.version',
  revision: 'org.opencontainers.image.revision',
  source: 'org.opencontainers.image.source',
};

function sha256(data) {
  return createHash('sha256').update(data).digest('hex');
}

function readPinnedImage(compose) {
  const lines = compose.split(/\r?\n/);
  const start = lines.findIndex(line => /^  camera-edge:\s*(?:#.*)?$/.test(line));
  if (start < 0) throw new Error('camera-edge service not found in deploy/images/compose.yaml');
  const end = lines.findIndex((line, index) => index > start && /^  [^\s#][^:]*:\s*(?:#.*)?$/.test(line));
  const service = lines.slice(start + 1, end < 0 ? undefined : end);
  const imageLines = service.filter(line => /^    image:\s*/.test(line));
  if (imageLines.length !== 1) throw new Error('camera-edge must declare exactly one image');
  const image = imageLines[0].replace(/^    image:\s*/, '').trim();
  const match = /^drakkan\/sftpgo:([A-Za-z0-9._-]+)@(sha256:[a-f0-9]{64})$/.exec(image);
  if (!match) throw new Error(`camera-edge image must be a pinned drakkan/sftpgo reference: ${image}`);
  return { image, tag: match[1], digest: match[2] };
}

async function responseBytes(response, url, maxBytes) {
  if (!response.ok || response.status >= 300) throw new Error(`Request failed (${response.status}) for ${url}`);
  const declaredSize = Number(response.headers.get('content-length'));
  if (Number.isFinite(declaredSize) && declaredSize > maxBytes) throw new Error(`Response too large for ${url}`);
  const reader = response.body?.getReader();
  if (!reader) return Buffer.alloc(0);
  const chunks = [];
  let size = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) return Buffer.concat(chunks, size);
    size += value.byteLength;
    if (size > maxBytes) {
      await reader.cancel();
      throw new Error(`Response too large for ${url}`);
    }
    chunks.push(Buffer.from(value));
  }
}

async function get(fetchImpl, url, token, maxBytes) {
  const original = new URL(url);
  let current = original;
  for (let redirects = 0; redirects <= 1; redirects++) {
    const response = await fetchImpl(current, {
      headers: token && current.hostname === original.hostname ? { authorization: `Bearer ${token}` } : {},
      redirect: 'manual',
      signal: AbortSignal.timeout(20000),
    });
    if (response.status >= 300 && response.status < 400) {
      const location = response.headers.get('location');
      const next = location && new URL(location, current);
      if (redirects !== 0 || original.hostname !== 'registry-1.docker.io' || !original.pathname.includes('/blobs/') ||
          !next || next.protocol !== 'https:' || next.hostname !== 'production.cloudfront.docker.com' || next.username || next.password)
        throw new Error(`Untrusted redirect from ${original.hostname}`);
      current = next;
      continue;
    }
    return responseBytes(response, `${original.hostname}${original.pathname}`, maxBytes);
  }
  throw new Error(`Too many redirects from ${original.hostname}`);
}

function json(bytes, label) {
  try { return JSON.parse(bytes.toString('utf8')); }
  catch { throw new Error(`Invalid ${label} JSON`); }
}

async function dockerToken(fetchImpl) {
  const url = 'https://auth.docker.io/token?service=registry.docker.io&scope=repository%3Adrakkan%2Fsftpgo%3Apull';
  const data = json(await get(fetchImpl, url, undefined, MAX_METADATA_BYTES), 'Docker Hub token');
  if (typeof data.token !== 'string' || !data.token) throw new Error('Docker Hub returned no pull token');
  return data.token;
}

async function registryManifest(fetchImpl, reference, token, expectedDigest) {
  const url = `https://registry-1.docker.io/v2/drakkan/sftpgo/manifests/${reference}`;
  const bytes = await get(fetchImpl, url, token, MAX_METADATA_BYTES);
  if (expectedDigest && `sha256:${sha256(bytes)}` !== expectedDigest) throw new Error(`OCI manifest digest mismatch for ${reference}`);
  return json(bytes, 'OCI manifest');
}

async function resolveConfig(fetchImpl, pinned, token) {
  const root = await registryManifest(fetchImpl, pinned.digest, token, pinned.digest);
  let manifest = root;
  if (Array.isArray(root.manifests)) {
    const matches = root.manifests.filter(item => item.platform?.os === 'linux' && item.platform?.architecture === 'amd64');
    if (matches.length !== 1 || !DIGEST.test(matches[0].digest)) throw new Error('Pinned image must have exactly one linux/amd64 manifest');
    manifest = await registryManifest(fetchImpl, matches[0].digest, token, matches[0].digest);
  }
  if (!manifest.config || !DIGEST.test(manifest.config.digest)) throw new Error('OCI image manifest has no valid config digest');
  const url = `https://registry-1.docker.io/v2/drakkan/sftpgo/blobs/${manifest.config.digest}`;
  const configBytes = await get(fetchImpl, url, token, MAX_METADATA_BYTES);
  if (`sha256:${sha256(configBytes)}` !== manifest.config.digest) throw new Error('OCI config digest mismatch');
  const config = json(configBytes, 'OCI image config');
  if (config.os !== 'linux' || config.architecture !== 'amd64') throw new Error('Pinned image config is not linux/amd64');
  return config.config?.Labels;
}

function metadataFromLabels(labels, tag) {
  if (!labels || typeof labels !== 'object') throw new Error('OCI image metadata has no labels');
  const version = labels[LABELS.version];
  const revision = labels[LABELS.revision];
  const source = labels[LABELS.source];
  if (typeof version !== 'string' || !version || version.replace(/^v/, '') !== tag.replace(/^v/, '')) throw new Error(`OCI image version label is missing or does not match tag ${tag}`);
  if (typeof revision !== 'string' || !REVISION.test(revision)) throw new Error('OCI image revision label must be a 40-character Git commit');
  if (source !== 'https://github.com/drakkan/sftpgo') throw new Error('OCI image source label is not the allowed SFTPGo repository');
  return { version, revision };
}

function replaceSection(template, metadata) {
  template = template.replace(/\r\n/g, '\n');
  const startMark = '<!-- BEGIN GENERATED SFTPGo ATTRIBUTION -->';
  const endMark = '<!-- END GENERATED SFTPGo ATTRIBUTION -->';
  const start = template.indexOf(startMark);
  const end = template.indexOf(endMark);
  if (start < 0 || end < start || template.indexOf(startMark, start + 1) >= 0 || template.indexOf(endMark, end + 1) >= 0)
    throw new Error('THIRD-PARTY-NOTICES.md must contain one valid SFTPGo attribution marker section');
  const section = [
    startMark,
    '',
    `The packaged image is SFTPGo ${metadata.version.startsWith('v') ? metadata.version : `v${metadata.version}`}.`,
    '',
    `- [Matching SFTPGo source](${metadata.source})`,
    `- [GNU AGPL version 3 license](${metadata.license}), identified by upstream as AGPL-3.0-only`,
    `- [Additional attribution and source-access terms](${metadata.notice})`,
    '',
    endMark,
  ].join('\n');
  return template.slice(0, start) + section + template.slice(end + endMark.length);
}

export async function generateAttribution({ sourceRoot = sourceDir, outputDir = join(sourceRoot, 'artifacts', 'attribution'), fetchImpl = fetch } = {}) {
  const compose = await readFile(join(sourceRoot, 'deploy', 'images', 'compose.yaml'), 'utf8');
  const pinned = readPinnedImage(compose);
  const labels = await resolveConfig(fetchImpl, pinned, await dockerToken(fetchImpl));
  const { version, revision } = metadataFromLabels(labels, pinned.tag);
  const metadata = {
    image: pinned.image,
    version,
    revision,
    source: `https://github.com/drakkan/sftpgo/tree/${revision}`,
    license: `https://github.com/drakkan/sftpgo/blob/${revision}/LICENSE`,
    notice: `https://github.com/drakkan/sftpgo/blob/${revision}/NOTICE`,
  };
  await mkdir(outputDir, { recursive: true });
  const jsonPath = join(outputDir, 'sftpgo.json');
  const noticePath = join(outputDir, 'THIRD-PARTY-NOTICES.md');
  await writeFile(jsonPath, `${JSON.stringify(metadata, null, 2)}\n`);
  await writeFile(noticePath, replaceSection(await readFile(join(sourceRoot, 'THIRD-PARTY-NOTICES.md'), 'utf8'), metadata));
  return { jsonPath, noticePath, metadata };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.length !== 0 && (args.length !== 2 || args[0] !== '--output')) {
    console.error('Usage: node tools/generate-attribution.mjs [--output <directory>]');
    process.exitCode = 2;
  } else {
    const outputDir = args.length ? resolve(args[1]) : undefined;
    generateAttribution({ outputDir }).then(({ metadata }) => {
      console.log(`Generated attribution for ${metadata.image}`);
    }).catch(error => {
      console.error(error.message);
      process.exitCode = 1;
    });
  }
}
