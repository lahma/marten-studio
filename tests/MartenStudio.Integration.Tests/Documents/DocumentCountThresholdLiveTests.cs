using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// <c>MartenStudioOptions.ExactCountThreshold</c> against a real database: above it, nothing counts.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-perf deliverable 2.</b> The option was documented behaviour the studio did not have —
/// <c>DocumentDataService</c> built its <c>CountEstimator</c> without the threshold in all three places
/// and never called the one method that read it. So a freshly bulk-loaded store, where every table
/// reports <c>reltuples = -1</c>, ran up to twenty-five sequential scans on opening the documents browser,
/// and the "=" button next to a ten-million-row collection was an unbounded <c>count(*)</c> a click away.
/// </para>
/// <para>
/// <b>How "no <c>count(*)</c> ran" is proved without <c>pg_stat_statements</c>.</b> The evidence is
/// positive rather than absent: <c>countedthing</c> is analysed at three hundred rows and then given two
/// hundred more without being analysed again, so <c>reltuples</c> says 300 while the table holds 500. A
/// count that ran would answer 500. The number that comes back is 300, and it is marked as an estimate
/// the studio declined to improve on — which is a thing only the un-run count can produce.
/// <c>DocumentVerdictLiveTests</c> proves a withheld read the other way, by removing the table; the same
/// trick is not available here because the estimate is read from <c>pg_class</c> and needs the table to
/// exist.
/// </para>
/// </remarks>
public class DocumentCountThresholdLiveTests(DocumentCountThresholdLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentCountThresholdLiveTests.Fixture>
{
    /// <summary>The threshold this class runs under. Small, so the sets it needs stay small.</summary>
    public const long Threshold = 100;

    /// <summary>Analysed at this many rows.</summary>
    public const int CountedAnalysed = 300;

    /// <summary>And holding this many by the time the tests run.</summary>
    public const int CountedReal = 500;

    /// <summary>Never analysed, and above the threshold.</summary>
    public const int BigRows = 150;

    /// <summary>Never analysed, and below it.</summary>
    public const int SmallRows = 40;

    /// <summary>Analysed, and below it.</summary>
    public const int TinyRows = 20;

    /// <summary>Never analysed, below the threshold by row count, and huge by page count.</summary>
    public const int HeavyRows = 10;

    /// <summary>Never analysed, below the threshold, and locked while the rail reads it.</summary>
    public const int LockedRows = 30;

    /// <summary>Per tenant, on the conjoined collection.</summary>
    public const int TenantedRows = 12;

    /// <summary>
    /// Rows per tenant in the conjoined collection whose whole table sits <em>above</em> the threshold:
    /// two tenants of these is 120 against a threshold of 100.
    /// </summary>
    public const int WideTenantedRows = 60;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Four collections, each in one of the four states the threshold has to tell apart.
    /// </summary>
    /// <remarks>
    /// Autovacuum is turned off on every one of them. Three of these tests are about what the studio does
    /// when <c>reltuples</c> is <c>-1</c>, and auto-analyze waking up mid-run would quietly turn them into
    /// tests of the other branch — passing, and measuring nothing.
    /// </remarks>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>The tables this class owns, without the <c>mt_doc_</c> prefix.</summary>
        private static readonly string[] Tables =
        [
            "countedthing", "bigthing", "smallthing", "tinything", "heavything", "lockedthing", "tenantedthing",
            "widetenantedthing",
        ];

        /// <summary>No demo data: these tests want collections of known, deliberate sizes.</summary>
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            // Registered up front so the tables exist after the fixture's apply, rather than being created
            // lazily by the first Store() call.
            options.Schema.For<CountedThing>();
            options.Schema.For<BigThing>();
            options.Schema.For<SmallThing>();
            options.Schema.For<TinyThing>();
            options.Schema.For<HeavyThing>();
            options.Schema.For<LockedThing>();
            options.Schema.For<TenantedThing>().MultiTenanted();
            options.Schema.For<WideTenantedThing>().MultiTenanted();
        }

        /// <inheritdoc />
        protected override void ConfigureStudio(MartenStudioOptions options) =>
            options.ExactCountThreshold = Threshold;

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using (NpgsqlConnection connection = await Postgres.OpenAsync())
            {
                foreach (var table in Tables)
                {
                    await using var off = new NpgsqlCommand(
                        $"alter table \"{Schema}\".\"mt_doc_{table}\" set (autovacuum_enabled = off)", connection);

                    await off.ExecuteNonQueryAsync();
                }
            }

            await WriteAsync<CountedThing>(CountedAnalysed);
            await WriteAsync<BigThing>(BigRows);
            await WriteAsync<SmallThing>(SmallRows);
            await WriteAsync<TinyThing>(TinyRows);
            await WriteAsync<HeavyThing>(HeavyRows);
            await WriteAsync<LockedThing>(LockedRows);

            await WriteTenantedAsync("acme");
            await WriteTenantedAsync("globex");
            await WriteWideTenantedAsync("acme");
            await WriteWideTenantedAsync("globex");

            await AnalyzeAsync("mt_doc_countedthing", "mt_doc_tinything", "mt_doc_tenantedthing", "mt_doc_widetenantedthing");

            // A heap Postgres has measured and a row count it has not: the guard reads relpages out of the
            // same pg_class row the estimate came from, and there is no way to make a table genuinely
            // occupy a hundred thousand pages inside a test. Doctoring the catalog is how the state gets
            // constructed; what is being tested is what the studio does with it.
            await using (NpgsqlConnection connection = await Postgres.OpenAsync())
            {
                await using var doctor = new NpgsqlCommand(
                    $"""
                     update pg_catalog.pg_class
                     set relpages = 100000, reltuples = -1
                     where oid = '"{Schema}"."mt_doc_heavything"'::regclass
                     """,
                    connection);

                await doctor.ExecuteNonQueryAsync();
            }

            // ... and only now the rows that make the estimate stale. A count(*) of countedthing answers
            // 500 from here on; reltuples goes on saying 300, because nothing will analyse it again.
            await WriteAsync<CountedThing>(CountedReal - CountedAnalysed);
        }

        private async Task WriteWideTenantedAsync(string tenantId)
        {
            await using IDocumentSession session = Marten.Store.LightweightSession(tenantId);

            for (var i = 0; i < WideTenantedRows; i++)
            {
                session.Store(new WideTenantedThing { Id = Guid.NewGuid() });
            }

            await session.SaveChangesAsync();
        }

        private async Task WriteTenantedAsync(string tenantId)
        {
            await using IDocumentSession session = Marten.Store.LightweightSession(tenantId);

            for (var i = 0; i < TenantedRows; i++)
            {
                session.Store(new TenantedThing { Id = Guid.NewGuid() });
            }

            await session.SaveChangesAsync();
        }

        private async Task WriteAsync<T>(int rows) where T : ThresholdDocument, new()
        {
            await using IDocumentSession session = Marten.Store.LightweightSession();

            for (var i = 0; i < rows; i++)
            {
                session.Store(new T { Id = Guid.NewGuid() });
            }

            await session.SaveChangesAsync();
        }

        private async Task AnalyzeAsync(params string[] tables)
        {
            await using NpgsqlConnection connection = await Postgres.OpenAsync();

            foreach (var table in tables)
            {
                await using var analyze = new NpgsqlCommand($"analyze \"{Schema}\".\"{table}\"", connection);

                await analyze.ExecuteNonQueryAsync();
            }
        }
    }

    /// <summary>
    /// Above the threshold the "=" button returns the estimate, and the number proves nothing counted.
    /// </summary>
    [PostgresFact]
    public async Task An_exact_count_is_declined_above_the_threshold_and_the_number_proves_it()
    {
        // The anti-vacuity half, first: the table really does hold five hundred rows, so the three hundred
        // asserted below can only have come from pg_class.
        (await RealCountAsync("mt_doc_countedthing")).Should().Be(CountedReal);

        using var documents = Documents();

        DocumentCount count = await documents.Service.CountExactAsync(Scope, "countedthing", Token);

        count.Value.Should().Be(
            CountedAnalysed,
            "a count(*) would have answered {0}; this is the stale reltuples, so none ran",
            CountedReal);

        count.IsEstimate.Should().BeTrue();
        count.IsExactRefused.Should().BeTrue();
        count.IsUnavailable.Should().BeFalse("declining to count is not a failure to read");
        count.ExactRefusedAbove.Should().Be(Threshold);
        count.Reason.Should().Contain("Estimate only").And.Contain("ExactCountThreshold");
    }

    /// <summary>Below the threshold the exact count is paid for and is exact.</summary>
    [PostgresFact]
    public async Task An_exact_count_below_the_threshold_is_paid_for()
    {
        using var documents = Documents();

        DocumentCount count = await documents.Service.CountExactAsync(Scope, "tinything", Token);

        count.Value.Should().Be(TinyRows);
        count.IsEstimate.Should().BeFalse();
        count.IsExactRefused.Should().BeFalse();
    }

    /// <summary>
    /// A never-analysed collection above the threshold is <c>Unknown</c> with the "estimate only" reason,
    /// and is never scanned.
    /// </summary>
    /// <remarks>
    /// <c>reltuples = -1</c> is the state of every table in a freshly restored or bulk-loaded database, so
    /// this is the branch that decides what opening the documents browser costs on exactly the store where
    /// it matters. The rail asks <c>select 1 … offset 100 limit 1</c> — bounded by the threshold — finds a
    /// row, and stops. It does not then count a hundred and fifty rows to find out how many there are.
    /// </remarks>
    [PostgresFact]
    public async Task A_never_analysed_collection_above_the_threshold_is_unknown_rather_than_scanned()
    {
        await AssertNeverAnalysedAsync("mt_doc_bigthing");

        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, Token);
        DocumentCount count = rail.Find("bigthing")!.Count;

        count.IsUnknown.Should().BeTrue();
        count.IsExactRefused.Should().BeTrue();
        count.Value.Should().Be(0, "there is no number, which is not the same as none");
        count.Reason.Should().Contain("Estimate only");

        // ... and the list header says the same thing, rather than paying for what the rail refused.
        DocumentPage page = await documents.Service.ListAsync(
            Scope, "bigthing", new DocumentListRequest { PageSize = 5 }, Token);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Rows.Should().HaveCount(5, "the page itself is keyset-paged and unaffected");
        page.Estimate.IsUnknown.Should().BeTrue();
        page.Estimate.IsExactRefused.Should().BeTrue();
    }

    /// <summary>
    /// Below the threshold the rail still pays for the truth, which is what P2-fix's H2 was about.
    /// </summary>
    /// <remarks>
    /// The threshold bounds the upgrade; it does not remove it. Drawing "~0 documents" beside a collection
    /// that plainly has rows in it is the failure this branch exists to avoid, and it is still avoided for
    /// every collection small enough that finding out costs nothing.
    /// </remarks>
    [PostgresFact]
    public async Task A_never_analysed_collection_below_the_threshold_is_still_counted()
    {
        await AssertNeverAnalysedAsync("mt_doc_smallthing");

        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, Token);
        DocumentCount count = rail.Find("smallthing")!.Count;

        count.Value.Should().Be(SmallRows);
        count.IsEstimate.Should().BeFalse("there was no estimate to be had, so the rail paid for the truth");
        count.IsUnknown.Should().BeFalse();
        count.IsExactRefused.Should().BeFalse();
    }

    /// <summary>
    /// A collection with an estimate keeps it on the rail, whatever side of the threshold it falls.
    /// </summary>
    /// <remarks>
    /// D8: the rail's number is the free one. The threshold governs whether the estimate may be
    /// <em>upgraded</em>, and on a table Postgres has measured there is nothing to decide — the estimate
    /// is the answer and no statement touches the collection.
    /// </remarks>
    [PostgresFact]
    public async Task The_rail_shows_the_estimate_for_an_analysed_collection_either_side_of_the_threshold()
    {
        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, Token);

        DocumentCount above = rail.Find("countedthing")!.Count;

        above.IsEstimate.Should().BeTrue();
        above.Value.Should().Be(CountedAnalysed);
        above.IsExactRefused.Should().BeFalse("the rail never wanted an exact count in the first place");

        DocumentCount below = rail.Find("tinything")!.Count;

        below.IsEstimate.Should().BeTrue();
        below.Value.Should().Be(TinyRows);
    }

    /// <summary>The count is still exactly the one the page's header shows, and it is the estimate.</summary>
    [PostgresFact]
    public async Task The_list_header_over_a_stale_estimate_shows_the_estimate_and_says_so()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "countedthing", new DocumentListRequest { PageSize = 10 }, Token);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Estimate.IsEstimate.Should().BeTrue();
        page.Estimate.Value.Should().Be(CountedAnalysed);
        page.Estimate.IsExactRefused.Should().BeFalse(
            "nobody asked for an exact count here - an estimate is simply what a header shows");
    }

    /// <summary>
    /// A never-analysed table whose heap is already large is declined before the row probe is even asked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row threshold cannot see this case. <c>heavything</c> holds ten rows — far below a hundred — so
    /// the probe would say "small enough" and the <c>count(*)</c> behind it would read a hundred thousand
    /// pages to count them. That is not a contrived shape: a collection of large documents is exactly a
    /// table with few rows and a lot of heap.
    /// </para>
    /// <para>
    /// The proof that nothing counted is again positive rather than absent: a count would have answered
    /// ten, and what comes back is no number at all.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_never_analysed_collection_with_a_large_heap_is_declined_before_the_row_probe()
    {
        (await RealCountAsync("mt_doc_heavything")).Should().Be(
            HeavyRows, "the row count is below the threshold, so only relpages can decline this");

        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, Token);
        DocumentCount count = rail.Find("heavything")!.Count;

        count.IsUnknown.Should().BeTrue();
        count.IsExactRefused.Should().BeTrue();
        count.Value.Should().NotBe(HeavyRows, "a count(*) would have answered {0}; none ran", HeavyRows);
        count.Reason.Should().Contain("too large").And.Contain("ANALYZE");
    }

    /// <summary>
    /// A collection the rail cannot read quickly costs the rail its own short patience, not the host's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rail is read on every load of the documents page, by every circuit, and it is not behind the
    /// snapshot cache. A speculative count that ran under the host's <c>QueryTimeout</c> — thirty seconds
    /// by default — put twenty-five of those between a visitor and their first screen.
    /// </para>
    /// <para>
    /// The lock forces the case deterministically: a blocked read takes exactly as long as whatever bound
    /// is in force, so the elapsed time <em>is</em> the assertion. Two seconds is the rail's; thirty would
    /// be the host's.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_rail_count_that_blocks_costs_the_rail_its_own_short_patience()
    {
        using var documents = Documents();

        await using NpgsqlConnection blocker = await Postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await blocker.BeginTransactionAsync(Token);

        await using (var take = new NpgsqlCommand(
            $"lock table \"{Schema}\".\"mt_doc_lockedthing\" in access exclusive mode", blocker, holding))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, Token);
        started.Stop();

        rail.Error.Should().BeNull("one slow collection must not take the rail down with it");
        rail.Find("lockedthing")!.Count.IsUnknown.Should().BeTrue();

        started.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(15),
            "the rail's own patience is two seconds; the host's QueryTimeout is thirty");

        await holding.RollbackAsync(Token);

        // The anti-vacuity half: with the lock gone the same collection is counted exactly, so the
        // assertion above is about the bound rather than about a collection that never answers.
        CollectionRail afterwards = await documents.Service.GetCollectionsAsync(Scope, Token);

        afterwards.Find("lockedthing")!.Count.Value.Should().Be(LockedRows);
    }

    /// <summary>
    /// A tenant-scoped visitor gets no estimate beside a conjoined collection, and can ask for a count.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The badge used to be whole-table <c>reltuples</c> when there was one and <em>this tenant's</em>
    /// exact count when there was not — two different questions under one number, which changed answer the
    /// next time autovacuum ran. The estimate half is also a cardinality disclosure: a visitor scoped to
    /// <c>acme</c> should not be told how many rows every other tenant has.
    /// </para>
    /// <para>
    /// D8 says an estimate is a whole-table number, so a visitor who is not looking at the whole table is
    /// given no number and an exact count on request. The "=" button answers in scope, as P2-fix's H1 made
    /// it.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task A_tenant_scoped_visitor_gets_no_whole_table_estimate_for_a_conjoined_collection()
    {
        using var documents = Documents();

        CollectionRail everyone = await documents.Service.GetCollectionsAsync(Scope, Token);
        DocumentCount whole = everyone.Find("tenantedthing")!.Count;

        whole.IsEstimate.Should().BeTrue("with no tenant in scope the whole-table estimate is the answer");
        whole.Value.Should().Be(TenantedRows * 2);

        CollectionRail acme = await documents.Service.GetCollectionsAsync(MartenFixture.ScopeFor("acme"), Token);
        DocumentCount scoped = acme.Find("tenantedthing")!.Count;

        scoped.IsUnknown.Should().BeTrue("reltuples counts every tenant's rows and this visitor is in one");
        scoped.IsEstimate.Should().BeFalse();
        scoped.IsExactRefused.Should().BeTrue();
        scoped.Reason.Should().Contain("conjoined").And.Contain("exact count");

        // And the number is there for the asking, in scope.
        DocumentCount asked = await documents.Service.CountExactAsync(
            MartenFixture.ScopeFor("acme"), "tenantedthing", Token);

        asked.Value.Should().Be(TenantedRows);
        asked.IsEstimate.Should().BeFalse();

        // A single-tenanted collection is unaffected: the scope has nothing to narrow it by.
        acme.Find("tinything")!.Count.IsEstimate.Should().BeTrue();
    }

    /// <summary>
    /// The "=" button under a tenant scope answers this tenant's count even when the whole table is above
    /// the threshold - and never the whole-table estimate.
    /// </summary>
    /// <remarks>
    /// The adversarial review of P2-perf measured the leak this closes: the exact-count path read the
    /// whole-table <c>reltuples</c> first, found it above the threshold, and answered with <em>that</em>
    /// number marked "estimate only" - every tenant's cardinality, handed to a visitor scoped to one, on
    /// the very button the rail's note told them to press. The threshold does not describe this query's
    /// cost either: Marten puts <c>tenant_id</c> first in a conjoined table's primary key, so a
    /// tenant-predicated <c>count(*)</c> is index-backed and proportional to this tenant's rows.
    /// </remarks>
    [PostgresFact]
    public async Task A_tenant_scoped_exact_count_of_a_conjoined_collection_above_the_threshold_is_this_tenants_count()
    {
        using var documents = Documents();

        CollectionRail everyone = await documents.Service.GetCollectionsAsync(Scope, Token);
        DocumentCount whole = everyone.Find("widetenantedthing")!.Count;

        whole.IsEstimate.Should().BeTrue("with no tenant in scope the whole-table estimate is the answer");
        whole.Value.Should().Be(WideTenantedRows * 2);
        (WideTenantedRows * 2).Should().BeGreaterThan((int) Threshold,
            "this collection has to sit above the threshold for the test to be about the branch it claims");

        DocumentCount asked = await documents.Service.CountExactAsync(
            MartenFixture.ScopeFor("acme"), "widetenantedthing", Token);

        asked.IsEstimate.Should().BeFalse("a tenant-scoped count is index-backed and is never refused for the table's size");
        asked.IsExactRefused.Should().BeFalse();
        asked.Value.Should().Be(WideTenantedRows, "and it is this tenant's number, never the whole table's");
        asked.Value.Should().NotBe(WideTenantedRows * 2);
    }

    private async Task AssertNeverAnalysedAsync(string table)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(Token);

        DocumentCount raw = await new CountEstimator().EstimateAsync(connection, Schema, table, Token);

        raw.IsUnknown.Should().BeTrue(
            "'{0}' has to be a never-analysed table for this test to be about the branch it claims", table);
    }

    private async Task<long> RealCountAsync(string table)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(Token);
        await using var command = new NpgsqlCommand($"select count(*) from \"{Schema}\".\"{table}\"", connection);

        return (long)(await command.ExecuteScalarAsync(Token))!;
    }
}

/// <summary>A document that exists only to be counted. Guid-keyed, no metadata worth having.</summary>
public abstract class ThresholdDocument
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }
}

/// <summary>Analysed at three hundred rows, then quietly given two hundred more.</summary>
public sealed class CountedThing : ThresholdDocument;

/// <summary>Never analysed, and above the threshold.</summary>
public sealed class BigThing : ThresholdDocument;

/// <summary>Never analysed, and below the threshold.</summary>
public sealed class SmallThing : ThresholdDocument;

/// <summary>Analysed, and below the threshold.</summary>
public sealed class TinyThing : ThresholdDocument;

/// <summary>Few rows, and a heap <c>pg_class</c> says is enormous.</summary>
public sealed class HeavyThing : ThresholdDocument;

/// <summary>Never analysed, and locked while the rail tries to count it.</summary>
public sealed class LockedThing : ThresholdDocument;

/// <summary>Conjoined-tenanted, so a scoped visitor must not be shown a whole-table number.</summary>
public sealed class TenantedThing : ThresholdDocument;

/// <summary>
/// Conjoined-tenanted and, as a whole table, above the threshold: the collection on which pressing "="
/// under a tenant scope must answer this tenant's exact count and never the whole-table estimate.
/// </summary>
public sealed class WideTenantedThing : ThresholdDocument;
