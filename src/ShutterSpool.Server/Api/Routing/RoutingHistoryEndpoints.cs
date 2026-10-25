using Microsoft.AspNetCore.WebUtilities;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Delivery;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ShutterSpool.Server.Api.Routing;

public static class RoutingHistoryEndpoints
{
    public static RouteGroupBuilder MapRoutingHistoryEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/captures/{id:guid}/routing", DecisionsAsync).WithSummary("Page Asset routing decisions")
            .Produces<RoutingDecisionPageResponse>().ProducesValidationProblem().ProducesProblem(404);
        management.MapGet("/captures/{id:guid}/deliveries", DeliveriesAsync).WithSummary("Page recorded Asset deliveries")
            .Produces<RoutingDeliveryPageResponse>().ProducesValidationProblem().ProducesProblem(404);
        management.MapGet("/captures/{id:guid}/actions", ActionsAsync).WithSummary("Page explicit Asset actions")
            .Produces<RoutingActionPageResponse>().ProducesValidationProblem().ProducesProblem(404);
        management.MapGet("/deliveries/{id:guid}/attempts", AttemptsAsync).WithSummary("Page delivery attempts")
            .Produces<RoutingAttemptPageResponse>().ProducesValidationProblem().ProducesProblem(404);
        management.MapGet("/destinations/{id:guid}/work", DestinationWorkAsync).WithSummary("Page recorded work for a destination")
            .Produces<RoutingDeliveryPageResponse>().ProducesValidationProblem().ProducesProblem(404);
        return management;
    }

    private static async Task<IResult> DecisionsAsync(Guid id, int? limit, string? cursor, Guid? assetId, Guid? destinationId,
        CaptureRoutingHistoryService history, CancellationToken token)
    {
        string key = Key("decisions", id, assetId, destinationId);
        if (!TryQuery(limit, cursor, key, assetId, destinationId, out var query)) return Invalid();
        var page = await history.GetDecisionsAsync(id, query, token);
        return page is null ? Missing() : Results.Ok(new RoutingDecisionPageResponse([.. page.Items.Select(RoutingDecisionResponse.From)], Encode(page.NextAt, page.NextId, key)));
    }

    private static async Task<IResult> DeliveriesAsync(Guid id, int? limit, string? cursor, Guid? assetId, Guid? destinationId,
        CaptureRoutingHistoryService history, CancellationToken token)
    {
        string key = Key("deliveries", id, assetId, destinationId);
        if (!TryQuery(limit, cursor, key, assetId, destinationId, out var query)) return Invalid();
        var page = await history.GetDeliveriesAsync(id, query, token);
        return page is null ? Missing() : Results.Ok(new RoutingDeliveryPageResponse([.. page.Items.Select(RoutingDeliveryResponse.From)], Encode(page.NextAt, page.NextId, key)));
    }

    private static async Task<IResult> ActionsAsync(Guid id, int? limit, string? cursor, Guid? assetId, Guid? destinationId,
        CaptureRoutingHistoryService history, CancellationToken token)
    {
        string key = Key("actions", id, assetId, destinationId);
        if (!TryQuery(limit, cursor, key, assetId, destinationId, out var query)) return Invalid();
        var page = await history.GetActionsAsync(id, query, token);
        return page is null ? Missing() : Results.Ok(new RoutingActionPageResponse([.. page.Items.Select(RoutingActionHistoryResponse.From)], Encode(page.NextAt, page.NextId, key)));
    }

    private static async Task<IResult> AttemptsAsync(Guid id, int? limit, string? cursor, CaptureRoutingHistoryService history, CancellationToken token)
    {
        string key = Key("attempts", id, null, null);
        if (!TryQuery(limit, cursor, key, null, null, out var query)) return Invalid();
        var page = await history.GetAttemptsAsync(id, query, token);
        return page is null ? Missing() : Results.Ok(new RoutingAttemptPageResponse([.. page.Items.Select(RoutingAttemptResponse.From)], Encode(page.NextAt, page.NextId, key)));
    }

    private static async Task<IResult> DestinationWorkAsync(Guid id, int? limit, string? cursor, string? state,
        CaptureRoutingHistoryService history, CancellationToken token)
    {
        DeliveryState? selectedState = null;
        if (state is not null)
        {
            if (!Enum.TryParse(state, ignoreCase: false, out DeliveryState parsed) || !Enum.IsDefined(parsed)
                || !string.Equals(state, parsed.ToString(), StringComparison.Ordinal))
                return Invalid("state", "Provide a valid delivery state.");
            selectedState = parsed;
        }
        string key = $"{Key("destination-work", id, null, id)}|created-desc,id-desc|{selectedState}";
        if (!TryQuery(limit, cursor, key, null, id, out RoutingHistoryQuery query, selectedState)) return Invalid();
        var page = await history.GetDestinationWorkAsync(id, query, token);
        return page is null ? Results.Problem(statusCode: 404, title: "Destination not found")
            : Results.Ok(new RoutingDeliveryPageResponse([.. page.Items.Select(RoutingDeliveryResponse.From)],
                Encode(page.NextAt, page.NextId, key)));
    }

    private static string Key(string kind, Guid id, Guid? asset, Guid? destination) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}|{id:N}|{asset:N}|{destination:N}|time-id-desc")));

    private static string? Encode(DateTimeOffset? at, Guid? id, string key) => at is DateTimeOffset time && id is Guid row
        ? WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{time.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{row:N}:{key}")) : null;

    private static bool TryQuery(int? limit, string? cursor, string key, Guid? asset, Guid? destination,
        out RoutingHistoryQuery query, DeliveryState? state = null)
    {
        query = new(limit ?? 30, AssetId: asset, DestinationId: destination, State: state);
        if (query.Limit is < 1 or > 100 || cursor is { Length: > 512 }) return false;
        if (cursor is null) return true;
        try
        {
            string[] parts = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)).Split(':');
            if (parts.Length != 3 || parts[2] != key || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks || !Guid.TryParseExact(parts[1], "N", out Guid id)) return false;
            query = query with { Before = new DateTimeOffset(ticks, TimeSpan.Zero), BeforeId = id };
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static IResult Invalid() => Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Provide a limit from 1 to 100 and a cursor for these history filters."] });
    private static IResult Invalid(string field, string error) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });
    private static IResult Missing() => Results.Problem(statusCode: 404, title: "History owner not found");
}
