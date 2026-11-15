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
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Workspaces;

namespace ShutterSpool.Infrastructure.Composition;

public static class InfrastructureServices
{
    public static IServiceCollection AddShutterSpoolInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        AddWorkspacePersistence(services, configuration);
        AddSecurityAndConfiguration(services, configuration);
        AddIngestAndSpool(services, configuration);
        AddCaptureWorkflows(services);
        AddRetention(services);
        AddDelivery(services, configuration);
        AddPreviews(services, configuration);
        return services;
    }

    public static Task InitializeShutterSpoolAsync(this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken) =>
        WorkspaceStartup.InitializeAsync(services, configuration, cancellationToken);

    private static IConfiguration Deployment(IServiceProvider provider) =>
        provider.GetRequiredService<WorkspaceDeploymentConfiguration>().Value;

    private static IConfiguration TenantConfiguration(IServiceProvider provider) =>
        provider.GetRequiredService<WorkspaceConfiguration>().Value;

    private static void AddWorkspacePersistence(IServiceCollection services, IConfiguration configuration)
    {
        // Freeze resolved secrets and manifest values for this process; retargeting requires a restart.
        services.AddSingleton(_ => new WorkspaceDeploymentConfiguration(new ConfigurationBuilder().AddInMemoryCollection(configuration.AsEnumerable()).Build()));
        services.AddScoped(_ => new WorkspaceContext());
        services.AddScoped(provider => new WorkspaceConfiguration(WorkspaceRuntime.Configure(Deployment(provider), provider.GetRequiredService<WorkspaceContext>())));
        services.AddSingleton<WorkspaceSpoolResources>();
        services.AddSingleton<WorkspaceWriterLease>();
        services.AddSingleton<WorkspaceInventory>();
        services.AddDbContextFactory<WorkspaceControlDbContext>((provider, options) =>
            options.UseNpgsql(WorkspaceControlDbContext.RuntimeConnection(Deployment(provider))));
        services.AddScoped<IWorkspaceRegistry, EfWorkspaceRegistry>();
        services.AddDbContextFactory<ShutterSpoolDbContext>((provider, options) =>
            options.UseNpgsql(TenantConfiguration(provider).GetConnectionString("shutterspool")), ServiceLifetime.Scoped);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<DatabaseInitializer>();
    }

    private static void AddSecurityAndConfiguration(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CredentialProtectionOptions>(configuration.GetSection("ShutterSpool:Security"));
        services.AddSingleton<ICredentialProtector, SecretProtector>();
        services.TryAddSingleton(new CameraCredentialPolicy());
        services.AddScoped<IAccessGrantStore, EfAccessGrantStore>();
        services.AddScoped<WorkspaceAccessContext>();
        services.AddScoped<IWorkspaceMediaScope, WorkspaceMediaScope>();
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
    }

    private static void AddIngestAndSpool(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IIngestJournal, EfIngestJournal>();
        services.AddScoped<ISpoolAccounting>(provider =>
            new FileSystemSpoolAccounting(TenantConfiguration(provider), provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<EfSpoolQuotaStore>();
        services.AddScoped<ISpoolQuotaStore>(provider => provider.GetRequiredService<EfSpoolQuotaStore>());
        services.AddScoped<IQuotaReservationCommitter>(provider => provider.GetRequiredService<EfSpoolQuotaStore>());
        services.AddScoped<ISpool>(provider => provider.GetRequiredService<WorkspaceSpoolResources>().Get(
            provider.GetRequiredService<WorkspaceContext>().Required.Id, new FileSystemSpoolOptions
        {
            RootPath = TenantConfiguration(provider)["ShutterSpool:Spool:RootPath"] ?? throw new InvalidOperationException("The spool root path is not configured."),
            ReservedBytes = long.TryParse(TenantConfiguration(provider)["ShutterSpool:Spool:ReservedBytes"], out long bytes) ? bytes : null,
            ReservedPercent = double.TryParse(TenantConfiguration(provider)["ShutterSpool:Spool:ReservedPercent"], out double percent) ? percent : null,
        }));
        services.AddScoped(provider => new PostgresOriginalAssetLock(TenantConfiguration(provider).GetConnectionString("shutterspool") ?? ""));
        services.AddScoped<IOriginalAssetLock>(provider => provider.GetRequiredService<PostgresOriginalAssetLock>());
        services.AddScoped<IExactOriginalRemoval>(provider => new FileSystemExactOriginalRemoval(TenantConfiguration(provider)));
        services.AddScoped<ICommittedOriginalReader>(provider => new PostgresCommittedOriginalReader(
            provider.GetRequiredService<ISpool>(), provider.GetRequiredService<IDbContextFactory<ShutterSpoolDbContext>>(),
            provider.GetRequiredService<PostgresOriginalAssetLock>()));
        services.AddScoped<IngestService>();

        if (configuration.GetValue<bool>("ShutterSpool:CameraIngress:Enabled"))
        {
            services.AddScoped<ICameraIngressStore, EfCameraIngressStore>();
            services.AddScoped<ICameraIngressDirectories>(provider => new FileSystemCameraIngressDirectories(Path.Combine(TenantConfiguration(provider)["ShutterSpool:Spool:RootPath"]!, ".camera-directories")));
            services.AddScoped<CameraIngressService>();
        }
    }

    private static void AddCaptureWorkflows(IServiceCollection services)
    {
        services.AddScoped<IAssetMetadataStore, EfAssetMetadataStore>();
        services.AddScoped<ICaptureGroupingStore, EfCaptureGroupingStore>();
        services.AddScoped<ICaptureCurationStore, EfCaptureCurationStore>();
        services.AddScoped<ICaptureFavoriteStore, EfCaptureFavoriteStore>();
        services.AddScoped<CaptureFavoriteService>();
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
        services.AddScoped<IMediaMetadataParser, MetadataExtractorMediaMetadataParser>();
    }

    private static void AddRetention(IServiceCollection services)
    {
        services.AddScoped<EfCaptureRetentionStore>();
        services.AddScoped<ICaptureRetentionReadStore>(provider => provider.GetRequiredService<EfCaptureRetentionStore>());
        services.AddScoped<ICaptureRetentionMutationStore>(provider => provider.GetRequiredService<EfCaptureRetentionStore>());
        services.AddScoped<CaptureRetentionMutationService>();
        services.AddScoped<IRetentionSettingsStore, EfRetentionSettingsStore>();
        services.AddScoped<RetentionSettingsService>();
        services.AddScoped<IRetentionCapacityReadStore, EfRetentionCapacityReadStore>();
        services.AddScoped<RetentionCapacityReadService>();
        services.AddScoped<IAssetProtectionStore, EfAssetProtectionStore>();
        services.AddScoped<IProtectionAdmissionLock>(provider =>
            new PostgresProtectionAdmissionLock(TenantConfiguration(provider).GetConnectionString("shutterspool") ?? "",
                provider.GetRequiredService<ISpool>()));
        services.AddScoped<AssetProtectionService>();
        services.AddScoped<IRetentionRemovalStore, EfRetentionRemovalStore>();
        services.AddScoped<IRetentionControllerLock>(provider =>
            new PostgresRetentionControllerLock(TenantConfiguration(provider).GetConnectionString("shutterspool") ?? ""));
        services.AddScoped<RetentionRemovalService>();
        services.AddScoped<PrivacyErasureSuppressionPersistence>();
        services.AddScoped<IPrivacyErasureStore, EfPrivacyErasureStore>();
        services.AddScoped<PrivacyErasureService>();
        services.AddScoped<PrivacyErasureSuppressionService>();
        services.AddScoped<IPrivacyErasureEvidenceCodec, PrivacyErasureEvidenceCodec>();
    }

    private static void AddDelivery(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(_ => new DestinationCapabilities(configuration.GetSection("ShutterSpool:Destinations:EnabledKinds").Get<string[]>()
            ?? []));
        var egress = configuration.GetSection("ShutterSpool:Destinations:WebDavEgress").Get<WebDavEgressOptions>() ?? new();
        var delivery = configuration.GetSection("ShutterSpool:DeliveryWorker").Get<DeliveryWorkerSettings>() ?? new();
        delivery.Validate();
        services.AddSingleton(delivery);
        services.AddScoped<IDeliveryWorkStore, EfDeliveryWorkProcessor>();
        services.AddScoped<IDestinationDeliveryAdapter, LocalDestinationDeliveryAdapter>();
        services.AddScoped<ILocalDestinationConfiguration, LocalDestinationConfigurationService>();
        services.AddScoped<IWebDavDestinationConfiguration, EfWebDavDestinationConfiguration>();
        services.AddHttpClient("webdav", client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new WebDavEgressPolicy(egress).CreateHandler())
            .RemoveAllLoggers();
        services.AddScoped<WebDavDestinationDeliveryAdapter>();
        services.AddScoped<IDestinationDeliveryAdapter>(provider => provider.GetRequiredService<WebDavDestinationDeliveryAdapter>());
        services.AddScoped<DeliveryAdapterRegistry>();
        services.AddScoped<DeliveryProcessingService>();
        services.AddSingleton<IDeliveryWorkSignal>(_ => new ValkeyDeliveryWorkSignal(configuration.GetConnectionString("valkey")));
    }

    private static void AddPreviews(IServiceCollection services, IConfiguration configuration)
    {
        var preview = configuration.GetSection("ShutterSpool:Preview").Get<PreviewSettings>() ?? new();
        preview.Validate();
        services.AddSingleton<WorkspacePreviewResources>();
        services.AddScoped(provider =>
            provider.GetRequiredService<WorkspacePreviewResources>().Settings(provider.GetRequiredService<WorkspaceContext>().Required.Id,
                () => TenantConfiguration(provider).GetSection("ShutterSpool:Preview").Get<PreviewSettings>() ?? new()));
        services.AddSingleton(_ => new PreviewGenerationGate(preview));
        services.AddScoped(provider => provider.GetRequiredService<WorkspacePreviewResources>()
            .Uses(provider.GetRequiredService<WorkspaceContext>().Required.Id));
        services.AddScoped<IPreviewSourceResolver, EfPreviewSourceResolver>();
        services.AddScoped(provider => new FileSystemPreviewCache(TenantConfiguration(provider),
            new WorkspaceBoundScopeFactory(provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<WorkspaceContext>().Required)));
        services.AddScoped<IPreviewCacheReclaimer>(provider => provider.GetRequiredService<FileSystemPreviewCache>());
        services.AddScoped<IPreviewCache>(provider => provider.GetRequiredService<PreviewSettings>().CacheMode == PreviewCacheMode.Memory
            ? provider.GetRequiredService<WorkspacePreviewResources>().Cache(provider.GetRequiredService<WorkspaceContext>().Required.Id,
                provider.GetRequiredService<PreviewSettings>())
            : provider.GetRequiredService<FileSystemPreviewCache>());
        services.AddScoped<IPreviewAssetCacheInvalidator>(provider =>
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
    }
}
