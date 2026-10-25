using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Core.Tests;

public sealed class RetentionPolicyTests
{
    private static readonly DateTimeOffset Accepted = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retention_intervals_must_be_positive_and_ordered()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionLifetime(TimeSpan.Zero, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetentionLifetime(TimeSpan.FromDays(2), TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Attention_window_uses_final_fifth_capped_at_one_day_and_explicit_trash_window()
    {
        DateTimeOffset end = Accepted.AddDays(7);

        Assert.Equal(end.AddHours(-24), RetentionPolicy.AttentionAt(RetentionTier.HeldOrUnfinished,
            Accepted, end));
        Assert.Equal(end.AddHours(-24), RetentionPolicy.AttentionAt(RetentionTier.Waiting,
            Accepted, end));
        Assert.Equal(end.AddMinutes(-288), RetentionPolicy.AttentionAt(RetentionTier.ConfirmedDelivery,
            end.AddDays(-1), end));
        Assert.Equal(end.AddHours(-24), RetentionPolicy.AttentionAt(null, Accepted, end, trashRecovery: true));
        Assert.Null(RetentionPolicy.AttentionAt(RetentionTier.ConfirmedFavorite, Accepted, null));
    }

    [Fact]
    public void Confirmed_delivery_uses_final_confirmation_for_both_deadlines()
    {
        var facts = new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            HasDeliveryIntent = true,
            HasPositiveConfirmationForEveryIntendedDelivery = true,
            FinalIntendedDeliveryConfirmedAt = Accepted.AddDays(3),
        };

        RetentionAssetDecision result = RetentionPolicy.Evaluate(facts);

        Assert.Equal(RetentionTier.ConfirmedDelivery, result.Tier);
        Assert.Equal(Accepted.AddDays(3).AddHours(1), result.PressureEligibleAt);
        Assert.Equal(Accepted.AddDays(4), result.TrashEligibleAt);
        Assert.Equal(Accepted.AddDays(3), result.IntervalStartedAt);
    }

    [Fact]
    public void Explicit_no_delivery_uses_its_separate_finite_lifetime()
    {
        RetentionAssetDecision result = RetentionPolicy.Evaluate(new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            ExplicitNoDelivery = true,
        });

        Assert.Equal(RetentionTier.ExplicitNoDelivery, result.Tier);
        Assert.Equal(Accepted.AddHours(24), result.PressureEligibleAt);
        Assert.Equal(Accepted.AddDays(7), result.TrashEligibleAt);
        Assert.True(result.IsEligible(Accepted.AddDays(7)));
    }

    [Fact]
    public void Explicit_no_delivery_never_overrides_existing_unfinished_delivery_intent()
    {
        RetentionAssetDecision result = RetentionPolicy.Evaluate(new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            ExplicitNoDelivery = true,
            HasDeliveryIntent = true,
            HasPositiveConfirmationForEveryIntendedDelivery = false,
        });

        Assert.Equal(RetentionTier.HeldOrUnfinished, result.Tier);
        Assert.Equal(Accepted.AddDays(14), result.PressureEligibleAt);
        Assert.Equal(Accepted.AddDays(180), result.TrashEligibleAt);
    }

    [Fact]
    public void Empty_delivery_intent_is_waiting_and_cannot_become_confirmed_favorite()
    {
        var facts = new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            Favorite = true,
            HasPositiveConfirmationForEveryIntendedDelivery = true,
        };

        RetentionAssetDecision result = RetentionPolicy.Evaluate(facts);

        Assert.Equal(RetentionTier.Waiting, result.Tier);
        Assert.Equal(Accepted.AddHours(24), result.PressureEligibleAt);
        Assert.Equal(Accepted.AddDays(7), result.TrashEligibleAt);
    }

    [Fact]
    public void Delivered_state_without_durable_confirmation_time_is_unfinished()
    {
        RetentionAssetDecision result = RetentionPolicy.Evaluate(new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            HasDeliveryIntent = true,
            HasPositiveConfirmationForEveryIntendedDelivery = true,
            FinalIntendedDeliveryConfirmedAt = null,
        });

        Assert.Equal(RetentionTier.HeldOrUnfinished, result.Tier);
        Assert.Equal(Accepted.AddDays(14), result.PressureEligibleAt);
        Assert.Equal(Accepted.AddDays(180), result.TrashEligibleAt);
    }

    [Fact]
    public void Fully_confirmed_favorite_has_pressure_selection_but_no_age_trash_deadline()
    {
        var facts = new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            Favorite = true,
            HasDeliveryIntent = true,
            HasPositiveConfirmationForEveryIntendedDelivery = true,
            FinalIntendedDeliveryConfirmedAt = Accepted,
        };

        RetentionAssetDecision result = RetentionPolicy.Evaluate(facts);

        Assert.Equal(RetentionTier.ConfirmedFavorite, result.Tier);
        Assert.Equal(Accepted.AddHours(1), result.PressureEligibleAt);
        Assert.Null(result.TrashEligibleAt);
        Assert.False(result.IsEligible(Accepted.AddYears(20)));
        Assert.True(RetentionPolicy.EvaluateCapture([result], Accepted.AddHours(1), underPressure: true).IsEligible);
    }

    [Fact]
    public void Newly_incomplete_favorite_uses_its_single_qualification_loss_clock()
    {
        var facts = new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            Favorite = true,
            HasDeliveryIntent = true,
            FavoriteQualificationLostAt = Accepted.AddDays(40),
        };

        RetentionAssetDecision result = RetentionPolicy.Evaluate(facts);

        Assert.Equal(RetentionTier.HeldOrUnfinished, result.Tier);
        Assert.Equal(Accepted.AddDays(54), result.PressureEligibleAt);
        Assert.Equal(Accepted.AddDays(220), result.TrashEligibleAt);
    }

    [Fact]
    public void Confirmed_recognized_replay_uses_its_own_later_acceptance_clock()
    {
        var facts = new RetentionAssetFacts
        {
            AcceptedAt = Accepted.AddDays(2),
            HasDeliveryIntent = true,
            HasPositiveConfirmationForEveryIntendedDelivery = true,
            FinalIntendedDeliveryConfirmedAt = Accepted,
            IsRecognizedReplay = true,
        };

        RetentionAssetDecision result = RetentionPolicy.Evaluate(facts);

        Assert.Equal(Accepted.AddDays(2).AddHours(1), result.PressureEligibleAt);
        Assert.Equal(Accepted.AddDays(3), result.TrashEligibleAt);
    }

    [Fact]
    public void Confirmed_favorite_uses_its_prospective_policy_revision()
    {
        var facts = new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            Favorite = true,
            HasDeliveryIntent = true,
            HasPositiveConfirmationForEveryIntendedDelivery = true,
            FinalIntendedDeliveryConfirmedAt = Accepted,
        };

        RetentionAssetDecision result = RetentionPolicy.Evaluate(facts,
            confirmed: new RetentionLifetime(TimeSpan.FromHours(2), TimeSpan.FromHours(48)));

        Assert.Equal(Accepted.AddHours(2), result.PressureEligibleAt);
        Assert.Null(result.TrashEligibleAt);
    }

    [Fact]
    public void Capture_waits_for_young_protected_variant_and_uses_most_protective_tier()
    {
        RetentionAssetDecision oldConfirmed = RetentionPolicy.Evaluate(new RetentionAssetFacts
        {
            AcceptedAt = Accepted,
            HasDeliveryIntent = true,
            HasPositiveConfirmationForEveryIntendedDelivery = true,
            FinalIntendedDeliveryConfirmedAt = Accepted,
        });
        RetentionAssetDecision protectedUnfinished = RetentionPolicy.Evaluate(new RetentionAssetFacts
        {
            AcceptedAt = Accepted.AddDays(5),
            HasDeliveryIntent = true,
            ProtectedUntil = Accepted.AddDays(20),
        });

        RetentionCaptureDecision result = RetentionPolicy.EvaluateCapture([oldConfirmed, protectedUnfinished], Accepted.AddDays(10));

        Assert.Equal(RetentionTier.HeldOrUnfinished, result.Tier);
        Assert.Equal(Accepted.AddDays(185), result.TrashEligibleAt);
        Assert.False(result.IsEligible);
    }

    [Fact]
    public void Unknown_or_missing_original_is_never_eligible()
    {
        foreach (RetentionAssetFacts facts in new[]
        {
            new RetentionAssetFacts { AcceptedAt = Accepted, IsUnknownOrQuarantined = true },
            new RetentionAssetFacts { AcceptedAt = Accepted, HasAvailableOriginal = false },
        })
        {
            RetentionAssetDecision decision = RetentionPolicy.Evaluate(facts);
            Assert.Null(decision.TrashEligibleAt);
            Assert.False(decision.IsEligible(Accepted.AddYears(1)));
        }
    }
}
