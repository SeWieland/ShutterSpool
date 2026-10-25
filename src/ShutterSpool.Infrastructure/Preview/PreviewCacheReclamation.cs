using ShutterSpool.Application.Preview;
using System.Diagnostics;
using System.Security;

namespace ShutterSpool.Infrastructure.Preview;

internal static class PreviewCacheReclamation
{
    private const int MaximumVisitedEntries = 50_000;
    private const int MaximumReclaimEntries = 10_000;
    internal const int MaximumOutputBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(2);

    public static PreviewCacheReclaimResult Reclaim(string root, long maximumBytes, int maximumEntries,
        Stopwatch timer, CancellationToken cancellationToken)
    {
        long removedBytes = 0;
        int removedEntries = 0;
        bool moreMayRemain = false;

        try
        {
            var scan = new CandidateScan(timer, cancellationToken);
            List<Candidate> candidates = CollectCandidates(root, scan);
            if (scan.FailureCode is not null)
                return new(removedBytes, removedEntries, true, scan.FailureCode);
            moreMayRemain = scan.MoreMayRemain;

            foreach (Candidate candidate in candidates.OrderBy(item => item.LastWriteUtc)
                         .ThenBy(item => item.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed >= MaximumDuration || removedEntries >= Math.Min(maximumEntries, MaximumReclaimEntries)
                    || removedBytes >= maximumBytes)
                {
                    moreMayRemain = true;
                    break;
                }

                string? failure = TryRemove(candidate, ref removedBytes, ref removedEntries, ref moreMayRemain);
                if (failure is not null) return new(removedBytes, removedEntries, true, failure);
            }

            return new(removedBytes, removedEntries, moreMayRemain);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return new(removedBytes, removedEntries, true, "cache_reclaim_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new(removedBytes, removedEntries, true, "cache_reclaim_unavailable");
        }
        catch (SecurityException)
        {
            return new(removedBytes, removedEntries, true, "cache_reclaim_unavailable");
        }
    }

    private static List<Candidate> CollectCandidates(string root, CandidateScan scan)
    {
        var candidates = new List<Candidate>();
        foreach (string captureDirectory in Directory.EnumerateDirectories(root))
        {
            scan.ThrowIfCancelled();
            if (scan.ReachedBound()) break;
            if (IsLinked(captureDirectory))
            {
                scan.FailureCode = "cache_reclaim_linked_path";
                break;
            }
            if (!Guid.TryParseExact(Path.GetFileName(captureDirectory), "N", out _)) continue;

            candidates.AddRange(CollectCaptureCandidates(captureDirectory, scan));
            if (scan.FailureCode is not null || scan.MoreMayRemain) break;
        }

        return candidates;
    }

    private static List<Candidate> CollectCaptureCandidates(string captureDirectory, CandidateScan scan)
    {
        var candidates = new List<Candidate>();
        foreach (string assetDirectory in Directory.EnumerateDirectories(captureDirectory))
        {
            scan.ThrowIfCancelled();
            if (scan.ReachedBound()) break;
            if (IsLinked(assetDirectory))
            {
                scan.FailureCode = "cache_reclaim_linked_path";
                break;
            }
            if (!Guid.TryParseExact(Path.GetFileName(assetDirectory), "N", out _)) continue;

            candidates.AddRange(CollectAssetCandidates(assetDirectory, scan));
            if (scan.FailureCode is not null || scan.MoreMayRemain) break;
        }
        return candidates;
    }

    private static List<Candidate> CollectAssetCandidates(string assetDirectory, CandidateScan scan)
    {
        var candidates = new List<Candidate>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(assetDirectory))
        {
            scan.ThrowIfCancelled();
            if (scan.ReachedBound()) break;
            FileAttributes attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                scan.FailureCode = "cache_reclaim_linked_path";
                break;
            }
            if (attributes.HasFlag(FileAttributes.Directory)) continue;

            string filename = Path.GetFileName(entry);
            if (!IsDerivativeFilename(filename)) continue;
            var info = new FileInfo(entry);
            long size = info.Length;
            if (size > MaximumOutputBytes)
            {
                scan.FailureCode = "cache_reclaim_oversized_derivative";
                break;
            }

            DateTime lastWriteUtc = info.LastWriteTimeUtc;
            info.Refresh();
            if (!info.Exists || info.Length != size || info.LastWriteTimeUtc != lastWriteUtc)
            {
                scan.MoreMayRemain = true;
                continue;
            }
            candidates.Add(new(entry, size, lastWriteUtc));
        }
        return candidates;
    }

    private static bool IsLinked(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static string? TryRemove(Candidate candidate, ref long removedBytes, ref int removedEntries,
        ref bool moreMayRemain)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(candidate.Path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            moreMayRemain = true;
            return null;
        }
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory))
            return "cache_reclaim_linked_path";

        var current = new FileInfo(candidate.Path);
        if (!current.Exists || current.Length != candidate.Size || current.LastWriteTimeUtc != candidate.LastWriteUtc)
        {
            moreMayRemain = true;
            return null;
        }

        File.Delete(candidate.Path);
        try
        {
            _ = File.GetAttributes(candidate.Path);
            return "cache_reclaim_delete_incomplete";
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            removedBytes = checked(removedBytes + candidate.Size);
            removedEntries++;
            return null;
        }
    }

    public static bool IsDerivativeFilename(string filename)
    {
        if (!filename.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return false;
        string stem = filename[..^5];
        int separator = stem.LastIndexOf('-');
        if (separator <= 0) return false;
        string profile = stem[..separator];
        string[] dimensions = stem[(separator + 1)..].Split('x');
        return profile is "grid" or "detail" && dimensions.Length == 2
            && int.TryParse(dimensions[0], out int width) && width is >= 64 and <= 4096
            && int.TryParse(dimensions[1], out int height) && height is >= 64 and <= 4096;
    }

    private sealed record Candidate(string Path, long Size, DateTime LastWriteUtc);

    private sealed class CandidateScan(Stopwatch timer, CancellationToken cancellationToken)
    {
        private int _visited;

        public bool MoreMayRemain { get; set; }
        public string? FailureCode { get; set; }

        public void ThrowIfCancelled() => cancellationToken.ThrowIfCancellationRequested();

        public bool ReachedBound()
        {
            if (timer.Elapsed < MaximumDuration && ++_visited <= MaximumVisitedEntries) return false;
            MoreMayRemain = true;
            return true;
        }
    }
}
