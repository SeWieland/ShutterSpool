using ShutterSpool.Server.Hosting;

namespace ShutterSpool.Server.Api.Sources;

public sealed record SourceDisplayResponse(Guid Id, string Name, string CurationMode,
    int CurationDelaySeconds, long ConfigurationRevision, bool Archived, string SourceType, string ConnectionProfile);

public static class SourceDisplayEndpoints
{
    public static RouteGroupBuilder MapSourceDisplayEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/sources/{id:guid}/display", ReadAsync)
            .WithSummary("Read a bounded non-secret Source display projection")
            .Produces<SourceDisplayResponse>().ProducesProblem(404);
        return management;
    }

    private static async Task<IResult> ReadAsync(Guid id, SourceDisplayCache cache, CancellationToken cancellationToken)
    {
        var source = await cache.ReadAsync(id, cancellationToken);
        return source is null ? Results.NotFound() : Results.Ok(new SourceDisplayResponse(source.Id, source.Name,
            source.CurationMode.ToString(), source.CurationDelaySeconds, source.ConfigurationRevision, source.Archived,
            source.SourceType.ToString(), source.ConnectionProfile.ToString()));
    }
}
