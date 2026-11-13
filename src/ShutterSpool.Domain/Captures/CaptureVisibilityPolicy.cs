using System.Linq.Expressions;

namespace ShutterSpool.Domain.Captures;

public sealed class CaptureVisibilityFacts
{
    public bool IsViewer { get; init; }
    public bool ReleasedOnly { get; init; }
    public bool IsActive { get; init; }
    public bool IsReleased { get; init; }
    public int Rating { get; init; }
    public Guid CaptureId { get; init; }
}

public static class CaptureVisibilityPolicy
{
    public static Expression<Func<CaptureVisibilityFacts, bool>> IsVisible { get; } = facts =>
        !facts.IsViewer || facts.IsActive && (!facts.ReleasedOnly || facts.IsReleased && facts.Rating != -1);
}
