using Npgsql;

using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Integration.Tests.Statements;

/// <summary>
/// W5-Q1-fix F1, live: pg_stat_statements created at 1.8 on PostgreSQL 14, with <c>pg_stat_statements.track = all</c>. The 1.8
/// function has no <c>toplevel</c> column, yet the server keeps a statement's top-level and nested runs as two entries of one
/// <c>queryid</c> and returns both - two rows the Statements page keys by <c>queryid</c> (a duplicate <c>@key</c> ends the
/// circuit) and T02 names a history series by (a series sampled twice fails the statement sweep). The read folds them into one
/// row with the calls of both, and T02 checks.
/// </summary>
/// <remarks>
/// On the version matrix's PostgreSQL 14, which preloads the library: <c>track</c> is set for this test's database alone
/// (<c>ALTER DATABASE … SET</c>, as the superuser), so no other test on the server tracks nested statements.
/// </remarks>
public class StatementsNestedLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task On_extension_1_8_a_statement_run_top_level_and_nested_is_one_row_and_T02_checks()
    {
        MatrixServer leg = await matrix.ServerAsync(14);
        leg.SkipIfUnavailable();

        string database = await leg.CreateDatabaseAsync("q1_nested", Token);
        await leg.ExecuteAsync("create extension if not exists pg_stat_statements version '1.8'", database, Token);
        await leg.ExecuteAsync("alter database " + SqlIdentifier.Quote(database) + " set pg_stat_statements.track = 'all'", cancellationToken: Token);

        string app = leg.ConnectionString(MatrixRole.App, database);
        await StatementsLive.SeedAsync(app);
        await StatementsLive.ExecuteAsync(
            app,
            "create function public.q1_count_orders() returns bigint language plpgsql as $$ "
            + "declare n bigint; begin select count(*) into n from public.orders; return n; end $$");

        // The same statement five times at the top level and three times inside the function.
        await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(app) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(Token);

            for (int i = 0; i < 5; i++)
            {
                await using NpgsqlCommand top = new("select count(*) from public.orders", connection);
                await top.ExecuteScalarAsync(Token);
            }

            for (int i = 0; i < 3; i++)
            {
                await using NpgsqlCommand nested = new("select public.q1_count_orders()", connection);
                await nested.ExecuteScalarAsync(Token);
            }
        }

        // Anti-vacuity: the extension itself returns a queryid twice for the app role here - or this proves nothing.
        Dictionary<long, long> twice = await TwiceAsync(leg, database);
        twice.Should().NotBeEmpty("PostgreSQL 14 under track = all keeps a top-level and a nested entry of one queryid, and 1.8 returns both");

        // T02 samples every kept statement into a series named by its queryid: a second row would fail the sweep outright.
        await using (MatrixHost matrixHost = await MatrixHost.CreateAsync(leg, MatrixRole.App, database))
        {
            HealthSweep sweep = await matrixHost.SweepAsync(HealthSweepKind.Statements);
            HealthCheckResult t02 = sweep.Results.Single(static x => x.Check.Code == "T02");
            t02.Outcome.Should().Be(HealthCheckOutcome.Checked, t02.Problem?.Reason);
        }

        await using StudioHost host = StatementsLive.Host(app);
        StatementsView view = await StatementsLive.ReadAsync(host);

        view.State.Should().Be(StatementsState.Read, StatementsLive.Describe(view));
        view.Rows.Select(static x => x.QueryId).Should().OnlyHaveUniqueItems("the page keys each row by its queryid");

        foreach ((long queryId, long calls) in twice)
        {
            StatementRowView row = view.Rows.Single(x => x.QueryId == queryId);
            row.SinceBegan.Calls.Should().Be(calls, "the top-level and nested runs are summed: " + StatementsLive.Describe(view));
            (row.Text ?? row.TextNote).Should().NotBeNull("one text for the one row");
        }

        view.Caveat.Should().Contain("pg_stat_statements 1.8 has no toplevel column");
        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    /// <summary>The app role's queryids the extension returns more than once in <paramref name="database" />, with their calls summed.</summary>
    private static async Task<Dictionary<long, long>> TwiceAsync(MatrixServer leg, string database)
    {
        await using NpgsqlConnection connection = await leg.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlCommand command = new(
            "select s.queryid, pg_catalog.sum(s.calls)::int8 from public.pg_stat_statements s "
            + "where s.dbid = (select d.oid from pg_catalog.pg_database d where d.datname = pg_catalog.current_database()) "
            + "and s.userid = 'app'::pg_catalog.regrole group by s.queryid having pg_catalog.count(*) > 1",
            connection);

        Dictionary<long, long> found = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            found[reader.GetInt64(0)] = reader.GetInt64(1);
        }

        return found;
    }
}
