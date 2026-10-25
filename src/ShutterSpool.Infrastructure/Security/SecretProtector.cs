using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace ShutterSpool.Infrastructure.Security;

internal sealed class SecretProtector(IOptions<CredentialProtectionOptions> options) : ShutterSpool.Application.Security.ICredentialProtector
{
    public string CreateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public byte[] CreateVerifier(string secret) => HMACSHA256.HashData(GetKey(), System.Text.Encoding.UTF8.GetBytes(secret));

    public bool Verify(string secret, byte[] verifier) => CryptographicOperations.FixedTimeEquals(CreateVerifier(secret), verifier);

    public string Protect(string secret)
    {
        byte[] key = GetKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes(secret);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. ciphertext]);
    }

    public string Unprotect(string protectedSecret)
    {
        byte[] payload = Convert.FromBase64String(protectedSecret);
        if (payload.Length < 28)
        {
            throw new CryptographicException("Invalid encrypted secret.");
        }

        byte[] key = GetKey();
        byte[] plaintext = new byte[payload.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload[..12], payload[28..], payload[12..28], plaintext);
        return System.Text.Encoding.UTF8.GetString(plaintext);
    }

    private byte[] GetKey()
    {
        if (string.IsNullOrWhiteSpace(options.Value.EncryptionKey))
        {
            throw new CryptographicException("The ShutterSpool encryption key is not configured.");
        }

        byte[] key;
        try { key = Convert.FromBase64String(options.Value.EncryptionKey); }
        catch (FormatException exception) { throw new CryptographicException("The ShutterSpool encryption key is invalid.", exception); }
        if (key.Length != 32)
        {
            throw new CryptographicException("The ShutterSpool encryption key must be 32 bytes.");
        }

        return key;
    }
}
