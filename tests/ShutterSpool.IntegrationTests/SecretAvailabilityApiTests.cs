using System.Security.Cryptography;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Invalid_protection_key_blocks_workspace_startup_before_any_source_admission()
    {
        CryptographicException failure = await Assert.ThrowsAsync<CryptographicException>(
            () => ConfigurationApiFixture.CreateAsync(invalidEncryptionKey: true));
        Assert.DoesNotContain("synthetic-invalid-base64", failure.Message, StringComparison.Ordinal);
        Assert.Contains("encryption key", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
