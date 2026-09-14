using System.Text.Json;

using MartenStudio.Services;
using MartenStudio.Services.Query;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>
/// Mode A against a real database: a Marten <c>where</c> clause, the SQL the studio says it composed, and
/// the plan Postgres gives for it.
/// </summary>
/// <remarks>
/// These run through the registered <see cref="IQueryService" /> with a real
/// <c>StudioScopeResolver</c> in front of it, so the authorization order is exercised rather than
/// bypassed. The clause needs no capability - it is a read - which is exactly what the first test is
/// about.
/// </remarks>
public class MartenQueryLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private QueryHarness harness = null!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        harness = await QueryHarness.CreateAsync(fixture, "martenquerylive");
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

    private async Task<string> PersonAliasAsync()
    {
        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(harness.Scope, Token);

        return types.Single(x => x.TypeName == nameof(QueryPerson)).Alias;
    }

    [PostgresFact]
    public async Task The_registered_document_types_are_listed_with_their_tables_and_duplicated_fields()
    {
        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(harness.Scope, Token);

        types.Should().HaveCountGreaterThanOrEqualTo(2);

        QueryDocumentTypeInfo person = types.Single(x => x.TypeName == nameof(QueryPerson));

        person.Schema.Should().Be(harness.Schema);
        person.Table.Should().Be("mt_doc_" + person.Alias);
        person.QualifiedTableName.Should().Be($"\"{harness.Schema}\".\"mt_doc_{person.Alias}\"");
        person.DuplicatedColumns.Should().Contain("Name", "the host duplicated that field, so a filter on it is indexed");
    }

    [PostgresFact]
    public async Task A_where_clause_returns_the_right_rows_and_says_what_sql_it_composed()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where data ->> 'Name' = 'Alice'"), Token);

        result.Succeeded.Should().BeTrue();
        result.Rows.Should().ContainSingle();
        result.Rows[0].Id.Should().Be("11111111-1111-1111-1111-111111111111");
        result.Rows[0].Json.Should().Contain("Alice");

        result.GeneratedSql.Should()
            .StartWith("select d.id, d.data from ")
            .And.Contain($"\"{harness.Schema}\".\"mt_doc_{alias}\" as d")
            .And.Contain("where data ->> 'Name' = 'Alice'");

        result.Parameters.Should().BeEmpty("parameters are not supported in a where clause in this release");
    }

    /// <summary>
    /// The duplicated column is a real column, so the same filter written against it is the one that can
    /// use an index - and it has to return the same row.
    /// </summary>
    [PostgresFact]
    public async Task A_clause_against_a_duplicated_column_returns_the_same_row()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where name = 'Alice'"), Token);

        result.Succeeded.Should().BeTrue();
        result.Rows.Should().ContainSingle();
    }

    [PostgresFact]
    public async Task A_string_id_document_comes_back_with_its_id_as_text()
    {
        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(harness.Scope, Token);
        string alias = types.Single(x => x.TypeName == nameof(QueryTag)).Alias;

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where data ->> 'Label' = 'Red'"), Token);

        result.Rows.Should().ContainSingle();
        result.Rows[0].Id.Should().Be("red");
    }

    [PostgresFact]
    public async Task An_empty_clause_returns_the_collection_under_the_row_cap()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, null, PageSize: 2), Token);

        result.Error.Should().BeNull(result.Error?.MessageText ?? string.Empty);
        result.Rows.Should().HaveCount(2, "the row cap is a server-side clamp");
        result.LimitApplied.Should().BeTrue();
        result.Truncated.Should().BeTrue();
        result.GeneratedSql.Should().EndWith("where 1 = 1 limit 2");
    }

    /// <summary>
    /// The plan is fetched through the SQL-console path, so it only appears when <c>RunSql</c> is granted -
    /// which this harness does.
    /// </summary>
    [PostgresFact]
    public async Task Explain_comes_back_with_a_plan_for_the_composed_statement()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where data ->> 'Name' = 'Alice'"), Token);

        result.Plan.Should().NotBeNull();
        result.Plan!.HasPlan.Should().BeTrue(result.Plan.UnavailableReason ?? "there should be a plan");

        using var document = JsonDocument.Parse(result.Plan.PlanJson!);

        document.RootElement[0].GetProperty("Plan").GetProperty("Node Type").GetString()
            .Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Without the console capability there is no plan, and the page is told why rather than being handed
    /// an exception: fetching a plan means sending a statement to Postgres, which is the thing
    /// <c>RunSql</c> gates.
    /// </summary>
    [PostgresFact]
    public async Task With_RunSql_off_the_clause_still_runs_and_the_plan_says_which_option_would_give_it()
    {
        await using QueryHarness readOnly = await QueryHarness.CreateAsync(
            fixture, "martenqueryliveplan", options => options.Capabilities.RunSql = false);

        IReadOnlyList<QueryDocumentTypeInfo> types = await readOnly.Queries.ListDocumentTypesAsync(readOnly.Scope, Token);
        string alias = types.Single(x => x.TypeName == nameof(QueryPerson)).Alias;

        MartenQueryResult result = await readOnly.Queries.RunMartenQueryAsync(
            readOnly.Scope, new MartenQueryRequest(alias, "where data ->> 'Name' = 'Alice'"), Token);

        result.Succeeded.Should().BeTrue("a where clause is a read and needs no capability");
        result.Rows.Should().ContainSingle();

        result.Plan!.HasPlan.Should().BeFalse();
        result.Plan.UnavailableReason.Should().Contain("MartenStudioOptions.Capabilities.RunSql");
    }

    /// <summary>
    /// A malformed clause is a <em>value</em>: the SQLSTATE and Postgres' own message, so the page can put
    /// a caret under the character it objected to.
    /// </summary>
    [PostgresFact]
    public async Task A_bad_where_clause_surfaces_the_postgres_error_rather_than_throwing()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where data ->> 'Name' ="), Token);

        result.Succeeded.Should().BeFalse();
        result.Error!.SqlState.Should().Be("42601", "that is Postgres' syntax_error");
        result.Error.MessageText.Should().NotBeEmpty();
        result.Error.Position.Should().BeGreaterThan(0, "the page turns this into a caret under the character");
        result.Rows.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_clause_naming_a_column_that_is_not_there_is_the_same_kind_of_value()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where nonexistent_column = 1"), Token);

        result.Succeeded.Should().BeFalse();
        result.Error!.SqlState.Should().Be("42703", "that is Postgres' undefined_column");
    }

    /// <summary>
    /// Mode A needs no capability and does not run in the console's read-only transaction, so a clause
    /// carrying a second statement would be an ungated SQL console. It is refused before anything is sent,
    /// and the table is what it was.
    /// </summary>
    [PostgresFact]
    public async Task A_clause_carrying_a_second_statement_is_refused_and_the_table_is_untouched()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult before = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, null), Token);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope,
            new MartenQueryRequest(alias, $"where 1 = 1; drop table \"{harness.Schema}\".\"mt_doc_{alias}\""),
            Token);

        result.Succeeded.Should().BeFalse();
        result.Rejection.Should().NotBeNull();
        result.Error.Should().BeNull("nothing was sent");

        MartenQueryResult after = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, null), Token);

        after.Rows.Should().HaveCount(before.Rows.Count, "the table is still there with the same rows");
    }

    /// <summary>
    /// Without <c>RunSql</c>, a clause may read the collection it filters and nothing else. The refusal
    /// happens before anything is sent, and it is written to the application's log under 9205 - the same
    /// id the SQL console's refusals carry, because it is the same question.
    /// </summary>
    [PostgresFact]
    public async Task A_subquery_clause_is_refused_without_RunSql_audited_as_9205_and_runs_nothing()
    {
        await using QueryHarness gated = await QueryHarness.CreateAsync(
            fixture, "martenqueryliveguard", options => options.Capabilities.RunSql = false);

        IReadOnlyList<QueryDocumentTypeInfo> types = await gated.Queries.ListDocumentTypesAsync(gated.Scope, Token);
        QueryDocumentTypeInfo person = types.Single(x => x.TypeName == nameof(QueryPerson));
        string clause = $"where d.id in (select id from {person.QualifiedTableName})";

        MartenQueryResult result = await gated.Queries.RunMartenQueryAsync(
            gated.Scope, new MartenQueryRequest(person.Alias, clause), Token);

        result.Succeeded.Should().BeFalse();
        result.Rejection.Should().NotBeNull();
        result.Rejection!.Message.Should().Contain("MartenStudioOptions.Capabilities.RunSql");
        result.Rejection.Token.Should().Be("select");
        result.Rows.Should().BeEmpty();
        result.Error.Should().BeNull("nothing was sent, so Postgres never saw it");

        gated.Logs.Should().ContainSingle(x => x.EventId == 9205)
            .Which.Message.Should().Contain(clause, "9205 carries the statement text");

        StudioActionLogEntry entry = gated.Audit.GetLatest()[0];

        entry.Action.Should().Be("MartenQuery");
        entry.Succeeded.Should().BeFalse();
        entry.Target.Should().Contain("select id from");
        gated.Logs.Should().NotContain(x => x.EventId == 9204, "nothing ran");
    }

    /// <summary>
    /// The same clause with <c>RunSql</c> granted: a subquery is the point of the mode for somebody who
    /// could have typed the whole statement into the console anyway.
    /// </summary>
    [PostgresFact]
    public async Task The_same_subquery_clause_runs_when_RunSql_is_granted()
    {
        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(harness.Scope, Token);
        QueryDocumentTypeInfo person = types.Single(x => x.TypeName == nameof(QueryPerson));

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope,
            new MartenQueryRequest(person.Alias, $"where d.id in (select id from {person.QualifiedTableName})"),
            Token);

        result.Rejection.Should().BeNull("the clause guard's nested-read rules are lifted by RunSql");
        result.Error.Should().BeNull(result.Error?.MessageText ?? string.Empty);
        result.Rows.Should().HaveCount(3);
    }

    [PostgresFact]
    public async Task A_clause_that_is_a_statement_of_its_own_is_refused()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "select current_user"), Token);

        result.Rejection.Should().NotBeNull();
        result.Rejection!.Message.Should().Contain("MartenStudioOptions.Capabilities.RunSql");
        result.Rows.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task An_unknown_alias_is_refused_before_anything_is_sent()
    {
        await harness.Queries
            .Invoking(x => x.RunMartenQueryAsync(harness.Scope, new MartenQueryRequest("no-such-alias", null), Token))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    /// <summary>
    /// The examples are built from this store's own aliases, tables and event schema - which is the whole
    /// reason they are worth having.
    /// </summary>
    [PostgresFact]
    public async Task The_examples_name_this_stores_own_tables()
    {
        QueryExamples examples = await harness.Queries.BuildExamplesAsync(harness.Scope, Token);

        examples.Where.Should().HaveCount(3);
        examples.Sql.Should().HaveCount(3);
        examples.Sql[0].Snippet.Should().Contain($"\"{harness.Schema}\".");
        examples.Sql[1].Snippet.Should().Contain($"\"{harness.Schema}_events\".\"mt_events\"");
    }

    /// <summary>
    /// Every example the idle panel offers has to be a statement that actually runs here - an example that
    /// needs editing before it does anything is the same as no example.
    /// </summary>
    [PostgresFact]
    public async Task Every_sql_example_runs_against_this_store()
    {
        QueryExamples examples = await harness.Queries.BuildExamplesAsync(harness.Scope, Token);

        foreach (QueryExample example in examples.Sql)
        {
            SqlConsoleResult result = await harness.Queries.RunSqlAsync(
                harness.Scope, new SqlConsoleRequest(example.Snippet), Token);

            result.Rejection.Should().BeNull(example.Snippet);
            result.Error.Should().BeNull(example.Snippet + " => " + (result.Error?.MessageText ?? string.Empty));
        }
    }

    [PostgresFact]
    public async Task Every_where_example_runs_against_this_store()
    {
        string alias = await PersonAliasAsync();
        QueryExamples examples = await harness.Queries.BuildExamplesAsync(harness.Scope, Token);

        foreach (QueryExample example in examples.Where)
        {
            MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
                harness.Scope, new MartenQueryRequest(example.Alias ?? alias, example.Snippet), Token);

            result.Error.Should().BeNull(
                example.Snippet + " => " + (result.Error?.MessageText ?? string.Empty));
        }
    }
}
