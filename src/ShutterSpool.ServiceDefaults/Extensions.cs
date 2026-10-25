using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace ShutterSpool.ServiceDefaults;

public static class Extensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        OpenTelemetryBuilder telemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddRuntimeInstrumentation().AddMeter("ShutterSpool.Retention"))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(options =>
                options.Filter = context => !context.Request.Path.StartsWithSegments("/internal/camera")
                    && !context.Request.Path.StartsWithSegments("/live/changes")
                    && !context.Request.Query.ContainsKey("access_token"))
                .AddHttpClientInstrumentation(options => options.FilterHttpRequestMessage = request =>
                    !request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("ShutterSpool.PrivateProviderRequest"), out bool suppress)
                    || !suppress));
        if (HasExplicitOtlpEndpoint(builder))
            telemetry.UseOtlpExporter();
        builder.Services.AddServiceDiscovery();
        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        return builder;
    }

    private static bool HasExplicitOtlpEndpoint(IHostApplicationBuilder builder) =>
        new[]
        {
            "OTEL_EXPORTER_OTLP_ENDPOINT",
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
            "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT",
        }.Any(key => !string.IsNullOrWhiteSpace(builder.Configuration[key]));

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
        });
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
        });
        return app;
    }
}
