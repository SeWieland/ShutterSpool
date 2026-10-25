using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Util;
using ShutterSpool.Application.Metadata;
using ShutterSpool.Infrastructure.Media;
using System.Globalization;

namespace ShutterSpool.Infrastructure.Metadata;

internal sealed class MetadataExtractorMediaMetadataParser : IMediaMetadataParser
{
    private const long MaximumFileSize = 512L * 1024 * 1024;
    private const int TagOffsetTimeOriginal = 0x9011;
    private const int TagCompression = 0x0103;
    private const int TagDngVersion = 0xc612;
    private const int TagSonyRawFileType = 0x7000;
    private const int SonyArwCompression = 32767;
    private const int Uncompressed = 1;
    private const int ColorFilterArray = 32803;

    public async Task<AssetMetadata> ParseAsync(Stream original, string originalName, long size, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(originalName);
        cancellationToken.ThrowIfCancellationRequested();

        SeekableMedia seekable;
        try
        {
            seekable = await SeekableMediaStream.OpenAsync(original, size, MaximumFileSize, cancellationToken);
        }
        catch (MediaSizeLimitException)
        {
            return Result(MetadataOutcome.Unsupported, null, "OTHER", originalName, null, null, null, "size_limit_exceeded");
        }
        catch (Exception error) when (error is MediaUnreadableException or EndOfStreamException)
        {
            return Result(MetadataOutcome.Failed, null, "OTHER", originalName, null, null, null, "original_unreadable");
        }
        catch (MediaSizeMismatchException)
        {
            return Result(MetadataOutcome.Failed, null, "OTHER", originalName, null, null, null, "original_size_mismatch");
        }

        await using (seekable)
        {
            return ParseSeekable(seekable.Stream, originalName, size);
        }
    }

    private static AssetMetadata ParseSeekable(Stream original, string originalName, long size)
    {
        original.Position = 0;

        try
        {
            using var bounded = new BoundedReadStream(original, size);
            FileType type = FileTypeDetector.DetectFileType(bounded, null);
            if (type is not (FileType.Jpeg or FileType.Arw or FileType.Tiff))
            {
                return Result(MetadataOutcome.Unsupported, null, "OTHER", originalName, null, null, null, "unsupported_media_type");
            }

            bounded.Position = 0;
            IReadOnlyList<MetadataExtractor.Directory> directories = ImageMetadataReader.ReadMetadata(bounded, null);
            ExifIfd0Directory? ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
            ExifSubIfdDirectory[] subIfds = [.. directories.OfType<ExifSubIfdDirectory>()];
            if (type == FileType.Tiff && IsSonyRaw(ifd0, subIfds))
                type = FileType.Arw;

            EmbeddedValue? make = Embedded(ifd0, ExifDirectoryBase.TagMake, "Exif IFD0:Make");
            EmbeddedValue? model = Embedded(ifd0, ExifDirectoryBase.TagModel, "Exif IFD0:Model");
            EmbeddedValue[] serials = [.. subIfds
                .Select(directory => Embedded(directory, ExifDirectoryBase.TagBodySerialNumber, "Exif SubIFD:BodySerialNumber"))
                .OfType<EmbeddedValue>().DistinctBy(value => value.Value, StringComparer.OrdinalIgnoreCase)];
            bool serialConflict = serials.Length > 1;
            EmbeddedValue? serial = serials.Length == 1 ? serials[0] : null;
            CaptureTime?[] originalTimes =
            [
                .. subIfds
                    .Where(directory => directory.ContainsTag(ExifSubIfdDirectory.TagDateTimeOriginal))
                    .Select(ReadCaptureTime)
            ];
            CaptureTime? captureTime = originalTimes.FirstOrDefault();
            if (originalTimes.Any(time => time is null || time.Local != captureTime?.Local || time.Offset != captureTime?.Offset || time.FractionalDigits != captureTime?.FractionalDigits))
                captureTime = null;
            if (serialConflict && captureTime is not null) captureTime = captureTime with { IsUncertain = true };
            string role = type switch { FileType.Jpeg => "JPEG", FileType.Arw => "RAW", _ => "OTHER" };
            string mediaType = type switch { FileType.Jpeg => "image/jpeg", FileType.Arw => "image/x-sony-arw", _ => "image/tiff" };
            bool mismatch = IsExtensionMismatch(originalName, role);
            MetadataOutcome outcome = captureTime is not null && !serialConflict && make is not null && model is not null
                ? MetadataOutcome.Complete
                : MetadataOutcome.Partial;

            return new AssetMetadata(outcome, mediaType, role, captureTime, make, model, serial, mismatch,
                serialConflict ? "conflicting_camera_serial" : null);
        }
        catch (ImageProcessingException)
        {
            return Result(MetadataOutcome.Failed, null, "OTHER", originalName, null, null, null, "malformed_media");
        }
        // MetadataExtractor uses IOException for a truncated JPEG segment; other I/O faults remain retryable.
        catch (IOException error) when (error.Message == "End of data reached.")
        {
            return Result(MetadataOutcome.Failed, null, "OTHER", originalName, null, null, null, "malformed_media");
        }
        catch (ArgumentException)
        {
            return Result(MetadataOutcome.Failed, null, "OTHER", originalName, null, null, null, "malformed_media");
        }
        catch (OverflowException)
        {
            return Result(MetadataOutcome.Failed, null, "OTHER", originalName, null, null, null, "malformed_metadata");
        }
    }

    private static bool IsSonyRaw(ExifIfd0Directory? ifd0, ExifSubIfdDirectory[] subIfds)
    {
        if (!string.Equals(ifd0?.GetString(ExifDirectoryBase.TagMake)?.Trim(), "SONY", StringComparison.OrdinalIgnoreCase))
            return false;

        ExifDirectoryBase[] imageDirectories = ifd0 is null ? subIfds : [ifd0, .. subIfds];
        // DNG and ordinary camera TIFFs must not be relabelled from their make or filename.
        if (imageDirectories.Any(directory => directory.ContainsTag(TagDngVersion))) return false;
        return imageDirectories.Any(directory => directory.TryGetInt32(TagCompression, out int compression)
            && (compression == SonyArwCompression || compression == Uncompressed
                && directory.TryGetInt32(ExifDirectoryBase.TagPhotometricInterpretation, out int photometric) && photometric == ColorFilterArray
                && directory.TryGetInt32(TagSonyRawFileType, out int rawType) && rawType is 0 or 1));
    }

    private static CaptureTime? ReadCaptureTime(ExifSubIfdDirectory? directory)
    {
        if (directory is null || !directory.ContainsTag(ExifSubIfdDirectory.TagDateTimeOriginal)) return null;
        string? raw = directory.GetString(ExifSubIfdDirectory.TagDateTimeOriginal);
        if (raw is null || !DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local)) return null;

        string? fraction = directory.GetString(ExifSubIfdDirectory.TagSubsecondTimeOriginal)?.Trim();
        int fractionalDigits = fraction?.Length ?? 0;
        if (fractionalDigits > 7 || (fraction is not null && !fraction.All(char.IsAsciiDigit))) return null;
        if (fractionalDigits > 0)
        {
            long ticks = long.Parse(fraction!.PadRight(7, '0'), CultureInfo.InvariantCulture);
            local = local.AddTicks(ticks);
        }

        string? offsetText = directory.GetString(TagOffsetTimeOriginal);
        TimeSpan? offset = null;
        if (offsetText is not null)
        {
            if (!TryParseOffset(offsetText, out TimeSpan parsedOffset)) return null;
            offset = parsedOffset;
        }

        return new CaptureTime(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset, fractionalDigits, "Exif SubIFD:DateTimeOriginal", false);
    }

    private static EmbeddedValue? Embedded(MetadataExtractor.Directory? directory, int tag, string tagName)
    {
        string? value = directory?.GetString(tag)?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 512 || tagName.Length > 256 ? null : new EmbeddedValue(value, tagName);
    }

    private static bool TryParseOffset(string value, out TimeSpan offset)
    {
        offset = default;
        if (value.Length != 6 || (value[0] != '+' && value[0] != '-') || value[3] != ':'
            || !int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
            || !int.TryParse(value.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
            || hours > 14 || minutes > 59 || (hours == 14 && minutes != 0)) return false;

        offset = TimeSpan.FromMinutes((hours * 60 + minutes) * (value[0] == '-' ? -1 : 1));
        return true;
    }

    private static AssetMetadata Result(MetadataOutcome outcome, string? mediaType, string role, string name, CaptureTime? captureTime, EmbeddedValue? make, EmbeddedValue? model, string reason) =>
        new(outcome, mediaType, role, captureTime, make, model, null, IsExtensionMismatch(name, role), reason);

    private static bool IsExtensionMismatch(string name, string role)
    {
        string extension = Path.GetExtension(name);
        return role switch
        {
            "JPEG" => !extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase),
            "RAW" => !extension.Equals(".arw", StringComparison.OrdinalIgnoreCase),
            _ => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".arw", StringComparison.OrdinalIgnoreCase)
        };
    }

    private sealed class BoundedReadStream(Stream inner, long length) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => inner.Position; set => Seek(value, SeekOrigin.Begin); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, (int)Math.Min(count, Math.Max(0, length - Position)));
        public override int Read(Span<byte> buffer) => inner.Read(buffer[..(int)Math.Min(buffer.Length, Math.Max(0, length - Position))]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, Math.Max(0, length - Position))], cancellationToken);
        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > length) throw new IOException("Seek exceeded bounded stream.");
            return inner.Seek(target, SeekOrigin.Begin);
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
