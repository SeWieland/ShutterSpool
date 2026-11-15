using ShutterSpool.Application.Sources;
using ShutterSpool.Server.Hosting;

namespace ShutterSpool.Server.Api.Sources;

public sealed record SourceAuthenticationObservationResponse(Guid SourceId, string Outcome, DateTimeOffset ObservedAt, long Version);
public sealed record SourceAuthenticationObservationsResponse(bool CoverageComplete,
    SourceAuthenticationObservationResponse[] Items);

internal static class SourceAuthenticationEndpoints
{
    public static void MapSourceAuthenticationEndpoints(this RouteGroupBuilder management) =>
        management.MapGet("/sources/authentication-observations", ReadAsync)
            .WithSummary("Read bounded Source authentication observations")
            .Produces<SourceAuthenticationObservationsResponse>().ProducesValidationProblem();

    private static async Task<IResult> ReadAsync(Guid[] sourceIds, ISourceAuthenticationObservationStore observations,
        SourceAuthenticationObservationWorker recorder, ShutterSpool.Application.Workspaces.WorkspaceContext workspace, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. sourceIds.Distinct()];
        if (ids.Length is < 1 or > 100)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["sourceIds"] = ["Provide 1 to 100 distinct Source identities."] });

        IReadOnlyList<SourceAuthenticationSnapshot> snapshots = await observations.ReadAsync(ids, cancellationToken);
        return Results.Ok(new SourceAuthenticationObservationsResponse(recorder.IsCoverageComplete(workspace.Id ?? Guid.Empty),
            [.. snapshots.Select(item => new SourceAuthenticationObservationResponse(item.SourceId,
                item.Outcome.ToString(), item.ObservedAt, item.Version))]));
    }
}
