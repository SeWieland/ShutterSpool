using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ShutterSpool.Server.Composition;
using ShutterSpool.Server.Hosting;

namespace ShutterSpool.IntegrationTests;

public sealed class CameraListenerTests : IDisposable
{
    private const string CallbackKey = "synthetic-camera-callback-listener-key";
    private readonly string _callbackCertificatePath = Path.Combine(Path.GetTempPath(), $"shutterspool-callback-{Guid.NewGuid():N}.pem");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Camera_listener_preserves_public_bindings_and_restricts_routes_by_port(bool listenOnAllInterfaces)
    {
        using var loopback = ReservePort();
        using var explicitIp = ReservePort();
        using var anyIpv4 = ReservePort();
        using var wildcard = ReservePort();
        using var callback = ReservePort();
        int loopbackPort = Port(loopback);
        int httpsPort = Port(explicitIp);
        int anyIpv4Port = Port(anyIpv4);
        int wildcardPort = Port(wildcard);
        int callbackPort = Port(callback);
        string urls = $"http://localhost:{loopbackPort}; https://127.0.0.1:{httpsPort}; ;"
            + $"http://0.0.0.0:{anyIpv4Port};http://wildcard.invalid:{wildcardPort}";

        using RSA key = RSA.Create(2048);
        using X509Certificate2 generated = CreateCertificate(key, "Application listener test");
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        using RSA callbackKey = RSA.Create(2048);
        using X509Certificate2 callbackCertificate = CreateCertificate(callbackKey, "Camera listener test");
        await File.WriteAllTextAsync(_callbackCertificatePath,
            callbackCertificate.ExportCertificatePem() + "\n" + callbackKey.ExportPkcs8PrivateKeyPem(), Token);
        var builder = CreateBuilder(urls, callbackPort);
        builder.Configuration["ShutterSpool:CameraIngress:CallbackCertificatePath"] = _callbackCertificatePath;
        builder.Configuration["ShutterSpool:CameraIngress:InternalListenOnAllInterfaces"] = listenOnAllInterfaces.ToString();
        builder.WebHost.ConfigureKestrel(server => server.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
        builder.AddShutterSpoolServer();
        builder.Services.RemoveAll<IHostedService>();
        await using WebApplication app = builder.Build();
        app.MapShutterSpoolServer();
        foreach (TcpListener listener in new[] { loopback, explicitIp, anyIpv4, wildcard, callback }) listener.Stop();
        await app.StartAsync(Token);

        Assert.Contains($"http://localhost:{loopbackPort}", app.Urls);
        Assert.Contains($"https://127.0.0.1:{httpsPort}", app.Urls);
        Assert.Contains($"http://0.0.0.0:{anyIpv4Port}", app.Urls);
        Assert.Contains(app.Urls, address => address == $"http://[::]:{wildcardPort}" || address == $"http://0.0.0.0:{wildcardPort}");
        string callbackAddress = listenOnAllInterfaces ? "0.0.0.0" : "localhost";
        Assert.Contains($"https://{callbackAddress}:{callbackPort}", app.Urls);
        using HttpClient client = CreateClient(certificate, callbackCertificate);
        foreach (string origin in new[] { $"http://localhost:{loopbackPort}", $"https://127.0.0.1:{httpsPort}",
            $"http://127.0.0.1:{anyIpv4Port}", $"http://127.0.0.1:{wildcardPort}" })
        {
            using HttpResponseMessage publicRoute = await client.GetAsync($"{origin}/auth/config", Token);
            Assert.Equal(HttpStatusCode.OK, publicRoute.StatusCode);
        }

        client.DefaultRequestHeaders.Add("X-API-KEY", CallbackKey);
        using HttpResponseMessage normalPort = await client.PostAsJsonAsync($"http://localhost:{loopbackPort}/internal/camera/auth",
            new { username = "synthetic-camera", password = "synthetic-password" }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, normalPort.StatusCode);
        string callbackOrigin = $"https://127.0.0.1:{callbackPort}";
        using HttpResponseMessage privatePort = await client.GetAsync($"{callbackOrigin}/auth/config", Token);
        Assert.Equal(HttpStatusCode.NotFound, privatePort.StatusCode);
        using HttpResponseMessage privateHealth = await client.GetAsync($"{callbackOrigin}/health/live", Token);
        Assert.Equal(HttpStatusCode.OK, privateHealth.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{callbackPort}/health/live", Token));

        using HttpClient untrustedClient = CreateClient(certificate);
        await Assert.ThrowsAsync<HttpRequestException>(() => untrustedClient.GetAsync($"{callbackOrigin}/health/live", Token));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"https://localhost:{callbackPort}/health/live", Token));
        client.DefaultRequestHeaders.Remove("X-API-KEY");
        client.DefaultRequestHeaders.Add("X-API-KEY", "synthetic-wrong-callback-listener-key");
        using HttpResponseMessage wrongKey = await client.PostAsJsonAsync($"{callbackOrigin}/internal/camera/auth",
            new { username = "synthetic-camera", password = "synthetic-password" }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, wrongKey.StatusCode);
    }

    [Theory]
    [InlineData("not-a-url", "an invalid configured server URL")]
    [InlineData("http://localhost:0", "an invalid configured server URL")]
    [InlineData("http://*:5000", "an invalid configured server URL")]
    [InlineData("http://+:5000", "an invalid configured server URL")]
    [InlineData("ftp://localhost:5000", "the configured 'ftp' server URL")]
    public void Camera_listener_rejects_unusable_public_urls(string url, string expectedMessage)
    {
        var builder = CreateBuilder(url, 8081);
        builder.AddShutterSpoolServer();
        builder.Services.RemoveAll<IHostedService>();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData("http://localhost:8081/internal/camera")]
    [InlineData("https://localhost:0/internal/camera")]
    [InlineData("https://localhost:8081/internal/other")]
    [InlineData("https://fixture@localhost:8081/internal/camera")]
    [InlineData("https://localhost:8081/internal/camera?fixture=value")]
    [InlineData("https://localhost:8081/internal/camera#fixture")]
    public void Enabled_camera_ingress_requires_an_https_callback_base(string url)
    {
        var settings = new CameraIngressSettings { Enabled = true, CallbackKey = CallbackKey, CallbackBaseUrl = url };
        Assert.Contains("an HTTPS URL pointing to /internal/camera", Assert.Throws<InvalidOperationException>(settings.Validate).Message);
    }

    private static WebApplicationBuilder CreateBuilder(string urls, int callbackPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls(urls);
        builder.Configuration["ConnectionStrings:shutterspool-control"] = "Host=127.0.0.1;Port=1;Database=listener_fixture;Username=fixture";
        builder.Configuration["ShutterSpool:CameraIngress:Enabled"] = "true";
        builder.Configuration["ShutterSpool:CameraIngress:CallbackKey"] = CallbackKey;
        builder.Configuration["ShutterSpool:CameraIngress:CallbackBaseUrl"] = $"https://localhost:{callbackPort}/internal/camera";
        builder.Configuration["ShutterSpool:CameraIngress:InternalPort"] = callbackPort.ToString();
        return builder;
    }

    private static TcpListener ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static X509Certificate2 CreateCertificate(RSA key, string name)
    {
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddHours(-1), now.AddHours(1));
    }

    private static HttpClient CreateClient(params X509Certificate2[] certificates)
    {
        var chainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        chainPolicy.CustomTrustStore.AddRange(certificates);
        return new HttpClient(new SocketsHttpHandler { UseProxy = false, SslOptions = { CertificateChainPolicy = chainPolicy } });
    }

    public void Dispose() => File.Delete(_callbackCertificatePath);
}
