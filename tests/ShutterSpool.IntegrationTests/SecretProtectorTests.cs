using Microsoft.Extensions.Options;
using ShutterSpool.Infrastructure.Security;
using System.Security.Cryptography;

namespace ShutterSpool.IntegrationTests;

public sealed class SecretProtectorTests
{
    [Fact]
    public void Protects_destination_secrets_and_verifies_source_secrets_without_storing_them()
    {
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));

        string encrypted = protector.Protect("destination-secret");
        byte[] verifier = protector.CreateVerifier("source-secret");

        Assert.DoesNotContain("destination-secret", encrypted, StringComparison.Ordinal);
        Assert.Equal("destination-secret", protector.Unprotect(encrypted));
        Assert.True(protector.Verify("source-secret", verifier));
        Assert.False(protector.Verify("different-secret", verifier));
    }

    [Fact]
    public void Source_verifier_rejects_the_right_password_when_the_deployment_key_changes()
    {
        var original = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        var changed = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) }));
        byte[] verifier = original.CreateVerifier("synthetic-source-password");

        Assert.False(changed.Verify("synthetic-source-password", verifier));
    }

    [Fact]
    public void Fails_closed_when_the_deployment_key_is_missing()
    {
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions()));

        Assert.Throws<CryptographicException>(() => protector.Protect("destination-secret"));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AQID")]
    public void Invalid_deployment_key_is_reported_as_cryptographic_unavailability(string value)
    {
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = value }));

        Assert.Throws<CryptographicException>(() => protector.Protect("destination-secret"));
    }
}
