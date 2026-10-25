using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Application.Captures;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.Collections;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Metadata;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Routing;
using ShutterSpool.Application.Security;
using ShutterSpool.Application.Sources;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Cache;
using ShutterSpool.Infrastructure.Destinations.Local;
using ShutterSpool.Infrastructure.Destinations.WebDav;
using ShutterSpool.Infrastructure.Metadata;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Preview;
using ShutterSpool.Infrastructure.Security;
using ShutterSpool.Infrastructure.Spool;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Composition;

public static class InfrastructureServices
{
    public static IServiceCollection AddShutterSpoolInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContextFactory<ShutterSpoolDbContext>(options => options.UseNpgsql(configuration.GetConnectionString("shutterspool")));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<DatabaseInitializer>();
        services.Configure<CredentialProtectionOptions>(configuration.GetSection("ShutterSpool:Security"));
        services.AddSingleton<ICredentialProtector, SecretProtector>();
        services.TryAddSingleton(new CameraCredentialPolicy());
        services.AddScoped<IAccessGrantStore, EfAccessGrantStore>();
        services.AddScoped<AccessGrantService>();
        services.AddScoped<ISourceConfigurationStore, EfSourceConfigurationStore>();
        services.AddScoped<ICollectionManagementStore, EfCollectionManagementStore>();
        services.AddScoped<CollectionManagementService>();
        services.AddScoped<ICaptureCollectionAssignmentStore, EfCaptureCollectionAssignmentStore>();
        services.AddScoped<CaptureCollectionAssignmentService>();
        services.AddScoped<ISourceDisplayStore, EfSourceDisplayStore>();
        services.AddScoped<ISourceAuthenticationObservationStore, EfSourceAuthenticationObservationStore>();
        services.AddScoped<IDestinationConfigurationStore, EfDestinationConfigurationStore>();
        services.AddSingleton<ISourceAccessAudit, SourceAccessAudit>();
        services.AddScoped<SourceManagementService>();
        services.AddScoped<SourceCredentialVerifier>();
        services.AddScoped<DestinationManagementService>();

        services.AddScoped<IIngestJournal, EfIngestJournal>();
        services.AddSingleton<ISpoolAccounting>(provider =>
            new FileSystemSpoolAccounting(configuration, provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<EfSpoolQuotaStore>();
        services.AddScoped<ISpoolQuotaStore>(provider => provider.GetRequiredService<EfSpoolQuotaStore>());
        services.AddScoped<IQuotaReservationCommitter>(provider => provider.GetRequiredService<EfSpoolQuotaStore>());
        services.AddScoped<IAssetMetadataStore, EfAssetMetadataStore>();
        services.AddScoped<ICaptureGroupingStore, EfCaptureGroupingStore>();
        services.AddScoped<ICaptureCurationStore, EfCaptureCurationStore>();
        services.AddScoped<ICaptureFavoriteStore, EfCaptureFavoriteStore>();
        services.AddScoped<CaptureFavoriteService>();
        services.AddScoped<EfCaptureRetentionStore>();
        services.AddScoped<ICaptureRetentionReadStore>(provider => provider.GetRequiredService<EfCaptureRetentionStore>());
        services.AddScoped<ICaptureRetentionMutationStore>(provider => provider.GetRequiredService<EfCaptureRetentionStore>());
        services.AddScoped<CaptureRetentionMutationService>();
        services.AddScoped<IRetentionSettingsStore, EfRetentionSettingsStore>();
        services.AddScoped<RetentionSettingsService>();
        services.AddScoped<IRetentionCapacityReadStore, EfRetentionCapacityReadStore>();
        services.AddScoped<RetentionCapacityReadService>();
        services.AddScoped<IAssetProtectionStore, EfAssetProtectionStore>();
        services.AddSingleton<IProtectionAdmissionLock>(provider =>
            new PostgresProtectionAdmissionLock(configuration.GetConnectionString("shutterspool") ?? "",
                provider.GetRequiredService<ISpool>()));
        services.AddScoped<AssetProtectionService>();
        services.AddScoped<IRetentionRemovalStore, EfRetentionRemovalStore>();
        services.AddSingleton<IRetentionControllerLock>(provider =>
            new PostgresRetentionControllerLock(configuration.GetConnectionString("shutterspool") ?? ""));
        services.AddScoped<RetentionRemovalService>();
        services.AddScoped<PrivacyErasureSuppressionPersistence>();
        services.AddScoped<IPrivacyErasureStore, EfPrivacyErasureStore>();
        services.AddScoped<PrivacyErasureService>();
        services.AddScoped<PrivacyErasureSuppressionService>();
        services.AddScoped<IPrivacyErasureEvidenceCodec, PrivacyErasureEvidenceCodec>();
        services.AddScoped<ICaptureReadStore, CaptureReadStore>();
        services.AddScoped<CaptureGroupingService>();
        services.AddScoped<CaptureCurationService>();
        services.AddScoped<CaptureReadService>();
        services.AddScoped<ICaptureRoutingStore, EfCaptureRoutingProcessor>();
        services.AddScoped<CaptureRoutingService>();
        services.AddScoped<IVerifiedOriginalSupplier, EfVerifiedOriginalSupplier>();
        services.AddScoped<ICaptureRoutingHistoryStore, CaptureRoutingHistoryStore>();
        services.AddScoped<CaptureRoutingHistoryService>();
        services.AddScoped<ICaptureRoutingActionStore, CaptureRoutingWorkService>();
        services.AddScoped<CaptureRoutingActionService>();
        services.AddScoped<IDestinationRoutingStore, DestinationRoutingAdminService>();
        services.AddScoped<DestinationRoutingService>();
        services.AddScoped<MetadataProcessingService>();
        services.AddSingleton<IMediaMetadataParser, MetadataExtractorMediaMetadataParser>();
        services.AddSingleton<ISpool>(_ => new FileSystemSpool(new FileSystemSpoolOptions
        {
            RootPath = configuration["ShutterSpool:Spool:RootPath"] ?? throw new InvalidOperationException("The spool root path is not configured."),
            ReservedBytes = long.TryParse(configuration["ShutterSpool:Spool:ReservedBytes"], out long bytes) ? bytes : null,
            ReservedPercent = double.TryParse(configuration["ShutterSpool:Spool:ReservedPercent"], out double percent) ? percent : null,
        }));
        services.AddSingleton(_ => new PostgresOriginalAssetLock(configuration.GetConnectionString("shutterspool") ?? ""));
        services.AddSingleton<IOriginalAssetLock>(provider => provider.GetRequiredService<PostgresOriginalAssetLock>());
        services.AddSingleton<IExactOriginalRemoval, FileSystemExactOriginalRemoval>();
        services.AddSingleton<ICommittedOriginalReader>(provider => new PostgresCommittedOriginalReader(
            provider.GetRequiredService<ISpool>(), provider.GetRequiredService<IDbContextFactory<ShutterSpoolDbContext>>(),
            provider.GetRequiredService<PostgresOriginalAssetLock>()));
        services.AddScoped<IngestService>();

        if (configuration.GetValue<bool>("ShutterSpool:CameraIngress:Enabled"))
        {
            string spoolRoot = configuration["ShutterSpool:Spool:RootPath"] ?? throw new InvalidOperationException("The spool root path is not configured.");
            services.AddScoped<ICameraIngressStore, EfCameraIngressStore>();
            services.AddSingleton<ICameraIngressDirectories>(_ => new FileSystemCameraIngressDirectories(Path.Combine(spoolRoot, ".camera-directories")));
            services.AddScoped<CameraIngressService>();
        }

        var delivery = configuration.GetSection("ShutterSpool:DeliveryWorker").Get<DeliveryWorkerSettings>() ?? new();
        delivery.Validate();
        services.AddSingleton(delivery);
        services.AddScoped<IDeliveryWorkStore, EfDeliveryWorkProcessor>();
        services.AddSingleton<IDestinationDeliveryAdapter, LocalDestinationDeliveryAdapter>();
        services.AddScoped<ILocalDestinationConfiguration, LocalDestinationConfigurationService>();
        services.AddScoped<IWebDavDestinationConfiguration, EfWebDavDestinationConfiguration>();
        services.AddHttpClient("webdav", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<WebDavDestinationDeliveryAdapter>();
        services.AddSingleton<IDestinationDeliveryAdapter>(provider => provider.GetRequiredService<WebDavDestinationDeliveryAdapter>());
        services.AddSingleton<DeliveryAdapterRegistry>();
        services.AddScoped<DeliveryProcessingService>();
        services.AddSingleton<IDeliveryWorkSignal>(_ => new ValkeyDeliveryWorkSignal(configuration.GetConnectionString("valkey")));

        var preview = configuration.GetSection("ShutterSpool:Preview").Get<PreviewSettings>() ?? new();
        preview.Validate();
        services.AddSingleton(preview);
        services.AddSingleton<PreviewGenerationGate>();
        services.AddSingleton<PreviewAssetUseGate>();
        services.AddScoped<IPreviewSourceResolver, EfPreviewSourceResolver>();
        services.AddSingleton(provider => new FileSystemPreviewCache(configuration,
            provider.GetRequiredService<IServiceScopeFactory>()));
        services.AddSingleton<IPreviewCacheReclaimer>(provider => provider.GetRequiredService<FileSystemPreviewCache>());
        services.AddSingleton<IPreviewCache>(provider => preview.CacheMode == PreviewCacheMode.Memory
            ? new MemoryPreviewCache(preview)
            : provider.GetRequiredService<FileSystemPreviewCache>());
        services.AddSingleton<IPreviewAssetCacheInvalidator>(provider =>
            new PreviewAssetCacheInvalidator((IPreviewAssetCacheInvalidator)provider.GetRequiredService<IPreviewCache>(),
                provider.GetRequiredService<FileSystemPreviewCache>()));
        services.AddScoped<IPreviewPrewarmStore, EfPreviewPrewarmStore>();
        services.AddScoped<PreviewService>();
        services.AddScoped<PreviewPrewarmService>();
        services.AddHttpClient<ImaginaryPreviewGenerator>(client =>
        {
            client.BaseAddress = new Uri(configuration["ShutterSpool:Preview:ImaginaryUrl"] ?? "http://localhost:9000/");
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddScoped<IPreviewGenerator>(provider => provider.GetRequiredService<ImaginaryPreviewGenerator>());
        return services;
    }

    public static async Task InitializeShutterSpoolAsync(this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        _ = scope.ServiceProvider.GetRequiredService<DeliveryAdapterRegistry>();
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("shutterspool")))
        {
            if (configuration.GetValue<bool>("ShutterSpool:CameraIngress:Enabled"))
                throw new InvalidOperationException("Camera ingress requires the authoritative PostgreSQL connection.");
            return;
        }
        string spoolRoot = configuration["ShutterSpool:Spool:RootPath"] ?? throw new InvalidOperationException("The spool root path is not configured.");
        DatabaseInitializer initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(spoolRoot, cancellationToken, async token =>
        {
            bool suppressionReconciled = false;
            async Task ReconcileSuppressionAsync(CancellationToken proofToken)
            {
                await SuppressionRestoreGate.ReconcileAsync(configuration,
                    scope.ServiceProvider.GetRequiredService<PrivacyErasureSuppressionService>(),
                    scope.ServiceProvider.GetRequiredService<PrivacyErasureService>(), proofToken);
                suppressionReconciled = true;
            }

            if (configuration.GetValue<bool>("ShutterSpool:Deployment:Packaged"))
                await initializer.ValidateSecretKeyAsync(scope.ServiceProvider.GetRequiredService<ICredentialProtector>(), token,
                    configuration.GetValue<bool>("ShutterSpool:Restore:Enabled") ? ReconcileSuppressionAsync : null);
            if (configuration.GetValue<bool>("ShutterSpool:Restore:Enabled") && !suppressionReconciled)
                await ReconcileSuppressionAsync(token);
            string? previewCacheJson = await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>()
                .RetentionPolicies.AsNoTracking().Where(policy => policy.Id == 1)
                .Select(policy => policy.PreviewCacheJson).SingleOrDefaultAsync(token);
            if (previewCacheJson is not null)
                (JsonSerializer.Deserialize<PreviewCacheSettings>(previewCacheJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("The saved preview cache settings are invalid."))
                    .ApplyTo(services.GetRequiredService<PreviewSettings>());
            string? issuer = configuration["ShutterSpool:Security:BootstrapIssuer"];
            string? subject = configuration["ShutterSpool:Security:BootstrapSubject"];
            if (!string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject))
                await scope.ServiceProvider.GetRequiredService<IAccessGrantStore>().BootstrapAsync(issuer, subject, token);
            await scope.ServiceProvider.GetRequiredService<IngestService>().RecoverAsync(token);
            if (configuration.GetValue<bool>("ShutterSpool:CameraIngress:Enabled"))
                await scope.ServiceProvider.GetRequiredService<CameraIngressService>().RecoverAsync(
                    configuration.GetValue("ShutterSpool:CameraIngress:TransferTimeoutSeconds", 600), token);
        });
    }
}
