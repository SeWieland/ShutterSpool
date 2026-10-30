using Microsoft.AspNetCore.Mvc;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Server.Api.Captures;

public sealed record RetentionLifetimeDto(TimeSpan PressureAge, TimeSpan OrdinaryAge)
{
    internal RetentionLifetime ToDomain() => new(PressureAge, OrdinaryAge);
    internal static RetentionLifetimeDto From(RetentionLifetime value) => new(value.PressureAge, value.OrdinaryAge);
}

public sealed record RetentionPolicyDto(int Revision, bool AutomaticRemovalEnabled, TimeSpan TrashRecoveryWindow,
    TimeSpan RestoreProtection, TimeSpan GalleryMaximum, RetentionLifetimeDto Confirmed,
    RetentionLifetimeDto NoDelivery, RetentionLifetimeDto Waiting, RetentionLifetimeDto HeldOrUnfinished,
    int CompletedAttemptHistoryDays, int CompletedPressureHistoryDays)
{
    internal RetentionSettings ToDomain() => new()
    {
        Revision = Revision,
        AutomaticRemovalEnabled = AutomaticRemovalEnabled,
        TrashRecoveryWindow = TrashRecoveryWindow,
        RestoreProtection = RestoreProtection,
        GalleryMaximum = GalleryMaximum,
        Confirmed = Confirmed.ToDomain(),
        NoDelivery = NoDelivery.ToDomain(),
        Waiting = Waiting.ToDomain(),
        HeldOrUnfinished = HeldOrUnfinished.ToDomain(),
        CompletedAttemptHistoryDays = CompletedAttemptHistoryDays,
        CompletedPressureHistoryDays = CompletedPressureHistoryDays,
    };

    internal static RetentionPolicyDto From(RetentionSettings value) => new(value.Revision,
        value.AutomaticRemovalEnabled, value.TrashRecoveryWindow, value.RestoreProtection, value.GalleryMaximum,
        RetentionLifetimeDto.From(value.Confirmed), RetentionLifetimeDto.From(value.NoDelivery),
        RetentionLifetimeDto.From(value.Waiting), RetentionLifetimeDto.From(value.HeldOrUnfinished),
        value.CompletedAttemptHistoryDays, value.CompletedPressureHistoryDays);
}

public sealed record SpoolQuotaSettingsDto(int Revision, bool IsActive, long? QuotaBytes,
    long? HighWatermarkBytes, long? RecoveryTargetBytes)
{
    internal SpoolQuotaSettings ToApplication() => new(Revision, IsActive, QuotaBytes, HighWatermarkBytes, RecoveryTargetBytes);
    internal static SpoolQuotaSettingsDto From(SpoolQuotaSettings value) => new(value.Revision, value.IsActive,
        value.QuotaBytes, value.HighWatermarkBytes, value.RecoveryTargetBytes);
}

public sealed record PreviewCacheSettingsDto(string CacheMode, long MemoryCacheBytes)
{
    internal PreviewCacheSettings ToApplication()
    {
        if (CacheMode is not ("Disk" or "Memory")
            || !Enum.TryParse(CacheMode, ignoreCase: false, out PreviewCacheMode mode))
            throw new ArgumentOutOfRangeException(nameof(CacheMode), "Cache mode must be Disk or Memory.");
        return new(mode, MemoryCacheBytes);
    }

    internal static PreviewCacheSettingsDto From(PreviewCacheSettings value) => new(value.CacheMode.ToString(), value.MemoryCacheBytes);
}

public sealed record RetentionSettingsRequest(int ExpectedSettingsRevision, int ExpectedQuotaRevision,
    RetentionPolicyDto Settings, SpoolQuotaSettingsDto Quota, PreviewCacheSettingsDto PreviewCache,
    bool AcknowledgeImpact = false);

public sealed record RetentionSettingsDocumentResponse(RetentionPolicyDto Settings, SpoolQuotaSettingsDto Quota,
    PreviewCacheSettingsDto PreviewCache, PreviewCacheSettingsDto EffectivePreviewCache, bool RestartRequired,
    long? WorkspaceAllocatedBytes = null)
{
    internal static RetentionSettingsDocumentResponse From(RetentionSettingsDocument value, long? allocation = null) => new(
        RetentionPolicyDto.From(value.Settings), SpoolQuotaSettingsDto.From(value.Quota),
        PreviewCacheSettingsDto.From(value.PreviewCache), PreviewCacheSettingsDto.From(value.EffectivePreviewCache),
        value.RestartRequired, allocation);
}

public sealed record RetentionSettingsImpactResponse(DateTimeOffset AsOf, bool Complete, bool AccountingKnown,
    long UsedBytes, long ReservedBytes, long? ProposedQuotaHeadroomBytes, long ProtectedBytes,
    int? EligibleCaptureCount, int? PlannedTrashCaptureCount, long? PlannedBytes,
    bool OverQuota, bool AdmissionBlocked, bool ImpactAcknowledgementRequired,
    bool QuotaActivationAcknowledgementRequired, string[] Warnings)
{
    internal static RetentionSettingsImpactResponse From(RetentionSettingsImpact value) => new(value.AsOf,
        value.Complete, value.AccountingKnown, value.UsedBytes, value.ReservedBytes, value.ProposedQuotaHeadroomBytes,
        value.ProtectedBytes, value.EligibleCaptureCount, value.PlannedTrashCaptureCount,
        value.PlannedBytes,
        value.OverQuota, value.AdmissionBlocked, value.ImpactAcknowledgementRequired,
        value.QuotaActivationAcknowledgementRequired, value.Warnings);
}

public sealed record RetentionSettingsUpdateResponse(bool Succeeded, RetentionSettingsDocumentResponse? Current,
    RetentionSettingsImpactResponse? Impact, string? ErrorCode)
{
    internal static RetentionSettingsUpdateResponse From(RetentionSettingsUpdateResult value, long? allocation = null) => new(value.Succeeded,
        value.Current is null ? null : RetentionSettingsDocumentResponse.From(value.Current, allocation),
        value.Impact is null ? null : RetentionSettingsImpactResponse.From(value.Impact), value.ErrorCode);
}

public static class RetentionSettingsEndpoints
{
    public static RouteGroupBuilder MapRetentionSettingsEndpoints(this RouteGroupBuilder management)
    {
        RouteGroupBuilder retention = management.MapGroup("/retention");
        retention.MapGet("/settings", GetAsync).WithSummary("Read retention and shared quota settings")
            .Produces<RetentionSettingsDocumentResponse>();
        retention.MapPost("/settings/preview", PreviewAsync).WithSummary("Preview retention and quota impact")
            .Produces<RetentionSettingsImpactResponse>().ProducesValidationProblem();
        retention.MapPut("/settings", UpdateAsync).WithSummary("Update retention and quota settings")
            .Produces<RetentionSettingsUpdateResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        return management;
    }

    private static async Task<RetentionSettingsDocumentResponse> GetAsync([FromServices] RetentionSettingsService service,
        ShutterSpool.Application.Workspaces.WorkspaceContext workspace, CancellationToken cancellationToken) =>
        RetentionSettingsDocumentResponse.From(await service.GetAsync(cancellationToken), workspace.Enabled ? workspace.Required.AllocatedBytes : null);

    private static async Task<IResult> PreviewAsync(RetentionSettingsRequest request,
        [FromServices] RetentionSettingsService service, CancellationToken cancellationToken)
    {
        try
        {
            RetentionSettingsImpact impact = await service.PreviewAsync(request.Settings.ToDomain(),
                request.Quota.ToApplication(), request.PreviewCache.ToApplication(), cancellationToken);
            return Results.Ok(RetentionSettingsImpactResponse.From(impact));
        }
        catch (ArgumentException exception) { return Invalid("settings", exception.Message); }
    }

    private static async Task<IResult> UpdateAsync(RetentionSettingsRequest request,
        [FromServices] RetentionSettingsService service, ShutterSpool.Application.Workspaces.WorkspaceContext workspace, CancellationToken cancellationToken)
    {
        RetentionSettingsUpdateResult result;
        try
        {
            result = await service.UpdateAsync(request.ExpectedSettingsRevision, request.ExpectedQuotaRevision,
                request.Settings.ToDomain(), request.Quota.ToApplication(), request.PreviewCache.ToApplication(),
                request.AcknowledgeImpact, cancellationToken);
        }
        catch (ArgumentException exception) { return Invalid("settings", exception.Message); }
        RetentionSettingsUpdateResponse response = RetentionSettingsUpdateResponse.From(result, workspace.Enabled ? workspace.Required.AllocatedBytes : null);
        return result.Succeeded ? Results.Ok(response) : Results.Conflict(response);
    }

    private static IResult Invalid(string field, string error) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [error] });
}
