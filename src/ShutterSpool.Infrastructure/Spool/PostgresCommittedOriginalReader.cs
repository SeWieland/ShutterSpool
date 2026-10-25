using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class PostgresCommittedOriginalReader(ISpool spool, IDbContextFactory<ShutterSpoolDbContext> databases,
    PostgresOriginalAssetLock locks) : ICommittedOriginalReader
{
    private readonly ICommittedOriginalReader _files = spool as ICommittedOriginalReader
        ?? throw new InvalidOperationException("The configured spool cannot read committed originals.");

    public async Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spoolKey) || spoolKey.Length > 1_024)
            throw new InvalidOperationException("The key does not name a committed original.");

        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        Guid? assetId = await database.Assets.AsNoTracking()
            .Where(asset => asset.SpoolKey == spoolKey)
            .Select(asset => (Guid?)asset.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (assetId is null) throw new FileNotFoundException("The committed original is unavailable.");

        IOriginalAssetLockLease lease = await locks.AcquireSharedAsync(assetId.Value, spoolKey, cancellationToken);
        try
        {
            if (!await locks.IsAvailableAsync(lease, cancellationToken))
                throw new FileNotFoundException("The committed original is unavailable.");
            Stream original = await _files.OpenReadAsync(spoolKey, cancellationToken);
            return new LockedOriginalStream(original, lease);
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public async Task<Stream> OpenReadAsync(Guid assetId, string spoolKey, IOriginalAssetLockLease sharedLease,
        CancellationToken cancellationToken)
    {
        if (sharedLease.AssetId != assetId || sharedLease.StorageKey != spoolKey)
            throw new InvalidOperationException("The shared original lock does not match the requested Asset.");
        if (!await locks.IsAvailableAsync(sharedLease, cancellationToken))
            throw new FileNotFoundException("The committed original is unavailable.");
        return await _files.OpenReadAsync(spoolKey, cancellationToken);
    }

    private sealed class LockedOriginalStream(Stream original, IOriginalAssetLockLease lease) : Stream
    {
        private Stream? _original = original;
        private IOriginalAssetLockLease? _lease = lease;
        private Stream Inner => Volatile.Read(ref _original) ?? throw new ObjectDisposedException(nameof(LockedOriginalStream));

        public override bool CanRead => _original?.CanRead ?? false;
        public override bool CanSeek => _original?.CanSeek ?? false;
        public override bool CanWrite => false;
        public override long Length => Inner.Length;
        public override long Position { get => Inner.Position; set => Inner.Position = value; }
        public override void Flush() => Inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => Inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Inner.ReadAsync(buffer, cancellationToken);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stream? ownedOriginal = Interlocked.Exchange(ref _original, null);
                IOriginalAssetLockLease? ownedLease = Interlocked.Exchange(ref _lease, null);
                try { ownedOriginal?.Dispose(); }
                finally
                {
                    try { ownedLease?.Dispose(); }
                    finally { base.Dispose(disposing); }
                }
            }
            else base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            Stream? ownedOriginal = Interlocked.Exchange(ref _original, null);
            IOriginalAssetLockLease? ownedLease = Interlocked.Exchange(ref _lease, null);
            try
            {
                if (ownedOriginal is not null) await ownedOriginal.DisposeAsync();
            }
            finally
            {
                try
                {
                    if (ownedLease is not null) await ownedLease.DisposeAsync();
                }
                finally
                {
                    GC.SuppressFinalize(this);
                }
            }
        }
    }
}
