using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Sources;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfSourceAuthenticationObservationStore(ShutterSpoolDbContext database) : ISourceAuthenticationObservationStore
{
    public async Task<IReadOnlyList<SourceAuthenticationSnapshot>> ReadAsync(IReadOnlyCollection<Guid> sourceIds,
        CancellationToken cancellationToken) => await database.SourceAuthenticationObservations.AsNoTracking()
        .Where(item => sourceIds.Contains(item.SourceId))
        .Select(item => new SourceAuthenticationSnapshot(item.SourceId,
            item.Outcome == nameof(SourceAuthenticationOutcome.Succeeded)
                ? SourceAuthenticationOutcome.Succeeded : SourceAuthenticationOutcome.Rejected,
            item.ObservedAt, item.Version))
        .ToArrayAsync(cancellationToken);

    public async Task<long?> RecordAsync(SourceAuthenticationObservation observation, CancellationToken cancellationToken)
    {
        string outcome = observation.Outcome.ToString();
        if (database.Database.IsRelational())
        {
            long[] versions = await database.Database.SqlQuery<long>($"""
                INSERT INTO source_authentication_observations ("SourceId", "Outcome", "ObservedAt", "ObservedAtTicks", "Version")
                SELECT {observation.SourceId}, {outcome}, {observation.ObservedAt}, {observation.ObservedAt.UtcTicks}, 1
                WHERE EXISTS (SELECT 1 FROM sources WHERE "Id" = {observation.SourceId})
                ON CONFLICT ("SourceId") DO UPDATE SET
                    "Outcome" = EXCLUDED."Outcome",
                    "ObservedAt" = EXCLUDED."ObservedAt",
                    "ObservedAtTicks" = EXCLUDED."ObservedAtTicks",
                    "Version" = source_authentication_observations."Version" + 1
                WHERE EXCLUDED."ObservedAtTicks" >= source_authentication_observations."ObservedAtTicks"
                RETURNING "Version" AS "Value"
                """).ToArrayAsync(cancellationToken);
            long version = versions.SingleOrDefault();
            return version == 0 ? null : version;
        }

        SourceAuthenticationObservationEntity? current = await database.SourceAuthenticationObservations
            .SingleOrDefaultAsync(item => item.SourceId == observation.SourceId, cancellationToken);
        if (current is null)
        {
            if (!await database.Sources.AnyAsync(item => item.Id == observation.SourceId, cancellationToken)) return null;
            current = new SourceAuthenticationObservationEntity { SourceId = observation.SourceId, Outcome = outcome, Version = 1 };
            database.SourceAuthenticationObservations.Add(current);
        }
        else if (current.ObservedAtTicks > observation.ObservedAt.UtcTicks) return null;
        else current.Version++;
        current.Outcome = outcome;
        current.ObservedAt = observation.ObservedAt;
        current.ObservedAtTicks = observation.ObservedAt.UtcTicks;
        await database.SaveChangesAsync(cancellationToken);
        return current.Version;
    }
}
