using Microsoft.Extensions.Options;
using ShutterSpool.Application.Retention;
using ShutterSpool.Infrastructure.Security;

namespace ShutterSpool.IntegrationTests;

public sealed class PrivacyErasureEvidenceCodecTests
{
    [Fact]
    public void Protected_suppression_envelopes_reject_malformed_tampered_and_cross_purpose_payloads()
    {
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions
        {
            EncryptionKey = Convert.ToBase64String(new byte[32]),
        }));
        var codec = new PrivacyErasureEvidenceCodec(protector);
        var manifest = new PrivacyErasureSuppressionManifest(1, Guid.NewGuid(), DateTimeOffset.UtcNow,
            1, PrivacyErasureSuppressionService.PageSize, 1, new string('A', 64));
        var page = new PrivacyErasureSuppressionPage(manifest.SnapshotId, 0,
        [new PrivacyErasureSuppressionRecord(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "spool/synthetic/original", 3, [], "synthetic", DateTimeOffset.UtcNow, true)]);

        string protectedManifest = codec.ProtectManifest(manifest);
        string protectedPage = codec.ProtectPage(page);
        Assert.True(codec.TryReadManifest(protectedManifest, out PrivacyErasureSuppressionManifest? decodedManifest));
        Assert.Equal(manifest, decodedManifest);
        Assert.True(codec.TryReadPage(protectedPage, out PrivacyErasureSuppressionPage? decodedPage));
        Assert.Equal(page.SnapshotId, decodedPage!.SnapshotId);
        Assert.Equal(page.PageIndex, decodedPage.PageIndex);
        PrivacyErasureSuppressionRecord original = Assert.Single(page.Records);
        PrivacyErasureSuppressionRecord restored = Assert.Single(decodedPage.Records);
        Assert.Equal(original.OperationId, restored.OperationId);
        Assert.Equal(original.CaptureId, restored.CaptureId);
        Assert.Equal(original.AssetId, restored.AssetId);
        Assert.Equal(original.SpoolKey, restored.SpoolKey);
        Assert.Equal(original.ExpectedSize, restored.ExpectedSize);
        Assert.Equal(original.StorageMarker, restored.StorageMarker);
        Assert.Equal(original.ErasedAt, restored.ErasedAt);
        Assert.Equal(original.Completed, restored.Completed);
        Assert.Empty(restored.KnownTransientKeys);

        Assert.False(codec.TryReadManifest("m205-erasure-manifest-v1:not-base64", out _));
        Assert.False(codec.TryReadManifest(protectedPage, out _));
        Assert.False(codec.TryReadPage(protectedManifest, out _));
        Assert.False(codec.TryReadManifest(protectedPage.Replace("m205-erasure-page-v1:",
            "m205-erasure-manifest-v1:", StringComparison.Ordinal), out _));
        Assert.False(codec.TryReadPage(protectedManifest.Replace("m205-erasure-manifest-v1:",
            "m205-erasure-page-v1:", StringComparison.Ordinal), out _));
        Assert.False(codec.TryReadManifest(Tamper(protectedManifest, "m205-erasure-manifest-v1:"), out _));
        Assert.False(codec.TryReadPage(Tamper(protectedPage, "m205-erasure-page-v1:"), out _));
    }

    private static string Tamper(string envelope, string prefix)
    {
        byte[] ciphertext = Convert.FromBase64String(envelope[prefix.Length..]);
        ciphertext[12] ^= 0x01;
        return prefix + Convert.ToBase64String(ciphertext);
    }
}
