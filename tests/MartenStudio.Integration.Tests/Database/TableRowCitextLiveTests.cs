using MartenStudio.Integration.Tests.Browser;
using MartenStudio.SampleDomain.Relational;
using MartenStudio.Services.Database;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// Acceptance 3, the <c>citext</c> case, in a database of its own: the demo adds its <c>citext</c> sender
/// column only where somebody installed the extension first, which this test does - the sample never
/// installs one.
/// </summary>
/// <param name="postgres">The assembly's container.</param>
public class TableRowCitextLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The value goes to Postgres as untyped text and resolves against the column, so <c>citext</c>'s own
    /// equality - which ignores case - is the one used: an upper-cased sender finds every spelling of it.
    /// </summary>
    [PostgresFact]
    public async Task A_citext_column_is_compared_with_citexts_own_equality()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, "table_rows_citext");

        await using (var bare = new NpgsqlConnection(connectionString))
        {
            await bare.OpenAsync(Token);

            await using (var install = new NpgsqlCommand("create extension citext", bare))
            {
                await install.ExecuteNonQueryAsync(Token);
            }

            RelationalDemoResult applied = await RelationalDemoSchema.ApplyAsync(bare, Token);
            applied.HasCitextColumn.Should().BeTrue();
        }

        string lowered;
        long spellings;
        long matches;

        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(Token);

            await using var probe = new NpgsqlCommand(
                """
                select pg_catalog.lower(sender::text), count(distinct sender::text), count(*)
                from legacy.integration_messages
                where sender is not null
                group by 1
                order by 2 desc, 3 desc, 1
                limit 1
                """,
                connection);

            await using NpgsqlDataReader reader = await probe.ExecuteReaderAsync(Token);
            (await reader.ReadAsync(Token)).Should().BeTrue();

            lowered = reader.GetString(0);
            spellings = reader.GetInt64(1);
            matches = reader.GetInt64(2);
        }

        spellings.Should().BeGreaterThan(1, "the demo writes Orders@ and orders@ for the same partner");

        await using RowsHost host = RowsHost.Create(connectionString);

        TableRowPage page = await host.RowsAsync(x => x.ListRowsAsync(
            RowsHost.Scope,
            "legacy",
            "integration_messages",
            new TableRowRequest
            {
                Filter = RowFilterGrammar.Format("sender", RowFilterOperator.Equal, lowered.ToUpperInvariant()),
                PageSize = 500,
            },
            Token));

        page.State.Should().Be(TableRowPageState.Loaded, page.Error?.Sentence ?? page.Reason);
        page.Columns.Single(static x => x.Name == "sender").Type.Should().EndWith("citext");
        page.Rows.Should().HaveCount((int) matches, "every spelling of the sender, which only citext's equality finds");
    }
}
