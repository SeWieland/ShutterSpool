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
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Workspaces;

namespace ShutterSpool.Infrastructure.Composition;

public static class InfrastructureServices
{
    public static IServiceCollection AddShutterSpoolInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        bool tenancy = configuration.GetValue<bool>("ShutterSpool:Workspaces:Enabled");
        services.AddSingleton(new DestinationCapabilities(configuration.GetSection("ShutterSpool:Destinations:EnabledKinds").Get<string[]>()
            ?? (tenancy ? [] : ["local", "webdav"])));
        var egress = configuration.GetSection("ShutterSpool:Destinations:WebDavEgress").Get<WebDavEgressOptions>() ?? new();
        services.AddScoped(_ => new WorkspaceContext(tenancy));
        services.AddScoped(provider => new WorkspaceConfiguration(tenancy
            ? WorkspaceRuntime.Configure(configuration, provider.GetRequiredService<WorkspaceContext>()) : configuration));
        services.AddSingleton<WorkspaceSpoolResources>();
        services.AddSingleton<WorkspaceWriterLease>();
        services.AddScoped<IWorkspaceRegistry>(provider => new PostgresWorkspaceRegistry(configuration,
            provider.GetRequiredService<WorkspaceContext>()));
        IConfiguration TenantConfiguration(IServiceProvider provider) => provider.GetRequiredService<WorkspaceConfiguration>().Value;
        if (tenancy)
        {
            services.AddDbContextFactory<ShutterSpoolDbContext>((provider, options) =>
                options.UseNpgsql(TenantConfiguration(provider).GetConnectionString("shutterspool"))
                    .AddInterceptors(new WorkspaceConnectionInterceptor(provider.GetRequiredService<WorkspaceContext>().Required)), ServiceLifetime.Scoped);
        }
        else services.AddDbContextFactory<ShutterSpoolDbContext>(options => options.UseNpgsql(configuration.GetConnectionString("shutterspool")));
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
        services.AddScoped<ISpoolAccounting>(provider =>
            new FileSystemSpoolAccounting(TenantConfiguration(provider), provider.GetRequiredService<TimeProvider>()));
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
        services.AddScoped<ISpool>(provider => provider.GetRequiredService<WorkspaceSpoolResources>().Get(
            provider.GetRequiredService<WorkspaceContext>().Id ?? Guid.Empty, new FileSystemSpoolOptions
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

        var preview = configuration.GetSection("ShutterSpool:Preview").Get<PreviewSettings>() ?? new();
        preview.Validate();
        services.AddSingleton<WorkspacePreviewResources>();
        if (tenancy) services.AddScoped(provider =>
            provider.GetRequiredService<WorkspacePreviewResources>().Settings(provider.GetRequiredService<WorkspaceContext>().Required.Id,
                () => TenantConfiguration(provider).GetSection("ShutterSpool:Preview").Get<PreviewSettings>() ?? new()));
        else services.AddSingleton(preview);
        services.AddSingleton(_ => new PreviewGenerationGate(preview));
        if (tenancy) services.AddScoped(provider => provider.GetRequiredService<WorkspacePreviewResources>()
            .Uses(provider.GetRequiredService<WorkspaceContext>().Required.Id));
        else services.AddSingleton<PreviewAssetUseGate>();
        services.AddScoped<IPreviewSourceResolver, EfPreviewSourceResolver>();
        services.AddScoped(provider => new FileSystemPreviewCache(TenantConfiguration(provider),
            tenancy ? new WorkspaceBoundScopeFactory(provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<WorkspaceContext>().Required) : provider.GetRequiredService<IServiceScopeFactory>()));
        services.AddScoped<IPreviewCacheReclaimer>(provider => provider.GetRequiredService<FileSystemPreviewCache>());
        services.AddScoped<IPreviewCache>(provider => provider.GetRequiredService<PreviewSettings>().CacheMode == PreviewCacheMode.Memory
            ? provider.GetRequiredService<WorkspacePreviewResources>().Cache(provider.GetRequiredService<WorkspaceContext>().Id ?? Guid.Empty,
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
        return services;
    }

    public static async Task InitializeShutterSpoolAsync(this IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.GetValue<bool>("ShutterSpool:Workspaces:Enabled"))
        {
            await using AsyncServiceScope control = services.CreateAsyncScope();
            WorkspaceDefinition[] workspaces = await control.ServiceProvider.GetRequiredService<IWorkspaceRegistry>().ListReadyAsync(cancellationToken);
            await ValidateWorkspaceInventoryAsync(configuration, workspaces, cancellationToken);
            foreach (WorkspaceDefinition workspace in workspaces)
                services.GetRequiredService<WorkspaceWriterLease>().Acquire(workspace);
            foreach (WorkspaceDefinition workspace in workspaces)
            {
                await using AsyncServiceScope tenant = services.CreateAsyncScope();
                tenant.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(workspace);
                IConfiguration scoped = tenant.ServiceProvider.GetRequiredService<WorkspaceConfiguration>().Value;
                await InitializeScopeAsync(tenant.ServiceProvider, scoped, cancellationToken, validateOnly: true);
            }
            return;
        }
        await using var scope = services.CreateAsyncScope();
        await InitializeScopeAsync(scope.ServiceProvider, configuration, cancellationToken, validateOnly: false);
    }

    private static async Task InitializeScopeAsync(IServiceProvider services, IConfiguration configuration,
        CancellationToken cancellationToken, bool validateOnly)
    {
        _ = services.GetRequiredService<DeliveryAdapterRegistry>();
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("shutterspool")))
        {
            if (configuration.GetValue<bool>("ShutterSpool:CameraIngress:Enabled"))
                throw new InvalidOperationException("Camera ingress requires the authoritative PostgreSQL connection.");
            return;
        }
        string spoolRoot = configuration["ShutterSpool:Spool:RootPath"] ?? throw new InvalidOperationException("The spool root path is not configured.");
        DatabaseInitializer initializer = services.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(spoolRoot, cancellationToken, async token =>
        {
            bool suppressionReconciled = false;
            async Task ReconcileSuppressionAsync(CancellationToken proofToken)
            {
                await SuppressionRestoreGate.ReconcileAsync(configuration,
                    services.GetRequiredService<PrivacyErasureSuppressionService>(),
                    services.GetRequiredService<PrivacyErasureService>(), proofToken);
                suppressionReconciled = true;
            }

            if (validateOnly || configuration.GetValue<bool>("ShutterSpool:Deployment:Packaged"))
                await initializer.ValidateSecretKeyAsync(services.GetRequiredService<ICredentialProtector>(), token,
                    configuration.GetValue<bool>("ShutterSpool:Restore:Enabled") ? ReconcileSuppressionAsync : null);
            if (configuration.GetValue<bool>("ShutterSpool:Restore:Enabled") && !suppressionReconciled)
                await ReconcileSuppressionAsync(token);
            string? previewCacheJson = await services.GetRequiredService<ShutterSpoolDbContext>()
                .RetentionPolicies.AsNoTracking().Where(policy => policy.Id == 1)
                .Select(policy => policy.PreviewCacheJson).SingleOrDefaultAsync(token);
            if (previewCacheJson is not null)
                (JsonSerializer.Deserialize<PreviewCacheSettings>(previewCacheJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("The saved preview cache settings are invalid."))
                    .ApplyTo(services.GetRequiredService<PreviewSettings>());
            string? issuer = configuration["ShutterSpool:Security:BootstrapIssuer"];
            string? subject = configuration["ShutterSpool:Security:BootstrapSubject"];
            if (!validateOnly && !string.IsNullOrWhiteSpace(issuer) && !string.IsNullOrWhiteSpace(subject))
                await services.GetRequiredService<IAccessGrantStore>().BootstrapAsync(issuer, subject, token);
            if (validateOnly) await WorkspaceRuntime.RecoverTemporaryAsync(services, token);
            await services.GetRequiredService<IngestService>().RecoverAsync(token);
            if (validateOnly && !(await services.GetRequiredService<ISpoolQuotaStore>().GetSnapshotAsync(token)).IsKnown)
                throw new InvalidOperationException("Workspace accounting is unknown; writers remain closed.");
            if (configuration.GetValue<bool>("ShutterSpool:CameraIngress:Enabled"))
                await services.GetRequiredService<CameraIngressService>().RecoverAsync(
                    configuration.GetValue("ShutterSpool:CameraIngress:TransferTimeoutSeconds", 600), token);
        }, validateOnly);
    }

    internal static async Task ValidateWorkspaceInventoryAsync(IConfiguration configuration, WorkspaceDefinition[] workspaces, CancellationToken token)
    {
        long usable = configuration.GetValue<long>("ShutterSpool:Workspaces:UsableCapacityBytes");
        if (usable <= 0 || workspaces.Aggregate(0L, (sum, item) => checked(sum + item.AllocatedBytes)) > usable)
            throw new InvalidOperationException("Workspace allocations exceed the operator's usable storage budget.");
        await using (var control = new Npgsql.NpgsqlConnection(PostgresWorkspaceRegistry.ControlConnection(configuration)))
        {
            await control.OpenAsync(token);
            await using var privileges = new Npgsql.NpgsqlCommand("""
                SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls
                    OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=pg_roles.oid)
                    OR has_schema_privilege(current_user,'public','CREATE')
                    OR has_table_privilege(current_user,'workspaces','INSERT,UPDATE,DELETE')
                    OR EXISTS(SELECT 1 FROM pg_database WHERE datname=current_database() AND pg_get_userbyid(datdba)=current_user)
                FROM pg_roles WHERE rolname=current_user
                """, control);
            if ((bool)(await privileges.ExecuteScalarAsync(token))!)
                throw new InvalidOperationException("Control runtime credentials must be restricted to registry reads and opaque ingress routing writes.");
            foreach (WorkspaceDefinition workspace in workspaces)
            {
                string name = new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString(workspace.ConnectionReference)).Database!;
                await using var denied = new Npgsql.NpgsqlCommand("SELECT has_database_privilege(current_user,@database,'CONNECT')", control);
                denied.Parameters.AddWithValue("database", name);
                if ((bool)(await denied.ExecuteScalarAsync(token))!) throw new InvalidOperationException("Control runtime credentials can connect to a tenant database.");
            }
        }
        for (int index = 0; index < workspaces.Length; index++)
        {
            WorkspaceDefinition workspace = workspaces[index];
            WorkspaceRuntime.ValidateRoot(workspace);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.StorageRoot));
            foreach (WorkspaceDefinition other in workspaces.Where(item => item.Id != workspace.Id))
            {
                string otherRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other.StorageRoot));
                if (root.Equals(otherRoot, StringComparison.OrdinalIgnoreCase) || root.StartsWith(otherRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Workspace storage allocations cannot overlap.");
            }
            var own = new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString(workspace.ConnectionReference));
            var controlTarget = new Npgsql.NpgsqlConnectionStringBuilder(PostgresWorkspaceRegistry.ControlConnection(configuration));
            if (!string.Equals(own.Host, controlTarget.Host, StringComparison.OrdinalIgnoreCase) || own.Port != controlTarget.Port)
                throw new InvalidOperationException("The supported workspace topology uses one explicitly selected PostgreSQL server.");
            await using var connection = new Npgsql.NpgsqlConnection(own.ConnectionString);
            await connection.OpenAsync(token);
            foreach (string database in workspaces.Where(item => item.Id != workspace.Id)
                .Select(item => new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString(item.ConnectionReference)).Database!)
                .Append(new Npgsql.NpgsqlConnectionStringBuilder(PostgresWorkspaceRegistry.ControlConnection(configuration)).Database!))
            {
                await using var command = new Npgsql.NpgsqlCommand("SELECT has_database_privilege(current_user,@database,'CONNECT')", connection);
                command.Parameters.AddWithValue("database", database);
                if ((bool)(await command.ExecuteScalarAsync(token))!)
                    throw new InvalidOperationException("Workspace runtime credentials can connect to another workspace or the control database.");
            }
        }
    }
}
