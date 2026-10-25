using ShutterSpool.Application.Metadata;
using ShutterSpool.Infrastructure.Metadata;
using System.Buffers.Binary;

namespace ShutterSpool.IntegrationTests;

[Collection(MediaTempFileCollection.Name)]
public sealed class MediaMetadataParserTests
{
    [Fact]
    public async Task DetectsJpegFromSignatureAndFlagsMisleadingExtension()
    {
        var parser = new MetadataExtractorMediaMetadataParser();
        await using var original = new MemoryStream([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x02, 0xff, 0xd9]);

        AssetMetadata metadata = await parser.ParseAsync(original, "camera.ARW", original.Length, CancellationToken.None);

        Assert.Equal("image/jpeg", metadata.DetectedMediaType);
        Assert.Equal("JPEG", metadata.Role);
        Assert.True(metadata.ExtensionMismatch);
        Assert.Equal(MetadataOutcome.Partial, metadata.Outcome);
        Assert.Null(metadata.CaptureTime);
        Assert.True(original.CanRead);
    }

    [Fact]
    public async Task RejectsUnsupportedBytesWithoutTrustingExtension()
    {
        var parser = new MetadataExtractorMediaMetadataParser();
        await using var original = new MemoryStream("private contents"u8.ToArray());

        AssetMetadata metadata = await parser.ParseAsync(original, "photo.jpg", original.Length, CancellationToken.None);

        Assert.Equal(MetadataOutcome.Unsupported, metadata.Outcome);
        Assert.Equal("OTHER", metadata.Role);
        Assert.Null(metadata.DetectedMediaType);
        Assert.True(metadata.ExtensionMismatch);
        Assert.DoesNotContain("private contents", metadata.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsOversizedOriginalBeforeInspectingBytes()
    {
        var parser = new MetadataExtractorMediaMetadataParser();
        await using var original = new MemoryStream();

        AssetMetadata metadata = await parser.ParseAsync(original, "large.arw", 512L * 1024 * 1024 + 1, CancellationToken.None);

        Assert.Equal(MetadataOutcome.Unsupported, metadata.Outcome);
        Assert.Equal("size_limit_exceeded", metadata.Reason);
    }

    [Fact]
    public async Task RejectsSeekableOriginalLongerThanCommittedSize()
    {
        var parser = new MetadataExtractorMediaMetadataParser();
        await using var original = new MemoryStream([0xff, 0xd8, 0xff, 0xd9, 0]);

        AssetMetadata metadata = await parser.ParseAsync(original, "capture.jpg", original.Length - 1, CancellationToken.None);

        Assert.Equal(MetadataOutcome.Failed, metadata.Outcome);
        Assert.Equal("original_size_mismatch", metadata.Reason);
        Assert.True(original.CanRead);
    }

    [Fact]
    public async Task Parses_nonseekable_original_and_reports_truncation_without_leaving_a_temp_file()
    {
        byte[] bytes = MakeExifJpeg();
        var parser = new MetadataExtractorMediaMetadataParser();
        string[] before = Directory.GetFiles(Path.GetTempPath(), "shutterspool-media-*.tmp");

        AssetMetadata metadata = await parser.ParseAsync(new NonSeekableStream(bytes), "capture.jpg", bytes.Length, CancellationToken.None);
        AssetMetadata truncated = await parser.ParseAsync(new NonSeekableStream(bytes[..^1]), "capture.jpg", bytes.Length, CancellationToken.None);
        AssetMetadata oversized = await parser.ParseAsync(new NonSeekableStream([.. bytes, 0]), "capture.jpg", bytes.Length, CancellationToken.None);

        Assert.Equal(MetadataOutcome.Complete, metadata.Outcome);
        Assert.Equal(MetadataOutcome.Failed, truncated.Outcome);
        Assert.Equal("original_unreadable", truncated.Reason);
        Assert.Equal("original_size_mismatch", oversized.Reason);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "shutterspool-media-*.tmp"));
    }

    [Fact]
    public async Task Cancellation_while_copying_nonseekable_original_removes_the_private_temp_file()
    {
        using var cancellation = new CancellationTokenSource();
        var original = new CancelDuringReadStream(cancellation);
        string[] before = Directory.GetFiles(Path.GetTempPath(), "shutterspool-media-*.tmp");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MetadataExtractorMediaMetadataParser()
            .ParseAsync(original, "capture.jpg", 128 * 1024, cancellation.Token));

        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "shutterspool-media-*.tmp"));
    }

    [Fact]
    public async Task ParsesNearLimitOriginalWithoutReadingOrAllocatingTheWholeFile()
    {
        byte[] header = MakeSonyArw(includeExif: false);
        await using var original = new SparseOriginal(header, 512L * 1024 * 1024);
        long before = GC.GetAllocatedBytesForCurrentThread();

        AssetMetadata metadata = await new MetadataExtractorMediaMetadataParser()
            .ParseAsync(original, "sony.arw", original.Length, CancellationToken.None);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal("RAW", metadata.Role);
        Assert.True(original.BytesRead < 1024 * 1024, $"Read {original.BytesRead} bytes");
        Assert.True(allocated < 32L * 1024 * 1024, $"Allocated {allocated} bytes");
    }

    [Fact]
    public async Task LeavesCaptureTimeUnknownWhenEmbeddedOffsetIsInvalid()
    {
        var parser = new MetadataExtractorMediaMetadataParser();
        byte[] bytes = MakeExifJpeg("+99:99");
        await using var original = new MemoryStream(bytes);

        AssetMetadata metadata = await parser.ParseAsync(original, "capture.jpg", bytes.Length, CancellationToken.None);

        Assert.Null(metadata.CaptureTime);
        Assert.Equal(MetadataOutcome.Partial, metadata.Outcome);
    }

    [Fact]
    public async Task DetectsSonyRawTiffFromContentWithoutExif()
    {
        var parser = new MetadataExtractorMediaMetadataParser();
        byte[] bytes = MakeSonyArw(includeExif: false);
        await using var original = new MemoryStream(bytes);

        AssetMetadata metadata = await parser.ParseAsync(original, "sony.jpg", bytes.Length, CancellationToken.None);

        Assert.Equal("image/x-sony-arw", metadata.DetectedMediaType);
        Assert.Equal("RAW", metadata.Role);
        Assert.True(metadata.ExtensionMismatch);
        Assert.Equal(MetadataOutcome.Partial, metadata.Outcome);
        Assert.Null(metadata.CaptureTime);
        Assert.Equal("SONY", metadata.CameraMake?.Value);
    }

    [Fact]
    public async Task DoesNotLabelOtherPrivateTiffCompressionAsSonyRaw()
    {
        byte[] bytes = MakeSonyArw(includeExif: false);
        "CANO"u8.CopyTo(bytes.AsSpan(38));
        await using var original = new MemoryStream(bytes);

        AssetMetadata metadata = await new MetadataExtractorMediaMetadataParser()
            .ParseAsync(original, "other.arw", bytes.Length, CancellationToken.None);

        Assert.Equal(MetadataOutcome.Partial, metadata.Outcome);
        Assert.Equal("OTHER", metadata.Role);
    }

    [Theory]
    [InlineData(32767, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public async Task DetectsSonyRawInImageSubIfdInsteadOfPreviewIfd(int compression, int rawType)
    {
        byte[] bytes = MakeSonyImageSubIfd(compression, rawType);
        await using var original = new MemoryStream(bytes);
        AssetMetadata metadata = await new MetadataExtractorMediaMetadataParser()
            .ParseAsync(original, "shot.bin", bytes.Length, CancellationToken.None);

        Assert.Equal("RAW", metadata.Role);
        Assert.Equal("image/x-sony-arw", metadata.DetectedMediaType);
        Assert.Equal(MetadataOutcome.Complete, metadata.Outcome);
        Assert.NotNull(metadata.CaptureTime);
        Assert.Equal("12345", metadata.CameraSerial?.Value);
        Assert.True(metadata.ExtensionMismatch);
    }

    [Fact]
    public async Task ContradictoryEmbeddedSerialsCannotSupplyCertainGroupingEvidence()
    {
        byte[] bytes = new byte[224];
        MakeSonyImageSubIfd(32767, 2).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(164), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(202), 0xa431);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(204), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(206), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(210), 218);
        "56789\0"u8.CopyTo(bytes.AsSpan(218));
        await using var original = new MemoryStream(bytes);
        AssetMetadata metadata = await new MetadataExtractorMediaMetadataParser()
            .ParseAsync(original, "shot.arw", bytes.Length, CancellationToken.None);

        Assert.Null(metadata.CameraSerial);
        Assert.True(metadata.CaptureTime?.IsUncertain);
        Assert.Equal(MetadataOutcome.Partial, metadata.Outcome);
        Assert.Equal("conflicting_camera_serial", metadata.Reason);
    }

    [Fact]
    public async Task PreservesShotEvidenceOnOtherTiffWithoutCallingItSonyRaw()
    {
        byte[] bytes = MakeSonyArw(includeExif: true);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(42), 1);
        await using var original = new MemoryStream(bytes);
        AssetMetadata metadata = await new MetadataExtractorMediaMetadataParser()
            .ParseAsync(original, "shot.tif", bytes.Length, CancellationToken.None);

        Assert.Equal("OTHER", metadata.Role);
        Assert.Equal("image/tiff", metadata.DetectedMediaType);
        Assert.Equal(MetadataOutcome.Complete, metadata.Outcome);
        Assert.NotNull(metadata.CaptureTime);
        Assert.Equal("ILCE-7M3", metadata.CameraModel?.Value);
    }

    private static byte[] MakeSonyImageSubIfd(int compression, int rawType)
    {
        byte[] bytes = new byte[206];
        MakeSonyArw(includeExif: true).CopyTo(bytes, 0);
        // IFD0 identifies the camera; raw image facts live in its image SubIFD.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 0x014a);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(36), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(42), 164);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(164), 3);
        WriteEntry(166, 0x0103, compression);
        WriteEntry(178, 0x0106, 32803);
        WriteEntry(190, 0x7000, rawType);
        return bytes;

        void WriteEntry(int offset, ushort tag, int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset + 2), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), (uint)value);
        }
    }

    [Fact]
    public async Task MalformedJpegEndsWithSafeTerminalFailure()
    {
        await using var original = new MemoryStream([0xff, 0xd8, 0xff, 0xe1, 0x7f, 0xff, 1, 2, 3]);

        AssetMetadata metadata = await new MetadataExtractorMediaMetadataParser()
            .ParseAsync(original, "private.jpg", original.Length, CancellationToken.None);

        Assert.Equal(MetadataOutcome.Failed, metadata.Outcome);
        Assert.Equal("malformed_media", metadata.Reason);
    }

    [Fact]
    public async Task ExtractsExifMetadataFromSonyRawTiffContent()
    {
        var parser = new MetadataExtractorMediaMetadataParser();
        byte[] bytes = MakeSonyArw(includeExif: true);
        await using var original = new MemoryStream(bytes);

        AssetMetadata metadata = await parser.ParseAsync(original, "sony.ARW", bytes.Length, CancellationToken.None);

        Assert.Equal("RAW", metadata.Role);
        Assert.False(metadata.ExtensionMismatch);
        Assert.Equal(new EmbeddedValue("SONY", "Exif IFD0:Make"), metadata.CameraMake);
        Assert.Equal(new EmbeddedValue("ILCE-7M3", "Exif IFD0:Model"), metadata.CameraModel);
        Assert.Equal(new EmbeddedValue("12345", "Exif SubIFD:BodySerialNumber"), metadata.CameraSerial);
        Assert.Equal(new DateTime(2024, 2, 3, 4, 5, 6, 123, DateTimeKind.Unspecified), metadata.CaptureTime!.Local);
        Assert.Equal(TimeSpan.FromHours(2), metadata.CaptureTime.Offset);
    }

    [Fact]
    public async Task PreservesEmbeddedLocalTimeOffsetPrecisionAndCameraProvenance()
    {
        var parser = new MetadataExtractorMediaMetadataParser();
        byte[] bytes = MakeExifJpeg();
        await using var original = new MemoryStream(bytes);

        AssetMetadata metadata = await parser.ParseAsync(original, "capture.jpg", bytes.Length, CancellationToken.None);

        Assert.Equal(new EmbeddedValue("SONY", "Exif IFD0:Make"), metadata.CameraMake);
        Assert.Equal(new EmbeddedValue("ILCE-7M3", "Exif IFD0:Model"), metadata.CameraModel);
        Assert.Equal(new EmbeddedValue("12345", "Exif SubIFD:BodySerialNumber"), metadata.CameraSerial);
        Assert.NotNull(metadata.CaptureTime);
        Assert.Equal(new DateTime(2024, 2, 3, 4, 5, 6, 123, DateTimeKind.Unspecified), metadata.CaptureTime!.Local);
        Assert.Equal(TimeSpan.FromHours(2), metadata.CaptureTime!.Offset);
        Assert.Equal(3, metadata.CaptureTime!.FractionalDigits);
        Assert.False(metadata.CaptureTime!.IsUncertain);
        Assert.Equal(MetadataOutcome.Complete, metadata.Outcome);
    }

    [Fact]
    public async Task DoesNotTrustContradictoryEmbeddedOriginalTimes()
    {
        byte[] first = MakeExifJpeg();
        byte[] second = MakeExifJpeg(timestamp: "2024:02:03 04:06:06");
        byte[] bytes = [.. first[..^2], .. second[2..]];
        await using var original = new MemoryStream(bytes);

        AssetMetadata metadata = await new MetadataExtractorMediaMetadataParser()
            .ParseAsync(original, "capture.jpg", bytes.Length, CancellationToken.None);

        Assert.True(metadata.CaptureTime is null || metadata.CaptureTime.IsUncertain);
    }

    private static byte[] MakeExifJpeg(string offset = "+02:00", string timestamp = "2024:02:03 04:05:06")
    {
        byte[] tiff = new byte[155];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        Put16(tiff, 2, 42);
        Put32(tiff, 4, 8);

        Put16(tiff, 8, 3);
        Entry(tiff, 10, 0x010f, 2, 5, 50);
        Entry(tiff, 22, 0x0110, 2, 9, 55);
        Entry(tiff, 34, 0x8769, 4, 1, 64);
        Put32(tiff, 46, 0);
        WriteAscii(tiff, 50, "SONY");
        WriteAscii(tiff, 55, "ILCE-7M3");

        Put16(tiff, 64, 4);
        Entry(tiff, 66, 0x9003, 2, 20, 118);
        Entry(tiff, 78, 0x9011, 2, 7, 142);
        Entry(tiff, 90, 0x9291, 2, 4, 0x00333231);
        Entry(tiff, 102, 0xa431, 2, 6, 149);
        Put32(tiff, 114, 0);
        WriteAscii(tiff, 142, offset);
        WriteAscii(tiff, 118, timestamp);
        WriteAscii(tiff, 149, "12345");

        byte[] exif = [.. "Exif\0\0"u8.ToArray(), .. tiff];
        int segmentLength = exif.Length + 2;
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe1, (byte)(segmentLength >> 8), (byte)segmentLength, .. exif, 0xff, 0xd9];
        return jpeg;

        static void Entry(byte[] buffer, int offset, int tag, int type, int count, int value)
        {
            Put16(buffer, offset, tag);
            Put16(buffer, offset + 2, type);
            Put32(buffer, offset + 4, count);
            Put32(buffer, offset + 8, value);
        }

        static void Put16(byte[] buffer, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)value);
        static void Put32(byte[] buffer, int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)value);
        static void WriteAscii(byte[] buffer, int offset, string value) => System.Text.Encoding.ASCII.GetBytes(value + "\0").CopyTo(buffer, offset);
    }

    private static byte[] MakeSonyArw(bool includeExif)
    {
        if (!includeExif)
        {
            byte[] tiff = new byte[43];
            tiff[0] = (byte)'I';
            tiff[1] = (byte)'I';
            Put16(tiff, 2, 42);
            Put32(tiff, 4, 8);
            Put16(tiff, 8, 2);
            Entry(tiff, 10, 0x0103, 3, 1, 32767);
            Entry(tiff, 22, 0x010f, 2, 5, 38);
            Put32(tiff, 34, 0);
            WriteAscii(tiff, 38, "SONY");
            return tiff;
        }

        byte[] raw = new byte[163];
        raw[0] = (byte)'I';
        raw[1] = (byte)'I';
        Put16(raw, 2, 42);
        Put32(raw, 4, 8);
        Put16(raw, 8, 4);
        Entry(raw, 10, 0x010f, 2, 5, 62);
        Entry(raw, 22, 0x0110, 2, 9, 67);
        Entry(raw, 34, 0x0103, 3, 1, 32767);
        Entry(raw, 46, 0x8769, 4, 1, 76);
        Put32(raw, 58, 0);
        WriteAscii(raw, 62, "SONY");
        WriteAscii(raw, 67, "ILCE-7M3");

        Put16(raw, 76, 4);
        Entry(raw, 78, 0x9003, 2, 20, 130);
        Entry(raw, 90, 0x9011, 2, 7, 150);
        Entry(raw, 102, 0x9291, 2, 4, 0x00333231);
        Entry(raw, 114, 0xa431, 2, 6, 157);
        Put32(raw, 126, 0);
        WriteAscii(raw, 130, "2024:02:03 04:05:06");
        WriteAscii(raw, 150, "+02:00");
        WriteAscii(raw, 157, "12345");
        return raw;

        static void Entry(byte[] buffer, int offset, int tag, int type, int count, int value)
        {
            Put16(buffer, offset, tag);
            Put16(buffer, offset + 2, type);
            Put32(buffer, offset + 4, count);
            Put32(buffer, offset + 8, value);
        }

        static void Put16(byte[] buffer, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)value);
        static void Put32(byte[] buffer, int offset, int value) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)value);
        static void WriteAscii(byte[] buffer, int offset, string value) => System.Text.Encoding.ASCII.GetBytes(value + "\0").CopyTo(buffer, offset);
    }

    private sealed class SparseOriginal(byte[] header, long length) : Stream
    {
        private long position;
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => position = value; }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - position);
            if (count <= 0) return 0;
            buffer[..count].Clear();
            if (position < header.Length)
            {
                int copied = (int)Math.Min(count, header.Length - position);
                header.AsSpan((int)position, copied).CopyTo(buffer);
            }
            position += count;
            BytesRead += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => Position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private sealed class CancelDuringReadStream(CancellationTokenSource cancellation) : Stream
    {
        private bool _firstRead = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                buffer.Span[0] = 0xff;
                return 1;
            }
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
