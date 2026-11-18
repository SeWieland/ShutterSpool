using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Domain.Delivery;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ShutterSpool.Infrastructure.Destinations.WebDav;

internal static class WebDavDestinationUris
{
    internal const string StagingCollection = ".shutterspool-staging";

    public static bool TryCollection(string value, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048 || !Uri.TryCreate(value, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query)
            || !HasUnambiguousHost(value, parsed)
            || !string.IsNullOrEmpty(parsed.Fragment) || value.Contains('\\') || value.Any(char.IsControl)
            || parsed.AbsolutePath.Contains("//", StringComparison.Ordinal)
            || HasUnsafeRawPath(value)
            || parsed.AbsolutePath.Split('/').Any(segment => segment is "." or ".."
                || Uri.UnescapeDataString(segment).Contains('/') || Uri.UnescapeDataString(segment).Contains('\\')))
            return false;
        uri = new Uri(parsed.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
        return true;
    }

    private static bool HasUnambiguousHost(string value, Uri parsed)
    {
        int start = value.IndexOf("://", StringComparison.Ordinal) + 3;
        if (start < 3) return false;
        int end = value.IndexOfAny(['/', '?', '#'], start);
        string authority = value[start..(end < 0 ? value.Length : end)];
        string expected = parsed.HostNameType == UriHostNameType.IPv6 ? $"[{parsed.IdnHost.Trim('[', ']')}]" : parsed.IdnHost;
        if (!parsed.IsDefaultPort) expected += $":{parsed.Port}";
        else if (authority.EndsWith(":443", StringComparison.Ordinal)) expected += ":443";
        return string.Equals(authority, expected, StringComparison.OrdinalIgnoreCase)
            && !parsed.IdnHost.Contains('%') && !parsed.IdnHost.EndsWith('.')
            && (parsed.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
                || parsed.HostNameType == UriHostNameType.Dns && parsed.IdnHost.Contains('.'));
    }

    private static bool HasUnsafeRawPath(string value)
    {
        int authorityStart = value.IndexOf("://", StringComparison.Ordinal);
        if (authorityStart < 0) return true;
        authorityStart += 3;
        int pathStart = value.IndexOf('/', authorityStart);
        if (pathStart < 0) return false;
        int pathEnd = value.IndexOfAny(['?', '#'], pathStart);
        string rawPath = value[pathStart..(pathEnd < 0 ? value.Length : pathEnd)];
        return rawPath.Contains("//", StringComparison.Ordinal) || rawPath.Split('/').Any(segment => segment is "." or ".."
            || segment.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || segment.Contains("%5c", StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryTarget(string value, out string[] segments)
    {
        segments = [];
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2_048 || value.StartsWith('/') || value.Contains('\\')) return false;
        string[] parts = value.Split('/');
        if (parts.Any(part => part.Length is 0 or > 255 || part is "." or ".." || part.Any(char.IsControl) || part.Contains(':')
            || part.Contains('%') && (part.Contains("%2f", StringComparison.OrdinalIgnoreCase)
                || part.Contains("%5c", StringComparison.OrdinalIgnoreCase) || part.Contains("%2e", StringComparison.OrdinalIgnoreCase)))) return false;
        if (parts[0].Equals(StagingCollection, StringComparison.OrdinalIgnoreCase)) return false;
        segments = parts;
        return true;
    }

    public static Uri Child(Uri collection, IEnumerable<string> segments) =>
        new(collection.AbsoluteUri + string.Join('/', segments.Select(Uri.EscapeDataString)), UriKind.Absolute);
}

internal sealed class WebDavDestinationDeliveryAdapter(IHttpClientFactory clients, DestinationCapabilities? capabilities = null) : IDestinationDeliveryAdapter
{
    internal const string HttpClientName = "webdav";
    internal const string PrivateRequestTelemetryKey = "ShutterSpool.PrivateProviderRequest";
    private const string DestinationKindDisabled = "DESTINATION_KIND_DISABLED";
    private const string TargetConflict = "TARGET_CONFLICT";
    private const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    private const string TlsValidationFailed = "TLS_VALIDATION_FAILED";
    private const string DavResponseInvalid = "DAV_RESPONSE_INVALID";
    private const string UnsupportedPublication = "UNSUPPORTED_PUBLICATION";
    private const int BufferSize = 64 * 1024;
    private const int MaxXmlBytes = 64 * 1024;
    public string Kind => "webdav";

    public async Task<(bool Writable, string? ErrorCode)> TestAsync(string collectionUrl, string username, string password,
        CancellationToken cancellationToken)
    {
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled(Kind)) return (false, DestinationKindDisabled);
        if (!WebDavDestinationUris.TryCollection(collectionUrl, out Uri? root)) return (false, "INVALID_COLLECTION_URL");
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken operationToken = deadline.Token;
        try
        {
            using HttpClient client = clients.CreateClient(HttpClientName);
            DeliveryResult? rootCheck = await VerifyCollectionAsync(client, root, username, password, operationToken);
            if (rootCheck is not null) return (false, rootCheck.ErrorCode);
            DeliveryResult? result = await VerifyNoReplaceAsync(client, root, username, password, operationToken);
            return result is null ? (true, null) : (false, result.ErrorCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (false, "TEST_TIMED_OUT"); }
        catch (Exception exception) when (exception is HttpRequestException or XmlException or IOException)
        { return (false, "CONNECTION_TEST_FAILED"); }
    }

    public async Task<DeliveryResult> WriteAsync(DeliveryRequest request, Stream original, CancellationToken cancellationToken)
    {
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled(Kind))
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: DestinationKindDisabled);
        if (!TryConfig(request, out Uri? root, out string? user, out string? password, out string[]? segments, out string? error))
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: error);
        if (!ValidExpectedContent(request))
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "INVALID_EXPECTED_CONTENT");
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken operationToken = deadline.Token;
        using HttpClient client = clients.CreateClient(HttpClientName);
        Uri target = WebDavDestinationUris.Child(root, segments);
        bool moveStarted = false;
        try
        {
            DeliveryResult? preflight = await VerifyCollectionAsync(client, root, user, password, operationToken)
                ?? await VerifyTargetAsync(client, target, user, password, request, token: operationToken)
                ?? await VerifyNoReplaceAsync(client, root, user, password, operationToken);
            if (preflight is not null) return preflight;
            await EnsureParentsAsync(client, root, segments, user, password, operationToken);
            Uri stageRoot = WebDavDestinationUris.Child(root, [WebDavDestinationUris.StagingCollection]);
            await MakeCollectionAsync(client, stageRoot, user, password, operationToken);
            Uri staged = WebDavDestinationUris.Child(root, [WebDavDestinationUris.StagingCollection, request.AttemptId.ToString("N")]);
            DeliveryResult? upload = await UploadOriginalAsync(client, staged, user, password, request, original, operationToken);
            if (upload is not null) return upload;

            using var move = NoReplaceMove(staged, target, user, password);
            moveStarted = true;
            using HttpResponseMessage moved = await client.SendAsync(move, HttpCompletionOption.ResponseHeadersRead, operationToken);
            if (moved.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
            {
                return await VerifyMoveCollisionAsync(client, target, user, password, request, operationToken);
            }
            if (!IsCompletedWriteStatus(moved.StatusCode)) return ResultFor(moved.StatusCode, mayHavePublished: true);
            return new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested || moveStarted
                ? new(DeliveryResultKind.Uncertain, ErrorCode: "WRITE_INTERRUPTED")
                : new(DeliveryResultKind.TransientFailure, ErrorCode: "PROVIDER_TIMEOUT");
        }
        catch (HttpRequestException exception)
        {
            return RequestFailure(exception, moveStarted);
        }
        catch (InvalidDataException) { return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "UNSAFE_TARGET"); }
        catch (XmlException) { return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: DavResponseInvalid); }
    }

    public async Task<DeliveryReconciliation> ReconcileAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled(Kind))
            return new(DeliveryReconciliationKind.Uncertain, ErrorCode: DestinationKindDisabled);
        if (!TryConfig(request, out Uri? root, out string? user, out string? password, out string[]? segments, out string? error))
            return new(DeliveryReconciliationKind.Conflict, ErrorCode: error);
        if (!ValidExpectedContent(request))
            return new(DeliveryReconciliationKind.Conflict, ErrorCode: "INVALID_EXPECTED_CONTENT");
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken operationToken = deadline.Token;
        try
        {
            using HttpClient client = clients.CreateClient(HttpClientName);
            DeliveryResult? result = await VerifyTargetAsync(client, WebDavDestinationUris.Child(root, segments), user, password, request, mayHavePublished: true, operationToken);
            if (result is null) return new(DeliveryReconciliationKind.Uncertain, ErrorCode: "ABSENCE_NOT_AUTHORITATIVE");
            if (result.Kind == DeliveryResultKind.Confirmed) return new(DeliveryReconciliationKind.Confirmed, result.Evidence);
            if (result.ErrorCode == TargetConflict) return new(DeliveryReconciliationKind.Conflict, ErrorCode: TargetConflict);
            return new(DeliveryReconciliationKind.Uncertain, ErrorCode: result.ErrorCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or XmlException or IOException)
        { return new(DeliveryReconciliationKind.Uncertain, ErrorCode: "RECONCILIATION_UNAVAILABLE"); }
    }

    internal static HttpRequestMessage Request(HttpMethod method, Uri uri, string username, string password)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password)));
        request.Options.Set(new HttpRequestOptionsKey<bool>(PrivateRequestTelemetryKey), true);
        return request;
    }

    private static HttpRequestMessage NoReplaceMove(Uri source, Uri target, string user, string password)
    {
        var move = Request(new HttpMethod("MOVE"), source, user, password);
        move.Headers.TryAddWithoutValidation("Destination", target.AbsoluteUri);
        move.Headers.TryAddWithoutValidation("Overwrite", "F");
        return move;
    }

    private static async Task<DeliveryResult?> UploadOriginalAsync(HttpClient client, Uri staged, string user, string password,
        DeliveryRequest request, Stream original, CancellationToken token)
    {
        var verifier = new HashingReadStream(original, request.Size, request.Sha256);
        using var put = Request(HttpMethod.Put, staged, user, password);
        put.Content = new StreamContent(verifier, BufferSize);
        put.Content.Headers.ContentLength = request.Size;
        put.Headers.TryAddWithoutValidation("X-Hash", "sha256");
        put.Headers.TryAddWithoutValidation("OC-Checksum", $"SHA256:{request.Sha256}");
        using HttpResponseMessage response = await client.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.Accepted)
            return new(DeliveryResultKind.TransientFailure, ErrorCode: ProviderUnavailable);
        if (!IsCompletedWriteStatus(response.StatusCode)) return ResultFor(response.StatusCode, mayHavePublished: false);
        if (!verifier.Matches) return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "ORIGINAL_CHANGED");
        if (!MatchesProviderHash(response, request.Sha256))
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "PROVIDER_HASH_MISMATCH");
        return null;
    }

    private static async Task<DeliveryResult> VerifyMoveCollisionAsync(HttpClient client, Uri target, string user, string password,
        DeliveryRequest request, CancellationToken token)
    {
        DeliveryResult? occupied = await VerifyTargetAsync(client, target, user, password, request, token: token);
        if (occupied?.Kind == DeliveryResultKind.Confirmed) return occupied;
        if (occupied?.ErrorCode == TargetConflict) return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: TargetConflict);
        return new(DeliveryResultKind.Uncertain, ErrorCode: "MOVE_CONFLICT_UNVERIFIABLE");
    }

    private static DeliveryResult RequestFailure(HttpRequestException exception, bool mayHavePublished)
    {
        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: TlsValidationFailed);
        if (exception.StatusCode is { } status) return ResultFor(status, mayHavePublished);
        if (mayHavePublished) return new(DeliveryResultKind.Uncertain, ErrorCode: "PROVIDER_OUTCOME_UNKNOWN");
        return new(DeliveryResultKind.TransientFailure, ErrorCode: ProviderUnavailable);
    }

    private static async Task EnsureParentsAsync(HttpClient client, Uri root, string[] segments, string user, string password, CancellationToken token)
    {
        for (int length = 1; length < segments.Length; length++)
            await MakeCollectionAsync(client, WebDavDestinationUris.Child(root, segments[..length]), user, password, token);
    }

    private static async Task<DeliveryResult?> VerifyNoReplaceAsync(HttpClient client, Uri root, string user, string password,
        CancellationToken token)
    {
        var probe = new NoReplaceProbe(root);
        bool cleanupAttempted = false;
        try
        {
            Uri stageRoot = WebDavDestinationUris.Child(root, [WebDavDestinationUris.StagingCollection]);
            await MakeCollectionAsync(client, stageRoot, user, password, token);
            DeliveryResult? failure = await probe.VerifyAsync(client, user, password, token);
            if (failure is not null) return failure;

            cleanupAttempted = true;
            if (!await probe.CleanupAsync(client, user, password, token))
                return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "PROBE_CLEANUP_FAILED");
            return null;
        }
        catch (HttpRequestException exception) { return RequestFailure(exception, mayHavePublished: false); }
        catch (OperationCanceledException) { return new(DeliveryResultKind.TransientFailure, ErrorCode: "PROVIDER_TIMEOUT"); }
        catch (XmlException) { return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: DavResponseInvalid); }
        finally
        {
            if (!probe.MutationMayStillBeRunning && !cleanupAttempted && !token.IsCancellationRequested)
                await probe.CleanupAsync(client, user, password, token);
        }
    }

    private sealed class NoReplaceProbe
    {
        private Uri Source { get; }
        private Uri Replacement { get; }
        private Uri Target { get; }
        private byte[] First { get; } = Encoding.UTF8.GetBytes("ShutterSpool WebDAV no-replace probe v1");
        private byte[] Second { get; } = Encoding.UTF8.GetBytes("ShutterSpool WebDAV occupied probe v1");
        public bool MutationMayStillBeRunning { get; private set; }

        public NoReplaceProbe(Uri root)
        {
            string id = Guid.NewGuid().ToString("N");
            Source = WebDavDestinationUris.Child(root, [WebDavDestinationUris.StagingCollection, $"probe-{id}-source"]);
            Replacement = WebDavDestinationUris.Child(root, [WebDavDestinationUris.StagingCollection, $"probe-{id}-replacement"]);
            Target = WebDavDestinationUris.Child(root, [$".shutterspool-probe-{id}.bin"]);
        }

        public async Task<DeliveryResult?> VerifyAsync(HttpClient client, string user, string password, CancellationToken token) =>
            await UploadAsync(client, Source, First, user, password, token)
            ?? await UploadAsync(client, Replacement, Second, user, password, token)
            ?? await MoveIntoEmptyTargetAsync(client, user, password, token)
            ?? await MoveOntoOccupiedTargetAsync(client, user, password, token);

        private async Task<DeliveryResult?> UploadAsync(HttpClient client, Uri resource, byte[] content, string user,
            string password, CancellationToken token)
        {
            using var put = Request(HttpMethod.Put, resource, user, password);
            put.Content = new ByteArrayContent(content);
            MutationMayStillBeRunning = true;
            using HttpResponseMessage response = await client.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, token);
            if (!IsCompletedWriteStatus(response.StatusCode)) return ResultFor(response.StatusCode, mayHavePublished: false);
            MutationMayStillBeRunning = false;
            return null;
        }

        private async Task<DeliveryResult?> MoveIntoEmptyTargetAsync(HttpClient client, string user, string password, CancellationToken token)
        {
            using (var move = NoReplaceMove(Source, Target, user, password))
            {
                MutationMayStillBeRunning = true;
                using HttpResponseMessage response = await client.SendAsync(move, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
                {
                    MutationMayStillBeRunning = false;
                    return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: UnsupportedPublication);
                }
                if (!IsCompletedWriteStatus(response.StatusCode))
                {
                    if (response.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
                        MutationMayStillBeRunning = false;
                    return ResultFor(response.StatusCode, mayHavePublished: true);
                }
                MutationMayStillBeRunning = false;
            }
            return await MatchesAsync(client, Target, user, password, First, token)
                ? null : new(DeliveryResultKind.ConfigurationFailure, ErrorCode: UnsupportedPublication);
        }

        private async Task<DeliveryResult?> MoveOntoOccupiedTargetAsync(HttpClient client, string user, string password, CancellationToken token)
        {
            using (var move = NoReplaceMove(Replacement, Target, user, password))
            {
                MutationMayStillBeRunning = true;
                using HttpResponseMessage response = await client.SendAsync(move, HttpCompletionOption.ResponseHeadersRead, token);
                if (IsCompletedWriteStatus(response.StatusCode))
                {
                    MutationMayStillBeRunning = false;
                    return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: UnsupportedPublication);
                }
                if (response.StatusCode is not (HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict))
                    return ResultFor(response.StatusCode, mayHavePublished: true);
                MutationMayStillBeRunning = false;
            }
            if (!await MatchesAsync(client, Target, user, password, First, token)
                || !await MatchesAsync(client, Replacement, user, password, Second, token))
                return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: UnsupportedPublication);
            return null;
        }

        public async Task<bool> CleanupAsync(HttpClient client, string user, string password, CancellationToken token)
        {
            bool targetCleaned = await CleanupOneProbeAsync(client, Target, user, password, [First, Second], token);
            bool sourceCleaned = await CleanupOneProbeAsync(client, Source, user, password, [First], token);
            bool replacementCleaned = await CleanupOneProbeAsync(client, Replacement, user, password, [Second], token);
            return targetCleaned && sourceCleaned && replacementCleaned;
        }
    }

    private static async Task MakeCollectionAsync(HttpClient client, Uri collection, string user, string password, CancellationToken token)
    {
        using var request = Request(new HttpMethod("MKCOL"), collection, user, password);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode is HttpStatusCode.Created) return;
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
        {
            DeliveryResult? result = await VerifyCollectionAsync(client, collection, user, password, token);
            if (result is null) return;
            if (result.Kind == DeliveryResultKind.TransientFailure)
                throw new HttpRequestException("DAV collection check failed.", null, HttpStatusCode.ServiceUnavailable);
        }
        throw new HttpRequestException("DAV collection creation failed.", null, response.StatusCode);
    }

    private static async Task<DeliveryResult?> VerifyCollectionAsync(HttpClient client, Uri uri, string user, string password, CancellationToken token)
    {
        using var request = Request(new HttpMethod("PROPFIND"), uri, user, password);
        request.Headers.TryAddWithoutValidation("Depth", "0");
        request.Content = new StringContent("<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.MultiStatus) return ResultFor(response.StatusCode, mayHavePublished: false);
        XDocument xml = await ReadXmlAsync(response, token);
        XNamespace dav = "DAV:";
        string expectedHref = uri.AbsolutePath.TrimEnd('/');
        bool found = xml.Root?.Name == dav + "multistatus" && xml.Root.Elements(dav + "response").Any(item =>
            string.Equals(item.Element(dav + "href")?.Value.TrimEnd('/'), expectedHref, StringComparison.Ordinal)
            && item.Elements(dav + "propstat").Any(stat => ((string?)stat.Element(dav + "status"))?.Contains(" 200 ", StringComparison.Ordinal) == true
                && stat.Element(dav + "prop")?.Element(dav + "resourcetype")?.Elements(dav + "collection").Any() == true));
        return found ? null : new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "COLLECTION_NOT_AVAILABLE");
    }

    private static async Task<DeliveryResult?> VerifyTargetAsync(HttpClient client, Uri target, string user, string password,
        DeliveryRequest request, bool mayHavePublished = false, CancellationToken token = default)
    {
        ResourceMetadata metadata = await InspectResourceAsync(client, target, user, password, request, token);
        if (metadata.Kind == ResourceMetadataKind.Missing) return null;
        if (metadata.Kind == ResourceMetadataKind.Conflict)
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: TargetConflict);
        if (metadata.Kind == ResourceMetadataKind.Receipt)
            return new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.ProviderReceipt);
        if (metadata.Kind == ResourceMetadataKind.Uncertain)
        {
            if (metadata.Status is { } status) return ResultFor(status, mayHavePublished);
            if (metadata.ErrorCode == TlsValidationFailed)
                return mayHavePublished
                    ? new(DeliveryResultKind.Uncertain, ErrorCode: metadata.ErrorCode)
                    : new(DeliveryResultKind.ConfigurationFailure, ErrorCode: metadata.ErrorCode);
            if (!mayHavePublished && metadata.ErrorCode == ProviderUnavailable)
                return new(DeliveryResultKind.TransientFailure, ErrorCode: ProviderUnavailable);
            return new(DeliveryResultKind.Uncertain, ErrorCode: metadata.ErrorCode ?? "READ_BACK_UNKNOWN");
        }

        return await ReadBackTargetAsync(client, target, user, password, request, mayHavePublished, token);
    }

    private static async Task<DeliveryResult?> ReadBackTargetAsync(HttpClient client, Uri target, string user, string password,
        DeliveryRequest request, bool mayHavePublished, CancellationToken token)
    {
        using var get = Request(HttpMethod.Get, target, user, password);
        using HttpResponseMessage response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null)
        {
            if (mayHavePublished) return new(DeliveryResultKind.Uncertain, ErrorCode: "READ_BACK_UNKNOWN");
            DeliveryResult failure = ResultFor(response.StatusCode, mayHavePublished: false);
            return failure.Kind == DeliveryResultKind.TransientFailure ? failure
                : new(DeliveryResultKind.ConfigurationFailure, ErrorCode: TargetConflict);
        }
        if (response.Content.Headers.ContentLength is long length && length != request.Size)
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: TargetConflict);
        using Stream input = await response.Content.ReadAsStreamAsync(token);
        byte[] expected;
        try { expected = Convert.FromHexString(request.Sha256); }
        catch (FormatException) { return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "INVALID_EXPECTED_HASH"); }
        return await MatchesContentAsync(input, request.Size, expected, token)
            ? new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.ReadBackHash)
            : new(DeliveryResultKind.ConfigurationFailure, ErrorCode: TargetConflict);
    }

    private static async Task<ResourceMetadata> InspectResourceAsync(HttpClient client, Uri target, string user, string password,
        DeliveryRequest request, CancellationToken token)
    {
        using var propfind = Request(new HttpMethod("PROPFIND"), target, user, password);
        propfind.Headers.TryAddWithoutValidation("Depth", "0");
        propfind.Content = new StringContent(
            "<d:propfind xmlns:d=\"DAV:\" xmlns:oc=\"http://owncloud.org/ns\"><d:prop><d:resourcetype/><d:getcontentlength/><oc:checksums/></d:prop></d:propfind>",
            Encoding.UTF8, "application/xml");
        HttpResponseMessage response;
        try { response = await client.SendAsync(propfind, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException exception)
        {
            string errorCode = exception.HttpRequestError == HttpRequestError.SecureConnectionError
                ? TlsValidationFailed : ProviderUnavailable;
            return new(ResourceMetadataKind.Uncertain, errorCode, exception.StatusCode);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) return new(ResourceMetadataKind.Missing);
            if (response.StatusCode != HttpStatusCode.MultiStatus)
                return new(ResourceMetadataKind.Uncertain, Status: response.StatusCode);
            try { return ParseResourceMetadata(await ReadXmlAsync(response, token), target, request); }
            catch (XmlException) { return new(ResourceMetadataKind.Uncertain, DavResponseInvalid); }
            catch (IOException) { return new(ResourceMetadataKind.Uncertain, ProviderUnavailable); }
            catch (FormatException) { return new(ResourceMetadataKind.Uncertain, "INVALID_EXPECTED_HASH"); }
        }
    }

    private static ResourceMetadata ParseResourceMetadata(XDocument xml, Uri target, DeliveryRequest request)
    {
        XNamespace dav = "DAV:";
        if (xml.Root?.Name != dav + "multistatus") return new(ResourceMetadataKind.Uncertain, DavResponseInvalid);
        XElement[] matching = [.. xml.Root.Elements(dav + "response").Where(item => IsTargetHref((string?)item.Element(dav + "href"), target))];
        if (matching.Length != 1) return new(ResourceMetadataKind.Uncertain);
        XElement resource = matching[0];
        XElement[] propstats = [.. resource.Elements(dav + "propstat")];
        if (resource.Element(dav + "status") is { } responseStatus)
        {
            if (!TryStatusCode(responseStatus.Value, out int status)) return new(ResourceMetadataKind.Uncertain, DavResponseInvalid);
            if (status == 404) return propstats.Length == 0
                ? new(ResourceMetadataKind.Missing) : new(ResourceMetadataKind.Uncertain);
            if (status is < 200 or >= 300) return new(ResourceMetadataKind.Uncertain, Status: (HttpStatusCode)status);
        }
        return ReadResourceProperties(propstats, request);
    }

    private static ResourceMetadata ReadResourceProperties(XElement[] propstats, DeliveryRequest request)
    {
        XNamespace dav = "DAV:";
        XNamespace ownCloud = "http://owncloud.org/ns";
        List<XElement> available = [];
        List<XElement> missing = [];
        foreach (XElement propstat in propstats)
        {
            XElement? prop = propstat.Element(dav + "prop");
            if (!TryStatusCode((string?)propstat.Element(dav + "status"), out int status) || prop is null)
                return new(ResourceMetadataKind.Uncertain, DavResponseInvalid);
            if (status is >= 200 and < 300) available.Add(prop);
            else if (status == 404) missing.Add(prop);
            else if (status >= 500 || status is 401 or 403
                || prop.Elements().Any(item => item.Name == dav + "resourcetype" || item.Name == dav + "getcontentlength"
                    || item.Name == ownCloud + "checksums"))
                return new(ResourceMetadataKind.Uncertain, Status: (HttpStatusCode)status);
        }
        return ClassifyResourceProperties(available, missing, request);
    }

    private static ResourceMetadata ClassifyResourceProperties(List<XElement> available, List<XElement> missing, DeliveryRequest request)
    {
        XNamespace dav = "DAV:";
        XNamespace ownCloud = "http://owncloud.org/ns";
        XElement[] types = [.. available.Elements(dav + "resourcetype")];
        XElement[] sizes = [.. available.Elements(dav + "getcontentlength")];
        XElement[] checksums = [.. available.Elements(ownCloud + "checksums")];
        if ((sizes.Length > 0 && missing.Elements(dav + "getcontentlength").Any())
            || (checksums.Length > 0 && missing.Elements(ownCloud + "checksums").Any()))
            return new(ResourceMetadataKind.Uncertain);
        if (types.Length != 1 || missing.Elements(dav + "resourcetype").Any()) return new(ResourceMetadataKind.Uncertain);
        if (types[0].Elements(dav + "collection").Any()) return new(ResourceMetadataKind.Conflict);
        if (types[0].Elements().Any()) return new(ResourceMetadataKind.Uncertain);

        int validSizes = 0;
        foreach (XElement size in sizes)
        {
            if (!long.TryParse(size.Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long length)) continue;
            if (length != request.Size) return new(ResourceMetadataKind.Conflict);
            validSizes++;
        }
        if (sizes.Length != 1 || validSizes != 1 || checksums.Length != 1) return new(ResourceMetadataKind.Fallback);
        return ClassifyChecksum(checksums[0], Convert.FromHexString(request.Sha256));
    }

    private static ResourceMetadata ClassifyChecksum(XElement checksums, byte[] expectedHash)
    {
        XNamespace ownCloud = "http://owncloud.org/ns";
        string[] values = [.. checksums.Elements(ownCloud + "checksum").Select(item => item.Value.Trim()).Where(value => value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))];
        byte[]? matchingHash = null;
        foreach (string value in values)
        {
            if (value.Length != 71) continue;
            byte[] storedHash;
            try { storedHash = Convert.FromHexString(value[7..]); }
            catch (FormatException) { continue; }
            if (!CryptographicOperations.FixedTimeEquals(storedHash, expectedHash))
                return new(ResourceMetadataKind.Conflict);
            matchingHash = storedHash;
        }
        return values.Length != 1 || matchingHash is null
            ? new(ResourceMetadataKind.Fallback)
            : new(ResourceMetadataKind.Receipt);
    }

    private static bool IsTargetHref(string? href, Uri target)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Any(char.IsControl) || href.Contains('\\')
            || href.Contains('?') || href.Contains('#')) return false;
        Uri? resolved;
        string rawPath;
        if (href.StartsWith('/'))
        {
            if (href.StartsWith("//", StringComparison.Ordinal) || !Uri.TryCreate(target, href, out resolved)) return false;
            rawPath = href;
        }
        else
        {
            if (!Uri.TryCreate(href, UriKind.Absolute, out resolved)) return false;
            int authority = href.IndexOf("://", StringComparison.Ordinal);
            if (authority < 0) return false;
            int pathStart = href.IndexOf('/', authority + 3);
            rawPath = pathStart < 0 ? "/" : href[pathStart..];
        }
        string targetOrigin = target.GetLeftPart(UriPartial.Authority);
        if (!string.Equals(resolved.GetLeftPart(UriPartial.Authority), targetOrigin, StringComparison.OrdinalIgnoreCase)) return false;
        string targetPath = "/" + target.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        return EscapedPathEquals(rawPath, targetPath);
    }

    private static bool EscapedPathEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i]) return false;
            if (left[i] != '%') continue;
            if (i + 2 >= left.Length || !HexDigitsEqual(left[i + 1], right[i + 1]) || !HexDigitsEqual(left[i + 2], right[i + 2]))
                return false;
            i += 2;
        }
        return true;
    }

    private static bool HexDigitsEqual(char left, char right)
    {
        int digit = HexValue(left);
        return digit >= 0 && digit == HexValue(right);
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1
    };

    private static bool TryStatusCode(string? value, out int status)
    {
        status = 0;
        string[] parts = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts.Length < 2 || !IsHttpVersion(parts[0]) || parts[1].Length != 3
            || parts[1].Any(digit => digit is < '0' or > '9')
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out status)) return false;
        return status is >= 100 and <= 599;
    }

    private static bool IsHttpVersion(string value) => value.Length == 8
        && value.StartsWith("HTTP/", StringComparison.Ordinal)
        && value[5] is >= '0' and <= '9' && value[6] == '.' && value[7] is >= '0' and <= '9';

    private enum ResourceMetadataKind { Missing, Receipt, Conflict, Fallback, Uncertain }
    private readonly record struct ResourceMetadata(ResourceMetadataKind Kind, string? ErrorCode = null, HttpStatusCode? Status = null);

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        using var bounded = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (bounded.Length + read > MaxXmlBytes) throw new XmlException("DAV response is too large.");
            await bounded.WriteAsync(buffer.AsMemory(0, read), token);
        }
        bounded.Position = 0;
        using XmlReader reader = XmlReader.Create(bounded, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes });
        return XDocument.Load(reader);
    }

    private static async Task<bool> MatchesAsync(HttpClient client, Uri uri, string user, string password, byte[] expected,
        CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, uri, user, password);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null) return false;
        if (response.Content.Headers.ContentLength is long length && length != expected.Length) return false;
        await using Stream body = await response.Content.ReadAsStreamAsync(token);
        return await MatchesContentAsync(body, expected.Length, SHA256.HashData(expected), token);
    }

    private static async Task<bool> MatchesContentAsync(Stream body, long expectedSize, byte[] expectedHash,
        CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        long count = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, token)) != 0)
        {
            if (count > expectedSize - read) return false;
            count += read;
            hash.AppendData(buffer, 0, read);
        }
        byte[] actual = hash.GetHashAndReset();
        return count == expectedSize && expectedHash.Length == actual.Length
            && CryptographicOperations.FixedTimeEquals(actual, expectedHash);
    }

    private static async Task<bool> DeleteOwnedAsync(HttpClient client, Uri uri, string user, string password,
        System.Net.Http.Headers.EntityTagHeaderValue etag,
        CancellationToken token)
    {
        using var request = Request(HttpMethod.Delete, uri, user, password);
        request.Headers.IfMatch.Add(etag);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent or HttpStatusCode.NotFound;
    }

    private static async Task<bool> CleanupOneProbeAsync(HttpClient client, Uri uri, string user, string password,
        byte[][] ownedContent, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, uri, user, password);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return true;
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null) return false;
        System.Net.Http.Headers.EntityTagHeaderValue? etag = response.Headers.ETag;
        if (etag is null || etag.IsWeak) return false;
        if (response.Content.Headers.ContentLength is long length && length > ownedContent.Max(content => content.Length)) return false;
        await using Stream body = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream(ownedContent.Max(content => content.Length) + 1);
        byte[] buffer = new byte[BufferSize];
        int read;
        while ((read = await body.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > ownedContent.Max(content => content.Length)) return false;
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        byte[] actual = output.ToArray();
        if (!ownedContent.Any(content => content.AsSpan().SequenceEqual(actual))) return false;
        return await DeleteOwnedAsync(client, uri, user, password, etag, token);
    }

    private static bool ValidExpectedContent(DeliveryRequest request)
    {
        if (request.Size < 0 || request.Sha256 is null || request.Sha256.Length != 64) return false;
        try { return Convert.FromHexString(request.Sha256).Length == 32; }
        catch (FormatException) { return false; }
    }

    private static bool IsCompletedWriteStatus(HttpStatusCode status) =>
        status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.NoContent;

    private static bool MatchesProviderHash(HttpResponseMessage response, string expectedHash)
    {
        if (!response.Headers.TryGetValues("X-Hash-SHA256", out IEnumerable<string>? values)) return true;
        string[] hashes = [.. values];
        if (hashes.Length != 1 || hashes[0].Length != 64) return false;
        try
        {
            byte[] actual = Convert.FromHexString(hashes[0]);
            byte[] expected = Convert.FromHexString(expectedHash);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static bool TryConfig(DeliveryRequest request, [NotNullWhen(true)] out Uri? root, [NotNullWhen(true)] out string? user, [NotNullWhen(true)] out string? password,
        [NotNullWhen(true)] out string[]? segments, out string? error)
    {
        root = null; user = null; password = null; segments = null; error = "WEBDAV_NOT_CONFIGURED";
        if (request.Configuration is not WebDavDestinationDeliveryConfiguration config || config.CollectionUrl is null
            || config.Username is null || config.Password is null) return false;
        if (!WebDavDestinationUris.TryCollection(config.CollectionUrl, out root)
            || string.IsNullOrWhiteSpace(config.Username) || config.Username.Length > 256 || config.Username.Contains(':')
            || config.Username.Any(char.IsControl) || config.Password.Length is 0 or > 4_096
            || !WebDavDestinationUris.TryTarget(request.TargetPath, out segments))
        { error = "INVALID_TARGET_OR_COLLECTION"; return false; }
        user = config.Username; password = config.Password; error = null; return true;
    }

    private static CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        return deadline;
    }

    private static DeliveryResult ResultFor(HttpStatusCode status, bool mayHavePublished)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest or HttpStatusCode.NotImplemented)
            return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: status == HttpStatusCode.Unauthorized ? "AUTHENTICATION_FAILED" : "DAV_OPERATION_REJECTED");
        if (mayHavePublished) return new(DeliveryResultKind.Uncertain, ErrorCode: "PROVIDER_OUTCOME_UNKNOWN");
        if ((int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            return new(DeliveryResultKind.TransientFailure, ErrorCode: ProviderUnavailable);
        return new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "DAV_OPERATION_REJECTED");
    }

    private sealed class HashingReadStream(Stream source, long expectedSize, string expectedHash) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _size;
        private bool _finished;
        public bool Matches { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                if (!_finished)
                {
                    _finished = true;
                    byte[] expected;
                    try { expected = Convert.FromHexString(expectedHash); }
                    catch (FormatException) { expected = []; }
                    byte[] actual = _hash.GetHashAndReset();
                    Matches = _size == expectedSize && expected.Length == actual.Length
                        && CryptographicOperations.FixedTimeEquals(actual, expected);
                }
                return 0;
            }
            if (_size > expectedSize - read) { _size += read; return read; }
            _size += read;
            _hash.AppendData(buffer.Span[..read]);
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync() { _hash.Dispose(); return ValueTask.CompletedTask; }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
