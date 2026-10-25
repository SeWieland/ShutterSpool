using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Application.Curation;

public sealed record CaptureCuration(int Rating, string? Label, string[] Tags);

public sealed record CaptureCurationState(
    Guid Id,
    int Revision,
    int Rating,
    string? Label,
    string[] Tags,
    CaptureSelection Selection,
    SourceCurationMode Policy,
    int? DelaySeconds,
    DateTimeOffset? ReleaseDueAt,
    bool Held,
    string State,
    DateTimeOffset? ReleasedAt,
    int? ReleasedRevision,
    int? ReleasedRating,
    string? ReleasedLabel,
    string[] ReleasedTags,
    CaptureSelection? ReleasedSelection);

public sealed record CaptureMutationResult(bool Succeeded, CaptureCurationState? State);
public sealed record CaptureBulkCurationItem(Guid Id, int ExpectedRevision, CaptureCuration? Curation = null);
public sealed record CaptureBulkResult(Guid Id, string Status, CaptureCurationState? State, string? ErrorCode = null);

public interface ICaptureCurationStore
{
    Guid? LastReleasedCaptureId => null;
    Task<CaptureCurationState?> GetAsync(Guid captureId, CancellationToken cancellationToken);
    Task<CaptureMutationResult> UpdateAsync(Guid captureId, int expectedRevision, CaptureCuration curation, CancellationToken cancellationToken);
    Task<CaptureMutationResult> HoldAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken);
    Task<CaptureMutationResult> RestoreAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken);
    Task<CaptureMutationResult> ReleaseNowAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken);
    Task<bool> ReleaseDueOneAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class CaptureCurationService(ICaptureCurationStore store)
{
    public Guid? LastReleasedCaptureId => store.LastReleasedCaptureId;
    public Task<CaptureCurationState?> GetAsync(Guid captureId, CancellationToken cancellationToken) => store.GetAsync(captureId, cancellationToken);

    public Task<CaptureMutationResult> UpdateAsync(Guid captureId, int expectedRevision, CaptureCuration curation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(curation);
        if (expectedRevision < 0) throw new ArgumentException("The Capture revision is invalid.");
        string[] tags = CaptureCurationPolicy.Validate(curation.Rating, curation.Label, curation.Tags);
        return store.UpdateAsync(captureId, expectedRevision, curation with { Tags = tags }, cancellationToken);
    }

    public Task<CaptureMutationResult> HoldAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken) => store.HoldAsync(captureId, expectedRevision, cancellationToken);
    public Task<CaptureMutationResult> RestoreAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken) => store.RestoreAsync(captureId, expectedRevision, cancellationToken);
    public Task<CaptureMutationResult> ReleaseNowAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken) => store.ReleaseNowAsync(captureId, expectedRevision, cancellationToken);
    public Task<bool> ReleaseDueOneAsync(DateTimeOffset now, CancellationToken cancellationToken) => store.ReleaseDueOneAsync(now, cancellationToken);

    public async Task<CaptureBulkResult[]> UpdateManyAsync(IReadOnlyList<CaptureBulkCurationItem> items, CancellationToken cancellationToken)
    {
        List<CaptureBulkResult> results = new(items.Count);
        foreach (CaptureBulkCurationItem item in items)
        {
            if (item.ExpectedRevision < 0 || item.Curation is null)
            {
                results.Add(new(item.Id, "invalid", null, "INVALID_CURATION"));
                continue;
            }
            try { results.Add(Result(item.Id, await UpdateAsync(item.Id, item.ExpectedRevision, item.Curation, cancellationToken))); }
            catch (ArgumentException) { results.Add(new(item.Id, "invalid", null, "INVALID_CURATION")); }
        }
        return [.. results];
    }

    public Task<CaptureBulkResult[]> HoldManyAsync(IReadOnlyList<CaptureBulkCurationItem> items, CancellationToken token) =>
        MutateManyAsync(items, (id, revision, ct) => HoldAsync(id, revision, ct), token);
    public Task<CaptureBulkResult[]> RestoreManyAsync(IReadOnlyList<CaptureBulkCurationItem> items, CancellationToken token) =>
        MutateManyAsync(items, (id, revision, ct) => RestoreAsync(id, revision, ct), token);
    public Task<CaptureBulkResult[]> ReleaseManyAsync(IReadOnlyList<CaptureBulkCurationItem> items, CancellationToken token) =>
        MutateManyAsync(items, (id, revision, ct) => ReleaseNowAsync(id, revision, ct), token);

    private static async Task<CaptureBulkResult[]> MutateManyAsync(IReadOnlyList<CaptureBulkCurationItem> items,
        Func<Guid, int, CancellationToken, Task<CaptureMutationResult>> mutate, CancellationToken token)
    {
        List<CaptureBulkResult> results = new(items.Count);
        foreach (CaptureBulkCurationItem item in items)
            results.Add(item.ExpectedRevision < 0 ? new(item.Id, "invalid", null, "INVALID_REVISION")
                : Result(item.Id, await mutate(item.Id, item.ExpectedRevision, token)));
        return [.. results];
    }

    private static CaptureBulkResult Result(Guid id, CaptureMutationResult result) => result.State is null
        ? new(id, "not_found", null)
        : new(id, result.Succeeded ? "committed" : "conflict", result.State);
}
