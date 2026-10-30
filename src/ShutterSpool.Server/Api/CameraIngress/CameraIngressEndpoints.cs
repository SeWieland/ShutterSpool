using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Server.Hosting;
using ShutterSpool.Domain.Sources;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ShutterSpool.Server.Api.CameraIngress;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
internal sealed record SftpGoAuthRequest([property: JsonPropertyName("username")] string? Username, [property: JsonPropertyName("password")] string? Password);
internal sealed record SftpGoSecret(string Status, string Payload);
internal sealed record SftpGoHttpConfig(string Endpoint, [property: JsonPropertyName("api_key")] SftpGoSecret ApiKey);
internal sealed record SftpGoFilesystem(int Provider, [property: JsonPropertyName("httpconfig")] SftpGoHttpConfig Httpconfig);
internal sealed record SftpGoAuthResponse(
    string Username,
    int Status,
    [property: JsonPropertyName("home_dir")] string HomeDir,
    IReadOnlyDictionary<string, string[]> Permissions,
    [property: JsonPropertyName("max_sessions")] int MaxSessions,
    [property: JsonPropertyName("upload_bandwidth")] int UploadBandwidth,
    [property: JsonPropertyName("filters")] SftpGoUserFilters Filters,
    [property: JsonPropertyName("filesystem")] SftpGoFilesystem Filesystem);
internal sealed record SftpGoUserFilters([property: JsonPropertyName("max_upload_file_size")] long MaxUploadFileSize,
    [property: JsonPropertyName("external_auth_cache_time")] int ExternalAuthCacheTime);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
internal sealed record SftpGoEvent(
    [property: JsonPropertyName("action")] string? Action = null,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("virtual_path")] string? VirtualPath = null,
    [property: JsonPropertyName("endpoint")] string? Endpoint = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null,
    [property: JsonPropertyName("timestamp")] long? Timestamp = null,
    [property: JsonPropertyName("status")] int? Status = null,
    [property: JsonPropertyName("protocol")] string? Protocol = null,
    [property: JsonPropertyName("fs_provider")] int? FsProvider = null,
    [property: JsonPropertyName("open_flags")] int? OpenFlags = null,
    [property: JsonPropertyName("file_size")] long? FileSize = null);
internal sealed record SftpGoStatResponse(string Name, long Size, uint Mode,
    [property: JsonPropertyName("last_modified")] DateTimeOffset LastModified);
internal sealed record SftpGoDirSizeResponse(long Files, long Size);
public sealed record CameraTransferStatusResponse(Guid Id, string State, Guid? AssetId, string? FailureReason);

internal static class SftpGoOpenFlags
{
    public const int O_WRONLY = 1;
    public const int O_CREAT = 64;
    public const int O_TRUNC = 512;
    public const int UploadOverwrite = O_WRONLY | O_CREAT | O_TRUNC;
}

internal sealed class CameraCallbackEndpointFilter(CameraIngressSettings settings) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;
        string suppliedKey = http.Request.Headers["X-API-KEY"].ToString();
        byte[] expected = Encoding.UTF8.GetBytes(settings.CallbackKey);
        byte[] supplied = Encoding.UTF8.GetBytes(suppliedKey);
        bool valid = http.Connection.LocalPort == settings.InternalPort && supplied.Length == expected.Length
            && CryptographicOperations.FixedTimeEquals(supplied, expected);
        return valid ? next(context) : ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
    }
}

internal static class CameraIngressEndpoints
{
    public static void MapCameraIngressEndpoints(this WebApplication app, CameraIngressSettings settings)
    {
        if (!settings.Enabled) return;
        RouteGroupBuilder group = app.MapGroup("/internal/camera")
            .AddEndpointFilter(new CameraCallbackEndpointFilter(settings))
            .ExcludeFromDescription();
        group.MapPost("/auth", (SftpGoAuthRequest request, CameraIngressService ingress, CameraIngressSettings options, ILogger<CameraIngressService> logger,
            CancellationToken token) => AuthenticateAsync(request, ingress, options, logger, SourceConnectionProfile.ModernFtps, token));
        group.MapPost("/legacy-auth", (SftpGoAuthRequest request, CameraIngressService ingress, CameraIngressSettings options, ILogger<CameraIngressService> logger,
            CancellationToken token) => AuthenticateAsync(request, ingress, options, logger, SourceConnectionProfile.LegacyFtps, token));
        group.MapPost("/plain-auth", (SftpGoAuthRequest request, CameraIngressService ingress, CameraIngressSettings options, ILogger<CameraIngressService> logger,
            CancellationToken token) => AuthenticateAsync(request, ingress, options, logger, SourceConnectionProfile.PlainFtpLan, token));
        group.MapPost("/hook", HookAsync);
        group.MapMethods("/fs/{loginId:guid}/{operation}/{**path}", ["GET", "POST", "DELETE", "PATCH"], FileSystemAsync);

        app.MapGet("/management/ingress/transfers", async (CameraIngressService ingress, CancellationToken token) =>
                Results.Ok((await ingress.GetStatusAsync(null, token)).Select(item =>
                    new CameraTransferStatusResponse(item.Id, item.State.ToString(), item.AssetId, item.FailureReason))))
            .RequireAuthorization(Security.SecurityExtensions.ManagementAccessPolicy)
            .WithSummary("Read redacted camera ingress transfer status");
    }

    private static async Task<IResult> AuthenticateAsync(SftpGoAuthRequest request, CameraIngressService ingress,
        CameraIngressSettings settings, ILogger<CameraIngressService> logger, SourceConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (!settings.IsEnabled(profile)) return Results.StatusCode(403);
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password)) return Results.StatusCode(403);
        CameraLogin? login;
        try { login = await ingress.AuthenticateAsync(request.Username, request.Password, cancellationToken, profile); }
        catch (CameraSourceRootInitializationException exception)
        {
            logger.LogWarning("Camera source root initialization failed for SourceId {SourceId}.", exception.SourceId);
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError,
                title: "Camera source storage is unavailable.",
                extensions: new Dictionary<string, object?> { ["errorCode"] = "CAMERA_ROOT_UNAVAILABLE" });
        }
        catch (Exception) { return Results.StatusCode(500); }
        if (login is null) return Results.StatusCode(403);

        var permissions = new Dictionary<string, string[]> { ["/"] = ["list", "upload", "overwrite", "create_dirs"] };
        var response = new SftpGoAuthResponse(login.Username, 1, $"/tmp/shutterspool-{login.Id:N}", permissions,
            settings.MaximumSessionsPerSource, settings.UploadBandwidthKiBPerSecond,
            new(settings.MaximumUploadBytes, 0),
            new(6, new($"{settings.CallbackBaseUrl.TrimEnd('/')}/fs/{login.Id:N}", new("Plain", settings.CallbackKey))));
        return Results.Json(response);
    }

    private static async Task<IResult> HookAsync(SftpGoEvent request, CameraIngressService ingress, CameraIngressSettings settings,
        CancellationToken cancellationToken)
    {
        if (request.FsProvider != 6 || request.Protocol != "FTP" || request.Timestamp is null || request.Status is null
            || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.Path) || string.IsNullOrWhiteSpace(request.VirtualPath)
            || !string.Equals(request.Path, request.VirtualPath, StringComparison.Ordinal)
            || !TryLoginId(request.Endpoint, settings, out Guid loginId)) return Results.StatusCode(403);
        string virtualPath = request.VirtualPath ?? request.Path ?? "";
        try
        {
            switch (request.Action)
            {
                case "pre-upload" when request.Status == 1:
                    CameraLogin? login = await ingress.FindLoginAsync(loginId, cancellationToken);
                    if (login is null || !settings.IsEnabled(login.ConnectionProfile)) return Results.StatusCode(403);
                    await ingress.AdmitAsync(loginId, request.Username, request.SessionId, virtualPath, request.Timestamp.Value,
                        settings.MaximumUploadBytes, settings.MaximumConcurrentTransfers, settings.TransferTimeoutSeconds, cancellationToken);
                    return Results.Ok();
                case "pre-upload":
                    return Results.StatusCode(403);
                case "upload" when request.Status == 1 && (request.FileSize is null or >= 0):
                    _ = await ingress.CompleteAsync(loginId, request.Username, request.SessionId, virtualPath, request.Timestamp.Value,
                        request.FileSize ?? 0, cancellationToken);
                    return Results.Ok();
                case "upload":
                    _ = await ingress.FailUploadAsync(loginId, request.Username, request.SessionId, virtualPath, request.Timestamp.Value, cancellationToken);
                    return Results.Ok();
                default:
                    return Results.StatusCode(403);
            }
        }
        catch (CameraIngressRejectedException) { return Results.StatusCode(403); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(500); }
        catch (Exception) { return Results.StatusCode(500); }
    }

    private static async Task<IResult> FileSystemAsync(HttpContext context, Guid loginId, string operation, CameraIngressService ingress,
        CameraIngressSettings settings, CancellationToken cancellationToken)
    {
        string path;
        try { path = ReadEncodedPath(context, loginId, operation); }
        catch (Exception) { return Results.StatusCode(403); }

        try
        {
            switch (context.Request.Method, operation)
            {
                case ("POST", "create"):
                    if (!int.TryParse(context.Request.Query["flags"], NumberStyles.None, CultureInfo.InvariantCulture, out int flags))
                        return Results.StatusCode(403);
                    if (flags != SftpGoOpenFlags.UploadOverwrite) return Results.StatusCode(403);
                    IHttpMaxRequestBodySizeFeature? bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                    if (bodyLimit?.IsReadOnly == true) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    if (bodyLimit is not null) bodyLimit.MaxRequestBodySize = settings.MaximumUploadBytes;
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TransferTimeoutSeconds));
                        await ingress.ReceiveAsync(loginId, path, context.Request.Body, settings.MaximumUploadBytes, deadline.Token);
                    }
                    return Results.Ok();
                case ("POST", "mkdir"):
                    await ingress.CreateDirectoryAsync(loginId, path, cancellationToken);
                    return Results.Ok();
                case ("GET", "stat"):
                case ("GET", "lstat"):
                    CameraFileInfo? info = await ingress.GetFileInfoAsync(loginId, path, cancellationToken);
                    return info is null ? Results.NotFound() : Results.Json(ToStat(info));
                case ("GET", "readdir"):
                    IReadOnlyList<CameraFileInfo> entries = await ingress.GetDirectoryAsync(loginId, path, cancellationToken);
                    return Results.Json(entries.Select(ToStat));
                case ("GET", "dirsize"):
                    (long files, long size) = await ingress.GetDirectorySizeAsync(loginId, cancellationToken);
                    return Results.Json(new SftpGoDirSizeResponse(files, size));
                case ("GET", "open"):
                case ("DELETE", "remove"):
                case ("PATCH", "rename"):
                case ("PATCH", "chmod"):
                case ("PATCH", "chtimes"):
                case ("PATCH", "truncate"):
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                default:
                    return Results.StatusCode(StatusCodes.Status501NotImplemented);
            }
        }
        catch (CameraIngressRejectedException) { return Results.StatusCode(403); }
        catch (IOException) { return Results.StatusCode(507); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Results.StatusCode(StatusCodes.Status408RequestTimeout); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Results.StatusCode(500); }
        catch (Exception) { return Results.StatusCode(500); }
    }

    private static SftpGoStatResponse ToStat(CameraFileInfo info)
    {
        const uint directoryMode = 2_147_484_141;
        return new(info.Name, info.Size, info.IsDirectory ? directoryMode : 420, info.LastModified);
    }

    private static string ReadEncodedPath(HttpContext context, Guid loginId, string operation)
    {
        string raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
        int queryIndex = raw.IndexOf('?');
        if (queryIndex >= 0) raw = raw[..queryIndex];
        string prefix = $"/internal/camera/fs/{loginId:N}/{operation}/";
        int pathIndex = raw.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (pathIndex < 0) throw new FormatException();
        return Uri.UnescapeDataString(raw[(pathIndex + prefix.Length)..]);
    }

    internal static bool TryLoginId(string? endpoint, CameraIngressSettings settings, out Guid loginId)
    {
        loginId = default;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? actual)
            || !Uri.TryCreate($"{settings.CallbackBaseUrl.TrimEnd('/')}/fs/", UriKind.Absolute, out Uri? root)
            || !string.Equals(actual.Scheme, root.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.Authority, root.Authority, StringComparison.OrdinalIgnoreCase)) return false;
        string expectedPath = root.AbsolutePath;
        if (!actual.AbsolutePath.StartsWith(expectedPath, StringComparison.OrdinalIgnoreCase)) return false;
        string suffix = actual.AbsolutePath[expectedPath.Length..].TrimEnd('/');
        return !suffix.Contains('/') && Guid.TryParseExact(suffix, "N", out loginId);
    }
}
