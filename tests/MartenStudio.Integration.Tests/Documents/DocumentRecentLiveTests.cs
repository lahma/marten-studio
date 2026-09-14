using MartenStudio.Services.Documents;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// The <c>_recent</c> pseudo-collection, and what it says when it cannot finish.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-perf deliverable 3.</b> Every branch of the union is
/// <c>order by mt_last_modified desc limit n</c> and Marten declares no index on that column, so each one
/// is a sequential scan and a top-N sort over its collection: 24 ms at 100 000 documents, 221 ms at 1.2 M,
/// against a 500 ms budget — inside it today and growing with the store. Reading the top <c>n</c> by
/// <c>id desc</c> instead was considered and rejected: id order is <em>write</em> order even on a comb
/// Guid, and a document edited today keeps the id it was created with, so it would answer a different
/// question while looking like the same one.
/// </para>
/// <para>
/// So the read is bounded rather than changed, and the bound produces a value. It runs inside a
/// transaction whose <c>statement_timeout</c> is the host's <c>QueryTimeout</c>, and <c>57014</c> becomes
/// "too large to scan" — which the region draws in place of rows. The old behaviour was an empty list,
/// indistinguishable from "nothing has changed lately": the studio being quietly wrong about the one thing
/// the region exists to say.
/// </para>
/// <para>
/// <b>Why a lock rather than a large data set.</b> A timeout has to be forced deterministically, and
/// "generate enough rows that a sort takes a second" is a test that is slow on a fast machine and flaky on
/// a slow one. An <c>ACCESS EXCLUSIVE</c> lock held on one collection makes the union block on the lock,
/// and <c>statement_timeout</c> covers lock waits, so the statement ends in exactly the <c>57014</c> a
/// genuinely oversized scan produces.
/// </para>
/// </remarks>
public class DocumentRecentLiveTests(DocumentRecentLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentRecentLiveTests.Fixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The demo data, under the shortest query timeout the options allow.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override void ConfigureStudio(MartenStudioOptions options) =>
            // One second is the floor the options validator allows, and it is what makes the blocked read
            // below take a second rather than thirty.
            options.QueryTimeout = TimeSpan.FromSeconds(1);
    }

    /// <summary>Ordinarily it returns rows and has nothing to say.</summary>
    [PostgresFact]
    public async Task The_recent_region_returns_rows_and_no_notice()
    {
        using var documents = Documents();

        RecentDocuments recent = await documents.Service.ListRecentAsync(Scope, 15, Token);

        recent.Rows.Should().NotBeEmpty();
        recent.Notice.Should().BeNull();
        recent.TooLargeToScan.Should().BeFalse();

        // Newest first, which is the only ordering claim the region makes.
        recent.Rows.Select(x => x.LastModified).Should().BeInDescendingOrder();
    }

    /// <summary>
    /// A union that cannot finish inside the query timeout renders "too large to scan" — a value.
    /// </summary>
    [PostgresFact]
    public async Task A_union_that_runs_past_the_query_timeout_renders_a_value_rather_than_an_empty_region()
    {
        using var documents = Documents();

        await using NpgsqlConnection blocker = await Postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await blocker.BeginTransactionAsync(Token);

        await using (var take = new NpgsqlCommand(
            $"lock table \"{Schema}\".\"mt_doc_customer\" in access exclusive mode", blocker, holding))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        var started = System.Diagnostics.Stopwatch.StartNew();
        RecentDocuments blocked = await documents.Service.ListRecentAsync(Scope, 15, Token);
        started.Stop();

        blocked.TooLargeToScan.Should().BeTrue();
        blocked.Rows.Should().BeEmpty();
        blocked.Notice.Should().Be(RecentDocuments.TooLargeNotice);
        blocked.Notice.Should().Contain("IndexLastModified", "the region says what would fix it");

        // Which 57014 this is: the server's statement_timeout, not Npgsql cancelling the command from the
        // client. Both surface as the same SQLSTATE, so the elapsed time is the only thing that tells them
        // apart - and only the server-side one is what the region's bound actually relies on. The fixture
        // sets QueryTimeout to one second; the host's CommandTimeout default is thirty.
        started.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(3),
            "the statement_timeout this region sets is one second; a client-side cancel would be thirty");

        await holding.RollbackAsync(Token);

        // The anti-vacuity half: the same call succeeds the moment the lock is gone, so the assertion
        // above is about the timeout and not about the region being broken.
        RecentDocuments afterwards = await documents.Service.ListRecentAsync(Scope, 15, Token);

        afterwards.TooLargeToScan.Should().BeFalse();
        afterwards.Rows.Should().NotBeEmpty();
    }
}
