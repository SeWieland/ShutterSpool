using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Sources;
using ShutterSpool.Domain.Collections;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;
using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed partial class CameraIngressIntegrationTests
{
    [Fact]
    public async Task Authentication_feedback_attributes_verified_disabled_credentials_and_aggregates_unproved_rejections()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("auth-attribution", "camera-secret");

        Assert.Null(await fixture.Ingress.AuthenticateAsync("cam_auth-attribution", "wrong-secret", Token));
        Assert.Empty(fixture.AuthenticationRecorder.Observations);
        Assert.Single(fixture.AuthenticationRecorder.Unassigned);

        Assert.NotNull(await new EfSourceConfigurationStore(fixture.Database).SetIngressEnabledAsync(sourceId, false, Token));
        Assert.Null(await fixture.Ingress.AuthenticateAsync("cam_auth-attribution", "camera-secret", Token));
        SourceAuthenticationObservation rejection = Assert.Single(fixture.AuthenticationRecorder.Observations);
        Assert.Equal(sourceId, rejection.SourceId);
        Assert.Equal(SourceAuthenticationOutcome.Rejected, rejection.Outcome);
    }

    [Fact]
    public async Task Camera_receive_uses_one_spool_owned_payload_and_waits_for_the_success_event_to_publish_it()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("one-pass", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_one-pass", "camera-secret", Token))!;
        SourceAuthenticationObservation observed = Assert.Single(fixture.AuthenticationRecorder.Observations);
        Assert.Equal(sourceId, observed.SourceId);
        Assert.Equal(SourceAuthenticationOutcome.Succeeded, observed.Outcome);
        const string path = "/DCIM/DSC0000.JPG";
        const string session = "FTP_session-one-pass";
        long timestamp = CurrentTimestamp();
        byte[] bytes = [2, 7, 1, 8, 2, 8];

        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, timestamp, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);

        Assert.True(Directory.Exists(Path.Combine(fixture.DirectoriesRoot, sourceId.ToString("N"), "files")));
        string[] privatePayloads = EnumeratePayloads(fixture.SpoolRoot);
        Assert.Single(privatePayloads);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(privatePayloads[0], Token));
        Assert.Empty(await fixture.Database.Assets.AsNoTracking().ToListAsync(Token));

        IngestResult result = await fixture.Ingress.CompleteAsync(login.Id, login.Username, session, path, timestamp + 1_000, bytes.Length, Token);

        Assert.Single(EnumeratePayloads(fixture.SpoolRoot));
        Assert.Equal(sourceId, result.Asset.SourceId);
        Assert.Equal(bytes.Length, result.Asset.Size);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), result.Asset.Sha256);
        Assert.Single(await fixture.Database.Assets.AsNoTracking().ToListAsync(Token));
    }

    [Fact]
    public async Task Transfer_activity_moves_from_receiving_to_accepted_only_after_durable_completion()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("phase-activity", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_phase-activity", "camera-secret", Token))!;
        const string path = "/DCIM/DSC0001.JPG";
        const string session = "FTP_session-phase-activity";
        long timestamp = CurrentTimestamp();
        byte[] bytes = [8, 3, 4];
        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, timestamp, 1_024, 2, 600, Token);

        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);
        CameraTransferActivity receiving = Assert.Single(fixture.ActivityPublisher.Items);
        Assert.Equal(CameraTransferPhase.Receiving, receiving.Phase);
        Assert.False(receiving.Accepted);
        Assert.Empty(await fixture.Database.Assets.AsNoTracking().ToArrayAsync(Token));

        IngestResult result = await fixture.Ingress.CompleteAsync(login.Id, login.Username, session, path,
            timestamp + 1_000, bytes.Length, Token);
        CameraTransferActivity accepted = fixture.ActivityPublisher.Items[^1];
        Assert.Equal(CameraTransferPhase.AcceptedAwaitingGrouping, accepted.Phase);
        Assert.Equal(receiving.TransferId, accepted.TransferId);
        Assert.Equal(sourceId, accepted.SourceId);
        Assert.Equal(result.Asset.Id, accepted.AssetId);
        Assert.True(accepted.Accepted);
        Assert.Equal(2, accepted.Version);

        CameraTransferActivityPage snapshot = await fixture.Store.GetActivityAsync([sourceId], null, 30,
            null, null, DateTimeOffset.UtcNow, Token);
        CameraTransferActivity projected = Assert.Single(snapshot.Items);
        Assert.Equal(result.Asset.Id, projected.AssetId);
        Assert.Null(projected.CaptureId);
        Assert.Equal(CameraTransferPhase.AcceptedAwaitingGrouping, projected.Phase);
        Assert.False(snapshot.HasMore);
    }

    [Fact]
    public async Task Transfer_snapshot_recovers_asset_mapping_after_commit_before_admission_completion()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("phase-commit-window", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_phase-commit-window", "camera-secret", Token))!;
        const string path = "/DCIM/DSC0002.JPG";
        const string session = "FTP_session-phase-commit-window";
        long admittedAt = CurrentTimestamp();
        long completedAt = admittedAt + 1_000;
        byte[] bytes = [1, 9, 8, 4];

        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, admittedAt, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);
        CameraUploadAdmission admission = Assert.Single(await fixture.Store.GetRecoverableAsync(Token));
        Assert.True(await fixture.Store.TryBeginHandoffAsync(admission.Id, completedAt, Token));
        IngestOperation operation = (await fixture.Ingest.FindOperationAsync(admission.Id, Token))!;
        IngestResult committed = await fixture.Ingest.FinalizeAdmittedAsync(operation, sourceId, "DSC0002.JPG", bytes.Length, Token);

        CameraTransferActivityPage snapshot = await fixture.Store.GetActivityAsync([sourceId], null, 30,
            null, null, DateTimeOffset.UtcNow, Token);
        CameraTransferActivity item = Assert.Single(snapshot.Items);
        Assert.Equal(CameraTransferState.Handoff, Assert.Single(await fixture.Store.GetRecoverableAsync(Token)).State);
        Assert.True(item.Accepted);
        Assert.Equal(committed.Asset.Id, item.AssetId);
        Assert.Null(item.CaptureId);
        Assert.Equal(CameraTransferPhase.AcceptedAwaitingGrouping, item.Phase);
    }

    [Fact]
    public async Task Virtual_directory_listing_combines_empty_folders_with_latest_completed_file_metadata()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        await fixture.CreateSourceAsync("directory-metadata", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_directory-metadata", "camera-secret", Token))!;
        await fixture.Ingress.CreateDirectoryAsync(login.Id, "/DCIM/100MEDIA", Token);
        const string path = "/DCIM/100MEDIA/PHOTO.JPG";
        const string session = "FTP_session-directory-metadata";
        long timestamp = CurrentTimestamp();
        byte[] bytes = [1, 2, 3, 4];

        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, timestamp, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);
        await fixture.Ingress.CompleteAsync(login.Id, login.Username, session, path, timestamp + 1_000, bytes.Length, Token);

        IReadOnlyList<CameraFileInfo> directories = await fixture.Ingress.GetDirectoryAsync(login.Id, "/DCIM", Token);
        IReadOnlyList<CameraFileInfo> files = await fixture.Ingress.GetDirectoryAsync(login.Id, "/DCIM/100MEDIA", Token);
        (long count, long size) = await fixture.Ingress.GetDirectorySizeAsync(login.Id, Token);

        Assert.Equal("100MEDIA", Assert.Single(directories).Name);
        CameraFileInfo file = Assert.Single(files);
        Assert.Equal("PHOTO.JPG", file.Name);
        Assert.False(file.IsDirectory);
        Assert.Equal(bytes.Length, file.Size);
        Assert.Equal((1, (long)bytes.Length), (count, size));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoriesRoot, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Virtual_metadata_survives_reauthentication_and_shows_latest_file_without_crossing_sources()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("metadata-reconnect", "camera-secret");
        await fixture.CreateSourceAsync("metadata-other-source", "other-secret");
        CameraLogin firstLogin = (await fixture.Ingress.AuthenticateAsync("cam_metadata-reconnect", "camera-secret", Token))!;
        CameraLogin otherLogin = (await fixture.Ingress.AuthenticateAsync("cam_metadata-other-source", "other-secret", Token))!;
        const string path = "/DCIM/100MEDIA/PHOTO.JPG";
        const string session = "FTP_session-metadata-reconnect";
        byte[] original = [1, 2, 3];

        await fixture.Ingress.CreateDirectoryAsync(firstLogin.Id, "/DCIM/100MEDIA", Token);
        long firstAdmission = CurrentTimestamp();
        await fixture.Ingress.AdmitAsync(firstLogin.Id, firstLogin.Username, session, path, firstAdmission, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(firstLogin.Id, path, new MemoryStream(original), 1_024, Token);
        await fixture.Ingress.CompleteAsync(firstLogin.Id, firstLogin.Username, session, path, firstAdmission + 1_000, original.Length, Token);

        CameraLogin secondLogin = (await fixture.Ingress.AuthenticateAsync("cam_metadata-reconnect", "camera-secret", Token))!;
        Assert.NotEqual(firstLogin.Id, secondLogin.Id);
        CameraFileInfo? firstStat = await fixture.Ingress.GetFileInfoAsync(secondLogin.Id, path, Token);
        IReadOnlyList<CameraFileInfo> firstListing = await fixture.Ingress.GetDirectoryAsync(secondLogin.Id, "/DCIM/100MEDIA", Token);
        CameraFileInfo? sourceFolder = await fixture.Ingress.GetFileInfoAsync(secondLogin.Id, "/DCIM", Token);

        Assert.NotNull(firstStat);
        Assert.NotNull(sourceFolder);
        Assert.Equal(original.Length, firstStat.Size);
        Assert.Equal("PHOTO.JPG", Assert.Single(firstListing).Name);
        Assert.True(sourceFolder.IsDirectory);
        Assert.Equal("100MEDIA", Assert.Single(await fixture.Ingress.GetDirectoryAsync(secondLogin.Id, "/DCIM", Token)).Name);
        Assert.Equal((1, (long)original.Length), await fixture.Ingress.GetDirectorySizeAsync(secondLogin.Id, Token));

        byte[] replacement = [9, 8, 7, 6, 5, 4];
        long secondAdmission = firstAdmission + 2_000;
        const string secondSession = "FTP_session-metadata-reconnect-2";
        await fixture.Ingress.AdmitAsync(secondLogin.Id, secondLogin.Username, secondSession, path, secondAdmission, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(secondLogin.Id, path, new MemoryStream(replacement), 1_024, Token);
        await fixture.Ingress.CompleteAsync(secondLogin.Id, secondLogin.Username, secondSession, path, secondAdmission + 1_000,
            replacement.Length, Token);

        CameraLogin reconnectedLogin = (await fixture.Ingress.AuthenticateAsync("cam_metadata-reconnect", "camera-secret", Token))!;
        CameraFileInfo? latest = await fixture.Ingress.GetFileInfoAsync(reconnectedLogin.Id, path, Token);
        CameraFileInfo latestListing = Assert.Single(await fixture.Ingress.GetDirectoryAsync(reconnectedLogin.Id, "/DCIM/100MEDIA", Token));
        Assert.NotEqual(secondLogin.Id, reconnectedLogin.Id);
        Assert.NotNull(latest);
        Assert.Equal(replacement.Length, latest.Size);
        Assert.Equal(replacement.Length, latestListing.Size);
        Assert.Equal((1, (long)replacement.Length), await fixture.Ingress.GetDirectorySizeAsync(reconnectedLogin.Id, Token));

        Assert.Null(await fixture.Ingress.GetFileInfoAsync(otherLogin.Id, path, Token));
        Assert.Empty(await fixture.Ingress.GetDirectoryAsync(otherLogin.Id, "/DCIM/100MEDIA", Token));
        Assert.Equal((0, 0), await fixture.Ingress.GetDirectorySizeAsync(otherLogin.Id, Token));
        Assert.Null(await fixture.Ingress.GetFileInfoAsync(otherLogin.Id, "/DCIM", Token));
        Assert.Equal(2, await fixture.Database.Assets.AsNoTracking().CountAsync(item => item.SourceId == sourceId, Token));
    }

    [Fact]
    public async Task Receive_eof_without_success_completion_is_recoverable_but_never_commits_an_asset()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        await fixture.CreateSourceAsync("eof", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_eof", "camera-secret", Token))!;
        string path = "/DCIM/DSC0000.JPG";
        long admittedAt = CurrentTimestamp() - 601_000_000_000;
        const string session = "FTP_session-eof";
        byte[] bytes = [3, 1, 4, 1, 5];

        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, admittedAt, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);

        CameraUploadAdmission admission = Assert.Single(await fixture.Store.GetRecoverableAsync(Token));
        IngestOperation received = (await new EfIngestJournal(fixture.Database).FindOperationAsync(admission.Id, Token))!;
        Assert.NotNull(received);
        Assert.Equal(bytes.Length, received.Size);
        Assert.NotNull(received.FinalKey);

        await using var restarted = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        IngestService ingest = new(new EfIngestJournal(restarted), fixture.Spool, TimeProvider.System);
        CameraIngressService recovering = fixture.CreateIngress(restarted);
        await ingest.RecoverAsync(Token);
        await recovering.RecoverAsync(600, Token);

        Assert.Empty(await restarted.Assets.AsNoTracking().ToListAsync(Token));
        IngestOperation quarantined = (await new EfIngestJournal(restarted).FindOperationAsync(admission.Id, Token))!;
        Assert.NotNull(quarantined);
        Assert.Equal(IngestOperationState.Quarantined, quarantined.State);
        string retainedPath = Assert.Single(EnumeratePayloads(fixture.SpoolRoot));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(retainedPath, Token));
    }

    [Fact]
    public async Task Recovery_commits_received_bytes_when_the_completion_marker_precedes_a_failed_asset_commit()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("commit-recovery", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_commit-recovery", "camera-secret", Token))!;
        const string path = "/DCIM/DSC0000.JPG";
        const string session = "FTP_session-commit-recovery";
        long admittedAt = CurrentTimestamp();
        long completedAt = admittedAt + 1_000;
        byte[] bytes = [1, 2, 7, 3, 8];

        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, admittedAt, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);
        var failingIngest = new IngestService(new FailingCommitJournal(new EfIngestJournal(fixture.Database)), fixture.Spool, TimeProvider.System);
        CameraIngressService failing = fixture.CreateIngress(fixture.Database, failingIngest);

        await Assert.ThrowsAsync<IOException>(() => failing.CompleteAsync(login.Id, login.Username, session, path, completedAt,
            bytes.Length, Token));

        CameraUploadAdmission released = Assert.Single(await fixture.Store.GetRecoverableAsync(Token));
        Assert.Equal(CameraTransferState.Received, released.State);
        Assert.Equal(completedAt, released.CompletionTimestamp);
        Assert.Empty(await fixture.Database.Assets.AsNoTracking().ToListAsync(Token));

        await using var restarted = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        await new IngestService(new EfIngestJournal(restarted), fixture.Spool, TimeProvider.System).RecoverAsync(Token);
        CameraIngressService recovering = fixture.CreateIngress(restarted);
        await recovering.RecoverAsync(600, Token);

        CameraTransferStatus state = Assert.Single(await recovering.GetStatusAsync(sourceId, Token));
        Assert.Equal(CameraTransferState.Completed, state.State);
        AssetEntity committed = await restarted.Assets.AsNoTracking().SingleAsync(Token);
        Assert.Equal(bytes.Length, committed.Size);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), committed.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot,
            committed.SpoolKey.Replace('/', Path.DirectorySeparatorChar)), Token));
    }

    [Fact]
    public async Task Released_completion_timestamp_rejects_new_success_and_failure_timestamps_but_accepts_exact_retry()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("timestamp-fence", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_timestamp-fence", "camera-secret", Token))!;
        const string path = "/DCIM/DSC0000.JPG";
        const string session = "FTP_timestamp-fence";
        byte[] bytes = [3, 1, 4, 1, 5];
        long admittedAt = CurrentTimestamp();
        long completedAt = admittedAt + 1_000;
        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, admittedAt, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);

        var failedCommit = new IngestService(new FailingCommitJournal(new EfIngestJournal(fixture.Database)), fixture.Spool, TimeProvider.System);
        CameraIngressService interrupted = fixture.CreateIngress(fixture.Database, failedCommit);
        await Assert.ThrowsAsync<IOException>(() => interrupted.CompleteAsync(login.Id, login.Username, session, path,
            completedAt, bytes.Length, Token));

        CameraUploadAdmission released = Assert.Single(await fixture.Store.GetRecoverableAsync(Token));
        Assert.Equal(CameraTransferState.Received, released.State);
        Assert.Equal(completedAt, released.CompletionTimestamp);
        await Assert.ThrowsAsync<CameraIngressRejectedException>(() => fixture.Ingress.CompleteAsync(login.Id, login.Username, session,
            path, completedAt + 1, bytes.Length, Token));
        Assert.False(await fixture.Ingress.FailUploadAsync(login.Id, login.Username, session, path, completedAt, Token));
        Assert.False(await fixture.Ingress.FailUploadAsync(login.Id, login.Username, session, path, completedAt + 1, Token));

        CameraUploadAdmission stillReleased = Assert.Single(await fixture.Store.GetRecoverableAsync(Token));
        Assert.Equal(CameraTransferState.Received, stillReleased.State);
        Assert.Equal(completedAt, stillReleased.CompletionTimestamp);
        IngestResult result = await fixture.Ingress.CompleteAsync(login.Id, login.Username, session, path,
            completedAt, bytes.Length, Token);

        Assert.Equal(sourceId, result.Asset.SourceId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.FinalPath(result), Token));
        Assert.Equal(CameraTransferState.Completed, Assert.Single(await fixture.Ingress.GetStatusAsync(sourceId, Token)).State);
    }

    [Fact]
    public async Task Failed_edge_completion_quarantines_direct_receive_bytes_without_publishing_an_asset()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        await fixture.CreateSourceAsync("failed", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_failed", "camera-secret", Token))!;
        const string path = "/DCIM/DSC0000.JPG";
        const string session = "FTP_session-failed";
        long admittedAt = CurrentTimestamp();
        byte[] bytes = [1, 6, 1, 8];

        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, admittedAt, 1_024, 2, 600, Token);
        CameraUploadAdmission admission = Assert.Single(await fixture.Store.GetRecoverableAsync(Token));
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);

        Assert.True(await fixture.Ingress.FailUploadAsync(login.Id, login.Username, session, path, admittedAt + 1_000, Token));

        Assert.Empty(await fixture.Database.Assets.AsNoTracking().ToListAsync(Token));
        IngestOperation operation = (await new EfIngestJournal(fixture.Database).FindOperationAsync(admission.Id, Token))!;
        Assert.NotNull(operation);
        Assert.Equal(IngestOperationState.Quarantined, operation.State);
        string retained = Assert.Single(EnumeratePayloads(fixture.SpoolRoot));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(retained, Token));
    }

    [Fact]
    public async Task Recovery_quarantines_eof_bytes_when_durable_receive_receipt_was_not_persisted()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("receipt-failure", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_receipt-failure", "camera-secret", Token))!;
        const string path = "/DCIM/DSC0000.JPG";
        byte[] bytes = [2, 3, 5, 7, 11];
        long admittedAt = CurrentTimestamp();
        await fixture.Ingress.AdmitAsync(login.Id, login.Username, "FTP_receipt-failure", path, admittedAt, 1_024, 2, 600, Token);
        var failingSpool = new FailingReceiveSpool(fixture.Spool);
        var ingest = new IngestService(new EfIngestJournal(fixture.Database), failingSpool, TimeProvider.System);
        CameraIngressService interrupted = fixture.CreateIngress(fixture.Database, ingest);

        await Assert.ThrowsAsync<IOException>(() => interrupted.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token));
        IngestOperation operation = (await new EfIngestJournal(fixture.Database).FindOperationAsync(
            Assert.Single(await fixture.Store.GetRecoverableAsync(Token)).Id, Token))!;
        Assert.Equal(IngestOperationState.AwaitingSource, operation.State);
        Assert.Empty(await fixture.Database.Assets.AsNoTracking().ToListAsync(Token));
        string candidate = Path.Combine(fixture.SpoolRoot, operation.FinalKey!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(candidate, Token));

        await using var restarted = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        var recoveredIngest = new IngestService(new EfIngestJournal(restarted), fixture.Spool, TimeProvider.System);
        await recoveredIngest.RecoverAsync(Token);
        CameraIngressService recovering = fixture.CreateIngress(restarted, recoveredIngest);
        await recovering.RecoverAsync(600, Token);

        Assert.Empty(await restarted.Assets.AsNoTracking().ToListAsync(Token));
        IngestOperation quarantined = (await new EfIngestJournal(restarted).FindOperationAsync(operation.Id, Token))!;
        Assert.Equal(IngestOperationState.Quarantined, quarantined.State);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(EnumeratePayloads(fixture.SpoolRoot)), Token));
        Assert.Equal(CameraTransferState.Quarantined, Assert.Single(await recovering.GetStatusAsync(sourceId, Token)).State);
    }

    [Fact]
    public async Task Completion_replay_is_idempotent_and_an_unknown_delayed_event_cannot_claim_the_next_same_name_upload()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        await fixture.CreateSourceAsync("camera", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_camera", "camera-secret", Token))!;
        string path = "/DCIM/DSC0001.JPG";
        string session = "FTP_session-1";
        long admittedAt = CurrentTimestamp();

        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, admittedAt, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream([1, 2, 3]), 1_024, Token);
        long firstCompletionAt = admittedAt + 1_000;
        IngestResult first = await fixture.Ingress.CompleteAsync(login.Id, login.Username, session, path, firstCompletionAt, 3, Token);
        IngestResult replay = await fixture.Ingress.CompleteAsync(login.Id, login.Username, session, path, firstCompletionAt, 3, Token);

        Assert.Equal(first.Asset.Id, replay.Asset.Id);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(fixture.FinalPath(first), Token));

        long secondAdmissionAt = admittedAt + 1_000_000_000;
        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, secondAdmissionAt, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream([7, 8]), 1_024, Token);
        await Assert.ThrowsAsync<CameraIngressRejectedException>(() => fixture.Ingress.CompleteAsync(
            login.Id, login.Username, session, path, secondAdmissionAt - 1, 2, Token));

        IngestResult second = await fixture.Ingress.CompleteAsync(login.Id, login.Username, session, path, secondAdmissionAt + 1_000, 2, Token);
        Assert.NotEqual(first.Asset.Id, second.Asset.Id);
        Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(fixture.FinalPath(second), Token));
        Assert.Equal(2, await fixture.Database.Assets.CountAsync(Token));
    }

    [Fact]
    public async Task Already_admitted_upload_finishes_after_rotation_but_its_old_login_cannot_admit_again()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("rotation", "camera-secret");
        CameraLogin oldLogin = (await fixture.Ingress.AuthenticateAsync("cam_rotation", "camera-secret", Token))!;
        long timestamp = CurrentTimestamp();
        const string path = "/DCIM/DSC0002.JPG";
        const string session = "FTP_session-rotation";
        await fixture.Ingress.AdmitAsync(oldLogin.Id, oldLogin.Username, session, path, timestamp, 1_024, 2, 600, Token);

        var configuration = new EfSourceConfigurationStore(fixture.Database);
        await configuration.SetIngressEnabledAsync(sourceId, false, Token);
        await configuration.RegenerateAsync(sourceId, new SecretProtector(fixture.ProtectionOptions).CreateVerifier("replacement-secret"), Token);
        await configuration.SetIngressEnabledAsync(sourceId, true, Token);

        await fixture.Ingress.ReceiveAsync(oldLogin.Id, path, new MemoryStream([4, 5]), 1_024, Token);
        IngestResult completed = await fixture.Ingress.CompleteAsync(oldLogin.Id, oldLogin.Username, session, path, timestamp + 1_000, 2, Token);
        Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(fixture.FinalPath(completed), Token));
        await Assert.ThrowsAsync<CameraIngressRejectedException>(() => fixture.Ingress.AdmitAsync(
            oldLogin.Id, oldLogin.Username, session, path, timestamp + 1_000_000_000, 1_024, 2, 600, Token));

        CameraLogin newLogin = (await fixture.Ingress.AuthenticateAsync("cam_rotation", "replacement-secret", Token))!;
        await fixture.Ingress.AdmitAsync(newLogin.Id, newLogin.Username, session, path, timestamp + 1_000_000_000,
            1_024, 2, 600, Token);
        Assert.NotEqual(oldLogin.Id, newLogin.Id);
    }

    [Fact]
    public async Task Trusted_profile_authentication_and_admission_require_exact_source_profile_and_generation()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("legacy-ftps", "camera-secret");

        Assert.Null(await fixture.Ingress.AuthenticateAsync("cam_legacy-ftps", "camera-secret", Token, SourceConnectionProfile.LegacyFtps));
        CameraLogin modernLogin = (await fixture.Ingress.AuthenticateAsync("cam_legacy-ftps", "camera-secret", Token))!;
        Assert.Equal(SourceConnectionProfile.ModernFtps, modernLogin.ConnectionProfile);

        var legacyLoginId = Guid.NewGuid();
        fixture.Database.CameraLogins.Add(new CameraLoginEntity
        {
            Id = legacyLoginId, SourceId = sourceId, Username = "cam_legacy-ftps", VirtualRoot = "sources/legacy-ftps",
            CredentialGeneration = 1, ConnectionProfile = SourceConnectionProfile.LegacyFtps, CreatedAt = DateTimeOffset.UtcNow,
        });
        await fixture.Database.SaveChangesAsync(Token);
        await Assert.ThrowsAsync<CameraIngressRejectedException>(() => fixture.Ingress.AdmitAsync(legacyLoginId,
            "cam_legacy-ftps", "FTP_default-legacy", "/DCIM/DSC0000.JPG", CurrentTimestamp(), 1_024, 2, 600, Token));

        var configuration = new EfSourceConfigurationStore(fixture.Database);
        SourceConfigurationWrite enabled = await configuration.UpdateConfigurationAsync(sourceId, 0,
            "Shared Source", SourceCurationMode.Delay, 3_600, SourceType.CameraFtpUpload,
            SourceConnectionProfile.LegacyFtps, CollectionName.BuiltInId, Token);
        Assert.True(enabled.Applied);
        Assert.Equal(SourceConnectionProfile.LegacyFtps, enabled.Current!.ConnectionProfile);
        Assert.Equal(2, enabled.Current.CredentialGeneration);

        CameraLogin legacyLogin = (await fixture.Ingress.AuthenticateAsync("cam_legacy-ftps", "camera-secret", Token,
            SourceConnectionProfile.LegacyFtps))!;
        Assert.Equal(SourceConnectionProfile.LegacyFtps, legacyLogin.ConnectionProfile);
        Assert.Null(await fixture.Ingress.AuthenticateAsync("cam_legacy-ftps", "camera-secret", Token));

        SourceConfigurationWrite suspended = await configuration.SetIngressEnabledAsync(sourceId,
            enabled.Current.ConfigurationRevision, false, Token);
        SourceConfigurationWrite resumed = await configuration.SetIngressEnabledAsync(sourceId,
            suspended.Current!.ConfigurationRevision, true, Token);
        Assert.Equal(SourceConnectionProfile.LegacyFtps, resumed.Current!.ConnectionProfile);
        Assert.Equal(enabled.Current.CredentialGeneration + 2, resumed.Current.CredentialGeneration);
        await Assert.ThrowsAsync<CameraIngressRejectedException>(() => fixture.Ingress.AdmitAsync(legacyLogin.Id,
            legacyLogin.Username, "FTP_legacy-stale-generation", "/DCIM/DSC0000.JPG", CurrentTimestamp(),
            1_024, 2, 600, Token));

        CameraLogin activeLegacyLogin = (await fixture.Ingress.AuthenticateAsync("cam_legacy-ftps", "camera-secret", Token,
            SourceConnectionProfile.LegacyFtps))!;
        long timestamp = CurrentTimestamp();
        await fixture.Ingress.AdmitAsync(activeLegacyLogin.Id, activeLegacyLogin.Username, "FTP_legacy", "/DCIM/DSC0001.JPG",
            timestamp, 1_024, 2, 600, Token);

        SourceConfigurationWrite disabled = await configuration.UpdateConfigurationAsync(sourceId, resumed.Current.ConfigurationRevision,
            "Shared Source", SourceCurationMode.Delay, 3_600, SourceType.CameraFtpUpload,
            SourceConnectionProfile.ModernFtps, CollectionName.BuiltInId, Token);
        Assert.True(disabled.Applied);
        Assert.Equal(SourceConnectionProfile.ModernFtps, disabled.Current!.ConnectionProfile);
        Assert.Equal(resumed.Current.CredentialGeneration + 1, disabled.Current.CredentialGeneration);
        byte[] admittedBytes = [9, 7, 2, 6];
        await fixture.Ingress.ReceiveAsync(activeLegacyLogin.Id, "/DCIM/DSC0001.JPG", new MemoryStream(admittedBytes), 1_024, Token);
        IngestResult completed = await fixture.Ingress.CompleteAsync(activeLegacyLogin.Id, activeLegacyLogin.Username, "FTP_legacy",
            "/DCIM/DSC0001.JPG", timestamp + 1_000, admittedBytes.Length, Token);
        Assert.Equal(admittedBytes.Length, completed.Asset.Size);
        Assert.Equal(SourceConnectionProfile.ModernFtps, (await configuration.GetAsync(sourceId, Token))!.ConnectionProfile);
        await Assert.ThrowsAsync<CameraIngressRejectedException>(() => fixture.Ingress.AdmitAsync(activeLegacyLogin.Id,
            activeLegacyLogin.Username, "FTP_legacy-new-session", "/DCIM/DSC0002.JPG", timestamp + 1_000_000_000,
            1_024, 2, 600, Token));
    }

    [Fact]
    public async Task Recovery_finishes_a_committed_handoff_and_quarantines_an_unproven_handoff_without_deleting_bytes()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        await fixture.CreateSourceAsync("recovery", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_recovery", "camera-secret", Token))!;
        string path = "/DCIM/DSC0003.JPG";
        const string session = "FTP_session-recovery";
        long firstTimestamp = CurrentTimestamp() - 601_000_000_000;
        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, firstTimestamp, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream([9, 8, 7]), 1_024, Token);
        Guid firstTransfer = Assert.Single(await fixture.Store.GetRecoverableAsync(Token), item => item.State == CameraTransferState.Received).Id;
        long firstCompletionAt = firstTimestamp + 1_000;
        Assert.True(await fixture.Store.TryBeginHandoffAsync(firstTransfer, firstCompletionAt, Token));
        IngestResult durable = await fixture.Ingest.FinalizeAdmittedAsync(firstTransfer, login.SourceId, "DSC0003.JPG", 3, Token);
        await fixture.Store.ReleaseHandoffAsync(firstTransfer, Token);

        await fixture.CreateSourceAsync("recovery-new", "new-camera-secret");
        CameraLogin otherLogin = (await fixture.Ingress.AuthenticateAsync("cam_recovery-new", "new-camera-secret", Token))!;
        await fixture.Ingress.AdmitAsync(otherLogin.Id, otherLogin.Username, "FTP_session-new", "/DCIM/DSC0005.JPG",
            CurrentTimestamp(), 1_024, 2, 600, Token);
        CameraUploadAdmission retained = Assert.Single(await fixture.Store.GetRecoverableAsync(Token), item => item.Id == firstTransfer);
        Assert.Equal(CameraTransferState.Received, retained.State);

        await using (var restarted = new ShutterSpoolDbContext(fixture.DatabaseOptions))
        {
            CameraIngressService recovering = fixture.CreateIngress(restarted);
            await new IngestService(new EfIngestJournal(restarted), fixture.Spool, TimeProvider.System).RecoverAsync(Token);
            await recovering.RecoverAsync(600, Token);
            CameraTransferStatus state = Assert.Single(await recovering.GetStatusAsync(login.SourceId, Token), item => item.Id == firstTransfer);
            Assert.Equal(CameraTransferState.Completed, state.State);
            IngestResult replay = await recovering.CompleteAsync(login.Id, login.Username, session, path, firstCompletionAt, 3, Token);
            Assert.Equal(durable.Asset.Id, replay.Asset.Id);
        }

        long secondTimestamp = firstTimestamp + 500_000_000;
        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, secondTimestamp, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream([6, 6]), 1_024, Token);
        Guid secondTransfer = Assert.Single(await fixture.Store.GetRecoverableAsync(Token), item => item.State == CameraTransferState.Received).Id;

        await using (var restarted = new ShutterSpoolDbContext(fixture.DatabaseOptions))
        {
            CameraIngressService recovering = fixture.CreateIngress(restarted);
            await recovering.RecoverAsync(600, Token);
            CameraTransferStatus state = Assert.Single(await recovering.GetStatusAsync(login.SourceId, Token), item => item.Id == secondTransfer);
            Assert.Equal(CameraTransferState.Quarantined, state.State);
            string[] retainedPayloads = EnumeratePayloads(fixture.SpoolRoot);
            Assert.Equal(2, retainedPayloads.Length);
            string quarantined = Assert.Single(retainedPayloads, path => !string.Equals(Path.GetFullPath(path),
                Path.GetFullPath(fixture.FinalPath(durable)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Assert.Equal(new byte[] { 6, 6 }, await File.ReadAllBytesAsync(quarantined, Token));
            Assert.Equal(1, await restarted.Assets.CountAsync(Token));
        }
    }

    [Fact]
    public async Task A_stale_admission_is_quarantined_before_it_can_block_the_next_camera_transfer()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        await fixture.CreateSourceAsync("deadline", "camera-secret");
        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_deadline", "camera-secret", Token))!;
        string path = "/DCIM/DSC0004.JPG";
        long now = CurrentTimestamp();
        long stale = now - 601_000_000_000;
        await fixture.Ingress.AdmitAsync(login.Id, login.Username, "FTP_old", path, stale, 1_024, 1, 600, Token);

        await fixture.Ingress.AdmitAsync(login.Id, login.Username, "FTP_new", path, now, 1_024, 1, 600, Token);

        Assert.Contains(await fixture.Ingress.GetStatusAsync(login.SourceId, Token), transfer => transfer.State == CameraTransferState.Quarantined);
        Assert.Contains(await fixture.Store.GetRecoverableAsync(Token), transfer => transfer.AdmissionTimestamp == now);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static long CurrentTimestamp() => checked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000);
    private static string[] EnumeratePayloads(string root) => [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".reason", StringComparison.Ordinal))];

    private sealed class CameraIngressFixture : IAsyncDisposable
    {
        private readonly ArchitectureTestDatabase databaseFixture;
        private readonly SecretProtector protector;

        private CameraIngressFixture(ArchitectureTestDatabase databaseFixture, ShutterSpoolDbContext database, SecretProtector protector)
        {
            this.databaseFixture = databaseFixture;
            Database = database;
            DatabaseOptions = databaseFixture.Options;
            ProtectionOptions = Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) });
            this.protector = protector;
            DirectoriesRoot = Path.Combine(databaseFixture.SpoolRoot, ".camera-directories");
            Spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = databaseFixture.SpoolRoot, ReservedBytes = 0 });
            Directories = new FileSystemCameraIngressDirectories(DirectoriesRoot);
            Store = new EfCameraIngressStore(database, TimeProvider.System);
            Ingest = new IngestService(new EfIngestJournal(database), Spool, TimeProvider.System);
            Ingress = CreateIngress(database);
        }

        public ShutterSpoolDbContext Database { get; }
        public DbContextOptions<ShutterSpoolDbContext> DatabaseOptions { get; }
        public IOptions<CredentialProtectionOptions> ProtectionOptions { get; }
        public string SpoolRoot => databaseFixture.SpoolRoot;
        public string DirectoriesRoot { get; }
        public FileSystemSpool Spool { get; }
        public FileSystemCameraIngressDirectories Directories { get; }
        public EfCameraIngressStore Store { get; }
        public IngestService Ingest { get; }
        public CameraIngressService Ingress { get; }

        public static async Task<CameraIngressFixture> CreateAsync()
        {
            ArchitectureTestDatabase fixture = await ArchitectureTestDatabase.CreateAsync();
            var database = new ShutterSpoolDbContext(fixture.Options);
            await new DatabaseInitializer(database).InitializeAsync(fixture.SpoolRoot, Token);
            var protectionOptions = Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) });
            return new(fixture, database, new SecretProtector(protectionOptions));
        }

        public async Task<Guid> CreateSourceAsync(string suffix, string secret)
        {
            Guid sourceId = Guid.NewGuid();
            Database.Sources.Add(new SourceEntity { Id = sourceId, Name = $"Camera {suffix}" });
            Database.SourceCredentials.Add(new SourceCredentialEntity
            {
                SourceId = sourceId,
                Username = $"cam_{suffix}",
                VirtualRoot = $"sources/{suffix}",
                SecretVerifier = protector.CreateVerifier(secret),
            });
            await Database.SaveChangesAsync(Token);
            return sourceId;
        }

        public CameraIngressService CreateIngress(ShutterSpoolDbContext database, IngestService? ingestOverride = null,
            ICameraIngressDirectories? directoriesOverride = null)
        {
            var store = ReferenceEquals(database, Database) ? Store : new EfCameraIngressStore(database, TimeProvider.System);
            var verifier = new SourceCredentialVerifier(new EfSourceConfigurationStore(database), protector,
                new SourceAccessAudit(NullLogger<SourceAccessAudit>.Instance));
            IngestService ingest = ingestOverride ?? (ReferenceEquals(database, Database)
                ? Ingest : new IngestService(new EfIngestJournal(database), Spool, TimeProvider.System));
            return new(verifier, store, directoriesOverride ?? Directories, ingest, TimeProvider.System,
                AuthenticationRecorder, ActivityPublisher);
        }

        public RecordingAuthenticationObservationRecorder AuthenticationRecorder { get; } = new();
        public RecordingTransferActivityPublisher ActivityPublisher { get; } = new();

        public string FinalPath(IngestResult result) => Path.Combine(databaseFixture.SpoolRoot,
            result.Asset.SpoolKey.Replace('/', Path.DirectorySeparatorChar));


        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            await databaseFixture.DisposeAsync();
        }
    }

    private sealed class RecordingAuthenticationObservationRecorder : ISourceAuthenticationObservationRecorder
    {
        public List<SourceAuthenticationObservation> Observations { get; } = [];
        public List<DateTimeOffset> Unassigned { get; } = [];
        public void Record(SourceAuthenticationObservation observation) => Observations.Add(observation);
        public void RecordUnassigned(DateTimeOffset observedAt) => Unassigned.Add(observedAt);
    }

    private sealed class RecordingTransferActivityPublisher : ICameraTransferActivityPublisher
    {
        public List<CameraTransferActivity> Items { get; } = [];
        public void Publish(CameraTransferActivity activity) => Items.Add(activity);
    }

    private sealed class FailingCommitJournal(IIngestJournal inner) : IIngestJournal
    {
        public Task<IngestResult?> FindCommittedResultAsync(Guid operationId, CancellationToken cancellationToken) =>
            inner.FindCommittedResultAsync(operationId, cancellationToken);
        public Task<IngestOperation?> FindOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
            inner.FindOperationAsync(operationId, cancellationToken);
        public Task<OriginalRemovalRecovery> GetOriginalRemovalRecoveryAsync(Guid assetId, CancellationToken cancellationToken) =>
            inner.GetOriginalRemovalRecoveryAsync(assetId, cancellationToken);
        public Task CreateAsync(IngestOperation operation, CancellationToken cancellationToken) => inner.CreateAsync(operation, cancellationToken);
        public Task MarkPromotedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken) =>
            inner.MarkPromotedAsync(operationId, write, cancellationToken);
        public Task MarkReceivedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken) =>
            inner.MarkReceivedAsync(operationId, write, cancellationToken);
        public Task CommitAsync(Guid operationId, DateTimeOffset ingestedAt, CancellationToken cancellationToken) =>
            throw new IOException("Injected Asset commit failure.");
        public Task<IReadOnlyList<IngestOperation>> GetAllAsync(CancellationToken cancellationToken) => inner.GetAllAsync(cancellationToken);
        public Task QuarantineAsync(Guid operationId, string reason, CancellationToken cancellationToken) =>
            inner.QuarantineAsync(operationId, reason, cancellationToken);
    }

    private sealed class FailingReceiveSpool(FileSystemSpool inner) : ISpool, IAdmittedSpool
    {
        private readonly IAdmittedSpool admitted = inner;
        public string GetPartialKey(Guid sourceId, Guid assetId) => inner.GetPartialKey(sourceId, assetId);
        public Task CheckAdmissionAsync(long? maximumUploadBytes, CancellationToken cancellationToken) => inner.CheckAdmissionAsync(maximumUploadBytes, cancellationToken);
        public Task<SpoolWrite> WriteAsync(Guid sourceId, Guid assetId, Stream content, long? maximumUploadBytes, CancellationToken cancellationToken) =>
            inner.WriteAsync(sourceId, assetId, content, maximumUploadBytes, cancellationToken);
        public Task<SpoolWrite?> FindFinalAsync(IngestOperation operation, CancellationToken cancellationToken) => inner.FindFinalAsync(operation, cancellationToken);
        public Task QuarantineAsync(IngestOperation operation, string reason, CancellationToken cancellationToken) => inner.QuarantineAsync(operation, reason, cancellationToken);
        public Task QuarantineUntrackedAsync(IReadOnlySet<string> knownKeys, CancellationToken cancellationToken) => inner.QuarantineUntrackedAsync(knownKeys, cancellationToken);
        public Task CheckAdmittedAdmissionAsync(long requiredFreeBytes, CancellationToken cancellationToken) => admitted.CheckAdmittedAdmissionAsync(requiredFreeBytes, cancellationToken);
        public string GetReceiveKey(Guid assetId) => admitted.GetReceiveKey(assetId);
        public async Task<SpoolWrite> ReceiveAdmittedAsync(string receiveKey, string partialKey, Stream content, long maximumUploadBytes, CancellationToken cancellationToken)
        {
            await admitted.ReceiveAdmittedAsync(receiveKey, partialKey, content, maximumUploadBytes, cancellationToken);
            throw new IOException("Injected receipt failure after writing the complete candidate.");
        }
        public Task<SpoolWrite?> VerifyReceivedAsync(IngestOperation operation, long expectedSize, CancellationToken cancellationToken) =>
            admitted.VerifyReceivedAsync(operation, expectedSize, cancellationToken);
    }
}
