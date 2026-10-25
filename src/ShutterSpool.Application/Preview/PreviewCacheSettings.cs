namespace ShutterSpool.Application.Preview;

public sealed record PreviewCacheSettings(PreviewCacheMode CacheMode, long MemoryCacheBytes)
{
    public const long MaximumMemoryCacheBytes = 1024L * 1024 * 1024;

    public void Validate()
    {
        if (!Enum.IsDefined(CacheMode)) throw new ArgumentOutOfRangeException(nameof(CacheMode), "Invalid preview cache mode.");
        if (MemoryCacheBytes is <= 0 or > MaximumMemoryCacheBytes)
            throw new ArgumentOutOfRangeException(nameof(MemoryCacheBytes), "Preview memory cache budget must be between 1 byte and 1 GiB.");
    }

    public void ApplyTo(PreviewSettings settings)
    {
        Validate();
        settings.CacheMode = CacheMode;
        settings.MemoryCacheBytes = MemoryCacheBytes;
        settings.Validate();
    }
}
