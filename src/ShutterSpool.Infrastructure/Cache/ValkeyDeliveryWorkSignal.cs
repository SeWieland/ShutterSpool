using ShutterSpool.Application.Delivery;
using StackExchange.Redis;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ShutterSpool.Infrastructure.Cache;

internal sealed class ValkeyDeliveryWorkSignal(string? connectionString) : IDeliveryWorkSignal, IAsyncDisposable
{
    private const string ChannelName = "delivery-work-available";
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private IConnectionMultiplexer? _connection;

    public async ValueTask NotifyAsync(CancellationToken cancellationToken)
    {
        _signals.Writer.TryWrite(1);
        IConnectionMultiplexer? client = await GetConnectionAsync(cancellationToken);
        if (client is null) return;
        try { await client.GetSubscriber().PublishAsync(RedisChannel.Literal(ChannelName), "1"); }
        catch (RedisException) { }
    }

    public async ValueTask WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_signals.Reader.TryRead(out _)) return;
        _ = await GetConnectionAsync(cancellationToken);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        try { _ = await _signals.Reader.ReadAsync(wait.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
    }

    private async Task<IConnectionMultiplexer?> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null || string.IsNullOrWhiteSpace(connectionString)) return _connection;
        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not null) return _connection;
            IConnectionMultiplexer? client = null;
            try
            {
                ConfigurationOptions options = ConfigurationOptions.Parse(connectionString);
                options.AbortOnConnectFail = false;
                options.ConnectTimeout = 1_000;
                client = await ConnectionMultiplexer.ConnectAsync(options);
                await client.GetSubscriber().SubscribeAsync(RedisChannel.Literal(ChannelName), (_, _) => _signals.Writer.TryWrite(1));
                _connection = client;
                return _connection;
            }
            catch (Exception exception) when (exception is RedisException or SocketException or FormatException or ArgumentException)
            {
                if (client is not null) await client.DisposeAsync();
                return null;
            }
        }
        finally { _connectGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _connectGate.Dispose();
    }
}
