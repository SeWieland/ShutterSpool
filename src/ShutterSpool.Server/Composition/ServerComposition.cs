using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Sources;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Server.Api.CameraIngress;
using ShutterSpool.Server.Api.Captures;
using ShutterSpool.Server.Api.Collections;
using ShutterSpool.Server.Api.Deliveries;
using ShutterSpool.Server.Api.Destinations;
using ShutterSpool.Server.Api.Live;
using ShutterSpool.Server.Api.Previews;
using ShutterSpool.Server.Api.Retention;
using ShutterSpool.Server.Api.Routing;
using ShutterSpool.Server.Api.Sources;
using ShutterSpool.Server.Hosting;
using ShutterSpool.Server.Security;
using ShutterSpool.ServiceDefaults;
using System.Text.Json.Serialization;

namespace ShutterSpool.Server.Composition;

internal static class ServerComposition
{
    public static void AddShutterSpoolServer(this WebApplicationBuilder builder)
    {
        RuntimeConfiguration.ValidatePackaged(builder.Configuration);
        TimeSpan shutdownTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue<int?>("ShutterSpool:ShutdownTimeoutSeconds") ?? 120);
        if (shutdownTimeout <= TimeSpan.Zero) throw new InvalidOperationException("The Server shutdown timeout must be positive.");
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = shutdownTimeout);
        string[] trustedProxyNetworks = builder.Configuration.GetSection("ShutterSpool:ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
        if (trustedProxyNetworks.Length > 0)
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            foreach (string network in trustedProxyNetworks)
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        });
        CameraIngressSettings cameraIngress = builder.Configuration.GetSection("ShutterSpool:CameraIngress").Get<CameraIngressSettings>() ?? new();
        cameraIngress.Validate();
        builder.Services.AddSingleton(cameraIngress);
        string? configuredMinimum = builder.Configuration["ShutterSpool:CameraCredentials:MinimumPasswordLength"];
        CameraCredentialPolicy cameraCredentialPolicy = new()
        {
            MinimumLength = configuredMinimum is null ? 12
                : int.TryParse(configuredMinimum, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int minimum) ? minimum
                : throw new InvalidOperationException("Camera credential minimum password length must be an integer from 1 to 128."),
        };
        cameraCredentialPolicy.Validate();
        builder.Services.AddSingleton(cameraCredentialPolicy);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        if (cameraIngress.Enabled) ConfigureCameraListener(builder, cameraIngress.InternalPort);
        builder.AddServiceDefaults();
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            context.ProblemDetails.Extensions.TryAdd("errorCode", context.ProblemDetails.Status switch
            {
                StatusCodes.Status400BadRequest => "INVALID_REQUEST",
                StatusCodes.Status401Unauthorized => "AUTHENTICATION_REQUIRED",
                StatusCodes.Status403Forbidden => "ACCESS_DENIED",
                StatusCodes.Status404NotFound => "RESOURCE_NOT_FOUND",
                StatusCodes.Status409Conflict => "CONFLICT",
                StatusCodes.Status413PayloadTooLarge => "REQUEST_TOO_LARGE",
                StatusCodes.Status503ServiceUnavailable => "DEPENDENCY_UNAVAILABLE",
                _ => "INTERNAL_ERROR",
            });
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.RespectRequiredConstructorParameters = true;
            options.SerializerOptions.RespectNullableAnnotations = true;
        });
        builder.Services.AddShutterSpoolSecurity(builder.Configuration);
        builder.Services.AddSingleton<LiveChangeNotifier>();
        builder.Services.AddSingleton<RetentionTelemetry>();
        builder.Services.AddHostedService(services => services.GetRequiredService<LiveChangeNotifier>());
        builder.Services.AddSingleton<ShutterSpool.Application.Ingress.ICameraTransferActivityPublisher>(services =>
            services.GetRequiredService<LiveChangeNotifier>());
        builder.Services.AddSingleton<IPreviewActivityPublisher>(services => services.GetRequiredService<LiveChangeNotifier>());
        builder.Services.AddSingleton<SourceAuthenticationObservationWorker>();
        builder.Services.AddSingleton<ShutterSpool.Application.Sources.ISourceAuthenticationObservationRecorder>(services =>
            services.GetRequiredService<SourceAuthenticationObservationWorker>());
        builder.Services.AddShutterSpoolInfrastructure(builder.Configuration);
        string? databaseConnection = builder.Configuration.GetConnectionString("shutterspool");
        if (!string.IsNullOrWhiteSpace(databaseConnection))
            builder.Services.AddHealthChecks().AddCheck("postgres", new PostgresReadinessCheck(databaseConnection), tags: ["ready"]);
        builder.Services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 4_096;
            options.MaximumKeyLength = 200;
        });
        builder.Services.AddSingleton<SourceDisplayCacheKeys>();
        builder.Services.AddScoped<SourceDisplayCache>();
        if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("shutterspool")))
        {
            builder.Services.AddHostedService(services => services.GetRequiredService<SourceAuthenticationObservationWorker>());
            builder.Services.AddHostedService<MetadataWorker>();
            builder.Services.AddHostedService<CaptureGroupingWorker>();
            builder.Services.AddHostedService<CaptureReleaseWorker>();
            builder.Services.AddHostedService<CaptureRoutingWorker>();
            builder.Services.AddHostedService<CaptureDeliveryWorker>();
            builder.Services.AddHostedService<RetentionRemovalWorker>();
            var preview = builder.Configuration.GetSection("ShutterSpool:Preview").Get<PreviewSettings>() ?? new();
            if (preview.PrewarmMode != PreviewPrewarmMode.Off) builder.Services.AddHostedService<PreviewPrewarmWorker>();
        }
    }

    public static Task InitializeShutterSpoolServerAsync(this WebApplication app) =>
        app.Services.InitializeShutterSpoolAsync(app.Configuration, app.Lifetime.ApplicationStopping);

    public static void MapShutterSpoolServer(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/management"))
            {
                const long maximumBodyBytes = 2 * 1024 * 1024;
                Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature? bodyLimit =
                    context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
                if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = maximumBodyBytes;
                if (context.Request.ContentLength > maximumBodyBytes)
                {
                    await Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                        title: "Management request body is too large",
                        extensions: new Dictionary<string, object?> { ["errorCode"] = "REQUEST_TOO_LARGE" })
                        .ExecuteAsync(context);
                    return;
                }
            }
            await next(context);
        });
        CameraIngressSettings cameraIngress = app.Services.GetRequiredService<CameraIngressSettings>();
        if (cameraIngress.Enabled)
        {
            app.Use(async (context, next) =>
            {
                int port = context.Connection.LocalPort;
                if (port == cameraIngress.InternalPort && !context.Request.Path.StartsWithSegments("/internal/camera")
                    && context.Request.Path != "/health" && context.Request.Path != "/health/live" && context.Request.Path != "/health/ready")
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next(context);
            });
        }
        app.UseExceptionHandler(handler => handler.Run(context =>
        {
            Exception? error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
            bool unavailable = error is not null && (error is Npgsql.NpgsqlException or System.Security.Cryptography.CryptographicException or TimeoutException
                || error.InnerException is Npgsql.NpgsqlException or System.Security.Cryptography.CryptographicException);
            int status = error is BadHttpRequestException badRequest ? badRequest.StatusCode
                : unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
            string errorCode = status switch
            {
                StatusCodes.Status400BadRequest => "INVALID_REQUEST",
                StatusCodes.Status413PayloadTooLarge => "REQUEST_TOO_LARGE",
                StatusCodes.Status503ServiceUnavailable => "DEPENDENCY_UNAVAILABLE",
                _ => "INTERNAL_ERROR",
            };
            string title = status switch
            {
                StatusCodes.Status400BadRequest => "The request is invalid",
                StatusCodes.Status413PayloadTooLarge => "Management request body is too large",
                StatusCodes.Status503ServiceUnavailable => "A required service is unavailable",
                _ => "The operation could not be completed",
            };
            return Results.Problem(statusCode: status, title: title,
                extensions: new Dictionary<string, object?> { ["errorCode"] = errorCode })
                .ExecuteAsync(context);
        }));
        app.UseCors();
        app.MapOpenApi();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseMiddleware<SecurityAuditMiddleware>();
        app.UseAuthorization();

        app.MapGet("/auth/config", (IOptions<SecurityOptions> options) => Results.Ok(new
        {
            authority = options.Value.Authority,
            clientId = options.Value.ClientId,
            audience = options.Value.Audience,
        }));

        var management = app.MapGroup("/management").RequireAuthorization(SecurityExtensions.ManagementAccessPolicy);
        management.WithMetadata(new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status401Unauthorized),
            new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status403Forbidden),
            new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status503ServiceUnavailable));
        management.MapGet("/status", () => Results.Ok());
        management.MapSourceEndpoints();
        management.MapCollectionEndpoints();
        management.MapSourceDisplayEndpoints();
        management.MapSourceAuthenticationEndpoints();
        management.MapTransferActivityEndpoints();
        management.MapDestinationEndpoints();
        management.MapLocalDestinationEndpoints();
        management.MapWebDavDestinationEndpoints();
        management.MapCaptureCurationEndpoints();
        management.MapCaptureReadEndpoints();
        management.MapCaptureCollectionEndpoints();
        management.MapCaptureFavoriteEndpoints();
        management.MapCaptureRetentionEndpoints();
        management.MapRetentionSettingsEndpoints();
        management.MapRetentionProtectionEndpoints();
        management.MapPrivacyErasureEndpoints();
        management.MapRoutingEndpoints();
        management.MapDeliveryEndpoints();
        app.MapPreviewEndpoints();
        app.MapGet("/media/originals/{id}", (string id) => Results.NotFound()).RequireAuthorization(SecurityExtensions.ManagementAccessPolicy);
        app.MapGet("/live/status", LiveStatusEndpoint.StreamAsync)
            .RequireAuthorization(SecurityExtensions.ManagementAccessPolicy)
            .Produces<string>(StatusCodes.Status200OK, contentType: "text/event-stream");
        app.MapHub<LiveChangesHub>("/live/changes", options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization(SecurityExtensions.ManagementAccessPolicy).RequireCors("live-hub");
        app.MapCameraIngressEndpoints(cameraIngress);
        app.MapFallbackToFile("index.html");
        app.MapDefaultEndpoints();
    }

    private static void ConfigureCameraListener(WebApplicationBuilder builder, int internalPort)
    {
        string? urls = builder.Configuration[Microsoft.AspNetCore.Hosting.WebHostDefaults.ServerUrlsKey]
            ?? builder.WebHost.GetSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.ServerUrlsKey);
        builder.WebHost.ConfigureKestrel((_, server) =>
        {
            if (!string.IsNullOrWhiteSpace(urls))
            {
                foreach (string value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? address) || address.Port < 1)
                        throw new InvalidOperationException("Camera ingress cannot preserve an invalid configured server URL.");
                    if (address.Scheme is not ("http" or "https"))
                        throw new InvalidOperationException($"Camera ingress cannot preserve the configured '{address.Scheme}' server URL.");

                    void ConfigureEndpoint(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions endpoint)
                    {
                        if (address.Scheme == "https") endpoint.UseHttps();
                    }

                    if (string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                        server.ListenLocalhost(address.Port, ConfigureEndpoint);
                    else if (System.Net.IPAddress.TryParse(address.Host, out System.Net.IPAddress? ip))
                        server.Listen(ip, address.Port, ConfigureEndpoint);
                    else
                        server.ListenAnyIP(address.Port, ConfigureEndpoint);
                }
            }
            server.ListenAnyIP(internalPort);
        });
    }
}
