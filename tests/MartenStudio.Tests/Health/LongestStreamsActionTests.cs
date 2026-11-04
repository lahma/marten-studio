using System.Text;

using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Services.Health;
using MartenStudio.Tests.Health.Ambiguous.Left;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Health;

#pragma warning disable CA1716, CA1720 // The names are the point: two types of one name in two namespaces.
#pragma warning disable IDE0130
namespace MartenStudio.Tests.Health.Ambiguous.Left
{
    /// <summary>One of two aggregates that share a name.</summary>
    public sealed class Shipment
    {
        public Guid Id { get; set; }

        public int Count { get; set; }

        public void Apply(StreamActionPinged ping) => Count++;
    }
}

namespace MartenStudio.Tests.Health.Ambiguous.Right
{
    /// <summary>The other.</summary>
    public sealed class Shipment
    {
        public Guid Id { get; set; }

        public int Count { get; set; }

        public void Apply(StreamActionPinged ping) => Count++;
    }
}
#pragma warning restore IDE0130
#pragma warning restore CA1716, CA1720

namespace MartenStudio.Tests.Health
{
    /// <summary>An event the aggregates below apply.</summary>
    public sealed record StreamActionPinged;

    /// <summary>An aggregate the test store only ever builds live.</summary>
    public sealed class StreamActionTrip
    {
        public Guid Id { get; set; }

        public int Count { get; set; }

        public void Apply(StreamActionPinged ping) => Count++;
    }

    /// <summary>An aggregate the test store keeps in an inline snapshot.</summary>
    public sealed class StreamActionOrder
    {
        public Guid Id { get; set; }

        public int Count { get; set; }

        public void Apply(StreamActionPinged ping) => Count++;
    }

    /// <summary>An aggregate the test host hides from the studio.</summary>
    public sealed class StreamActionSecretLedger
    {
        public Guid Id { get; set; }

        public int Count { get; set; }

        public void Apply(StreamActionPinged ping) => Count++;
    }

    /// <summary>An aggregate that throws when the host is asked whether it may be shown.</summary>
    public sealed class StreamActionThrowing
    {
        public Guid Id { get; set; }

        public int Count { get; set; }

        public void Apply(StreamActionPinged ping) => Count++;
    }

    /// <summary>
    /// E06's on-demand action (1.2-H-C), over a real studio container and a store that never connects: what a finding offers, the
    /// gates before any connection is opened, what the process knows of an aggregate, and every sentence a result says.
    /// </summary>
    public class LongestStreamsActionTests
    {
        private const string Id = "e06.longest-streams";

        // Marten files a stream under its aggregate's table alias (type.Name.ToTableAlias()): snake case.
        private const string TripAlias = "stream_action_trip";
        private const string OrderAlias = "stream_action_order";
        private const string LedgerAlias = "stream_action_secret_ledger";

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        private static void Aggregates(StoreOptions options)
        {
            options.Projections.LiveStreamAggregation<StreamActionTrip>();
            options.Projections.Snapshot<StreamActionOrder>(SnapshotLifecycle.Inline);
            options.Projections.LiveStreamAggregation<StreamActionSecretLedger>();
            options.Projections.LiveStreamAggregation<StreamActionThrowing>();
        }

        private static HealthHarness Harness(bool conjoined, bool activity = false, bool policies = false) => HealthHarness.Create(
            checks: [],
            studio: options =>
            {
                options.KnownTenantIds.Add("acme");
                options.Capabilities.ViewDatabaseActivity = activity;
            },
            store: options =>
            {
                if (conjoined)
                {
                    options.Events.TenancyStyle = TenancyStyle.Conjoined;
                    options.Schema.For<StreamActionOrder>().MultiTenanted();
                }

                Aggregates(options);
            },
            policies: policies);

        private static HealthReportFinding E06(string code = "E06") => E06("db", code);

        /// <summary>The finding as the report for <paramref name="harness" />'s database would hold it: the runner refuses another database's.</summary>
        private static HealthReportFinding E06(HealthHarness harness, string code = "E06") => E06(harness.Scope.DatabaseId, code);

        private static HealthReportFinding E06(string databaseId, string code)
        {
            HealthFinding finding = FakeHealthService.Finding(code, HealthSeverity.Info, "Some streams are long", HealthArea.Events);

            return new HealthReportFinding(finding, finding.Evidence, HealthService.MuteKeyOf("default", databaseId, finding), "https://example.invalid/", true, null);
        }

        // ---------------------------------------------------------------------------------------------------
        // What a finding offers
        // ---------------------------------------------------------------------------------------------------

        [Fact]
        public async Task E06_offers_the_action_and_no_other_finding_does()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            IHealthActionService service = harness.Circuit.GetRequiredService<IHealthActionService>();

            HealthActionOffer offer = service.OffersFor(E06()).Should().ContainSingle().Subject;

            offer.Id.Should().Be(Id);
            offer.Label.Should().Be("Find the 20 longest streams");
            offer.Description.Should().Contain("mt_streams").And.Contain("statement_timeout").And.Contain("audited").And.Contain("kept nowhere");
            service.OffersFor(E06("E05")).Should().BeEmpty();
            service.OffersFor(E06("D01")).Should().BeEmpty();
        }

        [Fact]
        public async Task The_action_is_a_singleton_registered_once_and_declares_no_capability()
        {
            await using HealthHarness harness = Harness(conjoined: false);

            IHealthFindingAction[] all = [.. harness.Services.GetServices<IHealthFindingAction>()];

            // E06's, and the two index what-ifs (T01, T15) both studios register (1.2-INDEX-B): each once.
            all.Select(static x => x.Id).Should().BeEquivalentTo(["e06.longest-streams", "t01.try-indexes", "t15.what-if"]);

            IHealthFindingAction[] actions = [.. all.OfType<LongestStreamsAction>()];

            actions.Should().ContainSingle();
            actions[0].Capability.Should().BeNull();
            actions[0].Gate.Should().Be(HealthGate.Database);
            actions[0].Code.Should().Be("E06");
            harness.Services.GetServices<IHealthFindingAction>().OfType<LongestStreamsAction>().Single().Should().BeSameAs(actions[0]);
        }

        // ---------------------------------------------------------------------------------------------------
        // Reach (D27): the gate before any connection
        // ---------------------------------------------------------------------------------------------------

        [Fact]
        public async Task On_a_conjoined_store_with_no_tenant_a_visitor_short_of_the_activity_gate_is_refused_before_any_connection()
        {
            await using HealthHarness harness = Harness(conjoined: true, activity: false);
            IHealthActionService service = harness.Circuit.GetRequiredService<IHealthActionService>();

            HealthActionResult result = await service.RunAsync(harness.Scope, E06(harness), Id, Token);

            // A connection to the refused port would have been a Failed result: a Refused one never reached it.
            result.Outcome.Should().Be(HealthActionOutcome.Refused);
            result.Reason.Should().Contain("each stream belongs to one tenant").And.Contain("Pick a tenant in the header").And.Contain("ViewDatabaseActivity");
            result.AuditCapability.Should().Be(StudioCapability.ViewDatabaseActivity);

            AuditEntries(harness).Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x =>
                !x.Succeeded && x.Capability == nameof(StudioCapability.ViewDatabaseActivity) && x.Target == "the event store's streams (mt_streams)");
        }

        [Fact]
        public async Task With_the_capability_on_the_write_policy_still_decides()
        {
            await using HealthHarness harness = Harness(conjoined: true, activity: true, policies: true);
            harness.Policies.Allow(static resource => resource.Capability is null);

            HealthActionResult result = await harness.Circuit.GetRequiredService<IHealthActionService>().RunAsync(harness.Scope, E06(harness), Id, Token);

            result.Outcome.Should().Be(HealthActionOutcome.Refused);
            result.Reason.Should().Contain("write policy");
        }

        [Fact]
        public async Task Past_the_activity_gate_the_action_goes_on_to_read_and_a_store_that_cannot_be_reached_is_said_so()
        {
            await using HealthHarness harness = Harness(conjoined: true, activity: true);

            HealthActionResult result = await harness.Circuit.GetRequiredService<IHealthActionService>().RunAsync(harness.Scope, E06(harness), Id, Token);

            result.Outcome.Should().Be(HealthActionOutcome.Failed);
            result.Reason.Should().Contain("could not connect");
            harness.Warnings.Should().BeEmpty("a refused connection is an expected state with a sentence of its own, not a logged anomaly");
        }

        [Fact]
        public async Task A_selected_tenant_on_a_conjoined_store_needs_only_the_findings_own_gate()
        {
            await using HealthHarness harness = Harness(conjoined: true, activity: false);

            HealthActionResult result = await harness.Circuit.GetRequiredService<IHealthActionService>()
                .RunAsync(harness.Scope with { TenantId = "acme" }, E06(harness), Id, Token);

            result.Outcome.Should().Be(HealthActionOutcome.Failed, "it reached the connection: no Activity gate stands in front of one tenant's streams");
            result.Reason.Should().Contain("could not connect");
        }

        [Fact]
        public async Task A_store_whose_events_are_not_conjoined_needs_only_the_findings_own_gate()
        {
            await using HealthHarness harness = Harness(conjoined: false, activity: false);

            HealthActionResult result = await harness.Circuit.GetRequiredService<IHealthActionService>().RunAsync(harness.Scope, E06(harness), Id, Token);

            result.Outcome.Should().Be(HealthActionOutcome.Failed);
            result.Reason.Should().Contain("could not connect");
        }

        [Fact]
        public async Task A_finding_about_a_tenant_the_store_policy_hides_is_refused_audited_once_and_the_action_never_runs()
        {
            await using HealthHarness harness = Harness(conjoined: false, activity: true, policies: true);
            harness.Policies.Allow(static resource => resource.TenantId != "acme");

            HealthReportFinding about = E06(harness) with { Finding = E06(harness).Finding with { TenantId = "acme" } };

            HealthActionResult result = await harness.Circuit.GetRequiredService<IHealthActionService>().RunAsync(harness.Scope, about, Id, Token);

            // A Failed result would mean it reached the (refused) connection: Refused means the tenant question stopped it first.
            result.Outcome.Should().Be(HealthActionOutcome.Refused);
            result.Reason.Should().Be("This finding names a tenant the store policy does not show you.").And.NotContain("acme");
            AuditEntries(harness).Should().ContainSingle().Which.Succeeded.Should().BeFalse();
            harness.Policies.Calls.Should().Contain(static x => x.Resource.TenantId == "acme", "the tenant's own store-policy question was asked");
        }

        [Fact]
        public async Task Every_press_is_audited_whatever_it_ends_in()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            IHealthActionService service = harness.Circuit.GetRequiredService<IHealthActionService>();

            await service.RunAsync(harness.Scope, E06(harness), Id, Token);
            await service.RunAsync(harness.Scope, E06(harness), Id, Token);

            AuditEntries(harness).Should().HaveCount(2).And.OnlyContain(static x => !x.Succeeded && x.Action == "Find the longest streams");
        }

        // ---------------------------------------------------------------------------------------------------
        // What the process knows of an aggregate
        // ---------------------------------------------------------------------------------------------------

        [Fact]
        public async Task A_live_only_aggregate_is_rebuilt_on_each_fetch_and_a_snapshotted_one_is_kept_by_a_projection()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            StreamAggregateMap map = StreamAggregateMap.From(harness.Store.Options, isVisible: null);

            StreamClass live = map.Classify(TripAlias.ToUpperInvariant());
            live.StartedAs.Should().Be("StreamActionTrip");
            live.Fetch.Should().Be(StreamFetch.RebuiltFromEveryEvent);
            live.LiveName.Should().Be("StreamActionTrip");

            StreamClass kept = map.Classify(OrderAlias);
            kept.StartedAs.Should().Be("StreamActionOrder");
            kept.Fetch.Should().Be(StreamFetch.KeptByProjection);
            kept.LiveName.Should().BeNull();
        }

        [Fact]
        public async Task A_stream_with_no_type_and_one_this_process_does_not_know_are_said_so()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            StreamAggregateMap map = StreamAggregateMap.From(harness.Store.Options, isVisible: null);

            map.Classify(null).Should().Be(new StreamClass("not recorded", StreamFetch.Unknown, null));
            map.Classify("  ").StartedAs.Should().Be("not recorded");

            StreamClass unknown = map.Classify("someone_elses_aggregate");
            unknown.StartedAs.Should().Be("someone_elses_aggregate", "with nothing hidden the stored alias is all there is to say");
            unknown.Fetch.Should().Be(StreamFetch.Unknown);
            map.ShownAliases.Should().BeNull("nothing hides, so the statement filters nothing");
            map.Hides.Should().BeFalse();
        }

        [Fact]
        public void Two_aggregate_types_of_one_name_are_ambiguous_and_named_as_the_alias_alone()
        {
            // Marten refuses two registered projections of one name, but an aggregate a process only aggregates live by code is
            // known to AllAggregateTypes() without one: the map is handed the pairs directly.
            StreamAggregateMap map = StreamAggregateMap.Of(Shipments, isVisible: null);

            StreamClass ambiguous = map.Classify("shipment");

            ambiguous.StartedAs.Should().Be("shipment (more than one aggregate type has this name)");
            ambiguous.Fetch.Should().Be(StreamFetch.Unknown);
            ambiguous.LiveName.Should().BeNull("no remedy is offered for a name that cannot say which type it means");
        }

        [Fact]
        public async Task The_aliases_these_tests_use_are_the_ones_Marten_files_streams_under()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            global::Marten.Events.EventGraph graph = (global::Marten.Events.EventGraph) harness.Store.Options.Events;

            graph.AggregateAliasFor(typeof(StreamActionTrip)).Should().Be(TripAlias);
            graph.AggregateAliasFor(typeof(StreamActionOrder)).Should().Be(OrderAlias);
            graph.AggregateAliasFor(typeof(StreamActionSecretLedger)).Should().Be(LedgerAlias);
            graph.AggregateAliasFor(typeof(StreamActionThrowing)).Should().Be("stream_action_throwing");
        }

        private static readonly (Type Type, string Alias, bool LiveOnly)[] Shipments =
        [
            (typeof(Shipment), "shipment", true),
            (typeof(Ambiguous.Right.Shipment), "SHIPMENT", true),
        ];

        [Fact]
        public async Task A_hidden_aggregate_is_not_in_the_statement_and_is_never_named_by_the_map()
        {
            await using HealthHarness harness = Harness(conjoined: false);

            StreamAggregateMap map = StreamAggregateMap.From(harness.Store.Options, static type => type != typeof(StreamActionSecretLedger));

            map.Hides.Should().BeTrue();
            map.ShownAliases.Should().NotBeNull().And.Contain(TripAlias).And.Contain(OrderAlias);
            map.ShownAliases.Should().NotContain(LedgerAlias, "its streams are not read");

            // Should one arrive anyway, it is not named.
            StreamClass hidden = map.Classify(LedgerAlias);
            hidden.StartedAs.Should().Be("a type this application does not show");
            hidden.LiveName.Should().BeNull();
            hidden.StartedAs.Should().NotContain("Ledger");
        }

        [Fact]
        public async Task A_visibility_delegate_that_throws_hides_the_type_and_an_unmapped_alias_is_treated_as_hidden()
        {
            await using HealthHarness harness = Harness(conjoined: false);

            StreamAggregateMap map = StreamAggregateMap.From(
                harness.Store.Options,
                static type => type == typeof(StreamActionThrowing) ? throw new InvalidOperationException("host bug") : true);

            map.ShownAliases.Should().NotContain("stream_action_throwing", "a delegate that did not say the type may be shown has not said so");
            map.ShownAliases.Should().NotContain("someone_elses_aggregate", "an alias this process cannot map is not among the shown ones while anything may be hidden");
            map.Classify("someone_elses_aggregate").StartedAs.Should().Be("a type this application does not show");
        }

        // ---------------------------------------------------------------------------------------------------
        // What a result says
        // ---------------------------------------------------------------------------------------------------

        private static readonly HealthStatement Statement = new("select 1", ["@limit"]) { Label = "the longest streams" };

        private static LongStreamRow Row(string id, string? type, long events, string? tenant = null) =>
            new(id, type, events, tenant, new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));

        [Fact]
        public async Task A_result_lists_each_stream_with_its_link_its_aggregate_and_how_it_is_fetched()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            StreamAggregateMap map = StreamAggregateMap.From(harness.Store.Options, isVisible: null);

            HealthActionResult result = LongestStreamsAction.Present(
                map,
                [
                    Row("11111111-1111-1111-1111-111111111111", TripAlias, 4_200),
                    Row("22222222-2222-2222-2222-222222222222", TripAlias.ToUpperInvariant(), 1_100),
                    Row("33333333-3333-3333-3333-333333333333", OrderAlias, 900),
                    Row("44444444-4444-4444-4444-444444444444", null, 20),
                ],
                new LongestStreamsShape(Archived: true, TenantFilter: null, TenantColumn: false, Conjoined: false, NotShown: 0),
                Statement,
                DateTimeOffset.UnixEpoch);

            result.Outcome.Should().Be(HealthActionOutcome.Done);
            result.Columns.Should().Equal("Stream", "Events", "Started as", "On each fetch", "Started");
            result.TableLabel.Should().Be("The longest streams");

            HealthActionCell first = result.Rows[0].Cells[0];
            first.Text.Should().Be("11111111-1111-1111-1111-111111111111");
            first.Mono.Should().BeTrue();
            first.Copy.Should().BeTrue();
            first.Link.Should().NotBeNull();
            first.Link!.Route.Should().Be("events/streams/s");
            first.Link.Query.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string?>("id", "11111111-1111-1111-1111-111111111111"));

            result.Rows[0].Cells[1].Should().Match<HealthActionCell>(static x => x.Text == "4,200" && x.Number);
            result.Rows[0].Cells[2].Text.Should().Be("StreamActionTrip");
            result.Rows[0].Cells[3].Text.Should().Be("rebuilt from every event");
            result.Rows[2].Cells[3].Text.Should().Be("kept by a projection");
            result.Rows[3].Cells[2].Text.Should().Be("not recorded");
            result.Rows[0].Cells[4].Time.Should().Be(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero));

            result.Summary.Should().Be(
                "The 4 longest live streams in this database: 2 were started as StreamActionTrip, rebuilt from every event on each fetch; "
                + "1 was started as StreamActionOrder, kept by a projection; 1 with no type. 2 are over 1,000 events.");

            result.Statements.Should().ContainSingle().Which.Should().Be(Statement);
            result.AuditMessage.Should().Be("Listed 4 of the longest live streams.");
            result.AuditCapability.Should().BeNull();
        }

        [Fact]
        public async Task The_remedy_names_the_aggregate_the_long_streams_belong_to_once_each_and_only_for_live_ones()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            StreamAggregateMap map = StreamAggregateMap.From(harness.Store.Options, isVisible: null);

            HealthActionResult result = LongestStreamsAction.Present(
                map,
                [Row("a", TripAlias, 2_000), Row("b", TripAlias, 1_500), Row("c", OrderAlias, 1_400), Row("d", "nobody", 1_300)],
                new LongestStreamsShape(true, null, false, false, 0),
                Statement,
                DateTimeOffset.UnixEpoch);

            result.Remedies.Should().ContainSingle().Which.Code.Should().Be("opts.Projections.Snapshot<StreamActionTrip>(SnapshotLifecycle.Inline);");
            result.Remedies[0].Explanation.Should().Be(MartenStudio.Services.Health.Checks.LongStreamsWithoutSnapshotCheck.SnapshotRemedyExplanation);
        }

        [Fact]
        public async Task A_kept_aggregate_alone_offers_no_remedy()
        {
            await using HealthHarness harness = Harness(conjoined: false);

            HealthActionResult result = LongestStreamsAction.Present(
                StreamAggregateMap.From(harness.Store.Options, isVisible: null),
                [Row("a", OrderAlias, 5_000)],
                new LongestStreamsShape(true, null, false, false, 0),
                Statement,
                DateTimeOffset.UnixEpoch);

            result.Remedies.Should().BeEmpty();
            result.Summary.Should().Contain("1 was started as StreamActionOrder, kept by a projection").And.Contain("1 is over 1,000 events");
        }

        [Fact]
        public async Task With_tenants_named_the_table_has_the_column_the_count_left_out_is_said_and_the_audit_is_under_the_capability()
        {
            await using HealthHarness harness = Harness(conjoined: true);

            HealthActionResult result = LongestStreamsAction.Present(
                StreamAggregateMap.From(harness.Store.Options, isVisible: null),
                [Row("a", TripAlias, 3_000, "acme")],
                new LongestStreamsShape(true, null, TenantColumn: true, Conjoined: true, NotShown: 3),
                Statement,
                DateTimeOffset.UnixEpoch);

            result.Columns.Should().Equal("Stream", "Events", "Started as", "On each fetch", "Tenant", "Started");
            result.Rows[0].Cells[4].Should().Match<HealthActionCell>(static x => x.Text == "acme" && x.Mono);
            result.Notes.Should().Contain("3 of the longest streams are of tenants your account may not see, and are not listed.");
            result.AuditMessage.Should().Be("Listed 1 of the longest live streams, 3 of tenants not shown.");
            result.AuditMessage.Should().NotContain("acme");
            result.AuditCapability.Should().Be(StudioCapability.ViewDatabaseActivity);
        }

        [Fact]
        public async Task A_tenant_the_read_was_narrowed_to_is_said_and_where_the_events_are_not_conjoined_the_note_says_how_the_list_was_narrowed()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            StreamAggregateMap map = StreamAggregateMap.From(harness.Store.Options, isVisible: null);

            HealthActionResult narrowed = LongestStreamsAction.Present(
                map, [Row("a", TripAlias, 3_000)], new LongestStreamsShape(true, "acme", false, Conjoined: false, 0), Statement, DateTimeOffset.UnixEpoch);
            HealthActionResult conjoined = LongestStreamsAction.Present(
                map, [Row("a", TripAlias, 3_000)], new LongestStreamsShape(true, "acme", false, Conjoined: true, 0), Statement, DateTimeOffset.UnixEpoch);

            narrowed.Notes.Should().Contain("Only streams appended under tenant acme are listed, as Events › Streams lists them.");
            conjoined.Summary.Should().Contain("in this database of tenant acme");
        }

        [Fact]
        public async Task The_notes_say_what_is_left_out_and_that_a_type_is_the_one_a_stream_was_started_as()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            StreamAggregateMap hiding = StreamAggregateMap.From(harness.Store.Options, static _ => true);
            StreamAggregateMap open = StreamAggregateMap.From(harness.Store.Options, isVisible: null);

            HealthActionResult withHiding = LongestStreamsAction.Present(hiding, [], new LongestStreamsShape(true, null, false, false, 0), Statement, DateTimeOffset.UnixEpoch);
            HealthActionResult without = LongestStreamsAction.Present(open, [], new LongestStreamsShape(false, null, false, false, 0), Statement, DateTimeOffset.UnixEpoch);

            withHiding.Notes.Should().Contain("Archived streams are left out.")
                .And.Contain("Streams of aggregate types this application hides from the studio are left out.")
                .And.Contain(static x => x.StartsWith("A stream's type is the aggregate it was started as", StringComparison.Ordinal));
            without.Notes.Should().NotContain("Archived streams are left out.").And.NotContain(static x => x.Contains("hides", StringComparison.Ordinal));
            without.Summary.Should().Be("No live stream was found in this database.");
        }

        [Fact]
        public async Task With_hiding_configured_the_name_of_a_hidden_type_appears_nowhere_in_the_result()
        {
            await using HealthHarness harness = Harness(conjoined: false);
            StreamAggregateMap map = StreamAggregateMap.From(harness.Store.Options, static type => type != typeof(StreamActionSecretLedger));

            // Even a row that should not be there (the statement filters it) is not named.
            HealthActionResult result = LongestStreamsAction.Present(
                map,
                [Row("a", TripAlias, 3_000), Row("b", LedgerAlias, 2_000)],
                new LongestStreamsShape(true, null, false, false, 0),
                Statement,
                DateTimeOffset.UnixEpoch);

            Dump(result).Should().NotContainEquivalentOf("Ledger").And.NotContainEquivalentOf("secret");
            result.Remedies.Should().ContainSingle().Which.Code.Should().Contain("StreamActionTrip");
        }

        [Fact]
        public void Two_types_of_one_name_get_no_remedy_and_say_so()
        {
            HealthActionResult result = LongestStreamsAction.Present(
                StreamAggregateMap.Of(Shipments, isVisible: null),
                [Row("a", "shipment", 5_000)],
                new LongestStreamsShape(true, null, false, false, 0),
                Statement,
                DateTimeOffset.UnixEpoch);

            result.Remedies.Should().BeEmpty();
            result.Rows[0].Cells[2].Text.Should().EndWith("(more than one aggregate type has this name)");
        }

        private static StudioActionLogEntry[] AuditEntries(HealthHarness harness) =>
            [.. harness.Circuit.GetRequiredService<StudioActionLog>().GetLatest().Where(static x => x.Action == "Find the longest streams")];

        /// <summary>Everything a result would put on a page, as one string.</summary>
        private static string Dump(HealthActionResult result)
        {
            StringBuilder text = new();
            text.AppendLine(result.Summary).AppendLine(result.Reason).AppendLine(result.AuditMessage).AppendLine(result.TableLabel);

            foreach (string column in result.Columns)
            {
                text.AppendLine(column);
            }

            foreach (HealthActionRow row in result.Rows)
            {
                foreach (HealthActionCell cell in row.Cells)
                {
                    text.AppendLine(cell.Text).AppendLine(cell.Link?.Label).AppendLine(cell.Link?.Route);
                }
            }

            foreach (string note in result.Notes)
            {
                text.AppendLine(note);
            }

            foreach (HealthRemedy remedy in result.Remedies)
            {
                text.AppendLine(remedy.Code).AppendLine(remedy.Explanation);
            }

            foreach (HealthStatement statement in result.Statements)
            {
                text.AppendLine(statement.Sql).AppendLine(string.Join(",", statement.ParameterNames)).AppendLine(statement.Label);
            }

            return text.ToString();
        }
    }
}
