using JasperFx.Events;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>
/// E02: a share of the recent window is tombstones - sequence numbers that saves drew and then failed to use.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a tombstone is</b>, read at <c>V9.39.0</c> and <c>V9.50.2</c> (<c>EventGraph.TryCreateTombstoneBatch</c>):
/// when <c>SaveChanges</c> fails after its events were given sequence numbers, Marten appends one
/// <c>JasperFx.Events.Tombstone</c> event into each of those numbers, in a stream of its own, so the sequence has no
/// hole the async daemon would have to wait out. Under Rich appends every event's number is drawn before the
/// insert, so every failed save leaves them. Under Quick appends the numbers come back from the append function, so a
/// failure later in the same batch still leaves tombstones - but one inside the function after it drew its numbers
/// (two appends racing past one version check, the loser failing on the duplicate version) leaves a gap instead,
/// which the daemon later skips (D07). The share here therefore counts every failure
/// on a Rich store and undercounts them on a Quick one; the finding says which.
/// </para>
/// <para>
/// Tier 2: one bounded read of the newest <c>HealthEventQueries.TrafficWindow</c> events, shared with E03
/// and E04. A window of fewer than <see cref="MinimumWindow" /> events is too small to judge, so a quiet store never
/// fires.
/// </para>
/// </remarks>
[HealthCheck("E02", HealthArea.Events, HealthCostTier.BoundedData, "Tombstones")]
internal sealed class TombstonesCheck : HealthCheck<TrafficProfile>
{
    /// <summary>The share of the window above which the tombstones are a finding.</summary>
    internal const double MaxTombstoneShare = 0.01;

    /// <summary>The fewest events a window may have and be judged.</summary>
    internal const long MinimumWindow = 1_000;

    /// <inheritdoc />
    public override Task<TrafficProfile> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
        EventHealth.TrafficAsync(context, cancellationToken);

    /// <inheritdoc />
    public override HealthVerdict Evaluate(TrafficProfile facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Events < MinimumWindow)
        {
            return HealthVerdict.Quiet(
                "The recent window holds " + EventHealth.Count(facts.Events) + " events; tombstones are judged over at least "
                + EventHealth.Count(MinimumWindow) + ".");
        }

        long tombstones = facts.Totals.Tombstones ?? 0;
        double share = (double) tombstones / facts.Events;

        if (share <= MaxTombstoneShare)
        {
            return HealthVerdict.Quiet(
                EventHealth.Count(tombstones) + " of the recent window's " + EventHealth.Count(facts.Events) + " events are tombstones ("
                + EventHealth.Percent(share) + ").");
        }

        bool rich = facts.AppendMode == EventAppendMode.Rich;

        List<HealthEvidence> evidence =
        [
            new("Tombstones in the window", EventHealth.Count(tombstones) + " (" + EventHealth.Percent(share) + ")"),
            new("Window", facts.DescribeWindow()),
            new("Append mode", facts.AppendMode.ToString()),
            new("Tombstone event type", facts.TombstoneType),
        ];

        if (facts.Conjoined)
        {
            int tenants = facts.Tenants.Count(static x => x.Tombstones is > 0);
            evidence.Add(new HealthEvidence(
                "Tenants with tombstones",
                EventHealth.Count(tenants) + (facts.Tenants.Count >= HealthEventQueries.MaxTenantRows ? " or more" : string.Empty)));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "E02",
            Severity = HealthSeverity.Warning,
            Title = "Saves are failing after drawing sequence numbers: " + EventHealth.Percent(share) + " of the recent events are tombstones",
            WhyItMatters =
                "A tombstone is the event Marten writes into a sequence number that a failed SaveChanges had already drawn, so "
                + "that the event sequence has no hole for the async daemon to wait out. "
                + EventHealth.Percent(share) + " of the last " + EventHealth.Count(facts.Events) + " events being tombstones means "
                + "that many appends are failing after the database handed them their numbers - most often a concurrency "
                + "conflict on a stream's version, which a busy stream (E03) and an application that retries blindly make "
                + "worse. Each one is a round trip, a write to mt_events and a sequence number spent for nothing, and the "
                + "failure itself surfaced as an exception in whatever code saved. "
                + (rich
                    ? "Under Rich appends every failed save leaves them, so this share counts every failed event."
                    : "Under " + facts.AppendMode + " appends a failure inside the append function after it drew its numbers - "
                        + "two appends racing past one version check, say - leaves a gap rather than a tombstone, which the daemon "
                        + "later skips (D07), so this share undercounts the failures."),
            Evidence = evidence,
            Caveat =
                "Counted over the newest " + EventHealth.Count(HealthEventQueries.TrafficWindow) + " events, "
                + "read once when somebody asked for the deeper checks. A tombstone fills one number a failed save drew, so this "
                + "counts failed events, not failed saves.",
        });
    }
}
