using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShutterSpool.ServiceDefaults;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed class FoundationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task OpenApi_describes_preview_bytes_and_live_stream_content()
    {
        using HttpClient client = factory.CreateClient();
        using JsonDocument document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json", TestContext.Current.CancellationToken));
        JsonElement paths = document.RootElement.GetProperty("paths");
        foreach (string route in new[] { "/media/previews/{id}", "/media/previews/{id}/{profile}" })
        {
            JsonElement responses = paths.GetProperty(route).GetProperty("get").GetProperty("responses");
            JsonElement content = responses.GetProperty("200").GetProperty("content");
            Assert.True(content.GetProperty("image/webp").TryGetProperty("schema", out _));
            Assert.True(content.GetProperty("image/svg+xml").TryGetProperty("schema", out _));
            Assert.Equal("binary", content.GetProperty("image/webp").GetProperty("schema").GetProperty("format").GetString());
            Assert.True(responses.TryGetProperty("304", out _));
            Assert.True(responses.TryGetProperty("404", out _));
        }
        Assert.True(paths.GetProperty("/live/status").GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("text/event-stream").TryGetProperty("schema", out _));
    }

    [Fact]
    public async Task Server_exposes_liveness_health_check()
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public void Server_ignores_forwarded_headers_without_an_explicit_proxy_network()
    {
        ForwardedHeadersOptions options = factory.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Equal(ForwardedHeaders.None, options.ForwardedHeaders);
    }

    [Fact]
    public void Service_defaults_register_otlp_export_only_for_an_explicit_endpoint()
    {
        IServiceCollection defaults = CreateServiceDefaults(null);
        IServiceCollection configured = CreateServiceDefaults("http://127.0.0.1:4317");

        Assert.False(HasOtlpExporter(defaults));
        Assert.True(HasOtlpExporter(configured));
    }

    private static IServiceCollection CreateServiceDefaults(string? endpoint)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        foreach (string key in new[]
        {
            "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
        })
            builder.Configuration[key] = null;
        if (endpoint is not null) builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = endpoint;
        builder.AddServiceDefaults();
        return builder.Services;
    }

    private static bool HasOtlpExporter(IServiceCollection services) => services.Any(descriptor =>
        descriptor.ServiceType.Assembly.GetName().Name == "OpenTelemetry.Exporter.OpenTelemetryProtocol"
        || descriptor.ImplementationType?.Assembly.GetName().Name == "OpenTelemetry.Exporter.OpenTelemetryProtocol");
}
