using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Infrastructure.Media;

internal static class SeekableMediaStream
{
    public static async Task<SeekableMedia> OpenAsync(Stream original, long size, long maximumSize, CancellationToken cancellationToken,
        WorkspaceContext? workspace = null, ISpoolQuotaStore? quota = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!original.CanRead) throw new MediaUnreadableException();
        if (size < 0 || size > maximumSize) throw new MediaSizeLimitException();
        if (original.CanSeek)
        {
            if (original.Length < size) throw new EndOfStreamException("Media is shorter than its committed size.");
            if (original.Length > size) throw new MediaSizeMismatchException();
            original.Position = 0;
            return new SeekableMedia(original, ownsStream: false);
        }

        Guid operation = Guid.NewGuid();
        bool tenant = workspace?.Enabled == true;
        string key = $"incoming/media-temporary/{operation:N}.tmp";
        string path = tenant ? Path.Combine(workspace!.Required.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(Path.GetTempPath(), $"shutterspool-media-{operation:N}.tmp");
        if (tenant)
        {
            if (quota is null || size <= 0 || !await quota.TryReserveAsync(new(operation, null, size, 0, [key], size), cancellationToken))
                throw new IOException("Temporary workspace media capacity is unavailable.");
        }
        FileStream? file = null;
        try
        {
            if (tenant)
            {
                string directory = Path.GetDirectoryName(path)!;
                System.IO.Directory.CreateDirectory(directory);
                for (DirectoryInfo? current = new(directory); current is not null; current = current.Parent)
                    if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Temporary media contains a linked path.");
            }
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            file = new FileStream(path, options);
            byte[] buffer = new byte[64 * 1024];
            long remaining = size;
            while (remaining > 0)
            {
                int read = await original.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new EndOfStreamException("Media is shorter than its committed size.");
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
            if (await original.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
                throw new MediaSizeMismatchException();
            await file.FlushAsync(cancellationToken);
            file.Position = 0;
            return new SeekableMedia(file, ownsStream: true, tenant ? async () =>
            {
                if (!File.Exists(path)) await quota!.ReleaseReservationAsync(operation, CancellationToken.None);
            } : null);
        }
        catch
        {
            if (file is not null) await file.DisposeAsync();
            else if (File.Exists(path)) File.Delete(path);
            if (tenant && !File.Exists(path)) await quota!.ReleaseReservationAsync(operation, CancellationToken.None);
            throw;
        }
    }
}

internal sealed class SeekableMedia(Stream stream, bool ownsStream, Func<Task>? released = null) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public async ValueTask DisposeAsync()
    {
        if (!ownsStream) return;
        await Stream.DisposeAsync();
        if (released is not null) await released();
    }
}

internal sealed class MediaUnreadableException() : Exception("Media cannot be read.");

internal sealed class MediaSizeLimitException() : Exception("Media exceeds its size limit.");

internal sealed class MediaSizeMismatchException() : Exception("Media length does not match its committed size.");
