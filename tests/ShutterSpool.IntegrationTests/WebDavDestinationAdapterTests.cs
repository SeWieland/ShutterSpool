using ShutterSpool.Application.Delivery;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Destinations.WebDav;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ShutterSpool.IntegrationTests;

public sealed class WebDavDestinationAdapterTests
{
    [Theory]
    [InlineData("https://2130706433/camera/")]
    [InlineData("https://0177.0.0.1/camera/")]
    [InlineData("https://0x7f000001/camera/")]
    [InlineData("https://intranet/camera/")]
    [InlineData("https://dav.example.test./camera/")]
    [InlineData("https://[fe80::1%251]/camera/")]
    public void Collection_urls_reject_ambiguous_or_scoped_hosts(string value)
    {
        Assert.False(WebDavDestinationUris.TryCollection(value, out _));
    }

    public enum FakeChecksumMode
    {
        Default,
        MultipleAlgorithms,
        FailedResourcePropstat,
        Malformed,
        DuplicateMatch,
        DuplicateConflict,
        MisleadingOtherHref,
        FailedChecksumPropstat,
        MixedFailedChecksumPropstat,
        ForeignNestedChecksum
    }

    [Theory]
    [InlineData("http://dav.example.test/collection/")]
    [InlineData("https://user:secret@dav.example.test/collection/")]
    [InlineData("https://dav.example.test/collection/?redirect=elsewhere")]
    [InlineData("https://dav.example.test/collection/#fragment")]
    [InlineData("https://dav.example.test/collection/%2fprivate/")]
    [InlineData("https://dav.example.test/collection/%2e%2e/private/")]
    [InlineData("https://dav.example.test/a\\..\\b/")]
    public void Collection_urls_reject_non_https_and_ambiguous_authority_or_path(string value)
    {
        Assert.False(WebDavDestinationUris.TryCollection(value, out _));
    }

    [Theory]
    [InlineData("../outside.raw")]
    [InlineData("folder/%2e%2e/outside.raw")]
    [InlineData("folder%2foutside.raw")]
    [InlineData(".shutterspool-staging/owned.raw")]
    [InlineData("folder\\outside.raw")]
    [InlineData("C:/outside.raw")]
    [InlineData(".SHUTTERSPOOL-STAGING/owned.raw")]
    public void Recorded_targets_reject_traversal_and_reserved_staging(string value)
    {
        Assert.False(WebDavDestinationUris.TryTarget(value, out _));
    }

    [Fact]
    public void Target_encoding_preserves_literal_percent_unicode_and_spaces_as_single_segments()
    {
        Assert.True(WebDavDestinationUris.TryCollection("https://dav.example.test/remote.php/dav/files/camera/", out Uri? collection));
        Assert.True(WebDavDestinationUris.TryTarget("Cards 100%/日本語.raw", out string[] segments));

        Uri target = WebDavDestinationUris.Child(collection!, segments);

        Assert.EndsWith("Cards%20100%25/%E6%97%A5%E6%9C%AC%E8%AA%9E.raw", target.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delivery_streams_to_staging_then_no_replace_moves_without_reading_back_the_final_file()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic camera original");
        var handler = new FakeDavHandler();
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));

        DeliveryResult result = await adapter.WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Confirmed, result.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite, result.Evidence);
        Assert.Equal(bytes, handler.Resources["/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw"]);
        Assert.All(handler.Operations.Where(item => item.Method == "PUT"), item => Assert.Contains(".shutterspool-staging", item.Path));
        Assert.All(handler.Operations.Where(item => item.Method == "MOVE"), item => Assert.Equal("F", item.Overwrite));
        Assert.DoesNotContain(handler.Operations, item => item.Method == "GET" && item.Path == "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw");
        Assert.All(handler.PropfindDepths, depth => Assert.Equal("0", depth));
        Assert.Contains(handler.PropfindBodies, body => body.Contains("<d:resourcetype/>", StringComparison.Ordinal)
            && body.Contains("<d:getcontentlength/>", StringComparison.Ordinal)
            && body.Contains("<oc:checksums/>", StringComparison.Ordinal));
        Assert.Contains(handler.MediaPutHeaders, item => item.Hash == "sha256"
            && item.Checksum == $"SHA256:{Convert.ToHexString(SHA256.HashData(bytes))}" && item.ContentLength == bytes.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("valid")]
    public async Task Completed_put_acknowledges_without_final_get_and_accepts_only_matching_optional_hash(string? hashMode)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        string? responseHash = hashMode == "valid" ? Convert.ToHexString(SHA256.HashData(bytes)) : null;
        var handler = new FakeDavHandler { MediaPutHashSha256 = responseHash };

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Confirmed, result.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite, result.Evidence);
        Assert.DoesNotContain(handler.Operations, item => item.Method == "GET" && item.Path == "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw");
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Invalid_or_mismatched_provider_hash_fails_before_publication(string responseHash)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { MediaPutHashSha256 = responseHash };

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("PROVIDER_HASH_MISMATCH", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method == "MOVE" && !item.Path.Contains("probe-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accepted_put_is_not_treated_as_a_completed_upload()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { MediaPutStatus = HttpStatusCode.Accepted };

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.TransientFailure, result.Kind);
        Assert.Equal("PROVIDER_UNAVAILABLE", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method == "MOVE" && !item.Path.Contains("probe-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Accepted_move_is_not_treated_as_a_completed_publication()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { MediaMoveStatus = HttpStatusCode.Accepted };

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.Equal("PROVIDER_OUTCOME_UNKNOWN", result.ErrorCode);
        Assert.Equal(bytes, handler.Resources[target]);
        Assert.DoesNotContain(handler.Operations, item => item.Method == "GET" && item.Path == target);
    }

    [Fact]
    public async Task Ignored_no_overwrite_blocks_delivery_before_it_touches_the_recorded_target()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { IgnoreNoReplace = true };
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));

        DeliveryResult result = await adapter.WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("UNSUPPORTED_PUBLICATION", result.ErrorCode);
        Assert.DoesNotContain("/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw", handler.Resources.Keys);
    }

    [Fact]
    public async Task Lost_move_response_is_ambiguous_then_provider_receipt_confirms_without_another_write()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler
        {
            LoseMoveResponseTarget = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw",
            ChecksumMode = FakeChecksumMode.MultipleAlgorithms
        };
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));
        DeliveryRequest request = Request(bytes);

        DeliveryResult write = await adapter.WriteAsync(request, new MemoryStream(bytes), CancellationToken.None);
        int moves = handler.Operations.Count(item => item.Method == "MOVE");
        DeliveryReconciliation recovered = await adapter.ReconcileAsync(request, CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, write.Kind);
        Assert.Equal(DeliveryReconciliationKind.Confirmed, recovered.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.ProviderReceipt, recovered.Evidence);
        Assert.Equal(moves, handler.Operations.Count(item => item.Method == "MOVE"));
        Assert.DoesNotContain(handler.Operations, item => item.Method == "GET" && item.Path == "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw");
    }

    [Fact]
    public async Task Missing_provider_checksum_falls_back_to_bounded_content_read()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { OmitChecksums = true };
        handler.Resources[target] = bytes;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Confirmed, result.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.ReadBackHash, result.Evidence);
        Assert.Equal(bytes, handler.Resources[target]);
        Assert.Single(handler.Operations, item => item.Method == "GET" && item.Path == target);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE");
    }

    [Fact]
    public async Task Metadata_transport_failure_retries_prepublication_and_stays_uncertain_during_reconciliation()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { Intercept = request => Task.FromResult<HttpResponseMessage?>(
            request.Method.Method == "PROPFIND" && request.RequestUri!.AbsolutePath == target
                ? new(HttpStatusCode.ServiceUnavailable) : null) };
        handler.Resources[target] = bytes;

        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));
        DeliveryRequest deliveryRequest = Request(bytes);
        DeliveryResult result = await adapter.WriteAsync(deliveryRequest, new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.TransientFailure, result.Kind);
        Assert.Equal("PROVIDER_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(DeliveryReconciliationKind.Uncertain,
            (await adapter.ReconcileAsync(deliveryRequest, CancellationToken.None)).Kind);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Fact]
    public async Task Invalid_metadata_xml_stays_uncertain_without_get_or_writes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { Intercept = request => Task.FromResult<HttpResponseMessage?>(
            request.Method.Method == "PROPFIND" && request.RequestUri!.AbsolutePath == target
                ? new(HttpStatusCode.MultiStatus) { Content = new StringContent("<d:multistatus>", Encoding.UTF8, "application/xml") } : null) };
        handler.Resources[target] = bytes;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.Equal("DAV_RESPONSE_INVALID", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Fact]
    public async Task Failed_resource_property_status_retries_before_write_and_stays_uncertain_during_reconciliation()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { ChecksumMode = FakeChecksumMode.FailedResourcePropstat };
        handler.Resources[target] = bytes;
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));
        DeliveryRequest request = Request(bytes);

        DeliveryResult result = await adapter.WriteAsync(request, new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.TransientFailure, result.Kind);
        Assert.Equal("PROVIDER_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(DeliveryReconciliationKind.Uncertain, (await adapter.ReconcileAsync(request, CancellationToken.None)).Kind);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Fact]
    public async Task Contradictory_success_and_failure_for_checksum_property_stays_uncertain()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { ChecksumMode = FakeChecksumMode.MixedFailedChecksumPropstat };
        handler.Resources[target] = bytes;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Fact]
    public async Task Mismatching_provider_checksum_conflicts_without_reading_or_replacing_target()
    {
        byte[] expected = Encoding.UTF8.GetBytes("synthetic original");
        byte[] occupied = Encoding.UTF8.GetBytes("unrelated content!");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler();
        handler.Resources[target] = occupied;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(expected), new MemoryStream(expected), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("TARGET_CONFLICT", result.ErrorCode);
        Assert.Equal(occupied, handler.Resources[target]);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE" or "DELETE");
    }

    [Theory]
    [InlineData(FakeChecksumMode.Malformed)]
    [InlineData(FakeChecksumMode.DuplicateMatch)]
    [InlineData(FakeChecksumMode.DuplicateConflict)]
    [InlineData(FakeChecksumMode.MisleadingOtherHref)]
    [InlineData(FakeChecksumMode.FailedChecksumPropstat)]
    [InlineData(FakeChecksumMode.ForeignNestedChecksum)]
    public async Task Unusable_or_misleading_checksum_never_false_confirms(FakeChecksumMode mode)
    {
        byte[] expected = Encoding.UTF8.GetBytes("synthetic original");
        byte[] occupied = Encoding.UTF8.GetBytes("unrelated content!");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { ChecksumMode = mode,
            MisleadingChecksum = $"SHA256:{Convert.ToHexString(SHA256.HashData(expected))}" };
        handler.Resources[target] = mode == FakeChecksumMode.DuplicateMatch ? expected : occupied;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(expected), new MemoryStream(expected), CancellationToken.None);

        Assert.Equal(mode == FakeChecksumMode.DuplicateMatch ? DeliveryResultKind.Confirmed : DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal(mode == FakeChecksumMode.DuplicateMatch ? DeliveryConfirmationEvidenceLevel.ReadBackHash : null, result.Evidence);
        Assert.Equal(mode == FakeChecksumMode.DuplicateMatch ? null : "TARGET_CONFLICT", result.ErrorCode);
        Assert.Equal(mode == FakeChecksumMode.DuplicateMatch ? expected : occupied, handler.Resources[target]);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE" or "DELETE");
        if (mode is FakeChecksumMode.Malformed or FakeChecksumMode.DuplicateMatch or FakeChecksumMode.MisleadingOtherHref
            or FakeChecksumMode.FailedChecksumPropstat or FakeChecksumMode.ForeignNestedChecksum)
            Assert.Contains(handler.Operations, item => item.Method == "GET" && item.Path == target);
        else Assert.DoesNotContain(handler.Operations, item => item.Method == "GET" && item.Path == target);
    }

    [Theory]
    [InlineData("/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw.backup")]
    [InlineData("https://elsewhere.example.test/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw")]
    public async Task Matching_checksum_on_a_different_href_cannot_confirm_the_requested_target(string href)
    {
        byte[] expected = Encoding.UTF8.GetBytes("synthetic original");
        byte[] occupied = Encoding.UTF8.GetBytes("unrelated existing content");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler
        {
            OmitChecksums = true,
            TargetHrefOverride = href,
            MisleadingChecksum = $"SHA256:{Convert.ToHexString(SHA256.HashData(expected))}"
        };
        handler.Resources[target] = occupied;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(expected), new MemoryStream(expected), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.Equal("READ_BACK_UNKNOWN", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Fact]
    public async Task Matching_checksum_accepts_lowercase_percent_escape_hex_in_target_href()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/nested/space%20%C3%BC/100%25%20synthetic.ARW";
        var handler = new FakeDavHandler
        {
            TargetHrefOverride = "/camera/nested/space%20%c3%bc/100%25%20synthetic.ARW"
        };
        handler.Resources[target] = bytes;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes, "nested/space ü/100% synthetic.ARW"), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Confirmed, result.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.ProviderReceipt, result.Evidence);
        Assert.DoesNotContain(handler.Operations, item => item.Method == "GET" && item.Path == target);
    }

    [Theory]
    [InlineData("/camera/nested/space%20%C3%BC/100%25%20synthetic.arw")]
    [InlineData("/camera/nested%2Fspace%20%C3%BC/100%25%20synthetic.ARW")]
    [InlineData("/camera/nested/space%20%C3%BC/../space%20%C3%BC/100%25%20synthetic.ARW")]
    public async Task Target_href_comparison_rejects_path_case_and_escaped_path_aliases(string href)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/nested/space%20%C3%BC/100%25%20synthetic.ARW";
        var handler = new FakeDavHandler { TargetHrefOverride = href };
        handler.Resources[target] = bytes;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes, "nested/space ü/100% synthetic.ARW"), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Theory]
    [InlineData("/camera/nested/%F0%Ag%98%9F.raw")]
    [InlineData("/camera/nested/%G0%9F%98%9F.raw")]
    public async Task Target_href_comparison_rejects_malformed_percent_escapes(string href)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/nested/%F0%9F%98%9F.raw";
        var handler = new FakeDavHandler { TargetHrefOverride = href };
        handler.Resources[target] = bytes;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes, "nested/😟.raw"), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Theory]
    [InlineData("HTTP/not-a-version 200 OK")]
    [InlineData("HTTP/1.1 000200 OK")]
    [InlineData("HTTP/1.1 0200 OK")]
    public async Task Malformed_resource_status_cannot_confirm_a_matching_provider_receipt(string status)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { ResourceStatusOverride = status };
        handler.Resources[target] = bytes;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Theory]
    [InlineData("HTTP/not-a-version 200 OK")]
    [InlineData("HTTP/1.1 000200 OK")]
    [InlineData("HTTP/1.1 0200 OK")]
    public async Task Malformed_propstat_status_cannot_confirm_a_matching_provider_receipt(string status)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { PropstatStatusOverride = status };
        handler.Resources[target] = bytes;

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "PUT" or "MOVE");
    }

    [Fact]
    public async Task Secure_connection_failure_during_target_metadata_check_is_not_downgraded_to_provider_outage()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler
        {
            Intercept = request => request.Method.Method == "PROPFIND" && request.RequestUri!.AbsolutePath == target
                ? Task.FromException<HttpResponseMessage?>(new HttpRequestException(HttpRequestError.SecureConnectionError, "synthetic TLS failure"))
                : Task.FromResult<HttpResponseMessage?>(null)
        };

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("TLS_VALIDATION_FAILED", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE");
    }

    [Fact]
    public async Task Redirect_is_rejected_without_contacting_the_redirect_target()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { RedirectPropfind = true };
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));

        DeliveryResult result = await adapter.WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Single(handler.Operations);
        Assert.Equal("dav.example.test", handler.Operations[0].Host);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "AUTHENTICATION_FAILED")]
    [InlineData(HttpStatusCode.Forbidden, "DAV_OPERATION_REJECTED")]
    [InlineData((HttpStatusCode)423, "DAV_OPERATION_REJECTED")]
    [InlineData((HttpStatusCode)507, "PROVIDER_UNAVAILABLE")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "DAV_OPERATION_REJECTED")]
    public async Task Rejected_staging_put_is_classified_before_publication(HttpStatusCode status, string error)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { Intercept = request => Task.FromResult<HttpResponseMessage?>(
            request.Method == HttpMethod.Put && !request.RequestUri!.AbsolutePath.Contains("probe-", StringComparison.Ordinal)
                ? new(status) { Content = new StringContent("private provider diagnostic must not escape") } : null) };
        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal((int)status >= 500 ? DeliveryResultKind.TransientFailure : DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal(error, result.ErrorCode);
        Assert.Null(result.ErrorMessage);
        Assert.DoesNotContain(handler.Operations, item => item.Method == "MOVE" && !item.Path.Contains("probe-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unsupported_move_cleans_only_finished_probe_resources_and_blocks_media_upload()
    {
        var handler = new FakeDavHandler { Intercept = request => Task.FromResult<HttpResponseMessage?>(
            request.Method.Method == "MOVE" ? new(HttpStatusCode.MethodNotAllowed) : null) };
        (bool writable, string? error) = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .TestAsync("https://dav.example.test/camera/", "operator", "synthetic-app-password", CancellationToken.None);

        Assert.False(writable);
        Assert.Equal("UNSUPPORTED_PUBLICATION", error);
        Assert.Empty(handler.Resources);
        Assert.Equal(2, handler.Operations.Count(item => item.Method == "DELETE"));
    }

    [Fact]
    public async Task Unknown_probe_move_response_retains_resources_without_cleanup()
    {
        var handler = new FakeDavHandler { Intercept = request => Task.FromResult<HttpResponseMessage?>(
            request.Method.Method == "MOVE" ? new(HttpStatusCode.InternalServerError) : null) };

        (bool writable, string? error) = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .TestAsync("https://dav.example.test/camera/", "operator", "synthetic-app-password", CancellationToken.None);

        Assert.False(writable);
        Assert.Equal("PROVIDER_OUTCOME_UNKNOWN", error);
        Assert.Equal(2, handler.Resources.Count);
        Assert.DoesNotContain(handler.Operations, item => item.Method == "DELETE");
        Assert.Single(handler.Operations, item => item.Method == "MOVE");
    }

    [Fact]
    public async Task Lost_probe_put_response_retains_resources_while_server_write_may_still_be_active()
    {
        FakeDavHandler handler = null!;
        handler = new FakeDavHandler { Intercept = async request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                handler.Resources[request.RequestUri!.AbsolutePath] = await request.Content!.ReadAsByteArrayAsync();
                throw new HttpRequestException("synthetic lost PUT response while server can still write");
            }
            return null;
        } };
        (bool writable, string? error) = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .TestAsync("https://dav.example.test/camera/", "operator", "synthetic-app-password", CancellationToken.None);

        Assert.False(writable);
        Assert.Equal("PROVIDER_UNAVAILABLE", error);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "DELETE" or "MOVE" or "GET");
        Assert.Single(handler.Resources);
    }

    [Fact]
    public async Task Lost_media_put_never_publishes_and_a_missing_final_target_remains_ambiguous()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { Intercept = request =>
        {
            if (request.Method == HttpMethod.Put && !request.RequestUri!.AbsolutePath.Contains("probe-", StringComparison.Ordinal))
                throw new HttpRequestException("synthetic lost PUT response");
            return Task.FromResult<HttpResponseMessage?>(null);
        } };
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));
        DeliveryRequest request = Request(bytes);
        DeliveryResult result = await adapter.WriteAsync(request, new MemoryStream(bytes), CancellationToken.None);
        int requests = handler.Operations.Count;
        DeliveryReconciliation reconciliation = await adapter.ReconcileAsync(request, CancellationToken.None);

        Assert.Equal(DeliveryResultKind.TransientFailure, result.Kind);
        Assert.Equal(DeliveryReconciliationKind.Uncertain, reconciliation.Kind);
        Assert.Equal("ABSENCE_NOT_AUTHORITATIVE", reconciliation.ErrorCode);
        Assert.Single(handler.Operations.Skip(requests));
        Assert.DoesNotContain(handler.Operations, item => item.Method == "MOVE" && !item.Path.Contains("probe-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_occupied_target_is_preserved_without_any_write()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { OmitChecksums = true, Intercept = request => Task.FromResult<HttpResponseMessage?>(
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == target ? new(HttpStatusCode.Forbidden) : null) };
        handler.Resources[target] = bytes;
        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal("TARGET_CONFLICT", result.ErrorCode);
        Assert.Equal(bytes, handler.Resources[target]);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE" or "DELETE");
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Unavailable_fallback_get_retries_without_writing_and_keeps_reconciliation_uncertain(HttpStatusCode status)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { OmitChecksums = true, Intercept = request => Task.FromResult<HttpResponseMessage?>(
            request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == target
                ? new(status) : null) };
        handler.Resources[target] = bytes;
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));
        DeliveryRequest request = Request(bytes);
        DeliveryResult result = await adapter.WriteAsync(request, new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.TransientFailure, result.Kind);
        Assert.Equal("PROVIDER_UNAVAILABLE", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE");
        Assert.Equal(DeliveryState.RetryWait, DeliveryPolicy.AfterWrite(result, true, true, 1, DateTimeOffset.UtcNow, 0).State);
        DeliveryReconciliation reconciliation = await adapter.ReconcileAsync(request, CancellationToken.None);
        Assert.Equal(DeliveryReconciliationKind.Uncertain, reconciliation.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Target_occupied_between_preflight_and_move_preserves_content_and_hash_verifies(bool identical)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        byte[] occupied = identical ? bytes : Encoding.UTF8.GetBytes("unrelated owner content");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        FakeDavHandler handler = null!;
        handler = new FakeDavHandler { Intercept = request =>
        {
            if (request.Method.Method == "MOVE" && new Uri(request.Headers.GetValues("Destination").Single()).AbsolutePath == target)
                handler.Resources[target] = occupied;
            return Task.FromResult<HttpResponseMessage?>(null);
        } };
        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(identical ? DeliveryResultKind.Confirmed : DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal(identical ? null : "TARGET_CONFLICT", result.ErrorCode);
        Assert.Equal(identical ? DeliveryConfirmationEvidenceLevel.ProviderReceipt : null, result.Evidence);
        Assert.Equal(occupied, handler.Resources[target]);
        Assert.Single(handler.Operations, item => item.Method == "MOVE" && !item.Path.Contains("probe-", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Operations, item => item.Method == "DELETE" && item.Path == target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_non_collection_parent_blocks_media_upload(bool missing)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { Intercept = request =>
        {
            if (request.RequestUri!.AbsolutePath.TrimEnd('/') != "/camera/holiday")
                return Task.FromResult<HttpResponseMessage?>(null);
            return Task.FromResult<HttpResponseMessage?>(request.Method.Method == "MKCOL" ? new(HttpStatusCode.Conflict)
                : missing ? new(HttpStatusCode.NotFound)
                : new(HttpStatusCode.MultiStatus) { Content = new StringContent(
                    "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/camera/holiday</d:href><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>",
                    Encoding.UTF8, "application/xml") });
        } };
        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Contains(handler.Operations, item => item.Method == "PROPFIND" && item.Path == "/camera/holiday");
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE" && !item.Path.Contains("probe-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("", "multistatus")]
    [InlineData("<d:resourcetype/>", "multistatus")]
    [InlineData("<d:resourcetype><d:notcollection/></d:resourcetype>", "multistatus")]
    [InlineData("<d:other><d:collection/></d:other>", "multistatus")]
    [InlineData("<d:resourcetype><d:other><d:collection/></d:other></d:resourcetype>", "multistatus")]
    [InlineData("<d:resourcetype><d:collection/></d:resourcetype>", "other")]
    public async Task Only_an_actual_successful_resourcetype_property_in_dav_multistatus_proves_collection(
        string property, string rootElement)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { Intercept = request => Task.FromResult<HttpResponseMessage?>(
            request.Method.Method != "PROPFIND" ? null : new(HttpStatusCode.MultiStatus) { Content = new StringContent(
                $"<d:{rootElement} xmlns:d=\"DAV:\"><d:response><d:href>/camera</d:href><d:propstat><d:prop>{property}</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:{rootElement}>",
                Encoding.UTF8, "application/xml") }) };
        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("COLLECTION_NOT_AVAILABLE", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE" or "MKCOL");
    }

    [Fact]
    public async Task Lost_reconciliation_read_back_does_not_reverse_a_completed_publication_acknowledgement()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        int finalReads = 0;
        var handler = new FakeDavHandler { Intercept = request =>
        {
            if (request.Method.Method == "PROPFIND" && request.RequestUri!.AbsolutePath == target && ++finalReads == 2)
                throw new HttpRequestException("synthetic lost read-back response");
            return Task.FromResult<HttpResponseMessage?>(null);
        } };
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));
        DeliveryRequest request = Request(bytes);
        DeliveryResult write = await adapter.WriteAsync(request, new MemoryStream(bytes), CancellationToken.None);
        DeliveryReconciliation reconciliation = await adapter.ReconcileAsync(request, CancellationToken.None);

        Assert.Equal(DeliveryResultKind.Confirmed, write.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite, write.Evidence);
        Assert.Equal(DeliveryReconciliationKind.Uncertain, reconciliation.Kind);
        Assert.Equal(2, finalReads);
        Assert.Equal(bytes, handler.Resources[target]);
    }

    [Fact]
    public async Task Provider_timeout_is_retryable_before_move_and_ambiguous_after_move()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var beforeMove = new FakeDavHandler { TimeoutPut = true };
        DeliveryResult before = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(beforeMove))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);
        var afterMove = new FakeDavHandler { TimeoutMoveResponseTarget = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw" };
        DeliveryResult after = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(afterMove))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.TransientFailure, before.Kind);
        Assert.Equal("PROVIDER_TIMEOUT", before.ErrorCode);
        Assert.Equal(DeliveryResultKind.Uncertain, after.Kind);
    }

    [Theory]
    [InlineData(-1, "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData(1, "not-a-sha256")]
    public async Task Invalid_expected_size_or_hash_fails_before_any_provider_request(long size, string hash)
    {
        var handler = new FakeDavHandler();
        DeliveryRequest request = Request([1]) with { Size = size, Sha256 = hash };

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(request, new MemoryStream([1]), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("INVALID_EXPECTED_CONTENT", result.ErrorCode);
        Assert.Empty(handler.Operations);
    }

    [Fact]
    public async Task Early_final_put_rejection_is_retryable_without_reading_original_as_complete()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { RejectDeliveryPut = true };

        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.TransientFailure, result.Kind);
        Assert.Equal("PROVIDER_UNAVAILABLE", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method == "MOVE"
            && !item.Path.Contains("probe-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Partial_content_never_confirms_a_matching_prefix_of_a_larger_target()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        byte[] larger = [.. bytes, .. Encoding.UTF8.GetBytes(" plus unreported bytes")];
        const string target = "/camera/holiday/IMG%20100%25%20%E6%97%A5%E6%9C%AC.raw";
        var handler = new FakeDavHandler { PartialGetPath = target, OmitChecksums = true };
        handler.Resources[target] = larger;
        DeliveryRequest request = Request(bytes);
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));

        DeliveryResult write = await adapter.WriteAsync(request, new MemoryStream(bytes), CancellationToken.None);
        DeliveryReconciliation reconciliation = await adapter.ReconcileAsync(request, CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, write.Kind);
        Assert.Equal("TARGET_CONFLICT", write.ErrorCode);
        Assert.Equal(DeliveryReconciliationKind.Conflict, reconciliation.Kind);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "GET" or "MOVE");
        Assert.Equal(larger, handler.Resources[target]);
    }

    [Fact]
    public async Task Probe_cleanup_requires_a_strong_etag_and_preserves_a_concurrent_replacement()
    {
        var handler = new FakeDavHandler { ReplaceBeforeConditionalDelete = true };
        var adapter = new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler));

        (bool writable, string? error) = await adapter.TestAsync("https://dav.example.test/camera/", "operator",
            "synthetic-app-password", CancellationToken.None);

        Assert.False(writable);
        Assert.Equal("PROBE_CLEANUP_FAILED", error);
        Assert.Contains(handler.Resources.Values, bytes => bytes.SequenceEqual(Encoding.UTF8.GetBytes("unrelated concurrent resource")));
        Assert.Contains(handler.Operations, operation => operation.Method == "DELETE");
    }

    [Fact]
    public async Task Mixed_status_multistatus_does_not_count_as_a_collection()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { MixedStatusPropfind = true };
        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("COLLECTION_NOT_AVAILABLE", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE");
    }

    [Fact]
    public void Provider_requests_are_marked_private_and_credentials_are_only_in_basic_authorization()
    {
        using HttpRequestMessage request = WebDavDestinationDeliveryAdapter.Request(new HttpMethod("PROPFIND"),
            new Uri("https://dav.example.test/camera/private-name.raw"), "operator", "synthetic-app-password");

        Assert.True(request.Options.TryGetValue(new HttpRequestOptionsKey<bool>(WebDavDestinationDeliveryAdapter.PrivateRequestTelemetryKey), out bool suppress));
        Assert.True(suppress);
        Assert.DoesNotContain("synthetic-app-password", request.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-app-password", request.Headers.Authorization!.Parameter, StringComparison.Ordinal);
        Assert.Equal("Basic", request.Headers.Authorization.Scheme);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dtd_and_oversized_dav_responses_are_rejected_without_uploading(bool dtd)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("synthetic original");
        var handler = new FakeDavHandler { OversizedPropfind = !dtd, DtdPropfind = dtd };
        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(new FakeHttpClientFactory(handler))
            .WriteAsync(Request(bytes), new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("DAV_RESPONSE_INVALID", result.ErrorCode);
        Assert.DoesNotContain(handler.Operations, item => item.Method is "PUT" or "MOVE");
    }

    private static DeliveryRequest Request(byte[] bytes, string target = "holiday/IMG 100% 日本.raw") => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        target, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "synthetic-trace", "webdav",
        new WebDavDestinationDeliveryConfiguration("https://dav.example.test/camera/", "operator", "synthetic-app-password"));

    internal sealed class FakeHttpClientFactory(FakeDavHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal sealed class FakeDavHandler : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Resources { get; } = new(StringComparer.Ordinal);
        public List<(string Method, string Path, string Host, string? Overwrite)> Operations { get; } = [];
        public List<(string Path, string? Hash, string? Checksum, long? ContentLength)> MediaPutHeaders { get; } = [];
        public string? MediaPutHashSha256 { get; init; }
        public HttpStatusCode? MediaPutStatus { get; init; }
        public HttpStatusCode? MediaMoveStatus { get; init; }
        public bool OmitChecksums { get; init; }
        public FakeChecksumMode ChecksumMode { get; init; }
        public string? MisleadingChecksum { get; init; }
        public string? TargetHrefOverride { get; init; }
        public List<string> PropfindBodies { get; } = [];
        public List<string> PropfindDepths { get; } = [];
        public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Intercept { get; init; }
        public bool IgnoreNoReplace { get; init; }
        public bool RedirectPropfind { get; init; }
        public bool TimeoutPut { get; init; }
        public bool RejectDeliveryPut { get; init; }
        public string? PartialGetPath { get; init; }
        public bool ReplaceBeforeConditionalDelete { get; init; }
        private bool _replacementInjected;
        public bool MixedStatusPropfind { get; init; }
        public bool OversizedPropfind { get; init; }
        public bool DtdPropfind { get; init; }
        public string? ResourceStatusOverride { get; init; }
        public string? PropstatStatusOverride { get; init; }
        public string? LoseMoveResponseTarget { get; init; }
        public string? TimeoutMoveResponseTarget { get; init; }
        private readonly HashSet<string> _collections = ["/camera"];
        private readonly Dictionary<string, string> _checksums = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            string path = uri.AbsolutePath.TrimEnd('/');
            string method = request.Method.Method;
            string? overwrite = request.Headers.TryGetValues("Overwrite", out IEnumerable<string>? values) ? values.Single() : null;
            Operations.Add((method, path, uri.Host, overwrite));
            if (Intercept is not null && await Intercept(request) is { } intercepted) return intercepted;
            if (method == "PUT" && TimeoutPut) throw new TaskCanceledException("synthetic PUT timeout");
            if (method == "PROPFIND" && RedirectPropfind)
                return new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://redirect.example.test/steal") } };
            if (method == "PROPFIND")
            {
                PropfindBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                PropfindDepths.Add(request.Headers.GetValues("Depth").Single());
                if (!_collections.Contains(path) && !Resources.ContainsKey(path)) return new(HttpStatusCode.NotFound);
                bool isCollection = _collections.Contains(path);
                bool omitChecksum = OmitChecksums || ChecksumMode == FakeChecksumMode.MisleadingOtherHref;
                XNamespace dav = "DAV:";
                XNamespace ownCloud = "http://owncloud.org/ns";
                var properties = new XElement(dav + "prop",
                    new XElement(dav + "resourcetype", isCollection ? new XElement(dav + "collection") : null),
                    isCollection ? null : new XElement(dav + "getcontentlength", Resources[path].Length),
                    isCollection || omitChecksum || ChecksumMode == FakeChecksumMode.FailedChecksumPropstat
                        ? null : ChecksumsFor(path));
                string propertyStatus = MixedStatusPropfind ? "404 Not Found"
                    : ChecksumMode == FakeChecksumMode.FailedResourcePropstat && !isCollection
                        ? "500 Internal Server Error" : "200 OK";
                var response = new XElement(dav + "response",
                    new XElement(dav + "href", isCollection ? path : TargetHrefOverride ?? path),
                    isCollection || ResourceStatusOverride is null ? null : new XElement(dav + "status", ResourceStatusOverride),
                    Propstat(properties, !isCollection && PropstatStatusOverride is not null
                        ? PropstatStatusOverride : $"HTTP/1.1 {propertyStatus}"));
                if (!isCollection && ChecksumMode is FakeChecksumMode.FailedChecksumPropstat or FakeChecksumMode.MixedFailedChecksumPropstat)
                    response.Add(Propstat(new XElement(dav + "prop", ChecksumsFor(path)), "HTTP/1.1 404 Not Found"));
                var responses = new List<object> { response };
                if (!isCollection && (ChecksumMode == FakeChecksumMode.MisleadingOtherHref || TargetHrefOverride is not null))
                    responses.Add(new XElement(dav + "response",
                        new XElement(dav + "href", "/camera/holiday/elsewhere.raw"),
                        Propstat(new XElement(dav + "prop", new XElement(dav + "resourcetype"),
                            new XElement(dav + "getcontentlength", Resources[path].Length),
                            new XElement(ownCloud + "checksums", new XElement(ownCloud + "checksum", MisleadingChecksum))), "HTTP/1.1 200 OK")));
                string xml = DtdPropfind ? "<!DOCTYPE dav [<!ENTITY secret SYSTEM 'file:///private-media'>]><d:multistatus xmlns:d=\"DAV:\">&secret;</d:multistatus>"
                    : OversizedPropfind ? $"<d:multistatus xmlns:d=\"DAV:\">{new string('x', 70_000)}</d:multistatus>"
                    : new XElement(dav + "multistatus", responses).ToString(SaveOptions.DisableFormatting);
                return new(HttpStatusCode.MultiStatus) { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
            }
            if (method == "MKCOL")
                return _collections.Add(path) ? new(HttpStatusCode.Created) : new(HttpStatusCode.MethodNotAllowed);
            if (method == "PUT")
            {
                if (RejectDeliveryPut && !path.Contains("probe-", StringComparison.Ordinal))
                    return new(HttpStatusCode.ServiceUnavailable);
                if (!path.Contains("probe-", StringComparison.Ordinal))
                    MediaPutHeaders.Add((path, request.Headers.TryGetValues("X-Hash", out IEnumerable<string>? hashes) ? hashes.Single() : null,
                        request.Headers.TryGetValues("OC-Checksum", out IEnumerable<string>? checksums) ? checksums.Single() : null,
                        request.Content?.Headers.ContentLength));
                await using Stream body = await request.Content!.ReadAsStreamAsync(cancellationToken);
                using var output = new MemoryStream();
                await body.CopyToAsync(output, cancellationToken);
                Resources[path] = output.ToArray();
                if (request.Headers.TryGetValues("OC-Checksum", out IEnumerable<string>? checksum))
                    _checksums[path] = checksum.Single();
                var response = new HttpResponseMessage(!path.Contains("probe-", StringComparison.Ordinal) && MediaPutStatus is { } putStatus
                    ? putStatus : HttpStatusCode.Created);
                if (!path.Contains("probe-", StringComparison.Ordinal) && MediaPutHashSha256 is { } sha256)
                    response.Headers.TryAddWithoutValidation("X-Hash-SHA256", sha256);
                return response;
            }
            if (method == "MOVE")
            {
                string source = path;
                string destination = new Uri(request.Headers.GetValues("Destination").Single()).AbsolutePath.TrimEnd('/');
                if (LoseMoveResponseTarget == destination)
                {
                    Resources[destination] = Resources[source];
                    if (_checksums.Remove(source, out string? lostMoveChecksum)) _checksums[destination] = lostMoveChecksum;
                    Resources.Remove(source);
                    throw new HttpRequestException("synthetic lost MOVE response");
                }
                if (!Resources.TryGetValue(source, out byte[]? bytes)) return new(HttpStatusCode.NotFound);
                if (Resources.ContainsKey(destination) && !IgnoreNoReplace) return new(HttpStatusCode.PreconditionFailed);
                Resources[destination] = bytes;
                if (_checksums.Remove(source, out string? checksum)) _checksums[destination] = checksum;
                Resources.Remove(source);
                if (TimeoutMoveResponseTarget == destination) throw new TaskCanceledException("synthetic MOVE timeout after publication");
                return new(MediaMoveStatus is { } moveStatus && !source.Contains("probe-", StringComparison.Ordinal)
                    ? moveStatus : HttpStatusCode.Created);
            }
            if (method == "GET")
            {
                if (PartialGetPath == path && Resources.TryGetValue(path, out byte[]? larger))
                {
                    var partial = new ByteArrayContent(larger[..(larger.Length - 1)]);
                    partial.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, larger.Length - 2, larger.Length);
                    return new(HttpStatusCode.PartialContent) { Content = partial };
                }
                return Resources.TryGetValue(path, out byte[]? content)
                    ? ResourceResponse(content)
                    : new(HttpStatusCode.NotFound);
            }
            if (method == "DELETE")
            {
                if (ReplaceBeforeConditionalDelete && !_replacementInjected && Resources.ContainsKey(path))
                {
                    _replacementInjected = true;
                    Resources[path] = Encoding.UTF8.GetBytes("unrelated concurrent resource");
                }
                if (request.Headers.IfMatch.Count != 1 || !Resources.TryGetValue(path, out byte[]? current)
                    || request.Headers.IfMatch.Single().Tag != Etag(current))
                    return new(HttpStatusCode.PreconditionFailed);
                return Resources.Remove(path) ? new(HttpStatusCode.NoContent) : new(HttpStatusCode.NotFound);
            }
            return new(HttpStatusCode.MethodNotAllowed);
        }

        private static HttpResponseMessage ResourceResponse(byte[] content) => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content),
            Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue(Etag(content)) }
        };

        private static string Etag(byte[] content) => $"\"{Convert.ToHexString(SHA256.HashData(content))}\"";
        private string ChecksumFor(string path) => _checksums.GetValueOrDefault(path,
            $"SHA256:{Convert.ToHexString(SHA256.HashData(Resources[path]))}");

        private static XElement Propstat(XElement properties, string status)
        {
            XNamespace dav = "DAV:";
            return new XElement(dav + "propstat", properties, new XElement(dav + "status", status));
        }

        private XElement ChecksumsFor(string path)
        {
            XNamespace dav = "DAV:";
            XNamespace ownCloud = "http://owncloud.org/ns";
            string checksum = ChecksumFor(path);
            IEnumerable<XElement> values = ChecksumMode switch
            {
                FakeChecksumMode.MultipleAlgorithms => [new(ownCloud + "checksum", $"SHA1:{new string('1', 40)}"), new(ownCloud + "checksum", checksum)],
                FakeChecksumMode.DuplicateMatch => [new(ownCloud + "checksum", checksum), new(ownCloud + "checksum", checksum)],
                FakeChecksumMode.DuplicateConflict => [new(ownCloud + "checksum", checksum), new(ownCloud + "checksum", $"SHA256:{new string('0', 64)}")],
                FakeChecksumMode.Malformed => [new(ownCloud + "checksum", "SHA256:not-a-hash")],
                FakeChecksumMode.ForeignNestedChecksum => [new(dav + "checksum", $"SHA256:{new string('0', 64)}")],
                _ => [new(ownCloud + "checksum", checksum)]
            };
            return new XElement(ownCloud + "checksums", values);
        }
    }
}
