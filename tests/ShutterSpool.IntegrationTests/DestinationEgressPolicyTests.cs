using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Destinations.WebDav;

namespace ShutterSpool.IntegrationTests;

public sealed class DestinationEgressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.1.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("ff02::1")]
    public async Task Private_and_special_addresses_never_reach_the_connector(string address)
    {
        int connections = 0;
        var policy = new WebDavEgressPolicy(new(), (_, _) => Task.FromResult(new[] { IPAddress.Parse(address) }),
            (_, _) => { connections++; return ValueTask.FromResult<Stream>(new MemoryStream()); });

        await Assert.ThrowsAsync<HttpRequestException>(() => policy.ConnectAsync(new("dav.example.test", 443), CancellationToken.None).AsTask());

        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task Every_resolved_address_is_checked_before_any_connection()
    {
        int connections = 0;
        var policy = new WebDavEgressPolicy(new(), (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback }),
            (_, _) => { connections++; return ValueTask.FromResult<Stream>(new MemoryStream()); });

        await Assert.ThrowsAsync<HttpRequestException>(() => policy.ConnectAsync(new("dav.example.test", 443), CancellationToken.None).AsTask());

        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task Reconnect_resolves_again_and_connects_only_the_approved_address()
    {
        int resolutions = 0;
        List<IPEndPoint> connected = [];
        var policy = new WebDavEgressPolicy(new(), (_, _) => Task.FromResult(new[] { ++resolutions == 1 ? IPAddress.Parse("8.8.8.8") : IPAddress.Loopback }),
            (endpoint, _) => { connected.Add(endpoint); return ValueTask.FromResult<Stream>(new MemoryStream()); });

        await using Stream first = await policy.ConnectAsync(new("dav.example.test", 8443), CancellationToken.None);
        await Assert.ThrowsAsync<HttpRequestException>(() => policy.ConnectAsync(new("dav.example.test", 8443), CancellationToken.None).AsTask());

        Assert.Equal(2, resolutions);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 8443), Assert.Single(connected));
    }

    [Fact]
    public async Task Operator_exception_allows_only_its_explicit_private_network()
    {
        IPEndPoint? connected = null;
        var policy = new WebDavEgressPolicy(new() { AllowedPrivateNetworks = ["10.42.0.0/16"] },
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.42.1.2") }),
            (endpoint, _) => { connected = endpoint; return ValueTask.FromResult<Stream>(new MemoryStream()); });

        await using Stream stream = await policy.ConnectAsync(new("dav.example.test", 443), CancellationToken.None);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("10.42.1.2"), 443), connected);
        using var handler = policy.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task Probe_and_delivery_use_the_same_connection_time_egress_boundary()
    {
        int resolutions = 0, connections = 0;
        var policy = new WebDavEgressPolicy(new(), (_, _) =>
        { resolutions++; return Task.FromResult(new[] { IPAddress.Loopback }); },
            (_, _) => { connections++; return ValueTask.FromResult<Stream>(new MemoryStream()); });
        var adapter = new WebDavDestinationDeliveryAdapter(new PolicyClientFactory(policy));
        byte[] original = [1, 2, 3];
        var request = new DeliveryRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic.raw",
            original.Length, Convert.ToHexString(SHA256.HashData(original)), "synthetic", "webdav",
            new WebDavDestinationDeliveryConfiguration("https://dav.example.test/camera/", "synthetic", "synthetic"));

        Assert.False((await adapter.TestAsync("https://dav.example.test/camera/", "synthetic", "synthetic", CancellationToken.None)).Writable);
        Assert.NotEqual(DeliveryResultKind.Confirmed, (await adapter.WriteAsync(request, new MemoryStream(original), CancellationToken.None)).Kind);
        Assert.True(resolutions >= 2);
        Assert.Equal(0, connections);
    }

    [Fact]
    public async Task Shared_handler_never_carries_provider_cookies_to_another_account()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        try
        {
            Task<List<string[]>> received = ReceiveCookieRequestsAsync(listener, deadline.Token);
            var policy = new WebDavEgressPolicy(new(), (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }),
                async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(endpoint, token); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                });
            using var client = new HttpClient(policy.CreateHandler());
            // HTTP isolates handler cookie behavior; adapter URL validation independently requires HTTPS.
            using var first = new HttpRequestMessage(HttpMethod.Get, $"http://dav.example.test:{endpoint.Port}/account-one/");
            first.Headers.Authorization = new("Basic", Convert.ToBase64String("one:synthetic"u8));
            using var firstResponse = await client.SendAsync(first, deadline.Token);
            using var second = new HttpRequestMessage(HttpMethod.Get, $"http://dav.example.test:{endpoint.Port}/account-two/");
            second.Headers.Authorization = new("Basic", Convert.ToBase64String("two:synthetic"u8));
            using var secondResponse = await client.SendAsync(second, deadline.Token);
            List<string[]> requests = await received;

            Assert.Equal(2, requests.Count);
            Assert.Contains(requests[0], header => header.StartsWith("Authorization: Basic ", StringComparison.Ordinal));
            Assert.Contains(requests[1], header => header.StartsWith("Authorization: Basic ", StringComparison.Ordinal));
            Assert.DoesNotContain(requests[1], header => header.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(requests[0].Single(header => header.StartsWith("Authorization:", StringComparison.Ordinal)),
                requests[1].Single(header => header.StartsWith("Authorization:", StringComparison.Ordinal)));
        }
        finally { listener.Stop(); }
    }

    private static async Task<List<string[]>> ReceiveCookieRequestsAsync(TcpListener listener, CancellationToken token)
    {
        List<string[]> requests = [];
        for (int index = 0; index < 2; index++)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync(token);
            await using NetworkStream stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            List<string> headers = [];
            string? line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(token))) headers.Add(line);
            requests.Add(headers.ToArray());
            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\nSet-Cookie: provider-session=account-one; Path=/\r\n\r\n");
            await stream.WriteAsync(response, token);
        }
        return requests;
    }

    private sealed class PolicyClientFactory(WebDavEgressPolicy policy) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("webdav", name);
            return new HttpClient(policy.CreateHandler());
        }
    }
}
