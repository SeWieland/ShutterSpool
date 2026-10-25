using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using ShutterSpool.Application.Security;
using ShutterSpool.Application.Sources;
using ShutterSpool.Server.Hosting;
using ShutterSpool.Domain.Sources;
using System.Text;

namespace ShutterSpool.Server.Api.Sources;

public sealed record CreateSourceRequest(string Name, string SourceType, string ConnectionProfile,
    string CurationMode = "Delay", int? DelaySeconds = null, string? Username = null, string? PasswordMode = null, string? Password = null,
    Guid? CollectionId = null);
public sealed record UpdateSourceConfigurationRequest(long ExpectedRevision, string Name, string CurationMode,
    string SourceType, string ConnectionProfile, int? DelaySeconds = null, Guid? CollectionId = null);
public sealed record UpdateSourceCurationPolicyRequest(long ExpectedRevision, string CurationMode, int? DelaySeconds = null);
public sealed record RegenerateSourceSecretRequest(long ExpectedRevision, string? Username = null, string? PasswordMode = null, string? Password = null);
public sealed record CreatedSourceResponse(Guid Id, string Username, string VirtualRoot, string? Secret, long ConfigurationRevision = 0);
public sealed record SourceConfigurationResponse(Guid Id, string Name, string CurationMode, int CurationDelaySeconds,
    string Username, long CredentialGeneration, bool IngressEnabled, long ConfigurationRevision, bool Archived,
    string SourceType, string ConnectionProfile, Guid CollectionId, string CollectionName)
{
    internal static SourceConfigurationResponse From(SourceManagementConfiguration source) => new(source.Id, source.Name,
        source.CurationMode.ToString(), source.CurationDelaySeconds, source.Username,
        source.CredentialGeneration, source.IsIngressEnabled, source.ConfigurationRevision, source.IsArchived,
        source.SourceType.ToString(), source.ConnectionProfile.ToString(), source.CollectionId, source.CollectionName);
}
public sealed class SourceConfigurationConflictProblemDetails : ProblemDetails
{
    public string ErrorCode { get; init; } = "CONFIGURATION_CONFLICT";
    public required SourceConfigurationResponse Current { get; init; }
}
public sealed record RegeneratedSourceSecretResponse(string? Secret, SourceConfigurationResponse Current);
public sealed record SetSourceIngressRequest(long ExpectedRevision, bool Enabled);
public sealed record SourceRemovalResponse(string Outcome);
public sealed record SourceConfigurationPageResponse(SourceConfigurationResponse[] Items, string? NextCursor);

public static class SourceEndpoints
{
    public static RouteGroupBuilder MapSourceEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/sources", ListAsync).WithSummary("List configured Sources").Produces<SourceConfigurationPageResponse>().ProducesValidationProblem();
        management.MapSourceCapabilityEndpoints();
        management.MapGet("/sources/{id:guid}", GetAsync).WithSummary("Read Source configuration").Produces<SourceConfigurationResponse>().ProducesProblem(404);
        management.MapPut("/sources/{id:guid}", UpdateAsync).WithSummary("Update Source configuration")
            .Produces<SourceConfigurationResponse>().ProducesValidationProblem().Produces<SourceConfigurationConflictProblemDetails>(409, "application/problem+json");
        management.MapPost("/sources", CreateAsync)
            .WithSummary("Create a camera Source")
            .WithDescription("Source curation defaults to Delay for one hour. Delay accepts 1 to 31,536,000 seconds. The returned secret is shown once.")
            .Produces<CreatedSourceResponse>(StatusCodes.Status201Created).ProducesValidationProblem().ProducesProblem(409);
        management.MapPost("/sources/{id:guid}/regenerate-secret", RegenerateAsync)
            .WithSummary("Regenerate a Source ingest secret").Produces<RegeneratedSourceSecretResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(404).Produces<SourceConfigurationConflictProblemDetails>(409, "application/problem+json");
        management.MapPut("/sources/{id:guid}/ingress", SetIngressAsync)
            .WithSummary("Suspend or re-enable Source ingress").Produces<SourceConfigurationResponse>()
            .ProducesProblem(404).Produces<SourceConfigurationConflictProblemDetails>(409, "application/problem+json");
        management.MapDelete("/sources/{id:guid}", RemoveAsync).WithSummary("Remove or archive a Source")
            .Produces<SourceRemovalResponse>().ProducesProblem(404)
            .Produces<SourceConfigurationConflictProblemDetails>(409, "application/problem+json");
        management.MapPut("/sources/{id:guid}/curation-policy", UpdatePolicyAsync)
            .WithSummary("Change a Source's curation policy")
            .WithDescription("The change applies to Captures first grouped after the update. Existing Captures retain their policy and deadline anchor.")
            .Produces<SourceConfigurationResponse>().ProducesValidationProblem().ProducesProblem(404)
            .Produces<SourceConfigurationConflictProblemDetails>(409, "application/problem+json");
        return management;
    }

    private static async Task<IResult> ListAsync(int? limit, string? cursor, SourceManagementService sources, CancellationToken token)
    {
        int pageSize = limit ?? 30;
        if (pageSize is < 1 or > 100) return Invalid("Provide a page size from 1 to 100.");
        string? name = null;
        Guid? id = null;
        if (cursor is not null && !TrySourceCursor(cursor, out name, out id)) return Invalid("The Source cursor is invalid.");
        SourceManagementConfiguration[] values = await sources.ListAsync(pageSize, name, id, token);
        bool more = values.Length > pageSize;
        SourceManagementConfiguration[] page = [.. values.Take(pageSize)];
        string? next = more ? EncodeSourceCursor(page[^1].Name, page[^1].Id) : null;
        return Results.Ok(new SourceConfigurationPageResponse([.. page.Select(SourceConfigurationResponse.From)], next));
    }

    private static async Task<IResult> GetAsync(Guid id, SourceManagementService sources, CancellationToken token)
    {
        SourceManagementConfiguration? source = await sources.GetAsync(id, token);
        return source is null ? Missing() : Results.Ok(SourceConfigurationResponse.From(source));
    }

    private static async Task<IResult> CreateAsync(CreateSourceRequest request, SourceManagementService sources,
        CameraIngressSettings settings,
        SourceDisplayCache displayCache, CancellationToken token)
    {
        try
        {
            if (!TryProfile(request.SourceType, request.ConnectionProfile, out SourceType sourceType, out SourceConnectionProfile profile)
                || !settings.IsEnabled(profile)) return Invalid("Choose a Source type and connection profile enabled in this deployment.");
            CreatedSource created = await sources.CreateAsync(request.Name, request.CurationMode, request.DelaySeconds, token,
                sourceType, profile, request.Username, request.PasswordMode, request.Password, request.CollectionId);
            await displayCache.InvalidateAsync(created.Id, token);
            return Results.Created($"/management/sources/{created.Id}", new CreatedSourceResponse(created.Id, created.Username, created.VirtualRoot,
                request.PasswordMode == "Custom" ? null : created.Secret));
        }
        catch (ArgumentException exception) { return Invalid(exception); }
        catch (SourceUsernameConflictException) { return UsernameConflict(); }
        catch (ConfigurationConflictException) { return Results.Problem(statusCode: 409, title: "Source name is already in use", extensions: new Dictionary<string, object?> { ["errorCode"] = "SOURCE_NAME_CONFLICT" }); }
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateSourceConfigurationRequest request,
        SourceManagementService sources, CameraIngressSettings settings, SourceDisplayCache displayCache, CancellationToken token)
    {
        try
        {
            if (!TryProfile(request.SourceType, request.ConnectionProfile, out SourceType sourceType, out SourceConnectionProfile profile))
                return Invalid("Choose an implemented Source type and connection profile.");
            SourceManagementConfiguration? current = await sources.GetAsync(id, token);
            if (current is null) return Missing();
            if (!settings.IsEnabled(profile) && (current.SourceType != sourceType || current.ConnectionProfile != profile))
                return Invalid("Choose a connection profile enabled in this deployment for a new assignment.");
            SourceConfigurationWrite result = await sources.UpdateConfigurationAsync(id, request.ExpectedRevision,
                request.Name, request.CurationMode, request.DelaySeconds, token, sourceType, profile, request.CollectionId);
            if (result.Applied) await displayCache.InvalidateAsync(id, token);
            return WriteResult(result);
        }
        catch (ArgumentException exception) { return Invalid(exception.Message); }
        catch (ConfigurationConflictException) { return Results.Problem(statusCode: 409, title: "Source name is already in use", extensions: new Dictionary<string, object?> { ["errorCode"] = "SOURCE_NAME_CONFLICT" }); }
    }

    private static async Task<IResult> UpdatePolicyAsync(Guid id, UpdateSourceCurationPolicyRequest request,
        SourceManagementService sources, SourceDisplayCache displayCache, CancellationToken token)
    {
        try
        {
            SourceManagementConfiguration? current = await sources.GetAsync(id, token);
            if (current is null) return Missing();
            SourceConfigurationWrite result = await sources.UpdateConfigurationAsync(id, request.ExpectedRevision, current.Name,
                request.CurationMode, request.DelaySeconds, token, current.SourceType, current.ConnectionProfile, current.CollectionId);
            if (result.Applied) await displayCache.InvalidateAsync(id, token);
            return WriteResult(result);
        }
        catch (ArgumentException exception) { return Invalid(exception.Message); }
    }

    private static async Task<IResult> RegenerateAsync(Guid id, RegenerateSourceSecretRequest request,
        SourceManagementService sources, SourceDisplayCache displayCache, CancellationToken token)
    {
        SourceSecretWrite result;
        try { result = await sources.RegenerateAsync(id, request.ExpectedRevision, token, request.Username, request.PasswordMode, request.Password); }
        catch (ArgumentException exception) { return Invalid(exception); }
        catch (UnchangedSourceCredentialException) { return Invalid(new ArgumentException("Choose a different username or password.", "password")); }
        catch (SourceUsernameConflictException) { return UsernameConflict(); }
        if (result.Current is null) return Missing();
        if (result.Applied) await displayCache.InvalidateAsync(id, token);
        return result.Applied ? Results.Ok(new RegeneratedSourceSecretResponse(request.PasswordMode == "Custom" ? null : result.Secret, SourceConfigurationResponse.From(result.Current))) : Conflict(result.Current);
    }

    private static async Task<IResult> SetIngressAsync(Guid id, SetSourceIngressRequest request,
        SourceManagementService sources, SourceDisplayCache displayCache, CancellationToken token)
    {
        SourceConfigurationWrite result = await sources.SetIngressEnabledAsync(id, request.ExpectedRevision, request.Enabled, token);
        if (result.Applied) await displayCache.InvalidateAsync(id, token);
        return WriteResult(result);
    }

    private static async Task<IResult> RemoveAsync(Guid id, long expectedRevision,
        SourceManagementService sources, SourceDisplayCache displayCache, CancellationToken token)
    {
        SourceRemovalWrite result = await sources.RemoveAsync(id, expectedRevision, token);
        if (!result.Applied) return result.Current is null ? Missing() : Conflict(result.Current);
        await displayCache.InvalidateAsync(id, token);
        return Results.Ok(new SourceRemovalResponse(result.Removed ? "deleted" : "archived"));
    }

    private static IResult WriteResult(SourceConfigurationWrite result) => result.Current is null ? Missing()
        : result.Applied ? Results.Ok(SourceConfigurationResponse.From(result.Current)) : Conflict(result.Current);

    private static IResult Conflict(SourceManagementConfiguration current) => Results.Json(new SourceConfigurationConflictProblemDetails
    {
        Status = 409, Title = "Source configuration revision conflict", Current = SourceConfigurationResponse.From(current),
    }, statusCode: 409, contentType: "application/problem+json");

    private static IResult Missing() => Results.Problem(statusCode: 404, title: "Source not found");
    private static IResult Invalid(string error) => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error] });
    private static IResult Invalid(ArgumentException error) => Results.ValidationProblem(new Dictionary<string, string[]> { [error.ParamName ?? "request"] = [error.Message] });
    private static IResult UsernameConflict() => Results.ValidationProblem(
        new Dictionary<string, string[]> { ["username"] = ["A Source already uses this username."] },
        statusCode: 409, title: "Camera username is already in use");

    private static bool TryProfile(string sourceTypeText, string profileText, out SourceType sourceType, out SourceConnectionProfile profile)
    {
        sourceType = default;
        profile = default;
        bool validType = Enum.TryParse(sourceTypeText, ignoreCase: false, out sourceType) && Enum.IsDefined(sourceType)
            && sourceTypeText == sourceType.ToString();
        bool validProfile = Enum.TryParse(profileText, ignoreCase: false, out profile) && Enum.IsDefined(profile)
            && profileText == profile.ToString();
        return validType && validProfile;
    }

    private static string EncodeSourceCursor(string name, Guid id) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{name}|{id:N}"));
    private static bool TrySourceCursor(string cursor, out string? name, out Guid? id)
    {
        name = null; id = null;
        if (cursor.Length is 0 or > 2048) return false;
        try
        {
            string decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor));
            int split = decoded.LastIndexOf('|');
            if (split is < 1 or > 256 || !Guid.TryParseExact(decoded[(split + 1)..], "N", out Guid parsed)) return false;
            name = decoded[..split]; id = parsed; return true;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { return false; }
    }
}
