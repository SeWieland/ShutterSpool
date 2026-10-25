using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ShutterSpool.AppHost;

namespace ShutterSpool.IntegrationTests;

public sealed class CameraIngressCertificateTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

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
