using ShutterSpool.Application.Retention;
using System.Diagnostics.Metrics;

namespace ShutterSpool.Server.Hosting;

public enum RetentionOperation { Selection, Trash, Purge, Failure, Retry }

public sealed class RetentionTelemetry : IDisposable
{
    public const string MeterName = "ShutterSpool.Retention";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _operationBytes;
    private readonly Counter<long> _operationCount;
    private readonly Histogram<double> _passDuration;
    private SpoolQuotaSnapshot? _capacity;
    private long _blockedSince;

    public RetentionTelemetry()
    {
        _meter.CreateObservableGauge("shutterspool.retention.used.bytes", () => Volatile.Read(ref _capacity)?.UsedBytes ?? 0, "By");
        _meter.CreateObservableGauge("shutterspool.retention.reserved.bytes", () => Volatile.Read(ref _capacity)?.ReservedBytes ?? 0, "By");
        _meter.CreateObservableGauge("shutterspool.retention.accounting.known", () => Volatile.Read(ref _capacity)?.IsKnown == true ? 1L : 0L);
        _meter.CreateObservableGauge("shutterspool.retention.admission.blocked", () =>
        {
            SpoolQuotaSnapshot? capacity = Volatile.Read(ref _capacity);
            return capacity is null || Blocked(capacity) ? 1L : 0L;
        });
        _meter.CreateObservableGauge("shutterspool.retention.admission.blocked.duration", () =>
        {
            long since = Interlocked.Read(ref _blockedSince);
            return since == 0 ? 0 : Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - since) / 1000d);
        }, "s");
        _meter.CreateObservableGauge("shutterspool.retention.observation.age", () =>
        {
            SpoolQuotaSnapshot? capacity = Volatile.Read(ref _capacity);
            return capacity is null ? -1 : Math.Max(0, (DateTimeOffset.UtcNow - capacity.ObservedAt).TotalSeconds);
        }, "s");
        _meter.CreateObservableGauge("shutterspool.retention.occupancy.bytes", Occupancy, "By");
        _operationBytes = _meter.CreateCounter<long>("shutterspool.retention.operation.bytes", "By");
        _operationCount = _meter.CreateCounter<long>("shutterspool.retention.operation.count");
        _passDuration = _meter.CreateHistogram<double>("shutterspool.retention.pass.duration", "s");
    }

    public void RecordCapacity(SpoolQuotaSnapshot capacity)
    {
        if (Blocked(capacity)) Interlocked.CompareExchange(ref _blockedSince, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0);
        else Interlocked.Exchange(ref _blockedSince, 0);
        Volatile.Write(ref _capacity, capacity);
    }

    private static bool Blocked(SpoolQuotaSnapshot capacity) =>
        capacity.AdmissionBlockedReason is not null || capacity.Settings.IsActive && !capacity.IsKnown;

    public void RecordOperation(RetentionOperation operation, long bytes, long count)
    {
        if (!Enum.IsDefined(operation) || bytes < 0 || count < 0) throw new ArgumentOutOfRangeException(nameof(operation));
        KeyValuePair<string, object?> dimension = new("operation", operation.ToString());
        _operationBytes.Add(bytes, dimension);
        _operationCount.Add(count, dimension);
    }

    public void RecordPass(TimeSpan duration, bool failed) => _passDuration.Record(duration.TotalSeconds,
        new KeyValuePair<string, object?>("outcome", failed ? "failed" : "completed"));

    private IEnumerable<Measurement<long>> Occupancy()
    {
        SpoolQuotaSnapshot? capacity = Volatile.Read(ref _capacity);
        if (capacity is null) yield break;
        yield return new(capacity.ActiveBytes, new KeyValuePair<string, object?>("category", "active"));
        yield return new(capacity.TrashBytes, new KeyValuePair<string, object?>("category", "trash"));
        yield return new(capacity.PartialBytes, new KeyValuePair<string, object?>("category", "partial"));
        yield return new(capacity.QuarantineBytes, new KeyValuePair<string, object?>("category", "quarantine"));
        yield return new(capacity.DiskPreviewBytes, new KeyValuePair<string, object?>("category", "preview"));
    }

    public void Dispose() => _meter.Dispose();
}
