using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Core.Tests;

public sealed class CapturePolicyTests
{
    [Fact]
    public void Ambiguous_or_uncertain_pairing_stays_separate_and_replay_keeps_prior_identity()
    {
        DateTime shot = new(2026, 9, 29, 10, 20, 30, DateTimeKind.Unspecified);
        GroupingAssetFacts raw = Asset("RAW", shot);
        GroupingAssetFacts jpeg = Asset("JPEG", shot);
        GroupingCaptureFacts first = new(Guid.NewGuid(), [raw]);
        GroupingCaptureFacts second = new(Guid.NewGuid(), [Asset("RAW", shot)]);

        Assert.Equal("ambiguous_candidates", CaptureGroupingPolicy.Decide(jpeg, [first, second], []).Reason);
        Assert.Equal("missing_or_uncertain_time", CaptureGroupingPolicy.Decide(jpeg with { CaptureTimeUncertain = true }, [first], []).Reason);
        GroupingDecision replay = CaptureGroupingPolicy.Decide(raw with { Id = Guid.NewGuid() }, [first],
            [new GroupingReplayFacts(first.Id, raw)]);
        Assert.Equal(first.Id, replay.CaptureId);
        Assert.Equal(raw.Id, replay.RelatedAssetId);
        Assert.Equal("recognized_replay", replay.Reason);
    }

    [Fact]
    public void Other_variants_join_only_one_evidence_backed_capture_and_keep_distinct_names()
    {
        DateTime shot = new(2026, 9, 29, 10, 20, 30, DateTimeKind.Unspecified);
        GroupingAssetFacts raw = Asset("RAW", shot);
        GroupingCaptureFacts capture = new(Guid.NewGuid(), [raw]);
        GroupingAssetFacts tiff = Asset("OTHER", shot) with { OriginalName = "IMG_0042.tif" };

        GroupingDecision paired = CaptureGroupingPolicy.Decide(tiff, [capture], []);
        Assert.Equal(capture.Id, paired.CaptureId);
        Assert.Equal("paired_complement", paired.Reason);
        GroupingCaptureFacts withOther = new(capture.Id, [raw, tiff]);
        Assert.Equal(capture.Id, CaptureGroupingPolicy.Decide(
            tiff with { OriginalName = "IMG_0042.dng" }, [withOther], []).CaptureId);

        GroupingCaptureFacts competing = new(Guid.NewGuid(), [Asset("JPEG", shot)]);
        Assert.Equal("ambiguous_candidates", CaptureGroupingPolicy.Decide(tiff, [capture, competing], []).Reason);
        Assert.Equal("missing_or_uncertain_time", CaptureGroupingPolicy.Decide(
            tiff with { CaptureTimeUncertain = true }, [capture], []).Reason);
        Assert.Equal("variant_name_conflict", CaptureGroupingPolicy.Decide(
            tiff with { OriginalName = raw.OriginalName }, [capture], []).Reason);
        GroupingAssetFacts otherFirst = Asset("OTHER", shot) with { OriginalName = raw.OriginalName };
        Assert.Equal("variant_name_conflict", CaptureGroupingPolicy.Decide(
            raw with { Sha256 = "different-bytes" }, [new GroupingCaptureFacts(Guid.NewGuid(), [otherFirst])], []).Reason);
    }

    [Fact]
    public void Raw_or_jpeg_can_join_an_evidence_backed_other_capture()
    {
        DateTime shot = new(2026, 9, 29, 10, 20, 30, DateTimeKind.Unspecified);
        GroupingCaptureFacts capture = new(Guid.NewGuid(), [Asset("OTHER", shot) with { OriginalName = "IMG_0042.tif" }]);

        GroupingDecision decision = CaptureGroupingPolicy.Decide(Asset("RAW", shot), [capture], []);

        Assert.Equal(capture.Id, decision.CaptureId);
        Assert.Equal("paired_complement", decision.Reason);
    }

    [Fact]
    public void Delay_and_completion_follow_frozen_policy()
    {
        DateTimeOffset anchor = new(2026, 9, 29, 10, 20, 30, TimeSpan.Zero);
        Assert.Equal(anchor.AddHours(1), CaptureCurationPolicy.DueAt(SourceCurationMode.Delay, null, anchor));
        Assert.False(CaptureCurationPolicy.IsDue(true, null, anchor, anchor.AddHours(2)));
        Assert.Equal(CaptureState.RELEASED, CaptureCompletionPolicy.State(false, false));
        Assert.Equal(CaptureState.RELEASED, CaptureCompletionPolicy.State(true, true));
        Assert.Equal(CaptureState.COMPLETE, CaptureCompletionPolicy.State(true, false));
    }

    [Fact]
    public void Rating_is_authoritative_and_label_values_are_canonical()
    {
        Assert.Equal(CaptureSelection.Rejected, CaptureCurationSnapshot.SelectionFor(-1));
        Assert.Equal(CaptureSelection.Neutral, CaptureCurationSnapshot.SelectionFor(0));
        Assert.Equal(CaptureSelection.Picked, CaptureCurationSnapshot.SelectionFor(1));
        Assert.Equal(CaptureSelection.Picked, CaptureCurationSnapshot.SelectionFor(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureCurationSnapshot.SelectionFor(6));
        Assert.Equal(["Portrait"], CaptureCurationPolicy.Validate(5, "Purple", [" Portrait "]));
        Assert.Throws<ArgumentException>(() => CaptureCurationPolicy.Validate(-2, null, []));
        Assert.Throws<ArgumentException>(() => CaptureCurationPolicy.Validate(1, "purple", []));
    }

    [Fact]
    public void Hold_and_restore_keep_the_original_deadline_and_reject_stale_or_released_mutations()
    {
        DateTimeOffset anchor = new(2026, 9, 29, 10, 20, 30, TimeSpan.Zero);
        CaptureCurationSnapshot original = Curation(anchor);
        CaptureCurationDecision held = CaptureCurationPolicy.Hold(original, 0);
        Assert.True(held.Changed);
        Assert.True(held.Snapshot.Held);
        Assert.Equal(1, held.Snapshot.Revision);
        Assert.False(CaptureCurationPolicy.ReleaseDue(held.Snapshot, anchor.AddHours(2)).Accepted);
        Assert.False(CaptureCurationPolicy.Restore(held.Snapshot, 0).Accepted);

        CaptureCurationDecision restored = CaptureCurationPolicy.Restore(held.Snapshot, 1);
        Assert.True(restored.Changed);
        Assert.False(restored.Snapshot.Held);
        Assert.Equal(anchor.AddHours(1), restored.Snapshot.ReleaseDueAt);
        Assert.True(CaptureCurationPolicy.ReleaseDue(restored.Snapshot, anchor.AddHours(2)).Accepted);
        Assert.False(CaptureCurationPolicy.Hold(CaptureCurationPolicy.ReleaseDue(restored.Snapshot, anchor.AddHours(2)).Snapshot, 3).Accepted);
    }

    [Fact]
    public void Release_freezes_curation_and_revision_once_while_later_edits_change_current_values()
    {
        DateTimeOffset anchor = new(2026, 9, 29, 10, 20, 30, TimeSpan.Zero);
        CaptureCurationSnapshot original = Curation(anchor);
        CaptureCurationDecision edited = CaptureCurationPolicy.Update(original, 0, 4, "Red", ["Portrait"]);
        Assert.True(edited.Changed);
        Assert.False(CaptureCurationPolicy.ReleaseNow(edited.Snapshot, 0, anchor).Accepted);

        CaptureCurationDecision release = CaptureCurationPolicy.ReleaseNow(edited.Snapshot, 1, anchor);
        Assert.True(release.Changed);
        Assert.Equal(2, release.Snapshot.Revision);
        Assert.Equal(1, release.Snapshot.ReleasedRevision);
        Assert.Equal(4, release.Snapshot.ReleasedRating);
        Assert.Equal("Red", release.Snapshot.ReleasedLabel);
        Assert.Equal(["Portrait"], release.Snapshot.ReleasedTags);
        Assert.Equal(CaptureSelection.Picked, release.Snapshot.ReleasedSelection);
        Assert.Equal(CaptureState.READY_FOR_RELEASE, release.Snapshot.State);
        Assert.Equal(anchor.AddHours(1), release.Snapshot.ReleaseDueAt);

        CaptureCurationDecision later = CaptureCurationPolicy.Update(release.Snapshot, 2, -1, null, ["Later"]);
        Assert.Equal(3, later.Snapshot.Revision);
        Assert.Equal(1, later.Snapshot.ReleasedRevision);
        Assert.Equal(["Portrait"], later.Snapshot.ReleasedTags);
        Assert.Equal(4, later.Snapshot.ReleasedRating);
        CaptureCurationDecision duplicate = CaptureCurationPolicy.ReleaseNow(later.Snapshot, 0, anchor.AddDays(1));
        Assert.True(duplicate.Accepted);
        Assert.False(duplicate.Changed);
        Assert.Equal(anchor, duplicate.Snapshot.ReleasedAt);
        Assert.False(CaptureCurationPolicy.ReleaseDue(later.Snapshot, anchor.AddDays(1)).Accepted);
    }

    private static CaptureCurationSnapshot Curation(DateTimeOffset anchor) => new(
        0, 0, null, [], SourceCurationMode.Delay, 3_600, anchor,
        anchor.AddHours(1), false, CaptureState.CURATING, null, null, null, null, []);

    private static GroupingAssetFacts Asset(string role, DateTime time) => new(Guid.NewGuid(), "frame.raw", "ABC", 10,
        role, "Camera", "Serial", time, null, 0, false);
}
