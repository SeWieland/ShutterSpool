using ShutterSpool.Application.Preview;
using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Application.Retention;

public sealed class RetentionSettingsService(IRetentionSettingsStore store)
{
    public Task<RetentionSettingsDocument> GetAsync(CancellationToken cancellationToken) =>
        store.GetAsync(cancellationToken);

    public Task<RetentionSettingsImpact> PreviewAsync(RetentionSettings settings, SpoolQuotaSettings quota,
        PreviewCacheSettings previewCache,
        CancellationToken cancellationToken)
    {
        Validate(settings, quota, previewCache);
        return store.PreviewAsync(settings, quota, previewCache, cancellationToken);
    }

    public Task<RetentionSettingsUpdateResult> UpdateAsync(int expectedSettingsRevision, int expectedQuotaRevision,
        RetentionSettings settings, SpoolQuotaSettings quota, PreviewCacheSettings previewCache, bool acknowledgeImpact,
        CancellationToken cancellationToken)
    {
        if (expectedSettingsRevision < 0 || expectedQuotaRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedSettingsRevision));
        Validate(settings, quota, previewCache);
        return store.UpdateAsync(expectedSettingsRevision, expectedQuotaRevision, settings, quota, previewCache,
            acknowledgeImpact, cancellationToken);
    }

    private static void Validate(RetentionSettings settings, SpoolQuotaSettings quota, PreviewCacheSettings previewCache)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(previewCache);
        settings.Validate();
        quota.Validate();
        previewCache.Validate();
        if (settings.AutomaticRemovalEnabled && !quota.IsActive)
            throw new ArgumentException("Automatic removal requires an explicitly active shared quota.");
    }
}
