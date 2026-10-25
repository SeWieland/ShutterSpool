using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Collections;
using ShutterSpool.Application.Captures;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Application.Sources;
using ShutterSpool.Domain.Collections;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.IntegrationTests;

public sealed class CollectionPostgresTests
{

    [Fact]
    public async Task Fresh_database_has_one_protected_collection_and_create_rename_delete_are_revision_guarded()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        CancellationToken token = TestContext.Current.CancellationToken;
        await database.Database.MigrateAsync(token);
        CollectionEntity[] seeded = await database.Collections.AsNoTracking().ToArrayAsync(token);
        Assert.False(database.Database.HasPendingModelChanges());
        CollectionEntity builtIn = Assert.Single(seeded);
        Assert.Equal(CollectionName.BuiltInId, builtIn.Id);
        Assert.Equal("ShutterSpool", builtIn.Path);
        Assert.True(builtIn.IsBuiltIn);

        var store = new EfCollectionManagementStore(database);
        CollectionSummary created = await store.CreateAsync(CollectionName.Parse("/Events/Berlin"), token);
        Assert.Equal("Events/Berlin", created.Path);
        CollectionSummary other = await store.CreateAsync(CollectionName.Parse("Travel/Oslo"), token);
        CollectionWrite duplicate = await store.RenameAsync(other.Id, other.Revision,
            CollectionName.Parse("/events/berlin"), token);
        Assert.False(duplicate.Applied);
        Assert.Equal(other.Id, duplicate.Current!.Id);

        CollectionWrite rename = await store.RenameAsync(created.Id, created.Revision,
            CollectionName.Parse("Travel/Berlin"), token);
        Assert.True(rename.Applied);
        Assert.Equal(1, rename.Current!.Revision);
        CollectionWrite staleDelete = await store.DeleteAsync(created.Id, created.Revision, token);
        Assert.False(staleDelete.Applied);
        CollectionWrite delete = await store.DeleteAsync(created.Id, rename.Current.Revision, token);
        Assert.True(delete.Applied);
        CollectionWrite protectedDelete = await store.DeleteAsync(builtIn.Id, builtIn.Revision, token);
        Assert.False(protectedDelete.Applied);
        Assert.True(protectedDelete.Referenced);
    }

    [Fact]
    public async Task Collection_summaries_bound_each_reference_count_and_include_archived_sources_and_trashed_captures()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        CancellationToken token = TestContext.Current.CancellationToken;
        await database.Database.MigrateAsync(token);
        var store = new EfCollectionManagementStore(database);
        CollectionSummary large = await store.CreateAsync(CollectionName.Parse("Many references"), token);
        CollectionSummary small = await store.CreateAsync(CollectionName.Parse("One reference"), token);

        foreach (CollectionSummary collection in new[] { large, small })
        {
            int count = collection.Id == large.Id ? 1002 : 1;
            for (int index = 0; index < count; index++)
            {
                Guid sourceId = Guid.NewGuid();
                database.Sources.Add(new SourceEntity { Id = sourceId, Name = $"camera-{sourceId:N}",
                    CollectionId = collection.Id, IsArchived = true });
                database.Captures.Add(new CaptureEntity { Id = Guid.NewGuid(), SourceId = sourceId,
                    CollectionId = collection.Id, NormalizedStem = $"frame-{index}", RetentionDisposition = "Trash" });
            }
        }
        await database.SaveChangesAsync(token);

        CollectionPage page = await store.ListAsync("", 10, null, null, token);
        CollectionSummary bounded = Assert.Single(page.Items, item => item.Id == large.Id);
        Assert.Equal(1001, bounded.SourceReferences);
        Assert.Equal(1001, bounded.CaptureReferences);
        CollectionSummary single = Assert.Single(page.Items, item => item.Id == small.Id);
        Assert.Equal(1, single.SourceReferences);
        Assert.Equal(1, single.CaptureReferences);
        CollectionSummary empty = Assert.Single(page.Items, item => item.Id == CollectionName.BuiltInId);
        Assert.Equal(0, empty.SourceReferences);
        Assert.Equal(0, empty.CaptureReferences);
        Assert.Equal(bounded, await store.GetAsync(large.Id, token));
    }

    [Fact]
    public async Task Source_default_and_capture_moves_are_authoritative_and_reject_stale_or_referenced_deletes()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), captureId = Guid.NewGuid();
        CollectionSummary selected;
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            selected = await new EfCollectionManagementStore(seed).CreateAsync(CollectionName.Parse("Berlin"), token);
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = CollectionName.BuiltInId,
                CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 });
            seed.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "camera-user",
                VirtualRoot = "private/source", SecretVerifier = new byte[32] });
            seed.Captures.Add(new CaptureEntity { Id = captureId, SourceId = sourceId, CollectionId = CollectionName.BuiltInId,
                NormalizedStem = "frame", FirstAcceptedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync(token);
        }

        await using (var update = new ShutterSpoolDbContext(options))
        {
            SourceConfigurationWrite result = await new EfSourceConfigurationStore(update).UpdateConfigurationAsync(
                sourceId, 0, "camera", SourceCurationMode.Delay, 3_600, SourceType.CameraFtpUpload,
                SourceConnectionProfile.ModernFtps, selected.Id, token);
            Assert.True(result.Applied);
            Assert.Equal(selected.Id, result.Current!.CollectionId);
        }

        await using (var move = new ShutterSpoolDbContext(options))
        {
            var assignment = new EfCaptureCollectionAssignmentStore(move);
            CollectionMoveResult applied = await assignment.MoveAsync(captureId, 0, selected.Id, token);
            Assert.Equal(CollectionMoveStatus.Applied, applied.Status);
            Assert.Equal(1, applied.CurrentRevision);
            Assert.Equal(selected.Id, applied.Current!.CurrentCollection!.Id);
            CollectionMoveResult conflict = await assignment.MoveAsync(captureId, 0, CollectionName.BuiltInId, token);
            Assert.Equal(CollectionMoveStatus.Conflict, conflict.Status);
            Assert.Equal(selected.Id, conflict.CurrentCollectionId);
            Assert.Equal(selected.Id, conflict.Current!.CurrentCollection!.Id);
        }

        await using (var archive = new ShutterSpoolDbContext(options))
        {
            SourceRemovalWrite removed = await new EfSourceConfigurationStore(archive).RemoveAsync(sourceId, 1, token);
            Assert.True(removed.Applied);
            Assert.False(removed.Removed);
            Assert.True(removed.Current!.IsArchived);
        }

        await using var verify = new ShutterSpoolDbContext(options);
        Assert.Equal(1, await verify.Sources.CountAsync(item => item.CollectionId == selected.Id, token));
        Assert.Equal(1, await verify.Captures.CountAsync(item => item.CollectionId == selected.Id, token));
        CollectionWrite blocked = await new EfCollectionManagementStore(verify).DeleteAsync(selected.Id, selected.Revision, token);
        Assert.False(blocked.Applied);
        Assert.True(blocked.Referenced);
        Assert.Equal(1, blocked.Current!.SourceReferences);
        Assert.Equal(1, blocked.Current.CaptureReferences);
    }

    [Fact]
    public async Task Concurrent_equivalent_creates_commit_at_most_one_collection()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using (var seed = new ShutterSpoolDbContext(fixture.Options))
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
        Task<bool>[] writes = [.. new[] { "Berlin", "/berlin" }.Select(async name =>
        {
            await using var context = new ShutterSpoolDbContext(fixture.Options);
            try
            {
                await new EfCollectionManagementStore(context).CreateAsync(CollectionName.Parse(name), TestContext.Current.CancellationToken);
                return true;
            }
            catch (InvalidOperationException) { return false; }
        })];

        Assert.Single(await Task.WhenAll(writes), applied => applied);
        await using var verify = new ShutterSpoolDbContext(fixture.Options);
        Assert.Equal(2, await verify.Collections.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Grouping_uses_current_source_default_for_new_captures_and_current_membership_for_complements()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), rawId = Guid.NewGuid(), jpegId = Guid.NewGuid();
        Guid firstCollectionId, laterCollectionId;
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            var collections = new EfCollectionManagementStore(seed);
            firstCollectionId = (await collections.CreateAsync(CollectionName.Parse("Berlin"), token)).Id;
            laterCollectionId = (await collections.CreateAsync(CollectionName.Parse("Travel"), token)).Id;
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = firstCollectionId,
                CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 });
            seed.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "camera-user",
                VirtualRoot = "private/source", SecretVerifier = new byte[32] });
            DateTime localTime = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);
            rawId = AddAsset(seed, sourceId, "frame.ARW", "raw", "RAW", DateTimeOffset.UtcNow.AddMinutes(-2), localTime);
            jpegId = AddAsset(seed, sourceId, "frame.JPG", "jpeg", "JPEG", DateTimeOffset.UtcNow.AddMinutes(-1), localTime);
            await seed.SaveChangesAsync(token);
        }

        await using (var group = new ShutterSpoolDbContext(options))
            Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(group)).ProcessOneAsync(token));
        await using (var update = new ShutterSpoolDbContext(options))
        {
            SourceConfigurationWrite changed = await new EfSourceConfigurationStore(update).UpdateConfigurationAsync(
                sourceId, 0, "camera", SourceCurationMode.Delay, 3_600, SourceType.CameraFtpUpload,
                SourceConnectionProfile.ModernFtps, laterCollectionId, token);
            Assert.True(changed.Applied);
        }
        await using (var group = new ShutterSpoolDbContext(options))
            Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(group)).ProcessOneAsync(token));

        await using var verify = new ShutterSpoolDbContext(options);
        Guid[] captureIds = await verify.CaptureAssets.Where(item => item.AssetId == rawId || item.AssetId == jpegId)
            .Select(item => item.CaptureId).Distinct().ToArrayAsync(token);
        Assert.Single(captureIds);
        Assert.Equal(firstCollectionId, await verify.Captures.Where(item => item.Id == captureIds[0])
            .Select(item => item.CollectionId).SingleAsync(token));
        Assert.Equal(laterCollectionId, await verify.Sources.Where(item => item.Id == sourceId)
            .Select(item => item.CollectionId).SingleAsync(token));
    }

    [Fact]
    public async Task Source_default_racing_new_grouping_commits_one_authoritative_collection_assignment()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), assetId;
        Guid originalDefault, updatedDefault;
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            var collections = new EfCollectionManagementStore(seed);
            originalDefault = (await collections.CreateAsync(CollectionName.Parse("Before"), token)).Id;
            updatedDefault = (await collections.CreateAsync(CollectionName.Parse("After"), token)).Id;
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = originalDefault,
                CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 });
            seed.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "camera-user",
                VirtualRoot = "private/source", SecretVerifier = new byte[32] });
            assetId = AddAsset(seed, sourceId, "frame.JPG", "racing-group", "JPEG", DateTimeOffset.UtcNow,
                new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified));
            await seed.SaveChangesAsync(token);
        }

        Task<SourceConfigurationWrite> changeDefault = Task.Run(async () =>
        {
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfSourceConfigurationStore(context).UpdateConfigurationAsync(sourceId, 0, "camera",
                SourceCurationMode.Delay, 3_600, SourceType.CameraFtpUpload, SourceConnectionProfile.ModernFtps,
                updatedDefault, token);
        }, token);
        Task<bool> group = Task.Run(async () =>
        {
            await using var context = new ShutterSpoolDbContext(options);
            return await new CaptureGroupingService(new EfCaptureGroupingStore(context)).ProcessOneAsync(token);
        }, token);
        SourceConfigurationWrite changed = await changeDefault;
        Assert.True(changed.Applied);
        Assert.True(await group);

        await using var verify = new ShutterSpoolDbContext(options);
        Guid[] memberships = await (from member in verify.CaptureAssets
            join capture in verify.Captures on member.CaptureId equals capture.Id
            where member.AssetId == assetId
            select capture.CollectionId).ToArrayAsync(token);
        Assert.Equal(updatedDefault, await verify.Sources.Where(item => item.Id == sourceId)
            .Select(item => item.CollectionId).SingleAsync(token));
        Assert.Single(memberships);
        Assert.Contains(memberships[0], new[] { originalDefault, updatedDefault });
    }

    [Fact]
    public async Task Capture_reads_keep_frozen_release_collection_after_rename_and_delete()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), captureId = Guid.NewGuid();
        CollectionSummary releasedCollection;
        await using (var database = new ShutterSpoolDbContext(options))
        {
            await database.Database.MigrateAsync(token);
            releasedCollection = await new EfCollectionManagementStore(database).CreateAsync(
                CollectionName.Parse("Events/Berlin"), token);
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = releasedCollection.Id,
                CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 });
            database.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "camera-user",
                VirtualRoot = "private/source", SecretVerifier = new byte[32] });
            database.Captures.Add(new CaptureEntity { Id = captureId, SourceId = sourceId,
                CollectionId = releasedCollection.Id, NormalizedStem = "frame" });
            await database.SaveChangesAsync(token);
        }

        await using (var release = new ShutterSpoolDbContext(options))
            Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(release, TimeProvider.System))
                .ReleaseNowAsync(captureId, 0, token)).Succeeded);
        await using (var move = new ShutterSpoolDbContext(options))
        {
            CollectionMoveResult moved = await new EfCaptureCollectionAssignmentStore(move).MoveAsync(
                captureId, 1, CollectionName.BuiltInId, token);
            Assert.Equal(CollectionMoveStatus.Applied, moved.Status);
        }
        await using (var update = new ShutterSpoolDbContext(options))
            Assert.True((await new EfSourceConfigurationStore(update).UpdateConfigurationAsync(sourceId, 0, "camera",
                SourceCurationMode.Delay, 3_600, SourceType.CameraFtpUpload, SourceConnectionProfile.ModernFtps,
                CollectionName.BuiltInId, token)).Applied);
        await using (var rename = new ShutterSpoolDbContext(options))
            Assert.True((await new EfCollectionManagementStore(rename).RenameAsync(releasedCollection.Id,
                releasedCollection.Revision, CollectionName.Parse("Events/Retitled"), token)).Applied);

        await using (var read = new ShutterSpoolDbContext(options))
        {
            CaptureRead capture = (await new CaptureReadStore(read).GetAsync(captureId, token))!;
            Assert.Equal(CollectionName.BuiltInId, capture.CurrentCollection!.Id);
            Assert.Equal(releasedCollection.Id, capture.ReleasedCollection!.Id);
            Assert.Equal("Events/Berlin", capture.ReleasedCollection.Path);
        }
        await using (var delete = new ShutterSpoolDbContext(options))
        {
            CollectionWrite removed = await new EfCollectionManagementStore(delete).DeleteAsync(
                releasedCollection.Id, releasedCollection.Revision + 1, token);
            Assert.True(removed.Applied);
        }
        await using (var read = new ShutterSpoolDbContext(options))
        {
            CaptureRead capture = (await new CaptureReadStore(read).GetAsync(captureId, token))!;
            Assert.Equal(releasedCollection.Id, capture.ReleasedCollection!.Id);
            Assert.Equal("Berlin", capture.ReleasedCollection.Name);
            Assert.Equal("Events/Berlin", capture.ReleasedCollection.Path);
        }
    }

    [Fact]
    public async Task Collection_delete_racing_a_capture_move_never_leaves_a_dangling_reference()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid collectionId = Guid.NewGuid(), sourceId = Guid.NewGuid(), captureId = Guid.NewGuid();
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            seed.Collections.Add(new CollectionEntity { Id = collectionId, Name = "Berlin", Path = "Berlin", Identity = "BERLIN" });
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = CollectionName.BuiltInId });
            seed.Captures.Add(new CaptureEntity { Id = captureId, SourceId = sourceId, CollectionId = CollectionName.BuiltInId,
                NormalizedStem = "frame", FirstAcceptedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync(token);
        }

        Task<CollectionMoveResult> move = Task.Run(async () =>
        {
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfCaptureCollectionAssignmentStore(context).MoveAsync(captureId, 0, collectionId, token);
        }, token);
        Task<CollectionWrite> delete = Task.Run(async () =>
        {
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfCollectionManagementStore(context).DeleteAsync(collectionId, 0, token);
        }, token);
        CollectionMoveResult moveResult = await move;
        CollectionWrite deleteResult = await delete;

        await using var verify = new ShutterSpoolDbContext(options);
        Guid finalMembership = await verify.Captures.Where(item => item.Id == captureId)
            .Select(item => item.CollectionId).SingleAsync(token);
        bool collectionSurvived = await verify.Collections.AnyAsync(item => item.Id == collectionId, token);
        Assert.True(collectionSurvived && finalMembership == collectionId
            && moveResult.Status == CollectionMoveStatus.Applied && !deleteResult.Applied
            || !collectionSurvived && finalMembership == CollectionName.BuiltInId
            && moveResult.Status == CollectionMoveStatus.TargetMissing && deleteResult.Applied);
    }

    [Fact]
    public async Task Equivalent_concurrent_renames_commit_one_name_and_report_one_conflict()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            seed.Collections.AddRange(
                new CollectionEntity { Id = first, Name = "One", Path = "One", Identity = "ONE" },
                new CollectionEntity { Id = second, Name = "Two", Path = "Two", Identity = "TWO" });
            await seed.SaveChangesAsync(token);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CollectionWrite>[] renames = [.. new[] { first, second }.Select(async id =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfCollectionManagementStore(context).RenameAsync(id, 0,
                CollectionName.Parse("Shared/Name"), token);
        })];
        start.SetResult();
        CollectionWrite[] results = await Task.WhenAll(renames);

        Assert.Single(results, result => result.Applied);
        Assert.Single(results, result => result.NameConflict);
        await using var verify = new ShutterSpoolDbContext(options);
        Assert.Equal(1, await verify.Collections.CountAsync(item => item.Identity == "SHARED/NAME", token));
    }

    [Fact]
    public async Task Collection_delete_racing_source_creation_or_default_selection_never_leaves_a_dangling_reference()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid collectionId = Guid.NewGuid(), defaultTargetId = Guid.NewGuid(), defaultSourceId = Guid.NewGuid();
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            seed.Collections.Add(new CollectionEntity { Id = collectionId, Name = "Source target", Path = "Source target", Identity = "SOURCE TARGET" });
            seed.Collections.Add(new CollectionEntity { Id = defaultTargetId, Name = "Default target", Path = "Default target", Identity = "DEFAULT TARGET" });
            seed.Sources.Add(new SourceEntity { Id = defaultSourceId, Name = "default-camera", CollectionId = CollectionName.BuiltInId,
                CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 });
            seed.SourceCredentials.Add(new SourceCredentialEntity { SourceId = defaultSourceId, Username = "default-camera-user",
                VirtualRoot = "private/default-camera", SecretVerifier = new byte[32] });
            await seed.SaveChangesAsync(token);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CollectionWrite> delete = Task.Run(async () =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfCollectionManagementStore(context).DeleteAsync(collectionId, 0, token);
        }, token);
        Task<bool> create = Task.Run(async () =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            try
            {
                await new EfSourceConfigurationStore(context).CreateAsync(new SourceProvisioning(Guid.NewGuid(),
                    "camera", SourceCurationMode.Delay, 3_600, "race-user", "private/race", new byte[32],
                    SourceType.CameraFtpUpload, SourceConnectionProfile.ModernFtps, collectionId), token);
                return true;
            }
            catch (ArgumentException) { return false; }
        }, token);
        start.SetResult();
        CollectionWrite deleted = await delete;
        bool created = await create;

        await using var verify = new ShutterSpoolDbContext(options);
        bool collectionExists = await verify.Collections.AnyAsync(item => item.Id == collectionId, token);
        bool sourceReferences = await verify.Sources.AnyAsync(item => item.CollectionId == collectionId, token);
        Assert.True(collectionExists && sourceReferences && created && !deleted.Applied
            || !collectionExists && !sourceReferences && !created && deleted.Applied);

        {
            var startDefault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<CollectionWrite> remove = Task.Run(async () =>
            {
                await startDefault.Task.WaitAsync(token);
                await using var context = new ShutterSpoolDbContext(options);
                return await new EfCollectionManagementStore(context).DeleteAsync(defaultTargetId, 0, token);
            }, token);
            Task<bool> changeDefault = Task.Run(async () =>
            {
                await startDefault.Task.WaitAsync(token);
                await using var context = new ShutterSpoolDbContext(options);
                try
                {
                    SourceConfigurationWrite result = await new EfSourceConfigurationStore(context).UpdateConfigurationAsync(
                        defaultSourceId, 0, "default-camera",
                        SourceCurationMode.Delay, 3_600, SourceType.CameraFtpUpload, SourceConnectionProfile.ModernFtps,
                        defaultTargetId, token);
                    return result.Applied;
                }
                catch (ArgumentException) { return false; }
            }, token);
            startDefault.SetResult();
            CollectionWrite removeResult = await remove;
            bool defaultChanged = await changeDefault;
            Assert.True(removeResult.Applied && !defaultChanged || !removeResult.Applied && defaultChanged);
            Assert.Equal(defaultChanged, await verify.Sources.AnyAsync(item => item.CollectionId == defaultTargetId, token));
            Assert.Equal(!removeResult.Applied, await verify.Collections.AnyAsync(item => item.Id == defaultTargetId, token));
        }
    }

    [Fact]
    public async Task Capture_move_racing_release_freezes_a_consistent_collection_revision()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), captureId = Guid.NewGuid(), targetId;
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            targetId = (await new EfCollectionManagementStore(seed).CreateAsync(CollectionName.Parse("After"), token)).Id;
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = CollectionName.BuiltInId,
                CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 });
            seed.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "camera-user",
                VirtualRoot = "private/source", SecretVerifier = new byte[32] });
            seed.Captures.Add(new CaptureEntity { Id = captureId, SourceId = sourceId,
                CollectionId = CollectionName.BuiltInId, NormalizedStem = "frame" });
            await seed.SaveChangesAsync(token);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CaptureMutationResult> release = Task.Run(async () =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            return await new CaptureCurationService(new EfCaptureCurationStore(context, TimeProvider.System))
                .ReleaseNowAsync(captureId, 0, token);
        }, token);
        Task<CollectionMoveResult> move = Task.Run(async () =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfCaptureCollectionAssignmentStore(context).MoveAsync(captureId, 0, targetId, token);
        }, token);
        start.SetResult();
        CaptureMutationResult released = await release;
        CollectionMoveResult moved = await move;
        await using var verify = new ShutterSpoolDbContext(options);
        CaptureEntity final = await verify.Captures.SingleAsync(item => item.Id == captureId, token);
        Assert.Equal(1, final.Revision);
        if (released.Succeeded)
        {
            Assert.Equal(CollectionMoveStatus.Conflict, moved.Status);
            Assert.Equal(CollectionName.BuiltInId, final.CollectionId);
            Assert.NotNull(final.ReleasedAt);
            Assert.Equal(CollectionName.BuiltInId, final.ReleasedCollectionId);
            Assert.Equal("ShutterSpool", final.ReleasedCollectionName);
            Assert.Equal("ShutterSpool", final.ReleasedCollectionPath);
        }
        else
        {
            Assert.Equal(CollectionMoveStatus.Applied, moved.Status);
            Assert.Equal(targetId, final.CollectionId);
            Assert.Null(final.ReleasedAt);
            Assert.Null(final.ReleasedCollectionId);
            Assert.Null(final.ReleasedCollectionName);
            Assert.Null(final.ReleasedCollectionPath);
        }
        Assert.Equal(final.CollectionId, moved.CurrentCollectionId);
        Assert.Equal(final.CollectionId, moved.Current!.CurrentCollection!.Id);
        Assert.Equal(final.Revision, moved.CurrentRevision);
        Assert.Equal(final.Revision, released.State!.Revision);
    }

    [Fact]
    public async Task Collection_rename_racing_release_freezes_one_complete_name_and_path()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), captureId = Guid.NewGuid();
        CollectionSummary collection;
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            collection = await new EfCollectionManagementStore(seed).CreateAsync(CollectionName.Parse("Events/Before"), token);
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = collection.Id,
                CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 });
            seed.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "camera-user",
                VirtualRoot = "private/source", SecretVerifier = new byte[32] });
            seed.Captures.Add(new CaptureEntity { Id = captureId, SourceId = sourceId,
                CollectionId = collection.Id, NormalizedStem = "frame" });
            await seed.SaveChangesAsync(token);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CollectionWrite> rename = Task.Run(async () =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfCollectionManagementStore(context).RenameAsync(collection.Id, 0,
                CollectionName.Parse("Events/After"), token);
        }, token);
        Task<CaptureMutationResult> release = Task.Run(async () =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            return await new CaptureCurationService(new EfCaptureCurationStore(context, TimeProvider.System))
                .ReleaseNowAsync(captureId, 0, token);
        }, token);
        start.SetResult();
        CollectionWrite renamed = await rename;
        CaptureMutationResult released = await release;
        Assert.True(renamed.Applied);
        Assert.True(released.Succeeded);

        await using var verify = new ShutterSpoolDbContext(options);
        CaptureEntity capture = await verify.Captures.SingleAsync(item => item.Id == captureId, token);
        Assert.Equal(collection.Id, capture.ReleasedCollectionId);
        Assert.True(capture.ReleasedCollectionPath == "Events/Before" && capture.ReleasedCollectionName == "Before"
            || capture.ReleasedCollectionPath == "Events/After" && capture.ReleasedCollectionName == "After");
        Assert.Equal("Events/After", await verify.Collections.Where(item => item.Id == collection.Id)
            .Select(item => item.Path).SingleAsync(token));
    }

    [Theory]
    [InlineData("frame.JPG", "JPEG", false)]
    [InlineData("frame.ARW", "RAW", true)]
    public async Task Complementary_or_replayed_grouping_racing_manual_move_keeps_one_capture_and_authoritative_membership(
        string relatedName, string role, bool replay)
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), firstAsset, secondAsset, captureId, targetId;
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            targetId = (await new EfCollectionManagementStore(seed).CreateAsync(CollectionName.Parse("Manual"), token)).Id;
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = CollectionName.BuiltInId,
                CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 });
            seed.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "camera-user",
                VirtualRoot = "private/source", SecretVerifier = new byte[32] });
            DateTime local = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);
            firstAsset = AddAsset(seed, sourceId, "frame.ARW", "move-group-raw", "RAW", DateTimeOffset.UtcNow, local);
            await seed.SaveChangesAsync(token);
        }
        await using (var group = new ShutterSpoolDbContext(options))
            Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(group)).ProcessOneAsync(token));
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            captureId = await seed.CaptureAssets.Where(item => item.AssetId == firstAsset).Select(item => item.CaptureId).SingleAsync(token);
            secondAsset = AddAsset(seed, sourceId, relatedName, "move-group-related", role, DateTimeOffset.UtcNow.AddSeconds(1),
                new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified));
            await seed.SaveChangesAsync(token);
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> complement = Task.Run(async () =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            return await new CaptureGroupingService(new EfCaptureGroupingStore(context)).ProcessOneAsync(token);
        }, token);
        Task<CollectionMoveResult> move = Task.Run(async () =>
        {
            await start.Task.WaitAsync(token);
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfCaptureCollectionAssignmentStore(context).MoveAsync(captureId, 0, targetId, token);
        }, token);
        start.SetResult();
        Assert.True(await complement);
        CollectionMoveResult moved = await move;
        Assert.Contains(moved.Status, new[] { CollectionMoveStatus.Applied, CollectionMoveStatus.Conflict });

        await using var verify = new ShutterSpoolDbContext(options);
        Assert.Equal(captureId, await verify.CaptureAssets.Where(item => item.AssetId == secondAsset)
            .Select(item => item.CaptureId).SingleAsync(token));
        CaptureAssetEntity related = await verify.CaptureAssets.SingleAsync(item => item.AssetId == secondAsset, token);
        Assert.Equal(!replay, related.IsPrimary);
        Assert.Equal(replay ? firstAsset : null, related.RelatedAssetId);
        Guid finalCollection = await verify.Captures.Where(item => item.Id == captureId)
            .Select(item => item.CollectionId).SingleAsync(token);
        Assert.Equal(moved.Status == CollectionMoveStatus.Applied ? targetId : CollectionName.BuiltInId, finalCollection);
        Assert.Equal(finalCollection, moved.CurrentCollectionId);
        Assert.Equal(finalCollection, moved.Current!.CurrentCollection!.Id);
        Assert.Equal(1, await verify.Captures.CountAsync(token));
        Assert.Equal(2, await verify.CaptureAssets.CountAsync(item => item.CaptureId == captureId, token));
        await using var replayContext = new ShutterSpoolDbContext(options);
        Assert.False(await new CaptureGroupingService(new EfCaptureGroupingStore(replayContext)).ProcessOneAsync(token));
        Assert.Equal(finalCollection, await replayContext.Captures.Where(item => item.Id == captureId)
            .Select(item => item.CollectionId).SingleAsync(token));
    }

    [Fact]
    public async Task Target_deletion_racing_bulk_move_commits_only_valid_collection_references()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), targetId = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            seed.Collections.Add(new CollectionEntity { Id = targetId, Name = "Bulk target", Path = "Bulk target", Identity = "BULK TARGET" });
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = CollectionName.BuiltInId });
            seed.Captures.AddRange(
                new CaptureEntity { Id = first, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "first" },
                new CaptureEntity { Id = second, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "second" });
            await seed.SaveChangesAsync(token);
        }

        var existsChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueBulk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CollectionMoveResult[]> bulk = Task.Run(async () =>
        {
            await using var context = new ShutterSpoolDbContext(options);
            var gated = new GatedCollectionAssignmentStore(new EfCaptureCollectionAssignmentStore(context),
                existsChecked, continueBulk);
            return await new CaptureCollectionAssignmentService(gated)
                .MoveAsync(targetId, [new(first, 0), new(second, 0)], token);
        }, token);
        await existsChecked.Task.WaitAsync(token);
        await using (var context = new ShutterSpoolDbContext(options))
        {
            CollectionWrite deletion = await new EfCollectionManagementStore(context).DeleteAsync(targetId, 0, token);
            Assert.True(deletion.Applied);
        }
        continueBulk.SetResult();
        CollectionMoveResult[] moves = await bulk;

        await using var verify = new ShutterSpoolDbContext(options);
        bool survived = await verify.Collections.AnyAsync(item => item.Id == targetId, token);
        Guid[] membership = await verify.Captures.Where(item => item.Id == first || item.Id == second)
            .Select(item => item.CollectionId).ToArrayAsync(token);
        Assert.False(survived);
        Assert.All(membership, id => Assert.Equal(CollectionName.BuiltInId, id));
        Assert.All(moves, item => Assert.Equal(CollectionMoveStatus.TargetMissing, item.Status));
    }

    private sealed class GatedCollectionAssignmentStore(ICaptureCollectionAssignmentStore inner,
        TaskCompletionSource existsChecked, TaskCompletionSource continueBulk) : ICaptureCollectionAssignmentStore
    {
        public async Task<bool> ExistsAsync(Guid collectionId, CancellationToken token)
        {
            bool exists = await inner.ExistsAsync(collectionId, token);
            existsChecked.SetResult();
            await continueBulk.Task.WaitAsync(token);
            return exists;
        }

        public Task<CollectionMoveResult> MoveAsync(Guid captureId, int expectedRevision, Guid collectionId,
            CancellationToken token) => inner.MoveAsync(captureId, expectedRevision, collectionId, token);
    }

    [Fact]
    public async Task Bulk_moves_keep_per_capture_partial_results_and_collection_filter_is_applied_before_paging()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), first = Guid.NewGuid(), stale = Guid.NewGuid(), trash = Guid.NewGuid(), targetId;
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            targetId = (await new EfCollectionManagementStore(seed).CreateAsync(CollectionName.Parse("Camera/One"), token)).Id;
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = CollectionName.BuiltInId });
            seed.Captures.AddRange(
                new CaptureEntity { Id = first, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "first", Rating = 3 },
                new CaptureEntity { Id = stale, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "stale" },
                new CaptureEntity { Id = trash, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "trash", RetentionDisposition = "Trash" });
            await seed.SaveChangesAsync(token);
        }

        await using (var context = new ShutterSpoolDbContext(options))
        {
            var service = new CaptureCollectionAssignmentService(new EfCaptureCollectionAssignmentStore(context));
            CollectionMoveResult[] results = await service.MoveAsync(targetId,
            [new(first, 0), new(stale, 7), new(trash, 0), new(Guid.NewGuid(), 0)], token);
            Assert.Equal([CollectionMoveStatus.Applied, CollectionMoveStatus.Conflict,
                CollectionMoveStatus.NotMovable, CollectionMoveStatus.NotFound], results.Select(item => item.Status));
            Assert.Equal(targetId, results[0].Current!.CurrentCollection!.Id);
            Assert.Equal(CollectionName.BuiltInId, results[1].Current!.CurrentCollection!.Id);
        }

        await using var browse = new ShutterSpoolDbContext(options);
        var reads = new ShutterSpool.Application.Captures.CaptureReadService(new CaptureReadStore(browse));
        CaptureInboxPage page = await reads.GetInboxAsync(1, null, null,
            new ShutterSpool.Application.Captures.CaptureReadFilter(CollectionIds: [targetId]), token);
        Assert.Single(page.Items);
        Assert.Equal(first, page.Items[0].Id);
        Assert.Equal(targetId, page.Items[0].CurrentCollection!.Id);
        Assert.Equal(3, await browse.Captures.Where(item => item.Id == first).Select(item => item.Rating).SingleAsync(token));
        Assert.Equal(0, await browse.Captures.Where(item => item.Id == stale || item.Id == trash)
            .Select(item => item.CollectionId == targetId ? 1 : 0).SumAsync(token));
    }

    [Fact]
    public async Task Bulk_move_failure_keeps_committed_subset_and_retry_moves_only_reread_unresolved_items()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), first = Guid.NewGuid(), interrupted = Guid.NewGuid(), remaining = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(token);
            seed.Collections.Add(new CollectionEntity { Id = targetId, Name = "Camera", Path = "Camera", Identity = "CAMERA" });
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CollectionId = CollectionName.BuiltInId });
            seed.Captures.AddRange(
                new CaptureEntity { Id = first, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "first" },
                new CaptureEntity { Id = interrupted, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "interrupted" },
                new CaptureEntity { Id = remaining, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "remaining" });
            await seed.SaveChangesAsync(token);
            await seed.Database.ExecuteSqlRawAsync("""
                CREATE TABLE collection_move_fault ("Calls" integer NOT NULL);
                INSERT INTO collection_move_fault ("Calls") VALUES (0);
                CREATE FUNCTION fail_collection_move() RETURNS trigger LANGUAGE plpgsql AS $body$
                DECLARE calls integer;
                BEGIN
                    UPDATE collection_move_fault SET "Calls" = "Calls" + 1 RETURNING "Calls" INTO calls;
                    IF calls = 2 THEN
                        RAISE EXCEPTION 'synthetic interrupted collection move';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER fail_collection_move BEFORE UPDATE OF "CollectionId" ON captures
                FOR EACH ROW EXECUTE FUNCTION fail_collection_move();
                """, token);
        }

        try
        {
            await using (var move = new ShutterSpoolDbContext(options))
            {
                var assignments = new CaptureCollectionAssignmentService(new EfCaptureCollectionAssignmentStore(move));
                await Assert.ThrowsAsync<DbUpdateException>(() => assignments.MoveAsync(targetId,
                    [new(first, 0), new(interrupted, 0), new(remaining, 0)], token));
            }

            CollectionMoveRequest[] unresolved;
            await using (var reconcile = new ShutterSpoolDbContext(options))
            {
                Assert.Equal(targetId, await reconcile.Captures.Where(item => item.Id == first)
                    .Select(item => item.CollectionId).SingleAsync(token));
                Assert.Equal(2, await reconcile.Captures.Where(item => item.Id == interrupted || item.Id == remaining)
                    .CountAsync(item => item.CollectionId == CollectionName.BuiltInId, token));
                unresolved = await reconcile.Captures.Where(item => item.Id == interrupted || item.Id == remaining)
                    .Select(item => new CollectionMoveRequest(item.Id, item.Revision)).ToArrayAsync(token);
            }
            Assert.Equal(2, unresolved.Length);

            await using (var removeFault = new ShutterSpoolDbContext(options))
                await removeFault.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_collection_move ON captures; DROP FUNCTION IF EXISTS fail_collection_move(); DROP TABLE IF EXISTS collection_move_fault;", token);
            await using (var retry = new ShutterSpoolDbContext(options))
            {
                var assignments = new CaptureCollectionAssignmentService(new EfCaptureCollectionAssignmentStore(retry));
                CollectionMoveResult[] results = await assignments.MoveAsync(targetId, unresolved, token);
                Assert.All(results, result => Assert.Equal(CollectionMoveStatus.Applied, result.Status));
            }
            await using var verify = new ShutterSpoolDbContext(options);
            Assert.Equal(3, await verify.Captures.CountAsync(item => item.CollectionId == targetId, token));
        }
        finally
        {
            await using var cleanup = new ShutterSpoolDbContext(options);
            await cleanup.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_collection_move ON captures; DROP FUNCTION IF EXISTS fail_collection_move(); DROP TABLE IF EXISTS collection_move_fault;", token);
        }
    }

    private static Guid AddAsset(ShutterSpoolDbContext database, Guid sourceId, string name, string key,
        string role, DateTimeOffset ingestedAt, DateTime localTime)
    {
        Guid id = Guid.NewGuid();
        database.Assets.Add(new AssetEntity
        {
            Id = id, SourceId = sourceId, OriginalName = name, SpoolKey = $"{key}.jpg", Size = 10,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2, 3])).ToLowerInvariant(),
            IngestedAt = ingestedAt, RecoveryCorrelation = Guid.NewGuid(), State = "Spooled",
            MetadataState = "Complete", MetadataOutcome = "Complete", MetadataRole = role,
            CaptureTimeLocal = localTime, CaptureTimeUncertain = false,
        });
        return id;
    }
}
