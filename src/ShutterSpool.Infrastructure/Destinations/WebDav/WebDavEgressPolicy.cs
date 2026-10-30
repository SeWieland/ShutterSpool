using System.Net;
using System.Net.Sockets;

namespace ShutterSpool.Infrastructure.Destinations.WebDav;

internal sealed class WebDavEgressOptions
{
    public string[] AllowedPrivateNetworks { get; set; } = [];
}

// Resolve and pin the socket address together; HttpClient still authenticates TLS using the original hostname.
internal sealed class WebDavEgressPolicy
{
    private static readonly IPNetwork[] BlockedNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"), IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/3"),
    ];
    private readonly IPNetwork[] _exceptions;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<Stream>> _connect;

    public WebDavEgressPolicy(WebDavEgressOptions options)
        : this(options, (host, token) => Dns.GetHostAddressesAsync(host, token), OpenSocketAsync) { }

    internal WebDavEgressPolicy(WebDavEgressOptions options,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect)
    {
        _exceptions = options.AllowedPrivateNetworks.Select(IPNetwork.Parse).ToArray();
        _resolve = resolve;
        _connect = connect;
    }

    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = (context, token) => ConnectAsync(context.DnsEndPoint, token),
    };

    internal async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken token)
    {
        IPAddress[] addresses = await _resolve(endpoint.Host, token);
        if (addresses.Length == 0 || addresses.Any(address => !IsAllowed(address)))
            throw new HttpRequestException("The destination network address is unavailable under the deployment egress policy.");

        foreach (IPAddress address in addresses)
        {
            try { return await _connect(new IPEndPoint(address, endpoint.Port), token); }
            catch (SocketException) when (!token.IsCancellationRequested) { }
        }
        throw new HttpRequestException("The destination network connection failed.");
    }

    private bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0) return false;
        if (_exceptions.Any(network => network.Contains(address))) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !BlockedNetworks.Any(network => network.Contains(address));
        // Accept ordinary global unicast only, excluding transition/documentation ranges and embedded IPv4 routes.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && IPNetwork.Parse("2000::/3").Contains(address)
            && !IPNetwork.Parse("2001::/23").Contains(address)
            && !IPNetwork.Parse("2001:db8::/32").Contains(address)
            && !IPNetwork.Parse("2002::/16").Contains(address);
    }

    private static async ValueTask<Stream> OpenSocketAsync(IPEndPoint endpoint, CancellationToken token)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
