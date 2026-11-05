using MetadataExtractor;
using MetadataExtractor.Formats.Jpeg;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Media;
using System.Net.Http.Headers;

namespace ShutterSpool.Infrastructure.Preview;

internal sealed class PreviewGenerationGate(PreviewSettings settings) : IDisposable
{
    // ponytail: this bounds one Server replica; add cross-replica coordination only if shared Imaginary load requires it.
    private readonly SemaphoreSlim _slots = new(settings.MaxConcurrentGenerations);
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _slots.WaitAsync(timeout, cancellationToken);
    public void Release() => _slots.Release();
    public void Dispose() => _slots.Dispose();
}

internal sealed class ImaginaryPreviewGenerator(ICommittedOriginalReader originals, HttpClient imaginary,
    PreviewSettings settings, PreviewGenerationGate generationGate,
    ShutterSpool.Application.Workspaces.WorkspaceContext? workspace = null,
    ShutterSpool.Application.Retention.ISpoolQuotaStore? quota = null) : IPreviewGenerator
{
    private const long MaximumInputBytes = 64L * 1024 * 1024;
    private const int MaximumOutputBytes = 4 * 1024 * 1024;

    public Task<byte[]> GenerateAsync(PreviewSource source, PreviewSize size, CancellationToken cancellationToken) =>
        GenerateCoreAsync(source, size, null, cancellationToken);

    public Task<byte[]> GenerateAsync(PreviewSource source, PreviewSize size, IOriginalAssetLockLease sharedLease,
        CancellationToken cancellationToken) => GenerateCoreAsync(source, size, sharedLease, cancellationToken);

    private async Task<byte[]> GenerateCoreAsync(PreviewSource source, PreviewSize size, IOriginalAssetLockLease? sharedLease,
        CancellationToken cancellationToken)
    {
        if (source.Size is <= 0 or > MaximumInputBytes) throw new PreviewPermanentException("Preview source exceeds its input limit.");
        if (!await generationGate.WaitAsync(TimeSpan.FromSeconds(settings.GenerationQueueTimeoutSeconds), cancellationToken))
            throw new TimeoutException("Preview generation is busy.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.GenerationTimeoutSeconds));
            await using Stream original = sharedLease is null
                ? await originals.OpenReadAsync(source.SpoolKey, deadline.Token)
                : await originals.OpenReadAsync(source.AssetId, source.SpoolKey, sharedLease, deadline.Token);
            SeekableMedia seekable;
            try { seekable = await SeekableMediaStream.OpenAsync(original, source.Size, MaximumInputBytes, deadline.Token, workspace, quota); }
            catch (Exception error) when (error is MediaUnreadableException or MediaSizeLimitException or MediaSizeMismatchException or InvalidDataException or EndOfStreamException)
            {
                throw new PreviewPermanentException("Preview source size does not match its committed metadata.", error);
            }
            await using (seekable)
            {
                try { ValidateJpegDimensions(seekable.Stream); }
                catch (InvalidDataException error) { throw new PreviewPermanentException("Invalid JPEG preview source.", error); }

                using var request = new HttpRequestMessage(HttpMethod.Post, $"fit?width={size.Width}&height={size.Height}&type=webp&stripmeta=true");
                request.Content = new StreamContent(new BoundedReadStream(seekable.Stream, MaximumInputBytes), 64 * 1024);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                using HttpResponseMessage response = await imaginary.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if ((int)response.StatusCode >= 500 || response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.RequestTimeout)
                    throw new HttpRequestException("Preview generator is temporarily unavailable.");
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumOutputBytes)
                    throw new PreviewPermanentException("Preview generator rejected the source or exceeded its output limit.");

                await using Stream output = await response.Content.ReadAsStreamAsync(deadline.Token);
                using var buffer = new MemoryStream();
                byte[] chunk = new byte[64 * 1024];
                int count;
                while ((count = await output.ReadAsync(chunk, deadline.Token)) != 0)
                {
                    if (buffer.Length > MaximumOutputBytes - count) throw new PreviewPermanentException("Preview output exceeds its limit.");
                    buffer.Write(chunk, 0, count);
                }
                byte[] bytes = buffer.ToArray();
                if (!PreviewMediaValidation.IsWebP(bytes, size)) throw new InvalidDataException("Preview generator returned invalid WebP.");
                return bytes;
            }
        }
        finally
        {
            generationGate.Release();
        }
    }

    private static void ValidateJpegDimensions(Stream jpeg)
    {
        // Imaginary has no decoded-resolution limit. Keep header parsing bounded and reject >60 MP/non-8-bit input.
        jpeg.Position = 0;
        try
        {
            using var headers = new BoundedReadStream(jpeg, 1024 * 1024);
            JpegDirectory? dimensions = JpegMetadataReader.ReadMetadata(headers, [new JpegReader()])
                .OfType<JpegDirectory>().FirstOrDefault();
            if (dimensions is null
                || !dimensions.TryGetInt32(JpegDirectory.TagDataPrecision, out int precision) || precision != 8
                || !dimensions.TryGetInt32(JpegDirectory.TagImageWidth, out int width) || width <= 0
                || !dimensions.TryGetInt32(JpegDirectory.TagImageHeight, out int height) || height <= 0
                || (long)width * height > 60_000_000)
                throw new InvalidDataException("JPEG dimensions are unavailable or exceed the preview limit.");
        }
        catch (Exception error) when (error is ImageProcessingException or IOException)
        {
            throw new InvalidDataException("Invalid JPEG preview source.", error);
        }
        finally { jpeg.Position = 0; }
    }

    private sealed class BoundedReadStream(Stream inner, long maximum) : Stream
    {
        private readonly long _maximum = maximum;
        private long _remaining = maximum;
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => Math.Min(inner.Length, _maximum);
        public override long Position { get => _maximum - _remaining; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining + 1));
            _remaining -= read;
            if (_remaining < 0) throw new PreviewPermanentException("Preview source exceeds its input limit.");
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining + 1)], cancellationToken);
            _remaining -= read;
            if (_remaining < 0) throw new PreviewPermanentException("Preview source exceeds its input limit.");
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0 || target > Length) throw new IOException("Seek exceeded the preview input limit.");
            long position = inner.Seek(target, SeekOrigin.Begin);
            _remaining = _maximum - position;
            return position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
