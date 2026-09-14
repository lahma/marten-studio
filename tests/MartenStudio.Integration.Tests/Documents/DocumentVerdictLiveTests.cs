using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// A red index verdict withholds the read — and "withholds" means no statement runs, not that the rows
/// are thrown away.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-fix follow-up 11.</b> <c>DocumentDataServiceLiveTests</c> already asserts that a red search comes
/// back as <c>BlockedByVerdict</c> with no rows, which is what the page renders — but an implementation
/// that ran the sequential scan and then discarded the result would pass it, and that is precisely the
/// denial of service the verdict exists to prevent. Counting statements from outside Postgres is awkward
/// (<c>pg_stat_statements</c> is not installed, and <c>pg_stat_user_tables</c> lags and is confounded by
/// the count the header takes), so this test makes the absence of the statement <em>observable</em>
/// instead: it removes the collection's table.
/// </para>
/// <para>
/// Nothing on the withheld path touches it. The mapping comes from <c>StoreOptions</c>,
/// <c>information_schema.columns</c> reports no columns, <c>pg_indexes</c> reports no indexes, and
/// <c>to_regclass</c> answers null for the estimate — all of which are values rather than errors. So a
/// withheld read still returns its verdict, while the same request with <c>RunAnyway</c> fails with
/// <c>42P01</c>. The second half is the anti-vacuity proof: it shows the table really is missing, so the
/// first half cannot be passing because the statement happened to succeed.
/// </para>
/// </remarks>
public class DocumentVerdictLiveTests(DocumentVerdictLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentVerdictLiveTests.Fixture>
{
    private const string RedSearch = "Name ~ Customer 0";

    /// <summary>The demo store with one collection's table taken away underneath it.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>No demo data: the point of this class is a table that is not there.</summary>
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using NpgsqlConnection connection = await Postgres.OpenAsync();

            // Cascade, because mt_doc_order declares a foreign key to it.
            await using var command = new NpgsqlCommand(
                $"drop table if exists \"{Schema}\".\"mt_doc_customer\" cascade", connection);

            await command.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task A_red_verdict_is_reported_without_a_statement_ever_reaching_the_collection()
    {
        using var documents = Documents();

        DocumentPage withheld = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest { Search = RedSearch },
            TestContext.Current.CancellationToken);

        withheld.State.Should().Be(DocumentListState.BlockedByVerdict);
        withheld.Verdict.FilterLevel.Should().Be(IndexVerdictLevel.Red);
        withheld.Error.Should().BeNull(
            "the table is not there, so any statement against the collection would have said so");
        withheld.Sql.Should().NotBeEmpty("the page still shows what it would have run");
        withheld.Rows.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task The_same_request_run_anyway_proves_the_table_really_is_missing()
    {
        using var documents = Documents();

        DocumentPage run = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest { Search = RedSearch, RunAnyway = true },
            TestContext.Current.CancellationToken);

        run.State.Should().Be(DocumentListState.Failed);
        run.SqlState.Should().Be("42P01", "undefined_table - which is what the withheld read did not hit");
    }
}
