using ShutterSpool.Server.Hosting;
using System.Diagnostics.Metrics;

namespace ShutterSpool.IntegrationTests;

public sealed class RetentionTelemetryTests
{
    [Fact]
    public void Capacity_metrics_preserve_unknown_state_and_never_use_private_reasons_as_labels()
    {
        var measurements = new List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == RetentionTelemetry.MeterName) current.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();
        using var telemetry = new RetentionTelemetry();
        telemetry.RecordCapacity(new(new(1, true, 1000, 800, 500), DateTimeOffset.UtcNow, false,
            900, 200, 600, 200, 50, 30, 20, null, null, 0, 600, "/private/source-name", "/private/path"));
        listener.RecordObservableInstruments();

        Assert.Contains(measurements, item => item.Name == "shutterspool.retention.accounting.known" && item.Value == 0);
        Assert.Contains(measurements, item => item.Name == "shutterspool.retention.used.bytes" && item.Value == 900);
        Assert.Contains(measurements, item => item.Name == "shutterspool.retention.reserved.bytes" && item.Value == 200);
        Assert.Contains(measurements, item => item.Name == "shutterspool.retention.admission.blocked" && item.Value == 1);
        Assert.All(measurements.SelectMany(item => item.Tags), tag => Assert.DoesNotContain("/private", tag.Value?.ToString() ?? ""));
    }
}
