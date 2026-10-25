namespace ShutterSpool.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MediaTempFileCollection
{
    public const string Name = "media temporary file lifecycle";
}
