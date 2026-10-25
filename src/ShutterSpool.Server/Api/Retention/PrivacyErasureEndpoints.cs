using ShutterSpool.Application.Retention;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Api.Retention;

public sealed record PrivacyErasureSelectionRequest(Guid CaptureId, int ExpectedRevision, Guid[] AssetIds)
{
    internal PrivacyErasureSelection ToApplication() => new(CaptureId, ExpectedRevision, AssetIds);
}
public sealed record PrivacyErasurePreviewRequest(PrivacyErasureSelectionRequest[] Selections);
public sealed record PrivacyErasureRequestDto(Guid OperationId, bool Confirmed,
    PrivacyErasureSelectionRequest[] Selections)
{
    internal PrivacyErasureRequest ToApplication() => new(OperationId,
        Selections.Select(item => item.ToApplication()).ToArray(), Confirmed);
}
public sealed record ContinuePrivacyErasureRequest(bool Confirmed);
public sealed record PrivacyErasurePreviewResponse(string Status, string? ErrorCode, int CaptureCount,
    int AssetCount, int GalleryPromiseCount, int PreservationHoldCount, int ActiveDeliveryCount,
    int AmbiguousDeliveryCount)
{
    internal static PrivacyErasurePreviewResponse From(PrivacyErasurePreview value) => new(value.Status.ToString(),
        value.ErrorCode, value.CaptureCount, value.AssetCount, value.GalleryPromiseCount,
        value.PreservationHoldCount, value.ActiveDeliveryCount, value.AmbiguousDeliveryCount);
}
public sealed record PrivacyErasureAssetResponse(Guid CaptureId, Guid AssetId, string MetadataRedaction, string Derivatives,
    string OriginalsAndTransients, string? ErrorCode)
{
    internal static PrivacyErasureAssetResponse From(PrivacyErasureAssetSurface value) => new(value.CaptureId, value.AssetId,
        value.MetadataRedaction.ToString(), value.Derivatives.ToString(), value.OriginalsAndTransients.ToString(),
        value.ErrorCode);
}
public sealed record PrivacyErasureResponse(Guid OperationId, string Status, string? ErrorCode,
    PrivacyErasureAssetResponse[] Assets)
{
    internal static PrivacyErasureResponse From(PrivacyErasureResult value) => new(value.OperationId,
        value.Status.ToString(), value.ErrorCode, [.. value.Assets.Select(PrivacyErasureAssetResponse.From)]);
}
public sealed record PrivacyErasureOperationSummaryResponse(Guid OperationId, string Status,
    DateTimeOffset RequestedAt, int AssetCount, int CompletedCount, int PendingCount, string? ErrorCode)
{
    internal static PrivacyErasureOperationSummaryResponse From(PrivacyErasureOperationSummary value) => new(
        value.OperationId, value.Status.ToString(), value.RequestedAt, value.AssetCount, value.CompletedCount,
        value.PendingCount, value.ErrorCode);
}
public sealed record PrivacyErasureOperationListResponse(PrivacyErasureOperationSummaryResponse[] Operations,
    string? NextCursor);
public sealed record SuppressionReconciliationRequest(Guid ExpectedSnapshotId, string ProtectedManifest);
public sealed record SuppressionPageReconciliationRequest(string ProtectedPage);
public sealed record SuppressionManifestResponse(Guid SnapshotId, int RecordCount, int PageSize,
    int PageCount, string Payload)
{
    internal static SuppressionManifestResponse From(PrivacyErasureProtectedManifest value) => new(
        value.SnapshotId, value.RecordCount, value.PageSize, value.PageCount, value.Payload);
}
public sealed record SuppressionPageResponse(Guid SnapshotId, int PageIndex, string Payload)
{
    internal static SuppressionPageResponse From(PrivacyErasureProtectedPage value) => new(
        value.SnapshotId, value.PageIndex, value.Payload);
}
public sealed record SuppressionImportResponse(Guid SnapshotId, int ExpectedPageCount, int ReceivedPageCount,
    bool Ready, string? ErrorCode)
{
    internal static SuppressionImportResponse From(PrivacyErasureImportStatus value) => new(value.SnapshotId,
        value.ExpectedPageCount, value.ReceivedPageCount, value.Ready, value.ErrorCode);
}

public static class PrivacyErasureEndpoints
{
    public static RouteGroupBuilder MapPrivacyErasureEndpoints(this RouteGroupBuilder management)
    {
        var routes = management.MapGroup("/retention/erasures");
        routes.MapPost("/preview", PreviewAsync).WithSummary("Preview a finite privacy-erasure selection")
            .Produces<PrivacyErasurePreviewResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        routes.MapPost("", BeginAsync).WithSummary("Confirm and begin privacy erasure")
            .Produces<PrivacyErasureResponse>(StatusCodes.Status202Accepted)
            .Produces<PrivacyErasureResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        routes.MapGet("", ListAsync).WithSummary("List recent privacy-erasure operations")
            .Produces<PrivacyErasureOperationListResponse>();
        routes.MapGet("/{operationId:guid}", GetAsync).WithSummary("Read privacy-erasure surface progress")
            .Produces<PrivacyErasureResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        routes.MapPost("/{operationId:guid}/continue", ContinueAsync)
            .WithSummary("Continue a pending privacy erasure after its blocker is resolved")
            .Produces<PrivacyErasureResponse>(StatusCodes.Status202Accepted)
            .Produces<PrivacyErasureResponse>().ProducesValidationProblem();

        routes.MapPost("/suppressions/exports", CreateExportAsync)
            .WithSummary("Create a bounded protected snapshot of current erasure suppression evidence")
            .Produces<SuppressionManifestResponse>().ProducesProblem(StatusCodes.Status413PayloadTooLarge);
        routes.MapGet("/suppressions/exports/{snapshotId:guid}/manifest", GetExportManifestAsync)
            .Produces<SuppressionManifestResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        routes.MapGet("/suppressions/exports/{snapshotId:guid}/pages/{pageIndex:int}", GetExportPageAsync)
            .Produces<SuppressionPageResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        routes.MapPost("/suppressions/reconciliation", BeginReconciliationAsync)
            .Produces<SuppressionImportResponse>().ProducesValidationProblem();
        routes.MapPost("/suppressions/reconciliation/pages", ReconcilePageAsync)
            .Produces<SuppressionImportResponse>().ProducesValidationProblem();
        routes.MapPost("/suppressions/reconciliation/{snapshotId:guid}/complete", CompleteReconciliationAsync)
            .Produces<SuppressionImportResponse>().ProducesProblem(StatusCodes.Status409Conflict);
        return management;
    }

    private static async Task<IResult> PreviewAsync(PrivacyErasurePreviewRequest request,
        PrivacyErasureService service, CancellationToken cancellationToken)
    {
        try
        {
            PrivacyErasurePreview preview = await service.PreviewAsync(
                request.Selections.Select(item => item.ToApplication()).ToArray(), cancellationToken);
            PrivacyErasurePreviewResponse response = PrivacyErasurePreviewResponse.From(preview);
            return preview.Status == PrivacyErasureStatus.Conflict ? Results.Conflict(response) : Results.Ok(response);
        }
        catch (ArgumentException exception) { return Invalid("selections", exception.Message); }
    }

    private static async Task<IResult> BeginAsync(PrivacyErasureRequestDto request,
        PrivacyErasureService service, LiveChangeNotifier changes, CancellationToken cancellationToken)
    {
        try
        {
            PrivacyErasureResult result = await service.EraseAsync(request.ToApplication(), cancellationToken);
            if (result.Status is PrivacyErasureStatus.Pending or PrivacyErasureStatus.Completed)
                foreach (Guid captureId in request.Selections.Select(item => item.CaptureId).Distinct())
                    changes.CaptureChanged(captureId, "erasure");
            return OperationResult(result);
        }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
    }

    private static async Task<IResult> ContinueAsync(Guid operationId, ContinuePrivacyErasureRequest request,
        PrivacyErasureService service, LiveChangeNotifier changes, CancellationToken cancellationToken)
    {
        try
        {
            PrivacyErasureResult result = await service.ContinueAsync(operationId, request.Confirmed, cancellationToken);
            if (result.Status is PrivacyErasureStatus.Pending or PrivacyErasureStatus.Completed)
                foreach (Guid captureId in result.Assets.Select(item => item.CaptureId).Distinct())
                    changes.CaptureChanged(captureId, "erasure");
            return OperationResult(result);
        }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
    }

    private static async Task<IResult> GetAsync(Guid operationId, PrivacyErasureService service,
        CancellationToken cancellationToken)
    {
        PrivacyErasureResult result = await service.GetAsync(operationId, cancellationToken);
        return result.Status == PrivacyErasureStatus.NotFound
            ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Erasure operation not found")
            : Results.Ok(PrivacyErasureResponse.From(result));
    }

    private static async Task<IResult> ListAsync(PrivacyErasureService service, int? pageSize, bool? pendingOnly,
        string? cursor, CancellationToken cancellationToken)
    {
        try
        {
            PrivacyErasureOperationPage page = await service.ListAsync(pageSize ?? 50, pendingOnly ?? false,
                cursor, cancellationToken);
            return Results.Ok(new PrivacyErasureOperationListResponse([.. page.Operations.Select(PrivacyErasureOperationSummaryResponse.From)], page.NextCursor));
        }
        catch (ArgumentOutOfRangeException exception) { return Invalid("pageSize", exception.Message); }
        catch (ArgumentException exception) { return Invalid("cursor", exception.Message); }
    }

    private static async Task<IResult> CreateExportAsync(PrivacyErasureSuppressionService service,
        CancellationToken cancellationToken)
    {
        try { return Results.Ok(SuppressionManifestResponse.From(await service.CreateExportAsync(cancellationToken))); }
        catch (InvalidOperationException exception) { return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "A suppression export page is too large", detail: exception.Message); }
    }

    private static async Task<IResult> GetExportManifestAsync(Guid snapshotId,
        PrivacyErasureSuppressionService service, CancellationToken cancellationToken)
    {
        PrivacyErasureProtectedManifest? manifest = await service.GetExportManifestAsync(snapshotId, cancellationToken);
        return manifest is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound,
            title: "Suppression export snapshot not found") : Results.Ok(SuppressionManifestResponse.From(manifest));
    }

    private static async Task<IResult> GetExportPageAsync(Guid snapshotId, int pageIndex,
        PrivacyErasureSuppressionService service, CancellationToken cancellationToken)
    {
        PrivacyErasureProtectedPage? page = await service.GetExportPageAsync(snapshotId, pageIndex, cancellationToken);
        return page is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound,
            title: "Suppression export page not found") : Results.Ok(SuppressionPageResponse.From(page));
    }

    private static async Task<IResult> BeginReconciliationAsync(SuppressionReconciliationRequest request,
        PrivacyErasureSuppressionService service, CancellationToken cancellationToken) =>
        ReconciliationResult(await service.BeginReconciliationAsync(request.ProtectedManifest,
            request.ExpectedSnapshotId, cancellationToken));

    private static async Task<IResult> ReconcilePageAsync(SuppressionPageReconciliationRequest request,
        PrivacyErasureSuppressionService service, CancellationToken cancellationToken) =>
        ReconciliationResult(await service.ReconcilePageAsync(request.ProtectedPage, cancellationToken));

    private static async Task<IResult> CompleteReconciliationAsync(Guid snapshotId,
        PrivacyErasureSuppressionService service, CancellationToken cancellationToken) =>
        ReconciliationResult(await service.CompleteReconciliationAsync(snapshotId, cancellationToken));

    private static IResult OperationResult(PrivacyErasureResult result)
    {
        PrivacyErasureResponse response = PrivacyErasureResponse.From(result);
        return result.Status switch
        {
            PrivacyErasureStatus.Completed => Results.Ok(response),
            PrivacyErasureStatus.Pending => Results.Accepted(value: response),
            PrivacyErasureStatus.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Erasure operation not found", extensions: new Dictionary<string, object?> { ["result"] = response }),
            PrivacyErasureStatus.Conflict => Results.Conflict(response),
            PrivacyErasureStatus.Blocked => Results.Problem(statusCode: StatusCodes.Status423Locked,
                title: "Privacy erasure is blocked", extensions: new Dictionary<string, object?> { ["result"] = response }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Privacy erasure failed"),
        };
    }

    private static IResult ReconciliationResult(PrivacyErasureImportStatus status)
    {
        SuppressionImportResponse response = SuppressionImportResponse.From(status);
        return status.ErrorCode is null ? Results.Ok(response) : Results.Conflict(response);
    }

    private static IResult Invalid(string field, string error) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [error] });
}
