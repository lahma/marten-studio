using System.Globalization;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Generation;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Query;
using MartenStudio.Services.Schema;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// The studio against 1.2 million documents and 1.2 million events.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in.</b> Every test here skips unless <c>MARTENSTUDIO_LARGE=1</c>, because building the set
/// takes minutes and several gigabytes. What it buys is the only measurement that distinguishes a read
/// that uses an index from one that does not: at a hundred thousand rows a sequential scan is
/// indistinguishable from an index lookup at the resolution a human notices, and at a million it is the
/// difference between a page and a coffee.
/// </para>
/// <para>
/// <b>Same probes, same budgets, deliberately.</b> A separate set of "large" budgets would mean a screen
/// could get ten times slower between the two sizes and both suites would stay green - which is precisely
/// the regression these exist to catch.
/// </para>
/// </remarks>
[Collection(GeneratedDataSuite.Name)]
public class LargeDataSetLiveTests(LargeDataSetFixture fixture) : IClassFixture<LargeDataSetFixture>
{
    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static DemoDataPlan Plan => DemoDataPlan.For(DemoDataSize.Large);

    /// <summary>The set is the size it claims to be, and the run reports its throughput.</summary>
    [LargeDataFact]
    public void The_large_set_is_over_a_million_documents_and_a_million_events()
    {
        Write(string.Format(
            CultureInfo.InvariantCulture,
            "Large: {0:N0} documents + {1:N0} events ({2:N0} streams) in {3:F1} s = {4:N0} rows/s",
            fixture.Counters.Documents,
            fixture.Counters.Events,
            fixture.Counters.Streams,
            fixture.GenerationTime.TotalSeconds,
            fixture.RowsPerSecond));

        fixture.Counters.Documents.Should().BeGreaterThan(1_000_000);
        fixture.Counters.Events.Should().BeGreaterThan(1_000_000);
        fixture.Counters.Documents.Should().Be(Plan.DocumentTarget);
        fixture.Counters.Events.Should().Be(Plan.EventTarget);
    }

    /// <summary>Every screen's read, at production scale, against the same budgets as the medium set.</summary>
    [LargeDataFact]
    public async Task Every_screens_read_stays_inside_its_budget()
    {
        using var probe = new StudioResponsivenessProbe(fixture.Marten);

        var report = new BudgetReport(Write);

        (TimeSpan railTime, CollectionRail rail) = await probe.RailAsync();
        rail.Error.Should().BeNull();
        report.Check("collections rail", railTime, ResponsivenessBudgets.CollectionsRail);

        (TimeSpan firstTime, DocumentPage first) = await probe.FirstPageAsync("customer");
        first.State.Should().Be(DocumentListState.Loaded, first.Error);
        first.Rows.Should().NotBeEmpty();
        report.Check("customer first page", firstTime, ResponsivenessBudgets.DocumentPage);

        (TimeSpan deepTime, DocumentPage deep) = await probe.DeepKeysetPageAsync("customer");
        deep.State.Should().Be(DocumentListState.Loaded, deep.Error);
        deep.Rows.Should().NotBeEmpty();
        report.Check("customer deep keyset page", deepTime, ResponsivenessBudgets.DocumentPage);

        (TimeSpan recentTime, RecentDocuments recent) = await probe.RecentAsync();
        recent.Rows.Should().NotBeEmpty();
        report.Check("_recent", recentTime, ResponsivenessBudgets.RecentDocuments);

        (TimeSpan feedTime, EventPage feed) = await probe.FeedFirstPageAsync();
        feed.Error.Should().BeNull();
        feed.Rows.Should().NotBeEmpty();
        report.Check("event feed first page", feedTime, ResponsivenessBudgets.EventFeedPage);

        long highest = await probe.HighestSequenceAsync() ?? 0;
        highest.Should().BeGreaterThan(1_000_000);

        (TimeSpan feedDeepTime, EventPage feedDeep) = await probe.FeedKeysetPageAsync(highest / 2);
        feedDeep.Error.Should().BeNull();
        feedDeep.Rows.Should().NotBeEmpty();
        report.Check("event feed keyset page", feedDeepTime, ResponsivenessBudgets.EventFeedPage);

        (TimeSpan streamsTime, StreamPage streams) = await probe.StreamsAsync();
        streams.Error.Should().BeNull();
        report.Check("stream list first page", streamsTime, ResponsivenessBudgets.StreamPage);

        (TimeSpan projectionsTime, ProjectionsView projections) = await probe.ProjectionsAsync();
        projections.Projections.Should().NotBeEmpty();
        report.Check("projections progress", projectionsTime, ResponsivenessBudgets.ProjectionsProgress);

        (TimeSpan tablesTime, SchemaTables tables) = await probe.SchemaTablesAsync();
        tables.Reason.Should().BeNull();
        tables.Tables.Should().NotBeEmpty();
        report.Check("schema Tables tab", tablesTime, ResponsivenessBudgets.SchemaTables);

        report.Unexpected.Should().BeEmpty(report.Because);
    }

    /// <summary>
    /// The rail reports an estimate rather than a count, which is the whole of D8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is asserted here rather than at the medium size because it only matters here: an exact
    /// <c>count(*)</c> of sixty thousand rows is a few milliseconds and of six hundred thousand is most
    /// of a second, per collection, per refresh.
    /// </para>
    /// <para>
    /// The estimate exists because the generator runs <c>analyze</c> when it finishes. Without that,
    /// <c>reltuples</c> is <c>-1</c> until autovacuum gets there — and before P2-perf,
    /// <c>DocumentDataService</c> fell back to a real exact count for up to twenty-five collections, so a
    /// freshly bulk-loaded million-row store was the one case where opening the browser did pay for the
    /// scan. It no longer is: a never-analysed collection is settled by a probe bounded by
    /// <c>ExactCountThreshold</c>, proved at small scale by <c>DocumentCountThresholdLiveTests</c>.
    /// </para>
    /// </remarks>
    [LargeDataFact]
    public async Task The_rail_estimates_rather_than_counting_a_collection_this_big()
    {
        using var probe = new StudioResponsivenessProbe(fixture.Marten);

        (_, CollectionRail rail) = await probe.RailAsync();

        CollectionInfo customer = rail.Find("customer")!;

        customer.Count.IsUnavailable.Should().BeFalse();
        customer.Count.IsEstimate.Should().BeTrue("a collection this size is never counted on a navigation path");
        customer.Count.Value.Should().BeGreaterThan(Plan.Customers / 2);

        // ... and asking for the exact number does not override it either (P2-perf deliverable 2). This
        // collection is six times the default ExactCountThreshold, so the studio declines and says so
        // rather than starting a sequential scan over six hundred thousand rows because somebody clicked.
        // The other half of D8 - that the exact count is there for the asking - is asserted by
        // MediumDataSetLiveTests, where the collections are inside the threshold.
        DocumentCount asked = await probe.CountExactAsync("customer");

        asked.IsExactRefused.Should().BeTrue();
        asked.IsEstimate.Should().BeTrue("the estimate is still the answer, and it says why");
        asked.ExactRefusedAbove.Should().Be(new MartenStudioOptions().ExactCountThreshold);
        asked.Reason.Should().Contain("ExactCountThreshold");
        asked.Value.Should().NotBe(
            Plan.Customers + 25, "a count(*) would have answered exactly that; none ran");
    }

    /// <summary>Offset paging refuses past the builder's cap rather than walking half a million rows.</summary>
    [LargeDataFact]
    public async Task Offset_paging_refuses_past_the_cap()
    {
        using var probe = new StudioResponsivenessProbe(fixture.Marten);

        (TimeSpan inTime, DocumentPage inside) = await probe.OffsetPageAsync("customer", DocumentQueryBuilder.MaxOffset);
        inside.State.Should().Be(DocumentListState.Loaded, inside.Error);
        Write(ResponsivenessBudgets.Line(
            "customer offset page at the cap", inTime, ResponsivenessBudgets.DocumentPage));

        (_, DocumentPage past) = await probe.OffsetPageAsync("customer", DocumentQueryBuilder.MaxOffset + 1);
        past.State.Should().Be(DocumentListState.Failed);
        past.Error.Should().Contain("keyset");
    }

    /// <summary>A free-text search over a million documents is described, never run.</summary>
    [LargeDataFact]
    public async Task A_free_text_search_is_withheld_rather_than_run()
    {
        using var probe = new StudioResponsivenessProbe(fixture.Marten);

        (TimeSpan median, DocumentPage page) = await probe.WithheldSearchAsync("customer");

        Write(ResponsivenessBudgets.Line("withheld free-text search", median, ResponsivenessBudgets.DocumentPage));

        page.State.Should().Be(DocumentListState.BlockedByVerdict);
        page.Verdict.FilterLevel.Should().Be(IndexVerdictLevel.Red);
        page.Rows.Should().BeEmpty();
        median.Should().BeLessThan(ResponsivenessBudgets.DocumentPage);
    }
}
