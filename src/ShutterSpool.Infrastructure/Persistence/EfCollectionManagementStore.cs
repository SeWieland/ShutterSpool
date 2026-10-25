using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ShutterSpool.Application.Collections;
using ShutterSpool.Domain.Collections;
using System.Text;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfCollectionManagementStore(ShutterSpoolDbContext database) : ICollectionManagementStore
{
    public async Task<CollectionPage> ListAsync(string query, int limit, string? afterPath, Guid? afterId,
        CancellationToken token)
    {
        IQueryable<CollectionEntity> rows = database.Collections.AsNoTracking();
        if (query.Length > 0)
        {
            string search = query.Normalize(NormalizationForm.FormC).ToUpperInvariant();
            rows = rows.Where(item => item.Identity.Contains(search));
        }
        if (afterPath is not null && afterId is Guid id)
            rows = rows.Where(item => string.Compare(item.Path, afterPath) > 0
                || item.Path == afterPath && item.Id.CompareTo(id) > 0);
        CollectionEntity[] page = await rows.OrderBy(item => item.Path).ThenBy(item => item.Id)
            .Take(limit + 1).ToArrayAsync(token);
        bool more = page.Length > limit;
        CollectionEntity[] selected = [.. page.Take(limit)];
        return new(await SummariesAsync(selected, token), more ? EncodeCursor(selected[^1]) : null);
    }

    public async Task<CollectionSummary?> GetAsync(Guid id, CancellationToken token)
    {
        CollectionEntity? row = await database.Collections.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token);
        return row is null ? null : (await SummariesAsync([row], token))[0];
    }

    public async Task<CollectionSummary> CreateAsync(CollectionName name, CancellationToken token)
    {
        var row = new CollectionEntity { Id = Guid.NewGuid(), Name = name.Name, Path = name.Path,
            Identity = name.Identity, Revision = 0 };
        database.Collections.Add(row);
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            database.Entry(row).State = EntityState.Detached;
            throw new InvalidOperationException("A Collection with an equivalent name already exists.", exception);
        }
        return (await SummariesAsync([row], token))[0];
    }

    public async Task<CollectionWrite> RenameAsync(Guid id, long expectedRevision, CollectionName name, CancellationToken token)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(token);
        await LockAssignmentsAsync(token);
        CollectionEntity? row = await database.Collections.SingleOrDefaultAsync(item => item.Id == id, token);
        if (row is null)
        {
            await transaction.CommitAsync(token);
            return new(false, null);
        }
        if (row.Revision != expectedRevision || row.IsBuiltIn)
        {
            await transaction.CommitAsync(token);
            return new(false, await GetAsync(id, token));
        }
        if (await database.Collections.AnyAsync(item => item.Id != id && item.Identity == name.Identity, token))
        {
            await transaction.CommitAsync(token);
            return new(false, await GetAsync(id, token), NameConflict: true);
        }
        row.Name = name.Name;
        row.Path = name.Path;
        row.Identity = name.Identity;
        row.Revision++;
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(token);
            throw new InvalidOperationException("A Collection with an equivalent name already exists.", exception);
        }
        await transaction.CommitAsync(token);
        return new(true, await GetAsync(id, token));
    }

    public async Task<CollectionWrite> DeleteAsync(Guid id, long expectedRevision, CancellationToken token)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(token);
        await LockAssignmentsAsync(token);
        CollectionEntity? row = await database.Collections.SingleOrDefaultAsync(item => item.Id == id, token);
        if (row is null)
        {
            await transaction.CommitAsync(token);
            return new(false, null);
        }
        bool referenced = row.IsBuiltIn
            || await database.Sources.AnyAsync(item => item.CollectionId == id, token)
            || await database.Captures.AnyAsync(item => item.CollectionId == id, token);
        if (row.Revision != expectedRevision || referenced)
        {
            await transaction.CommitAsync(token);
            return new(false, await GetAsync(id, token), referenced);
        }
        database.Collections.Remove(row);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(true, null);
    }

    private async Task<CollectionSummary[]> SummariesAsync(CollectionEntity[] rows, CancellationToken token)
    {
        if (rows.Length == 0) return [];
        Guid[] ids = [.. rows.Select(row => row.Id)];
        CollectionReferenceCount[] counts = await database.Collections.AsNoTracking()
            .Where(collection => ids.Contains(collection.Id))
            .Select(collection => new CollectionReferenceCount
            {
                CollectionId = collection.Id,
                SourceReferences = database.Sources.Where(source => source.CollectionId == collection.Id).Take(1001).Count(),
                CaptureReferences = database.Captures.Where(capture => capture.CollectionId == collection.Id).Take(1001).Count()
            }).ToArrayAsync(token);
        Dictionary<Guid, CollectionReferenceCount> byId = counts.ToDictionary(item => item.CollectionId);
        return [.. rows.Select(item => new CollectionSummary(item.Id, item.Name, item.Path, item.Revision,
            item.IsBuiltIn, byId.GetValueOrDefault(item.Id)?.SourceReferences ?? 0,
            byId.GetValueOrDefault(item.Id)?.CaptureReferences ?? 0))];
    }

    private Task<int> LockAssignmentsAsync(CancellationToken token) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-collection-assignment', 0))", token);

    private static string EncodeCursor(CollectionEntity item) => $"{item.Path}|{item.Id:N}";

    private sealed class CollectionReferenceCount
    {
        public Guid CollectionId { get; set; }
        public int SourceReferences { get; set; }
        public int CaptureReferences { get; set; }
    }
}
