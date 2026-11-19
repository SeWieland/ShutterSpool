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

namespace ShutterSpool.IntegrationTests;

public sealed class CameraListenerTests
{
    private const string CallbackKey = "synthetic-camera-callback-listener-key";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Camera_listener_preserves_public_bindings_and_restricts_routes_by_port()
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
        var request = new CertificateRequest("CN=Camera listener test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 generated = request.CreateSelfSigned(now.AddHours(-1), now.AddHours(1));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pkcs12), null);
        var builder = CreateBuilder(urls, callbackPort);
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
        var chainPolicy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        chainPolicy.CustomTrustStore.Add(certificate);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, SslOptions = { CertificateChainPolicy = chainPolicy } });
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
        using HttpResponseMessage privatePort = await client.GetAsync($"http://127.0.0.1:{callbackPort}/auth/config", Token);
        Assert.Equal(HttpStatusCode.NotFound, privatePort.StatusCode);
        using HttpResponseMessage privateHealth = await client.GetAsync($"http://127.0.0.1:{callbackPort}/health/live", Token);
        Assert.Equal(HttpStatusCode.OK, privateHealth.StatusCode);
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

    private static WebApplicationBuilder CreateBuilder(string urls, int callbackPort)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls(urls);
        builder.Configuration["ConnectionStrings:shutterspool-control"] = "Host=127.0.0.1;Port=1;Database=listener_fixture;Username=fixture";
        builder.Configuration["ShutterSpool:CameraIngress:Enabled"] = "true";
        builder.Configuration["ShutterSpool:CameraIngress:CallbackKey"] = CallbackKey;
        builder.Configuration["ShutterSpool:CameraIngress:CallbackBaseUrl"] = $"http://localhost:{callbackPort}/internal/camera";
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
}
