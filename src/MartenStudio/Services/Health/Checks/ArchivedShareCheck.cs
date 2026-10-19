using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What E07 read.</summary>
/// <param name="Partitioned"><c>Events.UseArchivedStreamPartitioning</c>.</param>
/// <param name="TenantPartitioned">
/// <c>Events.UseTenantPartitionedEvents</c>, which Marten refuses to combine with archived partitioning at startup.
/// </param>
/// <param name="Events">What the planner's statistics say about <c>mt_events</c>.</param>
/// <param name="Streams">What they say about <c>mt_streams</c>.</param>
internal sealed record ArchivedShareFacts(bool Partitioned, bool TenantPartitioned, TableStatistics Events, TableStatistics Streams)
{
    /// <summary>The share of <paramref name="table" />'s rows the statistics say are archived, or <see langword="null" />.</summary>
    public static double? ArchivedShare(TableStatistics table)
    {
        ArgumentNullException.ThrowIfNull(table);

        double sampled = table.SampledRows("is_archived");
        if (sampled <= 0)
        {
            return null;
        }

        return table.EstimatedCommon("is_archived", static value => value is "t" or "true") / sampled;
    }
}

/// <summary>
/// E07: most of the event store is archived, and it is not partitioned by the archive flag.
/// </summary>
/// <remarks>
/// <para>
/// Archiving a stream sets <c>is_archived</c> on its row and its events; reads of live data then filter
/// <c>is_archived = false</c> over tables that are mostly rows they skip. With
/// <c>opts.Events.UseArchivedStreamPartitioning</c>, <c>mt_events</c> and <c>mt_streams</c> are list-partitioned on
/// the flag and the archived rows move to their own partition, out of every live read.
/// </para>
/// <para>
/// <b>Info, with the cost said plainly:</b> turning it on for an existing store migrates both tables from plain to
/// partitioned, which copies every row out and back - Marten's own documentation recommends running that script by
/// hand, in downtime. Tier 1, no scan: <c>most_common_freqs</c> of <c>is_archived</c>, shared with E05 and E06.
/// </para>
/// </remarks>
[HealthCheck("E07", HealthArea.Events, HealthCostTier.Catalog, "Archived share without partitioning")]
internal sealed class ArchivedShareCheck : HealthCheck<ArchivedShareFacts>
{
    /// <summary>The archived share of <c>mt_events</c> above which partitioning is worth suggesting.</summary>
    internal const double MaxArchivedShare = 0.5;

    /// <summary>The fewest events a store must have before the suggestion is worth its migration.</summary>
    internal const double MinimumEvents = 100_000;

    /// <inheritdoc />
    public override async Task<ArchivedShareFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool partitioned = context.StoreOptions.Events.UseArchivedStreamPartitioning;
        bool tenantPartitioned = context.StoreOptions.Events.UseTenantPartitionedEvents;

        if (partitioned || tenantPartitioned)
        {
            // The configuration answers it; nothing to read.
            return new ArchivedShareFacts(
                partitioned,
                tenantPartitioned,
                TableStatistics.Missing(HealthEventQueries.EventsTable),
                TableStatistics.Missing(HealthEventQueries.StreamsTable));
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        EventHealth.RequireEvents(shape);

        EventStatistics statistics = await EventHealth.StatisticsAsync(context, cancellationToken).ConfigureAwait(false);

        if (!statistics.Events.Readable)
        {
            throw new HealthCheckUnavailableException(
                "The role the studio reads as may not select from mt_events, so PostgreSQL shows it none of that table's statistics.");
        }

        if (!statistics.Events.HasColumn("is_archived"))
        {
            throw new HealthCheckUnavailableException(
                "PostgreSQL holds no statistics for mt_events.is_archived yet: the table has not been analysed since its events arrived.");
        }

        return new ArchivedShareFacts(false, false, statistics.Events, statistics.Streams);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ArchivedShareFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Partitioned)
        {
            return HealthVerdict.Quiet("Events.UseArchivedStreamPartitioning is on: archived streams and their events have a partition of their own.");
        }

        if (facts.TenantPartitioned)
        {
            return HealthVerdict.Quiet(
                "This store partitions its events by tenant, which Marten does not combine with archived-stream partitioning.");
        }

        double? share = ArchivedShareFacts.ArchivedShare(facts.Events);
        double rows = facts.Events.EstimatedRows ?? 0;

        if (share is not { } archived || rows < MinimumEvents || archived <= MaxArchivedShare)
        {
            return HealthVerdict.Quiet(share is { } known
                ? EventHealth.Percent(known) + " of about " + EventHealth.Count(rows) + " events belong to archived streams."
                : null);
        }

        List<HealthEvidence> evidence =
        [
            new("Archived share of mt_events", EventHealth.Percent(archived) + " of about " + EventHealth.Count(rows) + " events"),
        ];

        if (ArchivedShareFacts.ArchivedShare(facts.Streams) is { } streams)
        {
            evidence.Add(new HealthEvidence(
                "Archived share of mt_streams",
                EventHealth.Percent(streams) + (facts.Streams.EstimatedRows is { } count ? " of about " + EventHealth.Count(count) + " streams" : string.Empty)));
        }

        evidence.Add(new HealthEvidence("Events.UseArchivedStreamPartitioning", "false"));

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "E07",
            Severity = HealthSeverity.Info,
            Title = EventHealth.Percent(archived) + " of the events belong to archived streams, in the same table as the live ones",
            WhyItMatters =
                "Archiving a stream only marks it: its row and its events stay where they were, flagged is_archived. Queries for "
                + "live streams and events then filter them out of tables that are now mostly rows they skip, a projection "
                + "rebuild reads through their whole sequence range, and every index on mt_events and mt_streams carries them. "
                + "With archived-stream partitioning both tables are partitioned on the flag, archiving moves a stream's rows "
                + "into the archived partition, and the planner leaves that partition out of every read that asks for live data.",
            Evidence = evidence,
            Caveat = EventHealth.StatisticsCaveat(facts.Events),
            Remedy = new HealthRemedy(
                "opts.Events.UseArchivedStreamPartitioning = true;",
                "The migration is not small. On an existing store the next schema apply turns mt_events and mt_streams from plain "
                + "tables into partitioned ones, which means copying every row out to a temporary table, dropping and recreating "
                + "each table partitioned, and copying every row back - an outage proportional to the event store's size. Marten's "
                + "own documentation recommends exporting that script with db-patch and running it by hand, in a maintenance "
                + "window. Once it is done, a reused archived stream id is allowed in the default partition; add "
                + "Events.EnableStrictStreamIdentityEnforcement if stream ids come from outside Marten."),
        });
    }
}
