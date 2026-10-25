using ShutterSpool.Application.Delivery;
using ShutterSpool.Domain.Delivery;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ShutterSpool.Infrastructure.Destinations.Local;

// Filesystem publication and recovery only; PostgreSQL claims and outcomes belong to the worker processor.
internal static class LocalFileDelivery
{
    private const int BufferSize = 64 * 1024;

    public static async Task<DeliveryResult> WriteAsync(string root, DeliveryRequest request, Stream original,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(root, request.TargetPath, out string? target, out string? error))
            return new(error == "MOUNT_IDENTITY_UNKNOWN" ? DeliveryResultKind.Uncertain : DeliveryResultKind.ConfigurationFailure,
                ErrorCode: error);
        if (!HasRootMarker(root, request.DestinationId))
            return new(DeliveryResultKind.Uncertain, ErrorCode: "MOUNT_IDENTITY_UNKNOWN");
        string partial = PartialPath(target!, request.AttemptId);
        bool publicationAttempted = false;
        bool partialOwned = false;
        try
        {
            if (TryGetAttributes(target!, out FileAttributes attributes))
                return (attributes & FileAttributes.Directory) != 0
                    ? HasRootMarker(root, request.DestinationId)
                        ? new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "TARGET_CONFLICT")
                        : new(DeliveryResultKind.Uncertain, ErrorCode: "MOUNT_IDENTITY_UNKNOWN")
                    : await ReadBackAsync(root, target!, request, cancellationToken);
            string parent = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(parent);
            EnsureSafePath(root, target!);
            ProbeNoReplace(parent);

            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                partialOwned = true;
                if (!await StageVerifiedAsync(original, output, request, cancellationToken))
                    return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "ORIGINAL_CHANGED");
            }

            EnsureSafePath(root, target!);
            if (!HasRootMarker(root, request.DestinationId))
                return new(DeliveryResultKind.Uncertain, ErrorCode: "MOUNT_IDENTITY_UNKNOWN");
            publicationAttempted = true;
            try { CreateHardLink(target!, partial); }
            catch (IOException) when (File.Exists(target)) { return await ReadBackAsync(root, target!, request, cancellationToken); }

            DeliveryResult result = await ReadBackAsync(root, target!, request, cancellationToken);
            if (result.ErrorCode == "TARGET_UNVERIFIABLE")
                return new(DeliveryResultKind.Uncertain, ErrorCode: "READ_BACK_UNKNOWN");
            return result.Kind == DeliveryResultKind.Confirmed ? result with { BytesSent = request.Size } : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(DeliveryResultKind.Uncertain, ErrorCode: "WRITE_INTERRUPTED");
        }
        catch (UnauthorizedAccessException)
        {
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "PERMISSION_DENIED");
        }
        catch (InvalidDataException)
        {
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "UNSAFE_PATH");
        }
        catch (IOException)
        {
            bool uncertain = publicationAttempted || !partialOwned && File.Exists(partial);
            return new(uncertain ? DeliveryResultKind.Uncertain : DeliveryResultKind.TransientFailure,
                ErrorCode: uncertain ? "PUBLICATION_UNKNOWN" : "LOCAL_IO_FAILED");
        }
        catch (PlatformNotSupportedException)
        {
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "UNSUPPORTED_PUBLICATION");
        }
        finally
        {
            if (partialOwned) TryDeletePartial(partial);
        }
    }

    public static async Task<DeliveryReconciliation> ReconcileAsync(string root, DeliveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryResolve(root, request.TargetPath, out string? target, out string? error))
            return new(error is "INVALID_TARGET" or "INVALID_ROOT" or "UNSAFE_PATH" or "PERMISSION_DENIED"
                ? DeliveryReconciliationKind.Conflict : DeliveryReconciliationKind.Uncertain, ErrorCode: error);
        if (!HasRootMarker(root, request.DestinationId))
            return new(DeliveryReconciliationKind.Uncertain, ErrorCode: "MOUNT_IDENTITY_UNKNOWN");
        try
        {
            if (TryGetAttributes(target!, out FileAttributes attributes))
            {
                if ((attributes & FileAttributes.Directory) != 0)
                    return HasRootMarker(root, request.DestinationId)
                        ? new(DeliveryReconciliationKind.Conflict, ErrorCode: "TARGET_CONFLICT")
                        : new(DeliveryReconciliationKind.Uncertain, ErrorCode: "MOUNT_IDENTITY_UNKNOWN");
                DeliveryResult result = await ExistingAsync(target!, request, cancellationToken);
                if (!HasRootMarker(root, request.DestinationId))
                    return new(DeliveryReconciliationKind.Uncertain, ErrorCode: "MOUNT_IDENTITY_UNKNOWN");
                return result.Kind == DeliveryResultKind.Confirmed
                    ? new(DeliveryReconciliationKind.Confirmed, DeliveryConfirmationEvidenceLevel.ReadBackHash)
                    : result.ErrorCode == "TARGET_UNVERIFIABLE"
                        ? new(DeliveryReconciliationKind.Uncertain, ErrorCode: result.ErrorCode)
                        : new(DeliveryReconciliationKind.Conflict, ErrorCode: result.ErrorCode);
            }
            if (!Directory.Exists(root)) return new(DeliveryReconciliationKind.Uncertain, ErrorCode: "ROOT_UNAVAILABLE");
            string partial = PartialPath(target!, request.AttemptId);
            EnsureSafePath(root, partial);
            if (File.Exists(partial))
            {
                using (new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                File.Delete(partial);
            }
            if (!HasRootMarker(root, request.DestinationId))
                return new(DeliveryReconciliationKind.Uncertain, ErrorCode: "MOUNT_IDENTITY_UNKNOWN");
            return new(DeliveryReconciliationKind.NotPublished, SafeToRepeat: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (InvalidDataException)
        {
            return new(DeliveryReconciliationKind.Conflict, ErrorCode: "UNSAFE_PATH");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(DeliveryReconciliationKind.Uncertain, ErrorCode: "RECONCILIATION_UNAVAILABLE");
        }
    }

    public static (bool Writable, string? ErrorCode) TestRoot(string root, Guid destinationId)
    {
        if (!Path.IsPathFullyQualified(root)) return (false, "INVALID_ROOT");
        try
        {
            EnsureSafePath(root, Path.Combine(root, "shutterspool-probe"));
            if (!HasRootMarker(root, destinationId)) return (false, "MOUNT_IDENTITY_UNKNOWN");
            ProbeNoReplace(root);
            return (true, null);
        }
        catch (UnauthorizedAccessException) { return (false, "PERMISSION_DENIED"); }
        catch (InvalidDataException) { return (false, "UNSAFE_PATH"); }
        catch (PlatformNotSupportedException) { return (false, "UNSUPPORTED_PUBLICATION"); }
        catch (IOException) { return (false, "ROOT_UNAVAILABLE"); }
    }

    public static void ProvisionRoot(string root, Guid destinationId)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) throw new IOException("Root is unavailable.");
        EnsureSafePath(root, Path.Combine(root, "shutterspool-probe"));
        ProbeNoReplace(root);
        string marker = RootMarker(root, destinationId);
        if (File.Exists(marker))
        {
            if (!HasRootMarker(root, destinationId)) throw new IOException("Root marker is invalid.");
            return;
        }
        using var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4_096, FileOptions.WriteThrough);
        output.Write(destinationId.ToByteArray());
        output.Flush(flushToDisk: true);
    }

    private static bool HasRootMarker(string root, Guid destinationId)
    {
        string marker = RootMarker(root, destinationId);
        try
        {
            EnsureSafePath(root, marker);
            if (!File.Exists(marker)) return false;
            using var file = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != 16) return false;
            Span<byte> bytes = stackalloc byte[16];
            file.ReadExactly(bytes);
            return bytes.SequenceEqual(destinationId.ToByteArray());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string RootMarker(string root, Guid destinationId) =>
        Path.Combine(root, $".shutterspool-{destinationId:N}.root");

    private static async Task<DeliveryResult> ExistingAsync(string target, DeliveryRequest request, CancellationToken token)
    {
        try
        {
            await using var file = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != request.Size) return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "TARGET_CONFLICT");
            byte[] hash = await SHA256.HashDataAsync(file, token);
            return HashMatches(hash, request.Sha256)
                ? new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.ReadBackHash)
                : new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "TARGET_CONFLICT");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "TARGET_UNVERIFIABLE");
        }
    }

    private static async Task<bool> StageVerifiedAsync(Stream original, FileStream output, DeliveryRequest request,
        CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        long size = 0;
        int read;
        while ((read = await original.ReadAsync(buffer, token)) != 0)
        {
            if (size > request.Size - read) return false;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            hash.AppendData(buffer, 0, read);
            size += read;
        }
        await output.FlushAsync(token);
        output.Flush(flushToDisk: true);
        return size == request.Size && HashMatches(hash.GetHashAndReset(), request.Sha256);
    }

    private static async Task<DeliveryResult> ReadBackAsync(string root, string target, DeliveryRequest request,
        CancellationToken token)
    {
        DeliveryResult result = await ExistingAsync(target, request, token);
        return HasRootMarker(root, request.DestinationId) ? result
            : new(DeliveryResultKind.Uncertain, ErrorCode: "MOUNT_IDENTITY_UNKNOWN");
    }

    private static bool HashMatches(byte[] actual, string expected)
    {
        if (expected.Length != 64) return false;
        try { return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)); }
        catch (FormatException) { return false; }
    }

    private static bool TryResolve(string root, string relative, out string? target, out string? error)
    {
        target = null;
        error = "INVALID_TARGET";
        if (!Path.IsPathFullyQualified(root)) { error = "INVALID_ROOT"; return false; }
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 2048 || relative.Contains('\\') || relative.StartsWith('/')
            || relative.Split('/').Any(segment => segment.Length is 0 or > 255 || segment is "." or ".."
                || segment.Any(char.IsControl) || segment.IndexOfAny(['?', '*', '"', '<', '>', '|', ':']) >= 0
                || segment.EndsWith('.') || segment.EndsWith(' ') || IsReservedDeviceName(segment))) return false;
        target = Path.Combine(Path.GetFullPath(root), relative.Replace('/', Path.DirectorySeparatorChar));
        try { EnsureSafePath(root, target); }
        catch (InvalidDataException) { error = "UNSAFE_PATH"; return false; }
        catch (UnauthorizedAccessException) { error = "PERMISSION_DENIED"; return false; }
        catch (IOException) { error = "MOUNT_IDENTITY_UNKNOWN"; return false; }
        return true;
    }

    private static void EnsureSafePath(string root, string target)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullTarget = Path.GetFullPath(target);
        if (!fullTarget.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Target leaves the configured root.");
        for (string? path = fullTarget; path is not null; path = Path.GetDirectoryName(path))
        {
            if (TryGetAttributes(path, out FileAttributes attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Path is redirected.");
        }
    }

    private static string PartialPath(string target, Guid attemptId) =>
        Path.Combine(Path.GetDirectoryName(target)!, $".shutterspool-{attemptId:N}.partial");

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { attributes = default; return false; }
        catch (DirectoryNotFoundException) { attributes = default; return false; }
    }

    private static bool IsReservedDeviceName(string segment)
    {
        string name = segment.Split('.')[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9';
    }

    private static void ProbeNoReplace(string directory)
    {
        string source = Path.Combine(directory, $".shutterspool-probe-{Guid.NewGuid():N}");
        string existing = source + ".existing";
        bool sourceOwned = false, existingOwned = false;
        try
        {
            using (var first = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                sourceOwned = true;
                first.WriteByte(1);
            }
            using (var second = new FileStream(existing, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                existingOwned = true;
                second.WriteByte(2);
            }
            try { CreateHardLink(existing, source); throw new IOException("No-replace hard link replaced a target."); }
            catch (IOException) when (File.ReadAllBytes(existing).SequenceEqual(new byte[] { 2 })) { }
            string linked = source + ".linked";
            bool linkedOwned = false;
            try { CreateHardLink(linked, source); linkedOwned = true; }
            finally { if (linkedOwned) File.Delete(linked); }
        }
        finally
        {
            if (sourceOwned) File.Delete(source);
            if (existingOwned) File.Delete(existing);
        }
    }

    private static void TryDeletePartial(string partial)
    {
        try { if (File.Exists(partial)) File.Delete(partial); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void CreateHardLink(string link, string source)
    {
        bool created = OperatingSystem.IsWindows() ? CreateHardLinkWindows(link, source, IntPtr.Zero) : LinkUnix(source, link) == 0;
        if (created) return;
        int error = Marshal.GetLastPInvokeError();
        if (OperatingSystem.IsWindows() ? error is 1 or 17 or 50 or 87 : error is 18 or 95)
            throw new PlatformNotSupportedException("This filesystem does not support no-replace hard links.");
        if (OperatingSystem.IsWindows() ? error == 5 : error is 1 or 13 or 30)
            throw new UnauthorizedAccessException("Hard-link publication was denied.");
        throw new IOException("This filesystem did not complete a no-replace hard link.");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string link, string source, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(string source, string link);
}
