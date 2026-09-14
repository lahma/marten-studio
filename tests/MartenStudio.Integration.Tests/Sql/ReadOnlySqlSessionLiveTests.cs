using MartenStudio.Internal.Sql;

using Npgsql;

namespace MartenStudio.Integration.Tests.Sql;

/// <summary>
/// D13, against a real database: <b>the transaction is the guard, not the parser.</b>
/// </summary>
/// <remarks>
/// Every mutating statement here is sent <em>past</em> <see cref="ReadOnlySqlGuard"/>, straight to the
/// session, precisely because the parser is not what is being tested. What is being tested is that
/// Postgres refuses each one with SQLSTATE 25006 and that the table is byte-for-byte what it was — which
/// is the claim the README makes to anyone deciding whether to grant the <c>RunSql</c> capability.
/// </remarks>
public class ReadOnlySqlSessionLiveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    protected override async Task SeedAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection, $"""
            create table "{Schema}".guarded (id int primary key, name text not null);
            insert into "{Schema}".guarded values (1, 'original'), (2, 'second');
            create table "{Schema}".numbers as select generate_series(1, 100) as n;
            """);
    }

    [PostgresTheory]
    [InlineData("delete from \"{0}\".guarded")]
    [InlineData("delete from \"{0}\".guarded where id = 1")]
    [InlineData("update \"{0}\".guarded set name = 'changed'")]
    [InlineData("insert into \"{0}\".guarded values (3, 'new')")]
    [InlineData("truncate \"{0}\".guarded")]
    [InlineData("drop table \"{0}\".guarded")]
    [InlineData("alter table \"{0}\".guarded add column extra int")]
    [InlineData("create table \"{0}\".sneaky (i int)")]
    [InlineData("with x as (delete from \"{0}\".guarded returning *) select * from x")]
    [InlineData("with x as (update \"{0}\".guarded set name = 'x' returning *) select * from x")]
    [InlineData("with x as (insert into \"{0}\".guarded values (4, 'x') returning *) select * from x")]
    [InlineData("select * from \"{0}\".guarded for update")]
    public async Task A_mutating_statement_is_refused_by_the_transaction_and_changes_nothing(string template)
    {
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Schema);
        var session = new ReadOnlySqlSession();

        await using var connection = await OpenAsync();

        var result = await session.ExecuteAsync(connection, sql);

        result.Succeeded.Should().BeFalse($"'{sql}' writes, and the transaction is read only");
        result.Error!.SqlState.Should().Be("25006", "that is Postgres' read_only_sql_transaction");
        result.Error.MessageText.Should().NotBeEmpty();

        await AssertTableUnchangedAsync();
    }

    /// <summary>
    /// The three statements the parser lets through are the reason this file exists. They are named
    /// separately so that a regression here reads as what it is.
    /// </summary>
    [PostgresFact]
    public async Task The_statements_the_parser_allows_are_the_ones_the_transaction_has_to_catch()
    {
        var writingCte = $"with x as (delete from \"{Schema}\".guarded returning *) select * from x";

        ReadOnlySqlGuard.Check(writingCte).Allowed.Should().BeTrue("the parser sees only the leading WITH");

        await using var connection = await OpenAsync();

        var result = await new ReadOnlySqlSession().ExecuteAsync(connection, writingCte);

        result.Error!.SqlState.Should().Be("25006");

        await AssertTableUnchangedAsync();
    }

    [PostgresFact]
    public async Task A_read_runs_and_comes_back_with_its_columns_and_their_Postgres_types()
    {
        await using var connection = await OpenAsync();

        var result = await new ReadOnlySqlSession().ExecuteAsync(
            connection, $"select id, name from \"{Schema}\".guarded order by id");

        result.Succeeded.Should().BeTrue();
        result.Columns.Select(x => x.Name).Should().Equal("id", "name");
        result.Columns.Select(x => x.PostgresType).Should().Equal("integer", "text");
        result.Rows.Should().HaveCount(2);
        result.Rows[0].Select(x => x.Text).Should().Equal("1", "original");
        result.Truncated.Should().BeFalse();
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [PostgresFact]
    public async Task The_transaction_is_rolled_back_so_the_connection_is_usable_again()
    {
        var session = new ReadOnlySqlSession();

        await using var connection = await OpenAsync();

        var failed = await session.ExecuteAsync(connection, $"delete from \"{Schema}\".guarded");

        failed.Succeeded.Should().BeFalse();

        // If the rollback had not happened this would come back as 25P02, "current transaction is aborted".
        var after = await session.ExecuteAsync(connection, "select 1");

        after.Succeeded.Should().BeTrue();
        after.Rows.Single().Single().Text.Should().Be("1");
    }

    [PostgresFact]
    public async Task The_statement_timeout_fires_and_comes_back_as_a_value()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = TimeSpan.FromMilliseconds(300),
        });

        await using var connection = await OpenAsync();

        var result = await session.ExecuteAsync(connection, "select pg_sleep(5)");

        result.Succeeded.Should().BeFalse();
        result.Error!.SqlState.Should().Be("57014", "that is Postgres' query_canceled");
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The shortest timeout anybody can ask for must not be no timeout. Postgres reads
    /// <c>statement_timeout = 0ms</c> as <em>disabled</em>, so clamping a sub-millisecond
    /// <see cref="TimeSpan"/> to zero turned "as tight as possible" into "unbounded" — and the query that
    /// proves it is the one that would otherwise run for five seconds.
    /// </summary>
    [PostgresFact]
    public async Task A_sub_millisecond_timeout_is_clamped_up_to_one_millisecond_and_not_down_to_disabled()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = TimeSpan.FromTicks(1),
        });

        await using var connection = await OpenAsync();

        var result = await session.ExecuteAsync(connection, "select pg_sleep(5)");

        result.Succeeded.Should().BeFalse();
        result.Error!.SqlState.Should().Be("57014", "1ms is a timeout; 0ms would have been no timeout");
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [PostgresFact]
    public async Task The_settings_the_session_applies_are_the_ones_Postgres_reports()
    {
        // The settings go to the server one round trip each, so the idle-in-transaction timeout must be
        // long enough to cover the gaps between them. This test once asked for a negative value to prove
        // the clamp to 1ms - and 1ms of idle time between two round trips is exactly what a loaded
        // machine cannot promise, so Postgres terminated the session (25P03) about one run in three. The
        // clamp is proven in ReadOnlySqlSessionTests without a server; here the values are ones a
        // session can actually live with.
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions
        {
            StatementTimeout = TimeSpan.FromSeconds(7),
            LockTimeout = TimeSpan.Zero,
            IdleInTransactionTimeout = TimeSpan.FromSeconds(3),
        });

        await using var connection = await OpenAsync();

        var result = await session.ExecuteAsync(
            connection,
            "select current_setting('statement_timeout') as s, current_setting('lock_timeout') as l, " +
            "current_setting('idle_in_transaction_session_timeout') as i");

        result.Error.Should().BeNull("the statement is a plain read of three settings");
        result.Succeeded.Should().BeTrue();

        var row = result.Rows.Single();

        row[0].Text.Should().Be("7s");
        row[1].Text.Should().Be("1ms", "zero would have been Postgres for 'no lock timeout at all'");
        row[2].Text.Should().Be("3s");
    }

    /// <summary>
    /// <c>COPY</c> reaches the server, is accepted, and is then refused by <em>Npgsql</em> — as a
    /// <see cref="NotSupportedException"/>, which is not a <c>PostgresException</c> and not a
    /// <c>DbException</c> either. Catching only <c>PostgresException</c> meant that exception escaped the
    /// session and took the Blazor circuit with it, for a statement the console can perfectly well render
    /// as a failed run. Sent straight to the session, past the guard, which would have refused it first.
    /// </summary>
    [PostgresFact]
    public async Task A_statement_the_driver_refuses_comes_back_as_an_error_rather_than_as_an_exception()
    {
        ReadOnlySqlGuard.Check($"copy \"{Schema}\".guarded to stdout").Allowed.Should().BeFalse(
            "the guard refuses copy first; this test is about what happens when something gets past it");

        var session = new ReadOnlySqlSession();

        await using var connection = await OpenAsync();

        var result = await session.ExecuteAsync(connection, $"copy \"{Schema}\".guarded to stdout");

        result.Succeeded.Should().BeFalse();
        result.Error!.MessageText.Should().NotBeEmpty();
        result.Rows.Should().BeEmpty();
        result.Columns.Should().BeEmpty();

        // Npgsql breaks the connector on this one - a COPY response it did not ask for leaves the protocol
        // stream somewhere it cannot resynchronise from - so this connection is genuinely finished, and the
        // rollback in the session's finally swallowed that rather than throwing on top of the first
        // failure. The next connection from the pool is fine, which is what the page's retry will get.
        await using var next = await OpenAsync();

        (await session.ExecuteAsync(next, "select 1")).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void The_guard_refuses_the_functions_a_read_only_transaction_would_have_run()
    {
        // Verified against this very container: pg_terminate_backend(pg_backend_pid()) runs inside
        // `begin; set transaction read only` and kills the backend. The transaction is the guarantee for
        // writes; this list is the guarantee for everything else, and it is policy, not a boundary.
        foreach (var sql in new[]
        {
            "select pg_terminate_backend(pg_backend_pid())",
            "select pg_cancel_backend(pg_backend_pid())",
            "select pg_read_file('postgresql.conf')",
            "select pg_ls_dir('.')",
            "select set_config('statement_timeout', '0', false)",
        })
        {
            var checkResult = ReadOnlySqlGuard.Check(sql);

            checkResult.Allowed.Should().BeFalse($"of: {sql}");
            checkResult.Reason.Should().Be(SqlRejectionReason.DisallowedFunction);
        }
    }

    [PostgresFact]
    public async Task The_row_cap_stops_the_reader_and_says_so_without_touching_the_statement()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { MaxRows = 10 });

        await using var connection = await OpenAsync();

        var result = await session.ExecuteAsync(connection, $"select n from \"{Schema}\".numbers order by n");

        result.Rows.Should().HaveCount(10);
        result.Truncated.Should().BeTrue();

        // The statement was not rewritten: these are the first ten rows of the query as typed, in its own
        // order, not the result of an appended LIMIT.
        result.Rows.Select(x => x.Single().Text).Should().Equal("1", "2", "3", "4", "5", "6", "7", "8", "9", "10");
    }

    [PostgresFact]
    public async Task A_result_that_fits_under_the_cap_is_not_reported_as_truncated()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { MaxRows = 100 });

        await using var connection = await OpenAsync();

        var result = await session.ExecuteAsync(connection, $"select n from \"{Schema}\".numbers order by n");

        result.Rows.Should().HaveCount(100);
        result.Truncated.Should().BeFalse();
    }

    [PostgresFact]
    public async Task The_session_can_run_as_another_role_and_gives_it_back_afterwards()
    {
        var role = "studio_reader_" + Guid.NewGuid().ToString("N")[..8];

        await using var connection = await OpenAsync();

        await ExecuteAsync(connection, $"create role \"{role}\" nologin");

        try
        {
            var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { Role = role });

            var result = await session.ExecuteAsync(connection, "select current_user");

            result.Succeeded.Should().BeTrue();
            result.Rows.Single().Single().Text.Should().Be(role);

            // SET LOCAL ROLE dies with the transaction, so the connection is itself again.
            var after = await ScalarAsync(connection, "select current_user");

            after.Should().NotBe(role);
        }
        finally
        {
            await ExecuteAsync(connection, $"drop role \"{role}\"");
        }
    }

    [PostgresFact]
    public async Task A_role_name_that_is_not_an_identifier_never_reaches_the_database()
    {
        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { Role = "x; drop table y" });

        await using var connection = await OpenAsync();

        var act = async () => await session.ExecuteAsync(connection, "select 1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a valid Postgres role name*");
    }

    [PostgresFact]
    public async Task A_syntax_error_comes_back_with_its_position()
    {
        await using var connection = await OpenAsync();

        var result = await new ReadOnlySqlSession().ExecuteAsync(connection, "select from where");

        result.Succeeded.Should().BeFalse();
        result.Error!.SqlState.Should().Be("42601");
        result.Error.Position.Should().BeGreaterThan(0);
    }

    [PostgresFact]
    public async Task A_json_cell_and_a_binary_cell_come_back_formatted()
    {
        await using var connection = await OpenAsync();

        var result = await new ReadOnlySqlSession().ExecuteAsync(
            connection, """select '{"a":1}'::jsonb as j, '\xdeadbeef'::bytea as b, null::text as n""");

        result.Succeeded.Should().BeTrue();

        var row = result.Rows.Single();

        row[0].Kind.ToString().Should().Be("Json");
        row[1].Text.Should().Be("\\xdeadbeef (4 bytes)");
        row[2].Kind.ToString().Should().Be("Null");
    }

    private async Task AssertTableUnchangedAsync()
    {
        await using var connection = await OpenAsync();

        var rows = await ScalarAsync(connection, $"select count(*) from \"{Schema}\".guarded");
        var names = await ScalarAsync(connection, $"select string_agg(name, ',' order by id) from \"{Schema}\".guarded");
        var sneaky = await ScalarAsync(
            connection,
            $"select count(*) from information_schema.tables where table_schema = '{Schema}' and table_name = 'sneaky'");

        rows.Should().Be(2L, "the table must still hold exactly what it held");
        names.Should().Be("original,second");
        sneaky.Should().Be(0L, "nothing may have been created either");
    }
}
