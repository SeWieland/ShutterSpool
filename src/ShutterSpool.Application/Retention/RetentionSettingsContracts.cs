using ShutterSpool.Application.Preview;
using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Application.Retention;

public sealed record RetentionSettingsDocument(RetentionSettings Settings, SpoolQuotaSettings Quota,
    PreviewCacheSettings PreviewCache, PreviewCacheSettings EffectivePreviewCache, bool RestartRequired);
public sealed record RetentionSettingsImpact(DateTimeOffset AsOf, bool Complete, bool AccountingKnown, long UsedBytes,
    long ReservedBytes, long? ProposedQuotaHeadroomBytes, long ProtectedBytes,
    int? EligibleCaptureCount, int? PlannedTrashCaptureCount, long? PlannedBytes, bool OverQuota,
    bool AdmissionBlocked, bool ImpactAcknowledgementRequired, bool QuotaActivationAcknowledgementRequired,
    string[] Warnings);
public sealed record RetentionSettingsUpdateResult(bool Succeeded, RetentionSettingsDocument? Current,
    RetentionSettingsImpact? Impact, string? ErrorCode);

public interface IRetentionSettingsStore
{
    Task<RetentionSettingsDocument> GetAsync(CancellationToken cancellationToken);
    Task<RetentionSettingsImpact> PreviewAsync(RetentionSettings settings, SpoolQuotaSettings quota,
        PreviewCacheSettings previewCache,
        CancellationToken cancellationToken);
    Task<RetentionSettingsUpdateResult> UpdateAsync(int expectedSettingsRevision, int expectedQuotaRevision,
        RetentionSettings settings, SpoolQuotaSettings quota, PreviewCacheSettings previewCache, bool acknowledgeImpact,
        CancellationToken cancellationToken);
}
