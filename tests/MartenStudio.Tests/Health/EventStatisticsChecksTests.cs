using JasperFx.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.EventHealthFacts;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The planner-statistics checks - E05, E06, E07 - over stated <c>pg_stats</c> rows, and the arithmetic they share.
/// </summary>
public class EventStatisticsChecksTests
{
    // ---------------------------------------------------------------------------------------------------
    // Folding pg_stats rows, and the histogram arithmetic
    // ---------------------------------------------------------------------------------------------------

    private static EventStatisticsRow Row(
        string root,
        bool isRoot,
        long relation,
        string kind,
        double rows,
        string? column,
        bool inherited = false,
        string[]? common = null,
        double[]? frequencies = null,
        string[]? histogram = null,
        int leaves = 1,
        double leafRows = 0) =>
        new(root, isRoot, relation, kind, rows, leaves, leafRows, Now.AddHours(-1), true, column, inherited, 0, 0,
            common ?? [], frequencies ?? [], histogram ?? []);

    [Fact]
    public void A_plain_table_is_folded_from_its_own_statistics()
    {
        EventStatistics folded = EventStatistics.From(
        [
            Row(HealthEventQueries.EventsTable, true, 1, "r", 10_000, "type", common: ["order_placed", "item_added"], frequencies: [0.6, 0.4], leafRows: 10_000),
        ]);

        folded.Events.Exists.Should().BeTrue();
        folded.Events.EstimatedRows.Should().Be(10_000);
        folded.Events.ValuesOf("type").Should().BeEquivalentTo(["order_placed", "item_added"]);
        folded.Events.EstimatedCommon("type", static x => x == "order_placed").Should().Be(6_000);
        folded.Streams.Exists.Should().BeFalse("no row named mt_streams");
    }

    [Fact]
    public void A_partitioned_table_without_inherited_statistics_is_its_partitions_summed()
    {
        EventStatistics folded = EventStatistics.From(
        [
            Row(HealthEventQueries.StreamsTable, true, 1, "p", -1, null, leaves: 2, leafRows: 1_000),
            Row(HealthEventQueries.StreamsTable, false, 2, "r", 800, "is_archived", common: ["f"], frequencies: [1.0], leaves: 2, leafRows: 1_000),
            Row(HealthEventQueries.StreamsTable, false, 3, "r", 200, "is_archived", common: ["t"], frequencies: [1.0], leaves: 2, leafRows: 1_000),
        ]);

        folded.Streams.EstimatedRows.Should().Be(1_000, "the leaves' sum, since the partitioned parent has no reltuples of its own");
        folded.Streams.Leaves.Should().Be(2);
        folded.Streams.LeavesSampled.Should().Be(2);
        ArchivedShareFacts.ArchivedShare(folded.Streams).Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void Inherited_statistics_on_the_root_win_over_the_partitions_for_that_column()
    {
        EventStatistics folded = EventStatistics.From(
        [
            Row(HealthEventQueries.StreamsTable, true, 1, "p", -1, "is_archived", inherited: true, common: ["t", "f"], frequencies: [0.5, 0.5], leaves: 2, leafRows: 1_000),
            Row(HealthEventQueries.StreamsTable, false, 2, "r", 800, "is_archived", common: ["f"], frequencies: [1.0], leaves: 2, leafRows: 1_000),
        ]);

        ArchivedShareFacts.ArchivedShare(folded.Streams).Should().BeApproximately(0.5, 1e-9, "the parent's sample covers every partition at once");
        folded.Streams.SampledRows("is_archived").Should().Be(1_000);
    }

    [Theory]
    [InlineData(new[] { "0", "100", "200", "300", "400" }, 250L, 0.375)]
    [InlineData(new[] { "0", "100", "200", "300", "400" }, -1L, 1.0)]
    [InlineData(new[] { "0", "100", "200", "300", "400" }, 400L, 0.0)]
    [InlineData(new[] { "0", "100" }, 50L, 0.5)]
    [InlineData(new[] { "5" }, 1L, 0.0)]
    public void The_histogram_share_above_a_threshold_interpolates_inside_its_bucket(string[] bounds, long threshold, double expected)
    {
        TableStatistics.HistogramFractionAbove(bounds, threshold).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Rows_above_a_threshold_count_the_common_values_exactly_and_the_histogram_by_its_share()
    {
        // 1,000 rows: 30% are version 1 (a common value), 10% version 5000 (common), the other 60% spread over 1..2000.
        TableStatistics streams = Statistics(
            HealthEventQueries.StreamsTable,
            1_000,
            ("version", [("1", 0.3), ("5000", 0.1)], ["1", "1000", "2000"]));

        streams.EstimatedAbove("version", 1_000).Should().BeApproximately(1_000 * (0.1 + 0.6 * 0.5), 1e-6);
        streams.LargestSeen("version").Should().Be(5_000);
    }

    // ---------------------------------------------------------------------------------------------------
    // E05 Unregistered event types
    // ---------------------------------------------------------------------------------------------------

    private static readonly AllTypeReader CatchAllProjection = new("CatchAll", IsProjection: true, IsAsync: true);

    private static readonly AllTypeReader RelaySubscription = new("Relay", IsProjection: false, IsAsync: true);

    private const string GhostType = "Ghost.GhostEvent, Ghost.Assembly";

    private static UnregisteredEventTypesFacts Types(
        string[] unmapped,
        string[]? readable = null,
        (string Alias, string DotNetType)[]? unreadable = null,
        double withoutDotNet = 0,
        bool upcastersKnown = true,
        bool rebuildsSkip = false,
        bool daemonSkips = true,
        AllTypeReader[]? readers = null) =>
        new(
            Statistics(HealthEventQueries.EventsTable, 10_000, ("type", [("order_placed", 1.0)], [])),
            AliasesSeen: 12,
            unmapped,
            readable ?? [],
            [.. (unreadable ?? []).Select(static x => new UnreadableAlias(x.Alias, x.DotNetType))],
            [.. (unreadable ?? []).Select(static x => x.DotNetType)],
            withoutDotNet,
            upcastersKnown,
            rebuildsSkip,
            daemonSkips,
            readers ?? [CatchAllProjection]);

    private static HealthFinding Fired(UnregisteredEventTypesFacts facts) =>
        new UnregisteredEventTypesCheck().Evaluate(facts, At()).Findings.Should().ContainSingle().Subject;

    [Fact]
    public void E05_every_alias_mapped_is_quiet()
    {
        HealthVerdict verdict = new UnregisteredEventTypesCheck().Evaluate(Types([]), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("is mapped in this process");
    }

    [Fact]
    public void E05_an_alias_read_through_its_loaded_dotnet_type_is_quiet()
    {
        HealthVerdict verdict = new UnregisteredEventTypesCheck().Evaluate(Types(["order_shipped"], readable: ["order_shipped"]), At());

        verdict.Findings.Should().BeEmpty("Marten registers event types lazily, so an alias this process has no mapping for is ordinary");
        verdict.Note.Should().Contain("read through its .NET type");
    }

    [Fact]
    public void E05_an_unmapped_alias_with_no_stored_type_name_to_match_is_quiet_and_named_as_unmatched()
    {
        // The type and mt_dotnet_type statistics are sampled and sorted separately: an alias whose own type name was not
        // seen is evidence of nothing, whatever other unloadable names the statistics show.
        HealthVerdict verdict = new UnregisteredEventTypesCheck().Evaluate(Types(["custom-alias"]), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("could be matched to").And.Contain("custom-alias");
    }

    [Fact]
    public void E05_an_alias_whose_own_stored_type_does_not_load_is_a_warning_when_a_projection_here_reads_every_type()
    {
        HealthFinding finding = Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)]));

        finding.Code.Should().Be("E05");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().EndWith(", so a rebuild will stop on them");
        finding.WhyItMatters.Should().Contain("UnknownEventTypeException").And.NotContain("Two services can share");
        finding.Evidence.Should().Contain(new HealthEvidence("Aliases whose stored .NET type does not load here", "ghost_event (" + GhostType + ")"));
        finding.Evidence.Should().Contain(new HealthEvidence("Projections and subscriptions here that read every event type", "CatchAll (async projection)"));
        finding.Remedy!.Code.Should().Contain("opts.Events.AddEventType<YourEventType>();")
            .And.Contain("opts.Projections.RebuildErrors.SkipUnknownEvents = true;");
        finding.Caveat.Should().Contain("rarer than the sample").And.Contain("only such pairs are named");
        finding.Link!.Route.Should().Be(MartenStudio.Services.Events.EventLinks.EventTypesRoute);
    }

    [Fact]
    public void E05_with_nothing_here_reading_every_type_it_is_an_info_that_another_service_may_read_them()
    {
        HealthFinding finding = Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)], readers: []));

        finding.Severity.Should().Be(HealthSeverity.Info, "every projection here filters by its types, so no rebuild here meets these events");
        finding.Title.Should().EndWith("though nothing registered here reads every event type");
        finding.WhyItMatters.Should().Contain("Two services can share one store").And.Contain("a rebuild here never loads these events");
        finding.Evidence.Should().Contain(static x => x.Label == "Projections and subscriptions here that read every event type" && x.Value.StartsWith("none", StringComparison.Ordinal));
    }

    [Fact]
    public void E05_rebuilds_that_skip_unknown_events_make_it_an_info_about_a_quietly_wrong_projection()
    {
        HealthFinding finding = Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)], rebuildsSkip: true));

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Title.Should().EndWith("and what reads them here skips them");
        finding.WhyItMatters.Should().Contain("silently lacks");
    }

    [Fact]
    public void E05_a_subscription_reading_every_type_under_a_daemon_that_skips_nothing_is_a_warning_that_the_daemon_will_stop()
    {
        HealthFinding finding = Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)], daemonSkips: false, readers: [RelaySubscription]));

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().EndWith(", so the daemon will stop on them");
        finding.Evidence.Should().Contain(new HealthEvidence("Projections and subscriptions here that read every event type", "Relay (subscription)"));
    }

    [Fact]
    public void E05_a_subscription_alone_is_never_rebuilt_so_rebuilds_that_skip_nothing_do_not_make_it_a_warning()
    {
        Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)], readers: [RelaySubscription]))
            .Severity.Should().Be(HealthSeverity.Info, "the daemon skips unknown events by default and a subscription is rewound, not rebuilt");
    }

    [Fact]
    public void E05_an_inline_projection_reading_every_type_still_stops_a_rebuild()
    {
        Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)], readers: [new AllTypeReader("Totals", IsProjection: true, IsAsync: false)]))
            .Evidence.Should().Contain(new HealthEvidence("Projections and subscriptions here that read every event type", "Totals (inline projection)"));
    }

    [Fact]
    public void E05_a_reader_writing_a_hidden_document_type_is_counted_and_never_named()
    {
        HealthFinding finding = Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)], readers: [new AllTypeReader("SecretLedger", true, true, Hidden: true)]));

        finding.Severity.Should().Be(HealthSeverity.Warning, "a hidden projection still stops a rebuild");
        finding.Evidence.Should().Contain(new HealthEvidence("Projections and subscriptions here that read every event type", "1, each writing a document type the studio hides"));
        string.Concat(finding.Evidence.Select(static x => x.Value)).Should().NotContain("SecretLedger");
    }

    [Fact]
    public void E05_an_unmatched_alias_beside_an_evidenced_one_is_listed_apart_from_it()
    {
        HealthFinding finding = Fired(Types(["custom-alias", "ghost_event"], unreadable: [("ghost_event", GhostType)]));

        finding.Evidence.Should().Contain(new HealthEvidence("Other aliases with no mapping, not matched to a .NET type in the statistics", "custom-alias"));
        finding.Evidence[0].Value.Should().NotContain("custom-alias");
    }

    [Fact]
    public void E05_events_without_a_dotnet_type_are_evidence_beside_a_finding()
    {
        Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)], withoutDotNet: 40))
            .Evidence.Should().Contain(static x => x.Label.StartsWith("Events with no mt_dotnet_type", StringComparison.Ordinal));
    }

    [Fact]
    public void E05_says_so_when_the_upcasters_aliases_could_not_be_read()
    {
        Fired(Types(["ghost_event"], unreadable: [("ghost_event", GhostType)], upcastersKnown: false))
            .Caveat.Should().Contain("upcasters' aliases");
    }

    [Fact]
    public void E05_a_long_list_is_cut_and_says_how_many_more()
    {
        (string, string)[] pairs =
        [
            .. Enumerable.Range(1, 13).Select(static x => x.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
                .Select(static x => ("ghost_" + x, "Ghost.Ghost" + x + ", Ghost")),
        ];

        HealthFinding finding = Fired(Types([.. pairs.Select(static x => x.Item1)], unreadable: pairs));

        finding.Evidence[0].Value.Should().EndWith("and 3 more").And.Contain("ghost_10").And.NotContain("ghost_11");
    }

    [Theory]
    [InlineData("Ghost.GhostEvent, Ghost.Assembly", EventNamingStyle.ClassicTypeName, "ghost_event")]
    [InlineData("Shop.Orders+OrderPlaced, Shop", EventNamingStyle.ClassicTypeName, "order_placed")]
    [InlineData("Shop.Orders+OrderPlaced, Shop", EventNamingStyle.SmarterTypeName, "orders.order_placed")]
    [InlineData("Shop.OrderPlaced, Shop", EventNamingStyle.SmarterTypeName, "order_placed")]
    [InlineData("Shop.Orders+OrderPlaced, Shop", EventNamingStyle.FullTypeName, "Shop.Orders.OrderPlaced")]
    [InlineData("Shop.Wrapper`1[[System.String, System.Private.CoreLib]], Shop", EventNamingStyle.ClassicTypeName, null)]
    public void E05_derives_an_alias_from_a_stored_type_name_without_loading_it(string stored, EventNamingStyle style, string? alias)
    {
        UnregisteredEventTypesCheck.AliasFromName(stored, style).Should().Be(alias);
    }

    [Theory]
    [InlineData(EventNamingStyle.ClassicTypeName)]
    [InlineData(EventNamingStyle.SmarterTypeName)]
    [InlineData(EventNamingStyle.FullTypeName)]
    public void E05_the_derived_alias_is_the_one_JasperFx_gives_the_loaded_type(EventNamingStyle style)
    {
        foreach (Type type in new[] { typeof(GhostWasSeen), typeof(GhostWasSeen.NestedGhostMoved) })
        {
            // The format Marten stores in mt_dotnet_type (JasperFx's EventMapping: FullName, then the assembly's short name).
            string stored = type.FullName + ", " + type.Assembly.GetName().Name;

            UnregisteredEventTypesCheck.AliasFromName(stored, style).Should().Be(type.GetEventTypeName(style), type.Name + " under " + style);
        }
    }

    internal sealed record GhostWasSeen
    {
        internal sealed record NestedGhostMoved;
    }

    // ---------------------------------------------------------------------------------------------------
    // E06 Long streams without a snapshot
    // ---------------------------------------------------------------------------------------------------

    private static TableStatistics Streams(double rows, (string Value, double Share)[] common, string[] histogram) =>
        Statistics(HealthEventQueries.StreamsTable, rows, ("version", common, histogram));

    [Fact]
    public void E06_no_live_only_aggregate_is_quiet()
    {
        HealthVerdict verdict = new LongStreamsWithoutSnapshotCheck()
            .Evaluate(new LongStreamsFacts([], 0, TableStatistics.Missing(HealthEventQueries.StreamsTable)), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("none is replayed live");
    }

    [Fact]
    public void E06_only_hidden_live_aggregates_are_quiet_and_say_nothing_about_them()
    {
        HealthVerdict verdict = new LongStreamsWithoutSnapshotCheck()
            .Evaluate(new LongStreamsFacts([], 1, TableStatistics.Missing(HealthEventQueries.StreamsTable)), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().BeNull("a hidden type's existence is not even hinted at");
    }

    [Fact]
    public void E06_short_streams_are_quiet()
    {
        LongStreamsFacts facts = new([new LiveAggregate("Order", true)], 0, Streams(5_000, [("3", 0.5)], ["1", "400", "900"]));

        HealthVerdict verdict = new LongStreamsWithoutSnapshotCheck().Evaluate(facts, At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("900");
    }

    [Fact]
    public void E06_a_live_only_aggregate_and_long_streams_is_an_info_with_a_snapshot_line()
    {
        LongStreamsFacts facts = new(
            [new LiveAggregate("Basket", Registered: false), new LiveAggregate("Order", Registered: true)],
            0,
            Streams(10_000, [("3", 0.5)], ["1", "500", "2500"]));

        HealthFinding finding = new LongStreamsWithoutSnapshotCheck().Evaluate(facts, At()).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("E06");
        finding.Severity.Should().Be(HealthSeverity.Info, "the statistics cannot say which aggregate a long stream belongs to");
        finding.Remedy!.Code.Should().Be("opts.Projections.Snapshot<Basket>(SnapshotLifecycle.Inline);");
        finding.Evidence.Should().Contain(new HealthEvidence("Longest stream the statistics saw", "2,500 events"));
        finding.Evidence.Should().Contain(static x => x.Label == "Aggregated only live" && x.Value.Contains("Basket (aggregated in code, not registered)", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(static x => x.Label == "Streams over 1,000 events" && x.Value.StartsWith("about", StringComparison.Ordinal));
        finding.Caveat.Should().Contain("cannot say which aggregate");
        finding.Link!.Route.Should().Be(MartenStudio.Services.Events.EventLinks.StreamsRoute);
    }

    [Fact]
    public void E06_a_single_long_stream_the_sample_saw_is_at_least_one()
    {
        // A common value of 1,500 at a share too small to make a whole stream: the sample saw it, so there is one.
        LongStreamsFacts facts = new([new LiveAggregate("Order", true)], 0, Streams(100, [("1500", 0.001)], []));

        new LongStreamsWithoutSnapshotCheck().Evaluate(facts, At()).Findings.Should().ContainSingle()
            .Which.Evidence.Should().Contain(new HealthEvidence("Streams over 1,000 events", "at least one"));
    }

    // ---------------------------------------------------------------------------------------------------
    // E07 Archived share without partitioning
    // ---------------------------------------------------------------------------------------------------

    private static ArchivedShareFacts Archived(double rows, double archivedShare, bool partitioned = false, bool tenantPartitioned = false) =>
        new(
            partitioned,
            tenantPartitioned,
            Statistics(HealthEventQueries.EventsTable, rows, ("is_archived", [("t", archivedShare), ("f", 1 - archivedShare)], [])),
            Statistics(HealthEventQueries.StreamsTable, rows / 10, ("is_archived", [("t", archivedShare), ("f", 1 - archivedShare)], [])));

    [Fact]
    public void E07_a_partitioned_store_is_quiet()
    {
        new ArchivedShareCheck().Evaluate(Archived(1_000_000, 0.9, partitioned: true), At()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void E07_a_tenant_partitioned_store_is_never_told_to_partition_by_archive()
    {
        HealthVerdict verdict = new ArchivedShareCheck().Evaluate(Archived(1_000_000, 0.9, tenantPartitioned: true), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("does not combine");
    }

    [Fact]
    public void E07_a_small_store_is_quiet_whatever_its_share()
    {
        new ArchivedShareCheck().Evaluate(Archived(99_999, 0.9), At()).Findings.Should().BeEmpty("the migration is not worth it below 100,000 events");
    }

    [Fact]
    public void E07_half_archived_is_quiet()
    {
        new ArchivedShareCheck().Evaluate(Archived(1_000_000, 0.5), At()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void E07_mostly_archived_is_an_info_that_says_the_migration_cost_plainly()
    {
        HealthFinding finding = new ArchivedShareCheck().Evaluate(Archived(1_000_000, 0.7), At()).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("E07");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Title.Should().StartWith("70%");
        finding.Remedy!.Code.Should().Be("opts.Events.UseArchivedStreamPartitioning = true;");
        finding.Remedy.Explanation.Should().Contain("copying every row").And.Contain("maintenance window").And.Contain("db-patch");
        finding.Evidence.Should().Contain(static x => x.Label == "Archived share of mt_streams");
    }
}
