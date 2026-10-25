using ShutterSpool.Server.Hosting;
using System.Net;

namespace ShutterSpool.IntegrationTests;

public sealed class HealthCheckCommandTests
{
    [Fact]
    public async Task Health_command_succeeds_only_when_the_server_is_ready()
    {
        Assert.Equal(0, await HealthCheckCommand.RunAsync(new StatusHandler(HttpStatusCode.OK)));
        Assert.Equal(1, await HealthCheckCommand.RunAsync(new StatusHandler(HttpStatusCode.ServiceUnavailable)));
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("http", request.RequestUri!.Scheme);
            Assert.Equal("127.0.0.1", request.RequestUri.Host);
            Assert.Equal("/health/ready", request.RequestUri.AbsolutePath);
            Assert.Equal("localhost:8080", request.Headers.Host);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
