using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Application.Sources;

public sealed record SourceDisplay(Guid Id, string Name, SourceCurationMode CurationMode,
    int CurationDelaySeconds, long ConfigurationRevision, bool Archived, SourceType SourceType, SourceConnectionProfile ConnectionProfile);

public interface ISourceDisplayStore
{
    Task<SourceDisplay?> ReadAsync(Guid id, CancellationToken cancellationToken);
}
