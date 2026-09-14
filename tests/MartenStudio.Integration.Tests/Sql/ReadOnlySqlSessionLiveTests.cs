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
