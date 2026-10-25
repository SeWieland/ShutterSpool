using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Sources;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfSourceDisplayStore(ShutterSpoolDbContext database) : ISourceDisplayStore
{
    public Task<SourceDisplay?> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        database.Sources.AsNoTracking().Where(source => source.Id == id)
            .Select(source => new SourceDisplay(source.Id, source.Name, source.CurationMode,
                source.CurationDelaySeconds, source.ConfigurationRevision, source.IsArchived, source.SourceType, source.ConnectionProfile))
            .SingleOrDefaultAsync(cancellationToken);
}
