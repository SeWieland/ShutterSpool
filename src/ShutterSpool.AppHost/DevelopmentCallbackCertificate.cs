using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace ShutterSpool.AppHost;

internal static class DevelopmentCallbackCertificate
{
    public static (string CertificatePath, string TrustPath) Resolve(string? directory, string hostname, string cameraDirectory)
    {
        if (directory is null)
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            if (!Path.IsPathFullyQualified(localData))
                throw new InvalidOperationException("Set ShutterSpool:CameraIngress:CallbackCertificateDirectory to a private directory.");
            directory = Path.Combine(localData, "ShutterSpool", "callback-certificates");
        }
        directory = Path.GetFullPath(directory);
        RequireSeparateDirectory(directory, cameraDirectory);
        if (!Path.Exists(Path.Combine(directory, "server.pem")) && !Path.Exists(Path.Combine(directory, "root.pem")))
        {
            directory = Path.Combine(directory, "generated");
            RequireSeparateDirectory(directory, cameraDirectory);
            if (!Path.Exists(directory)) PublishPair(directory, hostname);
        }
        string bundlePath = Path.Combine(directory, "server.pem");
        string rootPath = Path.Combine(directory, "root.pem");
        CameraIngressCertificate.ValidateCallback(bundlePath, rootPath, hostname);
        return (bundlePath, rootPath);
    }

    private static void RequireSeparateDirectory(string directory, string cameraDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (cameraDirectory.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(directory),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(cameraDirectory)), comparison))
            throw new InvalidOperationException("Camera-facing FTPS and private HTTPS callbacks require separate certificate directories.");
    }

    private static void PublishPair(string directory, string hostname)
    {
        string staging = Path.Combine(Path.GetDirectoryName(directory)!, ".callback-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
            new DirectoryInfo(staging).Create(security);
        }
        else Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Generate(staging, hostname);
            CameraIngressCertificate.ValidateCallback(Path.Combine(staging, "server.pem"), Path.Combine(staging, "root.pem"), hostname);
            // Publish both files together. Concurrent starts reuse the first complete pair.
            try { Directory.Move(staging, directory); }
            catch (IOException) when (Directory.Exists(directory)) { }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void Generate(string directory, string hostname)
    {
        var names = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(hostname, out var address)) names.AddIpAddress(address);
        else names.AddDnsName(hostname);

        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=ShutterSpool development callback CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var ca = caRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(2));

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ShutterSpool development callback", serverKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var server = request.Create(ca, now.AddMinutes(-5), now.AddYears(1), RandomNumberGenerator.GetBytes(16));

        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var bundle = new StreamWriter(new FileStream(Path.Combine(directory, "server.pem"), options));
        bundle.WriteLine(server.ExportCertificatePem());
        bundle.WriteLine(serverKey.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(directory, "root.pem"), ca.ExportCertificatePem());
    }
}
