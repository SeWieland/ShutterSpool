// PowerShell Add-Type also compiles this file without the project's global imports or nullable settings.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ShutterSpool.AppHost
{
    public static class CameraIngressCertificate
    {
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

        public static void Validate(string bundlePath, string rootPath, string hostname)
        {
            try
            {
                ValidateCertificate(bundlePath, rootPath, hostname);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
                || exception is CryptographicException || exception is ArgumentException || exception is InvalidOperationException)
            {
                throw new InvalidOperationException(
                    "Camera ingress certificate is missing or invalid. Check the certificate, private key, hostname, validity, and trusted root.",
                    exception);
            }
        }

        private static void ValidateCertificate(string bundlePath, string rootPath, string hostname)
        {
            if (string.IsNullOrWhiteSpace(hostname))
                throw new ArgumentException("A certificate hostname is required.");

            string bundle = File.ReadAllText(bundlePath);
            string rootPem = File.ReadAllText(rootPath);
            var certificates = new X509Certificate2Collection();
            certificates.ImportFromPem(bundle);
            if (certificates.Count == 0)
                throw new CryptographicException("The bundle must contain a leaf certificate.");

            try
            {
                using X509Certificate2 server = X509Certificate2.CreateFromPem(bundle, bundle);
                using X509Certificate2 trustedRoot = X509Certificate2.CreateFromPem(rootPem);
                using var chain = new X509Chain();
                if (!server.HasPrivateKey
                    || !server.Extensions.Cast<X509Extension>().Any(extension => extension.Oid?.Value == "2.5.29.17")
                    || !server.MatchesHostname(hostname, allowWildcards: true, allowCommonName: false))
                    throw new CryptographicException("The certificate does not match its private key and hostname.");

                DateTime now = DateTime.UtcNow;
                if (now < server.NotBefore.ToUniversalTime() || now > server.NotAfter.ToUniversalTime())
                    throw new CryptographicException("The certificate is outside its validity period.");

                X509EnhancedKeyUsageExtension? enhancedKeyUsage = server.Extensions
                    .OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
                if (enhancedKeyUsage == null || !enhancedKeyUsage.EnhancedKeyUsages
                    .Cast<Oid>().Any(oid => oid.Value == ServerAuthenticationOid))
                    throw new CryptographicException("The certificate is not valid for server authentication.");

                X509KeyUsageExtension? keyUsage = server.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
                using RSA? publicKey = server.GetRSAPublicKey();
                if (keyUsage == null || (keyUsage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0
                    || publicKey != null && (keyUsage.KeyUsages & X509KeyUsageFlags.KeyEncipherment) == 0)
                    throw new CryptographicException("The certificate key usage is not suitable for TLS.");

                if (!trustedRoot.Extensions.OfType<X509BasicConstraintsExtension>().Any(extension => extension.CertificateAuthority))
                    throw new CryptographicException("The trusted root is not a certificate authority.");

                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.DisableCertificateDownloads = true;
                foreach (X509Certificate2 certificate in certificates)
                    if (!certificate.Thumbprint.Equals(server.Thumbprint, StringComparison.OrdinalIgnoreCase))
                        chain.ChainPolicy.ExtraStore.Add(certificate);

                if (!chain.Build(server))
                    throw new CryptographicException("The certificate chain does not end at the supplied trusted root.");

                var suppliedThumbprints = new HashSet<string>(certificates.Cast<X509Certificate2>()
                    .Select(certificate => certificate.Thumbprint), StringComparer.OrdinalIgnoreCase);
                foreach (X509ChainElement element in chain.ChainElements)
                {
                    if (!element.Certificate.Thumbprint.Equals(trustedRoot.Thumbprint, StringComparison.OrdinalIgnoreCase)
                        && !suppliedThumbprints.Contains(element.Certificate.Thumbprint))
                        throw new CryptographicException("The bundle is missing a certificate required to build its chain.");
                }
            }
            finally
            {
                foreach (X509Certificate2 certificate in certificates)
                    certificate.Dispose();
            }
        }
    }
}
