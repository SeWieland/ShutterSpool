namespace ShutterSpool.Domain.Retention;

public enum RetentionTier
{
    ConfirmedDelivery = 0,
    ExplicitNoDelivery = 1,
    ConfirmedFavorite = 2,
    Waiting = 3,
    HeldOrUnfinished = 4,
}

public sealed record RetentionLifetime
{
    public TimeSpan PressureAge { get; }
    public TimeSpan OrdinaryAge { get; }

    public RetentionLifetime(TimeSpan pressureAge, TimeSpan ordinaryAge)
    {
        if (pressureAge <= TimeSpan.Zero || ordinaryAge <= TimeSpan.Zero || pressureAge > ordinaryAge)
            throw new ArgumentOutOfRangeException(nameof(pressureAge), "Retention intervals must be positive and pressure selection cannot follow ordinary expiry.");
        PressureAge = pressureAge;
        OrdinaryAge = ordinaryAge;
    }

    public static RetentionLifetime Confirmed { get; } = new(TimeSpan.FromHours(1), TimeSpan.FromHours(24));
    public static RetentionLifetime NoDelivery { get; } = new(TimeSpan.FromHours(24), TimeSpan.FromDays(7));
    public static RetentionLifetime Waiting { get; } = new(TimeSpan.FromHours(24), TimeSpan.FromDays(7));
    public static RetentionLifetime HeldOrUnfinished { get; } = new(TimeSpan.FromDays(14), TimeSpan.FromDays(180));
}

public sealed record RetentionAssetFacts
{
    public required DateTimeOffset AcceptedAt { get; init; }
    public DateTimeOffset? FinalIntendedDeliveryConfirmedAt { get; init; }
    public DateTimeOffset? FavoriteQualificationLostAt { get; init; }
    public bool HasDeliveryIntent { get; init; }
    public bool HasPositiveConfirmationForEveryIntendedDelivery { get; init; }
    public bool IsRecognizedReplay { get; init; }
    public bool ExplicitNoDelivery { get; init; }
    public bool Held { get; init; }
    public bool Favorite { get; init; }
    public bool IsUnknownOrQuarantined { get; init; }
    public bool HasAvailableOriginal { get; init; } = true;
    public DateTimeOffset? ProtectedUntil { get; init; }
}

public sealed record RetentionAssetDecision(
    RetentionTier? Tier,
    DateTimeOffset? PressureEligibleAt,
    DateTimeOffset? TrashEligibleAt,
    DateTimeOffset? ProtectedUntil,
    string Reason)
{
    public DateTimeOffset? IntervalStartedAt { get; init; }
    public bool IsEligible(DateTimeOffset now) => TrashEligibleAt <= now
        && (ProtectedUntil is null || ProtectedUntil <= now);
}

public static class RetentionPolicy
{
    public static DateTimeOffset? AttentionAt(RetentionTier? tier, DateTimeOffset? startedAt,
        DateTimeOffset? endsAt, bool trashRecovery = false)
    {
        if (endsAt is not DateTimeOffset end || startedAt is not DateTimeOffset start || end <= start)
            return null;
        if (trashRecovery || tier == RetentionTier.HeldOrUnfinished) return end - TimeSpan.FromHours(24);
        long durationTicks = (end - start).Ticks;
        long warningTicks = Math.Min(TimeSpan.FromHours(24).Ticks, durationTicks / 5);
        return end - TimeSpan.FromTicks(warningTicks);
    }

    public static RetentionAssetDecision Evaluate(RetentionAssetFacts facts,
        RetentionLifetime? confirmed = null, RetentionLifetime? noDelivery = null,
        RetentionLifetime? waiting = null, RetentionLifetime? heldOrUnfinished = null)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!facts.HasAvailableOriginal || facts.IsUnknownOrQuarantined)
            return new(null, null, null, facts.ProtectedUntil, "unavailable-or-unknown");

        RetentionTier tier;
        DateTimeOffset origin;
        RetentionLifetime lifetime;
        string reason;
        if (facts.Favorite && facts.HasDeliveryIntent && facts.HasPositiveConfirmationForEveryIntendedDelivery
            && facts.FinalIntendedDeliveryConfirmedAt is DateTimeOffset favoriteConfirmedAt)
        {
            origin = facts.IsRecognizedReplay && facts.AcceptedAt > favoriteConfirmedAt
                ? facts.AcceptedAt : favoriteConfirmedAt;
            return new(RetentionTier.ConfirmedFavorite, origin + (confirmed ?? RetentionLifetime.Confirmed).PressureAge,
                null, facts.ProtectedUntil, "confirmed-favorite") { IntervalStartedAt = origin };
        }

        if (facts.Favorite && facts.FavoriteQualificationLostAt is DateTimeOffset qualificationLostAt)
        {
            tier = RetentionTier.HeldOrUnfinished;
            origin = qualificationLostAt;
            lifetime = heldOrUnfinished ?? RetentionLifetime.HeldOrUnfinished;
            reason = "favorite-became-incomplete";
        }
        else if (facts.Held || facts.HasDeliveryIntent
            && (!facts.HasPositiveConfirmationForEveryIntendedDelivery || facts.FinalIntendedDeliveryConfirmedAt is null))
        {
            tier = RetentionTier.HeldOrUnfinished;
            origin = facts.AcceptedAt;
            lifetime = heldOrUnfinished ?? RetentionLifetime.HeldOrUnfinished;
            reason = facts.Held ? "held" : "delivery-unfinished";
        }
        else if (facts.HasDeliveryIntent && facts.HasPositiveConfirmationForEveryIntendedDelivery
            && facts.FinalIntendedDeliveryConfirmedAt is DateTimeOffset confirmedAt)
        {
            tier = RetentionTier.ConfirmedDelivery;
            origin = facts.IsRecognizedReplay && facts.AcceptedAt > confirmedAt ? facts.AcceptedAt : confirmedAt;
            lifetime = confirmed ?? RetentionLifetime.Confirmed;
            reason = "all-intended-deliveries-confirmed";
        }
        else if (facts.ExplicitNoDelivery)
        {
            tier = RetentionTier.ExplicitNoDelivery;
            origin = facts.AcceptedAt;
            lifetime = noDelivery ?? RetentionLifetime.NoDelivery;
            reason = "explicit-no-delivery";
        }
        else
        {
            tier = RetentionTier.Waiting;
            origin = facts.AcceptedAt;
            lifetime = waiting ?? RetentionLifetime.Waiting;
            reason = "waiting-or-unmatched";
        }

        return new(tier, origin + lifetime.PressureAge, origin + lifetime.OrdinaryAge,
            facts.ProtectedUntil, reason) { IntervalStartedAt = origin };
    }

    public static RetentionCaptureDecision EvaluateCapture(IReadOnlyCollection<RetentionAssetDecision> assets,
        DateTimeOffset now, bool underPressure = false)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Count == 0 || assets.Any(asset => asset.Tier is null || asset.PressureEligibleAt is null))
            return new(null, null, null, [.. assets.Select(asset => asset.Reason).Distinct()], false);

        RetentionTier tier = assets.Max(asset => asset.Tier!.Value);
        DateTimeOffset pressureEligibleAt = assets.Max(asset => Max(asset.PressureEligibleAt!.Value,
            asset.ProtectedUntil));
        DateTimeOffset? trashEligibleAt = assets.Any(asset => asset.TrashEligibleAt is null)
            ? null : assets.Max(asset => Max(asset.TrashEligibleAt!.Value, asset.ProtectedUntil));
        DateTimeOffset? protectedUntil = assets.Where(asset => asset.ProtectedUntil > now)
            .Select(asset => asset.ProtectedUntil).Max();
        string[] reasons = [.. assets.Select(asset => asset.Reason).Distinct()];
        bool eligibilityReached = underPressure
            ? assets.All(asset => asset.PressureEligibleAt <= now)
            : trashEligibleAt <= now;
        return new(tier, pressureEligibleAt, trashEligibleAt, reasons, protectedUntil is null && eligibilityReached);
    }

    private static DateTimeOffset Max(DateTimeOffset value, DateTimeOffset? protection) =>
        protection is DateTimeOffset until && until > value ? until : value;
}

public sealed record RetentionCaptureDecision(RetentionTier? Tier, DateTimeOffset? PressureEligibleAt,
    DateTimeOffset? TrashEligibleAt, string[] Reasons, bool IsEligible);
