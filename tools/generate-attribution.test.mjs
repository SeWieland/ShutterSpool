import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, onTestFinished, test } from 'vitest';
import { generateAttribution } from './generate-attribution.mjs';

const revision = '9888a3d169aed9011ae6e4f7a97ae735c1643068';
const hash = value => createHash('sha256').update(value).digest('hex');
const noticeTemplate = 'Intro\r\n\r\n<!-- BEGIN GENERATED SFTPGo ATTRIBUTION -->\r\nold\r\n<!-- END GENERATED SFTPGo ATTRIBUTION -->\r\n\r\nFooter\r\n';

async function fixture({ platform = { os: 'linux', architecture: 'amd64' }, version = '2.7.5', imageRevision = revision, labels = {
  'org.opencontainers.image.version': version,
  'org.opencontainers.image.revision': imageRevision,
  'org.opencontainers.image.source': 'https://github.com/drakkan/sftpgo',
}, pinDigest, configDigestOverride, redirectHost, oversizedResponse, notices = noticeTemplate } = {}) {
  const root = await mkdtemp(join(tmpdir(), 'attribution-'));
  onTestFinished(() => rm(root, { recursive: true, force: true }));
  const outputDir = join(root, 'output');
  await mkdir(join(root, 'deploy', 'images'), { recursive: true });
  await writeFile(join(root, 'THIRD-PARTY-NOTICES.md'), notices);

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
    const parsed = new URL(value);
    if (value.startsWith('https://auth.docker.io/')) return oversizedResponse
      ? new Response('x'.repeat(2 * 1024 * 1024 + 1)) : Response.json({ token: 'test-token' });
    if (value.includes('/manifests/sha256:')) {
      const requested = value.slice(value.lastIndexOf('/') + 1);
      if (requested === image.split('@')[1]) return response(indexBytes);
      if (requested === imageDigest) return response(imageManifestBytes, 'application/vnd.oci.image.manifest.v1+json');
    }
    if (value.includes('/blobs/')) {
      if (redirectHost === null) return new Response(null, { status: 307 });
      if (redirectHost) return new Response(null, { status: 307, headers: { location: `https://${redirectHost}/signed/blob?X-Amz-Signature=synthetic-signature` } });
      return response(configBytes);
    }
    if (parsed.pathname === '/signed/blob') {
      redirectedAuth = init.headers.authorization;
      return response(configBytes);
    }
    throw new Error(`Unexpected URL ${value}`);
  };
  return { root, outputDir, image, fetchImpl, get redirectedAuth() { return redirectedAuth; } };
}

test('generates attribution and matching source links from the exact pinned image metadata', async () => {
  const f = await fixture();
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
  assert.doesNotMatch(noticeText, /\r/);
});

describe('rejects duplicate attribution markers', () => {
  test.each(['BEGIN', 'END'])('duplicate %s marker', async marker => {
    const f = await fixture({ notices: `${noticeTemplate}<!-- ${marker} GENERATED SFTPGo ATTRIBUTION -->\r\n` });
    await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }),
      /must contain one valid SFTPGo attribution marker section/);
  });
});

test('updates attribution automatically for a new version and revision', async () => {
  const nextRevision = 'a'.repeat(40);
  const f = await fixture({ version: 'v2.7.6', imageRevision: nextRevision });
  const result = await generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl });
  const attribution = JSON.parse(await readFile(result.jsonPath, 'utf8'));
  assert.equal(attribution.image, f.image);
  assert.equal(attribution.version, 'v2.7.6');
  assert.equal(attribution.revision, nextRevision);
  assert.equal(attribution.source, `https://github.com/drakkan/sftpgo/tree/${nextRevision}`);
});

describe('rejects malformed or unsupported image metadata', () => {
  const completeLabels = {
    'org.opencontainers.image.version': '2.7.5',
    'org.opencontainers.image.revision': revision,
    'org.opencontainers.image.source': 'https://github.com/drakkan/sftpgo',
  };
  test.each([
    ['missing version label', { labels: { ...completeLabels, 'org.opencontainers.image.version': undefined } }, /version/],
    ['missing revision label', { labels: { ...completeLabels, 'org.opencontainers.image.revision': undefined } }, /revision/],
    ['missing source label', { labels: { ...completeLabels, 'org.opencontainers.image.source': undefined } }, /source/],
    ['foreign source repository', { labels: { 'org.opencontainers.image.version': '2.7.5', 'org.opencontainers.image.revision': revision, 'org.opencontainers.image.source': 'https://evil.example/sftpgo' } }, /source/],
    ['wrong platform', { platform: { os: 'linux', architecture: 'arm64' } }, /linux\/amd64/],
  ])('%s', async (_name, overrides, message) => {
    const f = await fixture(overrides);
    await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), message);
  });
});

test('rejects a registry manifest that does not match the pinned image digest', async () => {
  const f = await fixture({ pinDigest: 'sha256:' + '1'.repeat(64) });
  await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), /digest mismatch/);
});

test('rejects an OCI config blob that does not match its declared digest', async () => {
  const f = await fixture({ configDigestOverride: 'sha256:' + '2'.repeat(64) });
  await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), /OCI config digest mismatch/);
});

describe('follows Docker Hub blob redirects without forwarding registry credentials', () => {
  test.each([
    ['CloudFront', 'production.cloudfront.docker.com'],
    ['S3', 'docker-images-prod.s3.dualstack.us-east-1.amazonaws.com'],
  ])('%s', async (_name, redirectHost) => {
    const f = await fixture({ redirectHost });
    const result = await generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl });
    assert.equal(result.metadata.revision, revision);
    assert.equal(f.redirectedAuth, undefined);
  });
});

test('rejects other S3 buckets and lookalike Docker blob hosts', async () => {
  for (const redirectHost of [
    'other-bucket.s3.dualstack.us-east-1.amazonaws.com',
    'docker-images-prod.s3.dualstack.us-east-1.amazonaws.com.evil.example',
  ]) {
    const f = await fixture({ redirectHost });
    await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), /Untrusted redirect/);
  }
});

test('reports an untrusted blob redirect hostname without URL credentials or signed query', async () => {
  for (const redirectHost of ['evil.example', 'private-user:private-password@evil.example']) {
    const f = await fixture({ redirectHost });
    await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }),
      { message: 'Untrusted redirect from registry-1.docker.io to evil.example' });
  }
});

test('rejects a blob redirect without a Location header', async () => {
  const f = await fixture({ redirectHost: null });
  await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }),
    { message: 'Untrusted redirect from registry-1.docker.io to (missing hostname)' });
});

test('bounds registry response content', async () => {
  const f = await fixture({ oversizedResponse: true });
  await assert.rejects(generateAttribution({ sourceRoot: f.root, outputDir: f.outputDir, fetchImpl: f.fetchImpl }), /Response too large/);
});
