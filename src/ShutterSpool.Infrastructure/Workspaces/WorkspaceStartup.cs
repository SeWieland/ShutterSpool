using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Security;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Persistence;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Workspaces;

internal static class WorkspaceStartup
{
    internal static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope control = services.CreateAsyncScope();
        WorkspaceDefinition[] workspaces = await control.ServiceProvider.GetRequiredService<IWorkspaceRegistry>().ListReadyAsync(cancellationToken);
        await WorkspaceInventoryValidator.ValidateAsync(configuration, workspaces, cancellationToken);
        string[] names = configuration.GetSection("ShutterSpool:Workspaces:Definitions").GetChildren().Select(item => item.Key).Order(StringComparer.Ordinal).ToArray();
        if (workspaces.Length == 0 || !names.SequenceEqual(workspaces.Select(item => item.Name).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("Provision every named workspace in the canonical manifest before starting writers.");
        services.GetRequiredService<WorkspaceInventory>().Freeze(workspaces);
        foreach (WorkspaceDefinition workspace in workspaces)
            services.GetRequiredService<WorkspaceWriterLease>().Acquire(workspace);
        foreach (WorkspaceDefinition workspace in workspaces)
        {
            await using AsyncServiceScope tenant = services.CreateAsyncScope();
            tenant.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(workspace);
            IConfiguration scoped = tenant.ServiceProvider.GetRequiredService<WorkspaceConfiguration>().Value;
            await InitializeScopeAsync(tenant.ServiceProvider, scoped, cancellationToken);
        }
    }

    private static async Task InitializeScopeAsync(IServiceProvider services, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        _ = services.GetRequiredService<DeliveryAdapterRegistry>();
        string spoolRoot = configuration["ShutterSpool:Spool:RootPath"] ?? throw new InvalidOperationException("The spool root path is not configured.");
        DatabaseInitializer initializer = services.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync(spoolRoot,
            token => RecoverWorkspaceAsync(services, configuration, initializer, token), validateOnly: true, cancellationToken);
    }

    private static async Task RecoverWorkspaceAsync(IServiceProvider services, IConfiguration configuration,
        DatabaseInitializer initializer, CancellationToken token)
    {
        await initializer.ValidateSecretKeyAsync(services.GetRequiredService<ICredentialProtector>(), cancellationToken: token);
        if (configuration.GetValue<bool>("ShutterSpool:Restore:Enabled"))
            await SuppressionRestoreGate.ReconcileAsync(configuration,
                services.GetRequiredService<PrivacyErasureSuppressionService>(),
                services.GetRequiredService<PrivacyErasureService>(), token);
        string? previewCacheJson = await services.GetRequiredService<ShutterSpoolDbContext>()
            .RetentionPolicies.AsNoTracking().Where(policy => policy.Id == 1)
            .Select(policy => policy.PreviewCacheJson).SingleOrDefaultAsync(token);
        if (previewCacheJson is not null)
            (JsonSerializer.Deserialize<PreviewCacheSettings>(previewCacheJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException("The saved preview cache settings are invalid."))
                .ApplyTo(services.GetRequiredService<PreviewSettings>());
        await WorkspaceRuntime.RecoverTemporaryAsync(services, token);
        await services.GetRequiredService<IngestService>().RecoverAsync(token);
        if (!(await services.GetRequiredService<ISpoolQuotaStore>().GetSnapshotAsync(token)).IsKnown)
            throw new InvalidOperationException("Workspace accounting is unknown; writers remain closed.");
        if (configuration.GetValue<bool>("ShutterSpool:CameraIngress:Enabled"))
            await services.GetRequiredService<CameraIngressService>().RecoverAsync(
                configuration.GetValue("ShutterSpool:CameraIngress:TransferTimeoutSeconds", 600), token);
    }
}
