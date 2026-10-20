using JasperFx.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health.Checks;

using static MartenStudio.Tests.Health.EventHealthFacts;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>E02, E03 and E04's judgement over a stated recent-traffic profile.</summary>
public class EventTrafficChecksTests
{
    // ---------------------------------------------------------------------------------------------------
    // E02 Tombstones
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void E02_a_window_too_small_to_judge_is_quiet_however_many_tombstones_it_holds()
    {
        HealthVerdict verdict = new TombstonesCheck().Evaluate(Profile(Totals(999, tombstones: 500)), At());

        verdict.Findings.Should().BeEmpty("a quiet store with a handful of events must never fire");
        verdict.Note.Should().Contain("999").And.Contain("1,000");
    }

    [Fact]
    public void E02_exactly_one_percent_is_quiet()
    {
        new TombstonesCheck().Evaluate(Profile(Totals(1_000, tombstones: 10)), At()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void E02_over_one_percent_on_a_Rich_store_is_a_warning_that_counts_every_failure()
    {
        HealthFinding finding = new TombstonesCheck()
            .Evaluate(Profile(Totals(10_000, tombstones: 250), mode: EventAppendMode.Rich), At())
            .Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("E02");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Contain("2.5%");
        finding.WhyItMatters.Should().Contain("Under Rich appends every failed save leaves them");
        finding.Evidence.Should().Contain(new HealthEvidence("Tombstones in the window", "250 (2.5%)"));
        finding.Evidence.Should().Contain(new HealthEvidence("Tombstone event type", "tombstone"));
        finding.Remedy.Should().BeNull("there is no StoreOptions line that stops a save from conflicting");
        finding.TenantId.Should().BeNull();
    }

    [Theory]
    [InlineData(EventAppendMode.Quick)]
    [InlineData(EventAppendMode.QuickWithServerTimestamps)]
    public void E02_on_a_Quick_store_says_the_share_undercounts(EventAppendMode mode)
    {
        HealthFinding finding = new TombstonesCheck()
            .Evaluate(Profile(Totals(10_000, tombstones: 250), mode: mode), At())
            .Findings.Should().ContainSingle().Subject;

        finding.WhyItMatters.Should().Contain("undercounts").And.Contain("D07");
    }

    [Fact]
    public void E02_on_a_conjoined_store_counts_the_tenants_and_names_none()
    {
        TrafficProfile facts = Profile(
            Totals(10_000, tombstones: 300, tenants: 2),
            tenants: [TenantOf("acme", 6_000, tombstones: 300), TenantOf("globex", 4_000)],
            conjoined: true);

        HealthFinding finding = new TombstonesCheck().Evaluate(facts, At()).Findings.Should().ContainSingle().Subject;

        finding.Evidence.Should().Contain(new HealthEvidence("Tenants with tombstones", "1"));
        finding.TenantId.Should().BeNull("the finding is about the database as a whole");
        string everything = finding.Title + finding.WhyItMatters + finding.Caveat + string.Join("|", finding.Evidence.Select(static x => x.Label + x.Value));
        everything.Should().NotContain("acme").And.NotContain("globex", "a tenant's name needs the activity gate and that tenant's policy");
    }

    // ---------------------------------------------------------------------------------------------------
    // E03 Hot stream
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void E03_a_window_too_small_to_judge_is_quiet_even_if_one_stream_has_all_of_it()
    {
        TrafficProfile facts = Profile(Totals(10), streams: [StreamOf("only-stream", 10)]);

        new HotStreamCheck().Evaluate(facts, At()).Findings.Should().BeEmpty("a store with ten events never has a hot stream");
    }

    [Fact]
    public void E03_tombstones_count_neither_in_the_window_nor_in_a_stream()
    {
        // 1,200 events, 300 of them tombstones: 900 appends is under the window's minimum.
        TrafficProfile facts = Profile(Totals(1_200, tombstones: 300), streams: [StreamOf("busy", 800)]);

        new HotStreamCheck().Evaluate(facts, At()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void E03_a_busiest_stream_at_or_under_a_fifth_is_quiet_and_the_note_says_its_share()
    {
        TrafficProfile facts = Profile(Totals(10_000), streams: [StreamOf("a", 2_000), StreamOf("b", 1_500)]);

        HealthVerdict verdict = new HotStreamCheck().Evaluate(facts, At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("20%");
    }

    [Fact]
    public void E03_one_stream_over_a_fifth_on_a_single_tenant_store_names_the_stream_at_the_checks_gate()
    {
        TrafficProfile facts = Profile(Totals(10_000), streams: [StreamOf("7c9e6679-7425-40de-944b-e07fc1f90ae7", 3_500), StreamOf("other", 100)]);

        HealthFinding finding = new HotStreamCheck().Evaluate(facts, At()).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("E03");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("One stream took 35% of the recent appends, from 7.3 transactions a second")
            .And.NotContain("7c9e6679", "a title names nothing a gate would hide");
        finding.Evidence.Should().Contain(new HealthEvidence("Transactions that appended to it", "3,500"));
        finding.Evidence.Should().Contain(new HealthEvidence("Their rate", "7.3 transactions a second"), "3,500 transactions over its eight minutes in the window");
        finding.TenantId.Should().BeNull("a single-tenant store's streams are the database's");
        finding.Subject.Should().Be("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        finding.Evidence.Should().Contain(new HealthEvidence("Stream", "7c9e6679-7425-40de-944b-e07fc1f90ae7"));
        finding.Evidence.Should().Contain(new HealthEvidence("Its appends in the window", "3,500 of 10,000 (35%)"));
        finding.Evidence.Should().OnlyContain(static x => x.Gate == HealthGate.None, "the stream id is shown at the finding's own gate");
        finding.WhyItMatters.Should().Contain("FetchForExclusiveWriting").And.Contain("E02");
    }

    [Fact]
    public void E03_on_a_conjoined_store_the_finding_names_the_streams_tenant_and_its_share()
    {
        TrafficProfile facts = Profile(
            Totals(10_000, tenants: 2),
            streams: [StreamOf("order-1", 4_000, tenant: "acme")],
            tenants: [TenantOf("acme", 7_000), TenantOf("globex", 3_000)],
            conjoined: true);

        HealthFinding finding = new HotStreamCheck().Evaluate(facts, At()).Findings.Should().ContainSingle().Subject;

        finding.TenantId.Should().Be("acme", "so only a visitor through the activity gate who may see acme is shown it");
        finding.Subject.Should().Be("acme/order-1", "two tenants may each have a stream of the same id");
        finding.Evidence.Should().Contain(new HealthEvidence("Tenant", "acme"));
        finding.Evidence.Should().Contain(new HealthEvidence("The tenant's share of the window", "70%"));
        finding.Title.Should().NotContain("acme").And.NotContain("order-1");
    }

    [Fact]
    public void E03_two_hot_streams_are_two_findings_with_distinct_keys()
    {
        TrafficProfile facts = Profile(Totals(10_000), streams: [StreamOf("a", 3_000), StreamOf("b", 2_500)]);

        IReadOnlyList<HealthFinding> findings = new HotStreamCheck().Evaluate(facts, At()).Findings;

        findings.Should().HaveCount(2);
        findings.Select(static x => x.Key).Should().OnlyHaveUniqueItems("a duplicate key is a Failed row");
    }

    [Fact]
    public void E03_a_large_share_at_under_one_transaction_a_second_is_an_info_not_contention()
    {
        // A quiet store whose ledger takes 30% of the window: 300 transactions over its eight minutes is 0.625 a second.
        TrafficProfile facts = Profile(Totals(1_000), streams: [StreamOf("ledger", 300)]);

        HealthFinding finding = new HotStreamCheck().Evaluate(facts, At()).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info, "writers are unlikely to meet on a row they touch once every couple of seconds");
        finding.Title.Should().Be("One stream took 30% of the recent appends, though from only 37.5 transactions a minute");
        finding.WhyItMatters.Should().Contain("not contention yet");
        finding.Evidence.Should().Contain(new HealthEvidence("Their rate", "37.5 transactions a minute"));
    }

    [Fact]
    public void E03_a_bulk_import_in_one_SaveChanges_is_one_writer_and_an_info_however_fast_it_went()
    {
        // The review's case: a thousand events appended in one transaction inside half a second. By events that is
        // 2,000 a second; by transactions it is one writer, and nobody queued.
        TrafficProfileRow import = StreamOf("import", 1_000, transactions: 1) with { FirstAt = Now, LastAt = Now.AddMilliseconds(500) };

        HealthFinding finding = new HotStreamCheck().Evaluate(Profile(Totals(2_000), streams: [import]), At())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Title.Should().Be("One stream took 50% of the recent appends, all in one transaction");
        finding.WhyItMatters.Should().Contain("a bulk import, or one writer saving in batches").And.Contain("a share is not a queue");
        finding.Evidence.Should().Contain(new HealthEvidence("Transactions that appended to it", "1"));
    }

    [Fact]
    public void E03_a_few_fast_batches_are_still_one_writer_below_the_minimum_transactions()
    {
        TrafficProfileRow batches = StreamOf("batches", 900, transactions: HotStreamCheck.MinimumContendingTransactions - 1)
            with { FirstAt = Now, LastAt = Now.AddSeconds(2) };

        HealthFinding finding = new HotStreamCheck().Evaluate(Profile(Totals(2_000), streams: [batches]), At())
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info, "nine saves in two seconds is a batch job, not a crowd");
        finding.Title.Should().EndWith("in only 9 transactions");
    }

    [Fact]
    public void E03_many_transactions_inside_one_second_count_as_contended()
    {
        TrafficProfileRow burst = StreamOf("burst", 300, transactions: 40) with { FirstAt = Now, LastAt = Now.AddMilliseconds(400) };

        new HotStreamCheck().Evaluate(Profile(Totals(1_000), streams: [burst]), At())
            .Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning, "forty writers inside a second is a queue");
    }

    [Fact]
    public void E03_the_rate_is_the_streams_transactions_over_its_own_span_of_at_least_a_second()
    {
        HotStreamCheck.TransactionsPerSecond(StreamOf("a", 480)).Should().BeApproximately(1.0, 1e-9, "480 transactions over eight minutes");
        HotStreamCheck.Contended(StreamOf("a", 479)).Should().BeFalse("479 over eight minutes is under one a second");
        HotStreamCheck.TransactionsPerSecond(StreamOf("a", 5_000, transactions: 480)).Should().BeApproximately(1.0, 1e-9, "events do not count, transactions do");
        HotStreamCheck.TransactionsPerSecond(StreamOf("a", 300, transactions: 12) with { FirstAt = Now, LastAt = Now })
            .Should().Be(12, "a span under a second is taken as one second");
    }

    [Fact]
    public void E03_an_archived_stream_is_no_longer_hot()
    {
        TrafficProfile facts = Profile(Totals(10_000), streams: [StreamOf("archived", 5_000, archived: 5_000)]);

        new HotStreamCheck().Evaluate(facts, At()).Findings.Should().BeEmpty("nothing appends to an archived stream, so no queue forms");
    }

    // ---------------------------------------------------------------------------------------------------
    // E04 Large event payloads
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void E04_a_window_too_small_for_a_percentile_is_quiet()
    {
        new LargeEventPayloadsCheck().Evaluate(Profile(Totals(99, p95: 1_000_000)), At()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void E04_a_95th_percentile_of_64_KB_is_quiet()
    {
        HealthVerdict verdict = new LargeEventPayloadsCheck().Evaluate(Profile(Totals(5_000, p95: 64 * 1024)), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("64 KB");
    }

    [Fact]
    public void E04_over_64_KB_is_a_warning_naming_the_types_over_the_line()
    {
        TrafficProfile facts = Profile(
            Totals(5_000, median: 2_000, p95: 90_000, max: 2_000_000),
            types: [TypeOf("document_attached", 400, 300_000), TypeOf("order_placed", 4_000, 1_000), TypeOf("rare_big", 5, 900_000)]);

        HealthFinding finding = new LargeEventPayloadsCheck().Evaluate(facts, At()).Findings.Should().ContainSingle().Subject;

        finding.Code.Should().Be("E04");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Contain("87.9 KB");
        finding.Evidence.Should().Contain(static x => x.Label == "Event type document_attached");
        finding.Evidence.Should().NotContain(static x => x.Label == "Event type order_placed", "it is under the line");
        finding.Evidence.Should().NotContain(static x => x.Label == "Event type rare_big", "five events are too few for a percentile of their own");
        finding.Caveat.Should().Contain("pg_column_size");
    }
}
