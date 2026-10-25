namespace ShutterSpool.Infrastructure.Media;

internal static class SeekableMediaStream
{
    public static async Task<SeekableMedia> OpenAsync(Stream original, long size, long maximumSize, CancellationToken cancellationToken)
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

        string path = Path.Combine(Path.GetTempPath(), $"shutterspool-media-{Guid.NewGuid():N}.tmp");
        FileStream? file = null;
        try
        {
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
            return new SeekableMedia(file, ownsStream: true);
        }
        catch
        {
            if (file is not null) await file.DisposeAsync();
            else if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }
}

internal sealed class SeekableMedia(Stream stream, bool ownsStream) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public ValueTask DisposeAsync() => ownsStream ? Stream.DisposeAsync() : ValueTask.CompletedTask;
}

internal sealed class MediaUnreadableException() : Exception("Media cannot be read.");

internal sealed class MediaSizeLimitException() : Exception("Media exceeds its size limit.");

internal sealed class MediaSizeMismatchException() : Exception("Media length does not match its committed size.");
