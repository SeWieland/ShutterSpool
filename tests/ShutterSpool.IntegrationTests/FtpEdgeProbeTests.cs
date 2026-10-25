using FluentFTP;
using FubarDev.FtpServer;
using FubarDev.FtpServer.FileSystem.DotNet;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Sockets;

namespace ShutterSpool.IntegrationTests;

public sealed class FtpEdgeProbeTests
{
    [Fact]
    public async Task Embedded_candidate_accepts_a_controlled_ftp_upload()
    {
        string root = Path.Combine(Path.GetTempPath(), $"shutterspool-ftp-probe-{Guid.NewGuid():N}");
        int port = GetUnusedPort();
        Directory.CreateDirectory(root);

        var services = new ServiceCollection();
        services.Configure<DotNetFileSystemOptions>(options => options.RootPath = root);
        services.Configure<FtpServerOptions>(options =>
        {
            options.ServerAddress = IPAddress.Loopback.ToString();
            options.Port = port;
        });
        services.AddFtpServer(builder => builder.UseDotNetFileSystem().EnableAnonymousAuthentication());

        await using ServiceProvider provider = services.BuildServiceProvider();
        IFtpServerHost server = provider.GetRequiredService<IFtpServerHost>();
        await server.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            await WaitForListenerAsync(port, TestContext.Current.CancellationToken);
            await using var client = new AsyncFtpClient(IPAddress.Loopback.ToString(), "anonymous", "probe@example.test", port);
            client.Config.ConnectTimeout = 5_000;
            client.Config.DataConnectionConnectTimeout = 5_000;
            await client.Connect(TestContext.Current.CancellationToken);

            byte[] media = [0x53, 0x50, 0x4F, 0x4F, 0x4C];
            await client.UploadStream(new MemoryStream(media), "probe.jpg", token: TestContext.Current.CancellationToken);

            Assert.Equal(media, await File.ReadAllBytesAsync(Path.Combine(root, "probe.jpg"), TestContext.Current.CancellationToken));
            await client.Disconnect(TestContext.Current.CancellationToken);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
            Directory.Delete(root, recursive: true);
        }
    }

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitForListenerAsync(int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        while (!timeout.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(50, timeout.Token);
            }
        }

        throw new TimeoutException($"FTP listener did not open port {port}.");
    }
}
