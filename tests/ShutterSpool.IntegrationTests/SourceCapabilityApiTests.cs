using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ShutterSpool.Server.Api.Sources;
using ShutterSpool.Server.Hosting;
using ShutterSpool.Application.Sources;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public void Camera_password_policy_accepts_all_configured_minimums_and_rejects_out_of_range_values()
    {
        for (int minimum = 1; minimum <= CameraCredentialPolicy.MaximumLength; minimum++)
        {
            var policy = new CameraCredentialPolicy { MinimumLength = minimum };
            policy.Validate();
            Assert.Equal(Math.Max(20, minimum), policy.GeneratedLength);
            Assert.Equal(minimum < 12, policy.WeakerProtection);
        }

        foreach (int invalidMinimum in new[] { 0, 129 })
            Assert.Throws<InvalidOperationException>(() => new CameraCredentialPolicy { MinimumLength = invalidMinimum }.Validate());
    }

    [Fact]
    public async Task Capabilities_return_only_valid_public_roots_and_preserve_disabled_profiles()
    {
        string rootDirectory = Path.Combine(Path.GetTempPath(), $"shutterspool-ca-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootDirectory);
        string rootPath = Path.Combine(rootDirectory, "root.pem");
        var settings = new CameraIngressSettings
        {
            Enabled = true,
            Hostname = "ingest.example.test",
            Profiles = new CameraIngressProfiles
            {
                ModernFtps = new() { Enabled = true, ControlPort = 2321, CaRootPath = rootPath },
                LegacyFtps = new() { Enabled = false, ControlPort = 2338 },
                PlainFtpLan = new() { Enabled = true, ControlPort = 2355 },
            },
        };

        try
        {
            await using var fixture = await ConfigurationApiFixture.CreateAsync(profileSettings: settings);
            using HttpResponseMessage initial = await fixture.Client.GetAsync("/management/source-capabilities", TestContext.Current.CancellationToken);
            initial.EnsureSuccessStatusCode();
            SourceCapabilitiesResponse capabilities = (await initial.Content.ReadFromJsonAsync<SourceCapabilitiesResponse>(
                TestContext.Current.CancellationToken))!;
            SourceProfileCapability disabled = Assert.Single(capabilities.Profiles, profile => profile.Profile == "LegacyFtps");
            Assert.False(disabled.Enabled);
            Assert.Null(disabled.Hostname);
            Assert.Null(disabled.ControlPort);

            await File.WriteAllTextAsync(rootPath, new string('x', 131_073), TestContext.Current.CancellationToken);
            await AssertRootUnavailableAsync(fixture, "exceeds the size limit");

            using (RSA leafKey = RSA.Create(2048))
            {
                var leafRequest = new CertificateRequest("CN=Not A CA", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using X509Certificate2 leaf = leafRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
                await File.WriteAllTextAsync(rootPath, leaf.ExportCertificatePem(), TestContext.Current.CancellationToken);
                await AssertRootUnavailableAsync(fixture, "not a self-issued CA root");
            }

            using (RSA expiredKey = RSA.Create(2048))
            {
                var expiredRequest = CreateRootRequest(expiredKey, "CN=Expired Root");
                using X509Certificate2 expired = expiredRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(-1));
                await File.WriteAllTextAsync(rootPath, expired.ExportCertificatePem(), TestContext.Current.CancellationToken);
                await AssertRootUnavailableAsync(fixture, "not a self-issued CA root");
            }

            using (RSA rootKey = RSA.Create(2048))
            {
                CertificateRequest rootRequest = CreateRootRequest(rootKey, "CN=Synthetic Root");
                using X509Certificate2 root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(2));
                string publicPem = root.ExportCertificatePem();
                await File.WriteAllTextAsync(rootPath, publicPem + rootKey.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);
                await AssertRootUnavailableAsync(fixture, "private key material");

                await File.WriteAllTextAsync(rootPath, publicPem, TestContext.Current.CancellationToken);
                using HttpResponseMessage validResponse = await fixture.Client.GetAsync("/management/source-capabilities", TestContext.Current.CancellationToken);
                SourceCapabilitiesResponse valid = (await validResponse.Content.ReadFromJsonAsync<SourceCapabilitiesResponse>(
                    TestContext.Current.CancellationToken))!;
                SourceProfileCapability modern = Assert.Single(valid.Profiles, profile => profile.Profile == "ModernFtps");
                Assert.True(modern.Enabled);
                Assert.Equal("ingest.example.test", modern.Hostname);
                Assert.Equal(2321, modern.ControlPort);
                Assert.True(modern.CaRootAvailable);
                Assert.Equal(root.Subject, modern.CaRootIdentity);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(root.RawData)), modern.CaRootFingerprint);
                Assert.Null(modern.CaRootReferenceUrl);

                using HttpResponseMessage download = await fixture.Client.GetAsync("/management/source-capabilities/ModernFtps/ca-root",
                    TestContext.Current.CancellationToken);
                Assert.Equal("application/x-pem-file", download.Content.Headers.ContentType?.MediaType);
                Assert.Equal(publicPem, await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static async Task AssertRootUnavailableAsync(ConfigurationApiFixture fixture, string expectedDetail)
    {
        using HttpResponseMessage response = await fixture.Client.GetAsync("/management/source-capabilities", TestContext.Current.CancellationToken);
        SourceCapabilitiesResponse capabilities = (await response.Content.ReadFromJsonAsync<SourceCapabilitiesResponse>(
            TestContext.Current.CancellationToken))!;
        SourceProfileCapability modern = Assert.Single(capabilities.Profiles, profile => profile.Profile == "ModernFtps");
        Assert.False(modern.CaRootAvailable);
        Assert.Contains(expectedDetail, modern.SetupError, StringComparison.OrdinalIgnoreCase);
        using HttpResponseMessage download = await fixture.Client.GetAsync("/management/source-capabilities/ModernFtps/ca-root",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, download.StatusCode);
        Assert.Contains("application/problem+json", download.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
    }

    private static CertificateRequest CreateRootRequest(RSA key, string subject)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request;
    }
}
