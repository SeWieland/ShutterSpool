import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { generateAttribution } from './generate-attribution.mjs';

const revision = '9888a3d169aed9011ae6e4f7a97ae735c1643068';
const hash = value => createHash('sha256').update(value).digest('hex');

async function fixture({ platform = { os: 'linux', architecture: 'amd64' }, version = '2.7.5', imageRevision = revision, labels = {
  'org.opencontainers.image.version': version,
  'org.opencontainers.image.revision': imageRevision,
  'org.opencontainers.image.source': 'https://github.com/drakkan/sftpgo',
}, pinDigest, configDigestOverride, redirectHost, oversizedResponse } = {}) {
  const root = await mkdtemp(join(tmpdir(), 'attribution-'));
  const outputDir = join(root, 'output');
  await mkdir(join(root, 'deploy', 'images'), { recursive: true });
  await writeFile(join(root, 'THIRD-PARTY-NOTICES.md'), 'Intro\n\n<!-- BEGIN GENERATED SFTPGo ATTRIBUTION -->\nold\n<!-- END GENERATED SFTPGo ATTRIBUTION -->\n\nFooter\n');

  const configBytes = Buffer.from(JSON.stringify({ os: 'linux', architecture: 'amd64', config: { Labels: labels } }));
  const configDigest = 'sha256:' + hash(configBytes);
  const imageManifestBytes = Buffer.from(JSON.stringify({ schemaVersion: 2, config: { digest: configDigestOverride ?? configDigest }, layers: [] }));
  const imageDigest = 'sha256:' + hash(imageManifestBytes);
  const indexBytes = Buffer.from(JSON.stringify({ schemaVersion: 2, manifests: [{ digest: imageDigest, platform }] }));
  const image = `drakkan/sftpgo:v${version.replace(/^v/, '')}@${pinDigest ?? `sha256:${hash(indexBytes)}`}`;
  await writeFile(join(root, 'deploy', 'images', 'compose.yaml'), `services:\n  camera-edge:\n    image: ${image}\n`);
  const response = (body, contentType = 'application/vnd.oci.image.index.v1+json') => new Response(body, { status: 200, headers: { 'content-type': contentType } });
  let redirectedAuth;
  const fetchImpl = async (url, init) => {
    const value = String(url);
    if (value.startsWith('https://auth.docker.io/')) return oversizedResponse
      ? new Response('x'.repeat(2 * 1024 * 1024 + 1)) : Response.json({ token: 'test-token' });
    if (value.includes('/manifests/sha256:')) {
      const requested = value.slice(value.lastIndexOf('/') + 1);
      if (requested === image.split('@')[1]) return response(indexBytes);
      if (requested === imageDigest) return response(imageManifestBytes, 'application/vnd.oci.image.manifest.v1+json');
    }
    if (value.includes('/blobs/')) {
      if (redirectHost) return new Response(null, { status: 307, headers: { location: `https://${redirectHost}/signed/blob?secret=redacted` } });
      return response(configBytes);
    }
    if (value.includes('production.cloudfront.docker.com')) {
      redirectedAuth = init.headers.authorization;
      return response(configBytes);
    }
    throw new Error(`Unexpected URL ${value}`);
  };
  return { root, outputDir, image, fetchImpl, get redirectedAuth() { return redirectedAuth; } };
}

test('generates attribution and matching source links from the exact pinned image metadata', async t => {
  const f = await fixture();
  t.after(() => rm(f.root, { recursive: true, force: true }));
  const result = await generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl });
  const attribution = JSON.parse(await readFile(result.jsonPath, 'utf8'));
  const noticeText = await readFile(result.noticePath, 'utf8');

  assert.equal(attribution.image, f.image);
  assert.equal(attribution.version, '2.7.5');
  assert.equal(attribution.revision, revision);
  assert.equal(attribution.source, `https://github.com/drakkan/sftpgo/tree/${revision}`);
  assert.equal(attribution.license, `https://github.com/drakkan/sftpgo/blob/${revision}/LICENSE`);
  assert.equal(attribution.notice, `https://github.com/drakkan/sftpgo/blob/${revision}/NOTICE`);
  assert.match(noticeText, new RegExp(`tree/${revision}`));
  assert.match(noticeText, new RegExp(`blob/${revision}/LICENSE`));
  assert.match(noticeText, new RegExp(`blob/${revision}/NOTICE`));
  assert.match(noticeText, /Intro[\s\S]*Footer/);
});

test('updates attribution automatically for a new version and revision', async t => {
  const nextRevision = 'a'.repeat(40);
  const f = await fixture({ version: 'v2.7.6', imageRevision: nextRevision });
  t.after(() => rm(f.root, { recursive: true, force: true }));
  const result = await generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl });
  const attribution = JSON.parse(await readFile(result.jsonPath, 'utf8'));
  assert.equal(attribution.image, f.image);
  assert.equal(attribution.version, 'v2.7.6');
  assert.equal(attribution.revision, nextRevision);
  assert.equal(attribution.source, `https://github.com/drakkan/sftpgo/tree/${nextRevision}`);
});

test('rejects malformed or unsupported image metadata', async t => {
  const completeLabels = {
    'org.opencontainers.image.version': '2.7.5',
    'org.opencontainers.image.revision': revision,
    'org.opencontainers.image.source': 'https://github.com/drakkan/sftpgo',
  };
  for (const [name, overrides, message] of [
    ['missing version label', { labels: { ...completeLabels, 'org.opencontainers.image.version': undefined } }, /version/],
    ['missing revision label', { labels: { ...completeLabels, 'org.opencontainers.image.revision': undefined } }, /revision/],
    ['missing source label', { labels: { ...completeLabels, 'org.opencontainers.image.source': undefined } }, /source/],
    ['foreign source repository', { labels: { 'org.opencontainers.image.version': '2.7.5', 'org.opencontainers.image.revision': revision, 'org.opencontainers.image.source': 'https://evil.example/sftpgo' } }, /source/],
    ['wrong platform', { platform: { os: 'linux', architecture: 'arm64' } }, /linux\/amd64/],
  ]) {
    await t.test(name, async t => {
      const f = await fixture(overrides);
      t.after(() => rm(f.root, { recursive: true, force: true }));
      await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), message);
    });
  }
});

test('rejects a registry manifest that does not match the pinned image digest', async t => {
  const f = await fixture({ pinDigest: 'sha256:' + '1'.repeat(64) });
  t.after(() => rm(f.root, { recursive: true, force: true }));
  await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), /digest mismatch/);
});

test('rejects an OCI config blob that does not match its declared digest', async t => {
  const f = await fixture({ configDigestOverride: 'sha256:' + '2'.repeat(64) });
  t.after(() => rm(f.root, { recursive: true, force: true }));
  await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), /OCI config digest mismatch/);
});

test('follows only the Docker Hub blob CDN over HTTPS without forwarding registry credentials', async t => {
  const f = await fixture({ redirectHost: 'production.cloudfront.docker.com' });
  t.after(() => rm(f.root, { recursive: true, force: true }));
  await generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl });
  assert.equal(f.redirectedAuth, undefined);
});

test('rejects a blob redirect to an untrusted host', async t => {
  const f = await fixture({ redirectHost: 'evil.example' });
  t.after(() => rm(f.root, { recursive: true, force: true }));
  await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), /Untrusted redirect/);
});

test('bounds registry response content', async t => {
  const f = await fixture({ oversizedResponse: true });
  t.after(() => rm(f.root, { recursive: true, force: true }));
  await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), /Response too large/);
});
