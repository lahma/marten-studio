using System.Globalization;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>
/// Mode B against a real database. <b>The transaction is the guard, not the parser</b> (D13).
/// </summary>
/// <remarks>
/// Every refusal here is checked twice: the studio said no <em>and</em> the database is byte-for-byte what
/// it was. That pair is the claim the README makes to anyone deciding whether to grant <c>RunSql</c>, and
/// a test that only asserted the error message would not support it.
/// </remarks>
public class SqlConsoleLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private QueryHarness harness = null!;
    private string personTable = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        harness = await QueryHarness.CreateAsync(fixture, "sqlconsolelive");

        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(harness.Scope, Token);
        personTable = types.Single(x => x.TypeName == nameof(QueryPerson)).QualifiedTableName;
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<long> PersonCountAsync()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync(Token);
        await using var command = new NpgsqlCommand($"select count(*) from {personTable}", connection);

        return (long) (await command.ExecuteScalarAsync(Token))!;
    }

    // ------------------------------------------------------------------------------------------------
    // It runs reads
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_count_runs_and_comes_back_with_its_column_type_and_its_duration()
    {
        SqlConsoleResult result = await harness.Queries.RunSqlAsync(
            harness.Scope, new SqlConsoleRequest($"select count(*) from {personTable}"), Token);

        result.Succeeded.Should().BeTrue();
        result.Columns.Should().ContainSingle();
        result.Columns[0].PostgresType.Should().Be("bigint");
        result.Rows.Should().ContainSingle();
        result.Rows[0][0].Text.Should().Be("3");
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.Truncated.Should().BeFalse();
    }

    [PostgresFact]
    public async Task A_jsonb_column_comes_back_as_a_json_cell_and_a_null_as_a_null_cell()
    {
        SqlConsoleResult result = await harness.Queries.RunSqlAsync(
            harness.Scope,
            new SqlConsoleRequest($"select data, null::text as nothing from {personTable} order by id limit 1"),
            Token);

        result.Succeeded.Should().BeTrue();
        result.Columns[0].PostgresType.Should().Be("jsonb");
        result.Rows[0][0].Kind.Should().Be(SqlCellKind.Json);
        result.Rows[0][1].Kind.Should().Be(SqlCellKind.Null);
        result.Rows[0][1].Text.Should().Be("NULL", "NULL is not the empty string");
    }

    /// <summary>
    /// The reader is stopped at the cap; no <c>limit</c> is ever appended, because rewriting somebody's SQL
    /// would make the answer a different question from the one they asked.
    /// </summary>
    [PostgresFact]
    public async Task The_row_cap_stops_the_reader_and_says_so()
    {
        await using QueryHarness capped = await QueryHarness.CreateAsync(
            fixture, "sqlconsolelivecap", options => options.MaxSqlConsoleRows = 10);

        SqlConsoleResult result = await capped.Queries.RunSqlAsync(
            capped.Scope, new SqlConsoleRequest("select generate_series(1, 100)"), Token);

        result.Succeeded.Should().BeTrue();
        result.Rows.Should().HaveCount(10);
        result.Truncated.Should().BeTrue();
        result.RowLimit.Should().Be(10);
    }

    // ------------------------------------------------------------------------------------------------
    // It refuses writes - the parser for a nicer message, the transaction for real
    // ------------------------------------------------------------------------------------------------

    [PostgresTheory]
    [InlineData("delete from {0}")]
    [InlineData("update {0} set data = '{{}}'")]
    [InlineData("insert into {0} (id, data) values (gen_random_uuid(), '{{}}')")]
    [InlineData("truncate {0}")]
    [InlineData("drop table {0}")]
    [InlineData("alter table {0} add column extra int")]
    [InlineData("create table {0}_sneaky (i int)")]
    [InlineData("select 1; delete from {0}")]
    public async Task A_statement_the_guard_refuses_never_runs_and_says_why(string template)
    {
        string statement = string.Format(CultureInfo.InvariantCulture, template, personTable);
        long before = await PersonCountAsync();

        SqlConsoleResult result = await harness.Queries.RunSqlAsync(
            harness.Scope, new SqlConsoleRequest(statement), Token);

        result.Succeeded.Should().BeFalse();
        result.Rejection.Should().NotBeNull(statement);
        result.Rejection!.Message.Should().NotBeEmpty();
        result.Error.Should().BeNull("the statement was never sent");

        (await PersonCountAsync()).Should().Be(before);
    }

    /// <summary>
    /// The statement the parser lets through is the reason the transaction exists: the guard sees only the
    /// leading <c>with</c>, and Postgres refuses it with 25006 having changed nothing.
    /// </summary>
    [PostgresTheory]
    [InlineData("with x as (delete from {0} returning *) select * from x")]
    [InlineData("with x as (update {0} set data = '{{}}' returning *) select * from x")]
    [InlineData("with x as (insert into {0} (id, data) values (gen_random_uuid(), '{{}}') returning *) select * from x")]
    public async Task A_writing_cte_is_refused_by_the_transaction_with_25006_and_changes_nothing(string template)
    {
        string statement = string.Format(CultureInfo.InvariantCulture, template, personTable);
        long before = await PersonCountAsync();

        ReadOnlySqlGuard.Check(statement).Allowed.Should().BeTrue("the parser sees only the leading WITH");

        SqlConsoleResult result = await harness.Queries.RunSqlAsync(
            harness.Scope, new SqlConsoleRequest(statement), Token);

        result.Succeeded.Should().BeFalse();
        result.Rejection.Should().BeNull("the statement was sent; the database is what refused it");
        result.Error!.SqlState.Should().Be(QuerySqlErrors.ReadOnlyTransaction);

        (await PersonCountAsync()).Should().Be(before);
    }

    [PostgresFact]
    public async Task The_statement_timeout_comes_back_as_57014()
    {
        await using QueryHarness impatient = await QueryHarness.CreateAsync(
            fixture, "sqlconsolelivetimeout", options => options.QueryTimeout = TimeSpan.FromSeconds(1));

        SqlConsoleResult result = await impatient.Queries.RunSqlAsync(
            impatient.Scope, new SqlConsoleRequest("select pg_sleep(10)"), Token);

        result.Succeeded.Should().BeFalse();
        result.Error!.SqlState.Should().Be(QuerySqlErrors.QueryCanceled);

        QuerySqlErrors.Describe(result.Error, TimeSpan.FromSeconds(1))
            .Should().Contain("timed out after 1 s");
    }

    /// <summary>
    /// The connection is usable afterwards, which is what proves the transaction was rolled back rather
    /// than left open on a pooled connection.
    /// </summary>
    [PostgresFact]
    public async Task A_refused_statement_leaves_the_next_one_working()
    {
        await harness.Queries.RunSqlAsync(
            harness.Scope, new SqlConsoleRequest($"with x as (delete from {personTable} returning *) select * from x"), Token);

        SqlConsoleResult after = await harness.Queries.RunSqlAsync(
            harness.Scope, new SqlConsoleRequest("select 1"), Token);

        after.Succeeded.Should().BeTrue();
        after.Rows[0][0].Text.Should().Be("1");
    }

    // ------------------------------------------------------------------------------------------------
    // The gate in front of it
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// D4: the console is off by default, and the refusal lives in the service rather than in the page,
    /// because a Blazor circuit is a long-lived object a client can drive.
    /// </summary>
    [PostgresFact]
    public async Task With_RunSql_off_the_service_refuses_and_nothing_is_executed()
    {
        await using QueryHarness denied = await QueryHarness.CreateAsync(
            fixture, "sqlconsolelivedenied", options => options.Capabilities.RunSql = false);

        long before = await PersonCountAsync();

        (await denied.Queries
            .Invoking(x => x.RunSqlAsync(denied.Scope, new SqlConsoleRequest($"select count(*) from {personTable}"), Token))
            .Should().ThrowAsync<StudioCapabilityDeniedException>())
            .Which.Message.Should().Contain("MartenStudioOptions.Capabilities.RunSql");

        (await PersonCountAsync()).Should().Be(before);

        denied.Audit.GetLatest().Should().ContainSingle()
            .Which.Succeeded.Should().BeFalse("a refusal is audited too");
    }

    [PostgresFact]
    public async Task ReadOnly_turns_the_console_off_whatever_the_capability_says()
    {
        await using QueryHarness readOnly = await QueryHarness.CreateAsync(
            fixture, "sqlconsoleliveread", options => options.ReadOnly = true);

        (await readOnly.Queries
            .Invoking(x => x.RunSqlAsync(readOnly.Scope, new SqlConsoleRequest("select 1"), Token))
            .Should().ThrowAsync<StudioCapabilityDeniedException>())
            .Which.Message.Should().Contain("MartenStudioOptions.ReadOnly");
    }

    /// <summary>
    /// It is a read, but it is the dangerous one, so it is resolved with the <em>write</em> policy and the
    /// refusal is recorded as a scope denial.
    /// </summary>
    [PostgresFact]
    public async Task The_write_policy_refusal_stops_it_before_anything_is_sent()
    {
        await using QueryHarness policed = await QueryHarness.CreateAsync(
            fixture,
            "sqlconsolelivepolicy",
            options => options.WriteAuthorizationPolicy = QueryHarness.DenyWritesPolicy);

        long before = await PersonCountAsync();

        await policed.Queries
            .Invoking(x => x.RunSqlAsync(policed.Scope, new SqlConsoleRequest("select 1"), Token))
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        (await PersonCountAsync()).Should().Be(before);

        policed.Audit.GetLatest().Should().ContainSingle()
            .Which.Message.Should().Contain("Not authorized");
    }

    // ------------------------------------------------------------------------------------------------
    // The audit
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_statement_that_ran_is_audited_with_its_text_its_rows_and_who_ran_it()
    {
        const string statement = "select 42 as answer";

        await harness.Queries.RunSqlAsync(harness.Scope, new SqlConsoleRequest(statement), Token);

        StudioActionLogEntry entry = harness.Audit.GetLatest()[0];

        entry.Action.Should().Be("RunSql");
        entry.Target.Should().Contain(statement);
        entry.Succeeded.Should().BeTrue();
        entry.Message.Should().Contain("1 rows");
        entry.Capability.Should().Be(nameof(StudioCapability.RunSql));
        entry.User.Should().Be("ops");
        entry.StoreKey.Should().Be(harness.Scope.StoreKey);
    }

    [PostgresFact]
    public async Task A_statement_the_guard_refused_is_audited_with_its_reason()
    {
        string statement = $"delete from {personTable}";

        await harness.Queries.RunSqlAsync(harness.Scope, new SqlConsoleRequest(statement), Token);

        StudioActionLogEntry entry = harness.Audit.GetLatest()[0];

        entry.Action.Should().Be("RunSql");
        entry.Target.Should().Contain("delete from");
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Contain("only runs");
    }

    [PostgresFact]
    public async Task A_statement_postgres_refused_is_audited_with_its_sqlstate()
    {
        await harness.Queries.RunSqlAsync(
            harness.Scope,
            new SqlConsoleRequest($"with x as (delete from {personTable} returning *) select * from x"),
            Token);

        StudioActionLogEntry entry = harness.Audit.GetLatest()[0];

        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().StartWith(QuerySqlErrors.ReadOnlyTransaction + ":");
    }

    /// <summary>
    /// The plan fetch is a console run of its own, so it is audited the same way - an EXPLAIN is still a
    /// statement somebody's studio sent to their database.
    /// </summary>
    [PostgresFact]
    public async Task Fetching_a_plan_is_audited_as_its_own_statement()
    {
        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(harness.Scope, Token);
        string alias = types.Single(x => x.TypeName == nameof(QueryPerson)).Alias;

        await harness.Queries.RunMartenQueryAsync(harness.Scope, new MartenQueryRequest(alias, "where name = 'Alice'"), Token);

        StudioActionLogEntry entry = harness.Audit.GetLatest()[0];

        entry.Action.Should().Be("ExplainQuery");
        entry.Target.Should().StartWith("explain (format json) select");
        entry.Succeeded.Should().BeTrue();
    }
}
