using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ShutterSpool.AppHost;

namespace ShutterSpool.IntegrationTests;

public sealed class CameraIngressCertificateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    [Fact]
    public void Default_callback_directory_cannot_also_hold_camera_facing_certificates()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        string cameraDirectory = Path.Combine(localData, "ShutterSpool", "callback-certificates");

        var error = Assert.Throws<InvalidOperationException>(() =>
            DevelopmentCallbackCertificate.Resolve(null, "aspire.dev.internal", cameraDirectory));

        Assert.Contains("require separate certificate directories", error.Message);
    }

    [Theory]
    [InlineData("aspire.dev.internal")]
    [InlineData("127.0.0.1")]
    public void Development_callback_creates_and_reuses_a_private_server_certificate(string hostname)
    {
        var (bundlePath, rootPath) = DevelopmentCallbackCertificate.Resolve(_directory, hostname, "");
        using var bundle = X509Certificate2.CreateFromPemFile(bundlePath, bundlePath);
        using var root = X509Certificate2.CreateFromPem(File.ReadAllText(rootPath));
        Assert.True(bundle.HasPrivateKey);
        Assert.True(bundle.MatchesHostname(hostname, allowWildcards: false, allowCommonName: false));
        Assert.NotEqual(root.Thumbprint, bundle.Thumbprint);
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(rootPath));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(bundlePath));

        Assert.Equal((bundlePath, rootPath), DevelopmentCallbackCertificate.Resolve(_directory, hostname, ""));
        Assert.Throws<InvalidOperationException>(() => DevelopmentCallbackCertificate.Resolve(_directory, "different.example", ""));
        using var reused = X509Certificate2.CreateFromPemFile(bundlePath, bundlePath);
        using var reusedRoot = X509Certificate2.CreateFromPem(File.ReadAllText(rootPath));
        Assert.Equal(bundle.Thumbprint, reused.Thumbprint);
        Assert.Equal(root.Thumbprint, reusedRoot.Thumbprint);
    }

    [Fact]
    public async Task Concurrent_first_starts_publish_one_callback_pair()
    {
        var starts = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            DevelopmentCallbackCertificate.Resolve(_directory, "aspire.dev.internal", ""), TestContext.Current.CancellationToken));
        var pairs = await Task.WhenAll(starts);
        Assert.Single(pairs.Distinct());
        Assert.Single(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public void Correcting_an_invalid_tunnel_hostname_recovers_first_start()
    {
        Assert.Throws<InvalidOperationException>(() => DevelopmentCallbackCertificate.Resolve(_directory, "localhost:8080", ""));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
        DevelopmentCallbackCertificate.Resolve(_directory, "aspire.dev.internal", "");
    }

    [Fact]
    public void Supplied_callback_pair_is_used_in_place()
    {
        using var supplied = new CertificateFixture(_directory);
        Assert.Equal((supplied.BundlePath, supplied.RootPath),
            DevelopmentCallbackCertificate.Resolve(_directory, "ingest.shutterspool.com", ""));
        Assert.Empty(Directory.GetDirectories(_directory));
    }

    [Theory]
    [InlineData("server.pem")]
    [InlineData("root.pem")]
    public void Development_callback_refuses_to_replace_a_partial_pair(string remainingFile)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, remainingFile);
        File.WriteAllText(path, "existing");
        Assert.Throws<InvalidOperationException>(() => DevelopmentCallbackCertificate.Resolve(_directory, "aspire.dev.internal", ""));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(_directory)));
        Assert.Equal("existing", File.ReadAllText(path));
    }

    [Fact]
    public void Validate_accepts_server_chain_and_matching_private_key()
    {
        using var certificates = new CertificateFixture(_directory);

        CameraIngressCertificate.Validate(certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com");
    }

    [Fact]
    public void Validate_accepts_server_directly_issued_by_trusted_root()
    {
        using var certificates = new CertificateFixture(_directory, includeIntermediate: false,
            includeIntermediateInBundle: false);

        CameraIngressCertificate.Validate(certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com");
    }

    [Fact]
    public void Validate_rejects_a_private_key_in_the_public_root_file()
    {
        using var certificates = new CertificateFixture(_directory);
        using RSA key = RSA.Create(2048);
        File.AppendAllText(certificates.RootPath, Environment.NewLine + key.ExportPkcs8PrivateKeyPem());

        Assert.Throws<InvalidOperationException>(() =>
            CameraIngressCertificate.Validate(certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com"));
    }

    [Fact]
    public void Callback_validation_rejects_a_second_authority_in_the_public_root_file()
    {
        const string hostname = "ingest.shutterspool.com";
        using var certificates = new CertificateFixture(_directory);
        CameraIngressCertificate.ValidateCallback(certificates.BundlePath, certificates.RootPath, hostname);

        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Additional synthetic root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 additional = request.CreateSelfSigned(now.AddHours(-1), now.AddDays(1));
        File.AppendAllText(certificates.RootPath, Environment.NewLine + additional.ExportCertificatePem());

        CameraIngressCertificate.Validate(certificates.BundlePath, certificates.RootPath, hostname);
        Assert.Throws<InvalidOperationException>(() =>
            CameraIngressCertificate.ValidateCallback(certificates.BundlePath, certificates.RootPath, hostname));
    }

    [Fact]
    public void Validate_rejects_missing_intermediate_even_when_it_is_in_current_user_ca_store()
    {
        using var certificates = new CertificateFixture(_directory, includeIntermediateInBundle: false);
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificates.IntermediateCertificate);

        try
        {
            Assert.Throws<InvalidOperationException>(() => CameraIngressCertificate.Validate(
                certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com"));
        }
        finally
        {
            store.Remove(certificates.IntermediateCertificate);
        }
    }

    [Fact]
    public void Validate_rejects_wrong_hostname()
    {
        using var certificates = new CertificateFixture(_directory);

        Assert.Throws<InvalidOperationException>(() => CameraIngressCertificate.Validate(
            certificates.BundlePath, certificates.RootPath, "other.shutterspool.com"));
    }

    [Fact]
    public void Validate_rejects_certificate_without_matching_private_key()
    {
        using var certificates = new CertificateFixture(_directory, mismatchedKey: true);

        Assert.Throws<InvalidOperationException>(() => CameraIngressCertificate.Validate(
            certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com"));
    }

    [Fact]
    public void Validate_rejects_untrusted_chain()
    {
        using var certificates = new CertificateFixture(_directory, wrongRoot: true);

        Assert.Throws<InvalidOperationException>(() => CameraIngressCertificate.Validate(
            certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com"));
    }

    [Fact]
    public void Validate_rejects_expired_certificate()
    {
        using var certificates = new CertificateFixture(_directory, expired: true);

        Assert.Throws<InvalidOperationException>(() => CameraIngressCertificate.Validate(
            certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com"));
    }

    [Fact]
    public void Validate_rejects_certificate_without_server_authentication_usage()
    {
        using var certificates = new CertificateFixture(_directory, serverAuthentication: false);

        Assert.Throws<InvalidOperationException>(() => CameraIngressCertificate.Validate(
            certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com"));
    }

    [Fact]
    public void Validate_rejects_certificate_without_san()
    {
        using var certificates = new CertificateFixture(_directory, includeSan: false);

        Assert.Throws<InvalidOperationException>(() => CameraIngressCertificate.Validate(
            certificates.BundlePath, certificates.RootPath, "ingest.shutterspool.com"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class CertificateFixture : IDisposable
    {
        private readonly RSA _rootKey = RSA.Create(2048);
        private readonly RSA _intermediateKey = RSA.Create(2048);
        private readonly RSA _leafKey = RSA.Create(2048);
        private readonly RSA _otherKey = RSA.Create(2048);
        private readonly RSA _wrongRootKey = RSA.Create(2048);

        public CertificateFixture(string directory, bool mismatchedKey = false, bool wrongRoot = false,
            bool includeIntermediate = true, bool includeIntermediateInBundle = true,
            bool expired = false, bool serverAuthentication = true,
            bool includeSan = true)
        {
            Directory.CreateDirectory(directory);
            BundlePath = Path.Combine(directory, "server.pem");
            RootPath = Path.Combine(directory, "root.pem");

            var rootRequest = new CertificateRequest("CN=Test Root", _rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(30));

            var intermediateRequest = new CertificateRequest("CN=Test Intermediate", _intermediateKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            using var issuedIntermediate = intermediateRequest.Create(root, DateTimeOffset.UtcNow.AddDays(-50),
                DateTimeOffset.UtcNow.AddDays(20), RandomNumberGenerator.GetBytes(16));
            using var intermediate = issuedIntermediate.CopyWithPrivateKey(_intermediateKey);
            IntermediateCertificate = X509CertificateLoader.LoadCertificate(intermediate.RawData);

            var leafRequest = new CertificateRequest("CN=ingest.shutterspool.com", _leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (includeSan)
            {
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("ingest.shutterspool.com");
                leafRequest.CertificateExtensions.Add(names.Build());
            }
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            var eku = new OidCollection { new(serverAuthentication ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") };
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
            DateTimeOffset leafNotBefore = expired ? DateTimeOffset.UtcNow.AddDays(-30) : DateTimeOffset.UtcNow.AddDays(-1);
            DateTimeOffset leafNotAfter = expired ? DateTimeOffset.UtcNow.AddDays(-20) : DateTimeOffset.UtcNow.AddDays(10);
            using var issued = leafRequest.Create(includeIntermediate ? intermediate : root,
                leafNotBefore, leafNotAfter, RandomNumberGenerator.GetBytes(16));
            using var leaf = issued.CopyWithPrivateKey(_leafKey);
            var unrelatedRootRequest = new CertificateRequest("CN=Wrong Root", _wrongRootKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            unrelatedRootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            unrelatedRootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            using var unrelatedRoot = unrelatedRootRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            File.WriteAllText(BundlePath,
                leaf.ExportCertificatePem() + Environment.NewLine
                + (includeIntermediateInBundle ? intermediate.ExportCertificatePem() + Environment.NewLine : "")
                + (mismatchedKey ? _otherKey.ExportPkcs8PrivateKeyPem() : _leafKey.ExportPkcs8PrivateKeyPem()));
            File.WriteAllText(RootPath, (wrongRoot ? unrelatedRoot : root).ExportCertificatePem());
        }

        public string BundlePath { get; }
        public string RootPath { get; }
        public X509Certificate2 IntermediateCertificate { get; }

        public void Dispose()
        {
            _rootKey.Dispose();
            _intermediateKey.Dispose();
            _leafKey.Dispose();
            _otherKey.Dispose();
            _wrongRootKey.Dispose();
            IntermediateCertificate.Dispose();
        }
    }
}
