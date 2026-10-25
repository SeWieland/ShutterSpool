namespace ShutterSpool.Domain.Sources;

public static class SourceCurationPolicy
{
    public static (SourceCurationMode Mode, int DelaySeconds)? Parse(string? value, int? requestedDelay)
    {
        if (!Enum.TryParse(value, out SourceCurationMode mode) || !Enum.IsDefined(mode)
            || !string.Equals(value, mode.ToString(), StringComparison.Ordinal)) return null;
        if (mode is SourceCurationMode.Immediate or SourceCurationMode.Manual)
            return requestedDelay is null ? (mode, 3_600) : null;
        int delay = requestedDelay ?? 3_600;
        return delay is > 0 and <= 31_536_000 ? (mode, delay) : null;
    }
}
