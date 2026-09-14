using System.Text.Json;

using JasperFx.MultiTenancy;

using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Query;

using Npgsql;

using NpgsqlTypes;

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
            .StartWith("select d.\"id\", d.\"data\"::text\n")
            .And.Contain($"from \"{harness.Schema}\".\"mt_doc_{alias}\" as d\n")
            .And.Contain("and (\n    data ->> 'Name' = 'Alice'\n  )")
            .And.EndWith("limit @limit");

        result.Parameters.Should().Equal(
            ["@limit = 50"], "the row cap is a parameter, not text in the statement");
        result.Predicate.Should().Be("data ->> 'Name' = 'Alice'");
    }

    /// <summary>
    /// The SQL tab is not a rendering of what ran, it <em>is</em> what ran. The studio's own log writes the
    /// statement it sent under 9204, so comparing the two proves it from an independent direction - and it
    /// is what makes the EXPLAIN on the same page a plan for the same statement rather than for a
    /// lookalike.
    /// </summary>
    [PostgresFact]
    public async Task The_sql_the_page_shows_is_the_statement_that_was_sent()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where data ->> 'Name' = 'Alice'"), Token);

        result.Succeeded.Should().BeTrue();

        harness.Logs.Should().Contain(
            x => x.EventId == 9204 && x.Message.Contains(result.GeneratedSql, StringComparison.Ordinal),
            "9204 carries the statement the studio actually executed");

        result.Plan!.HasPlan.Should().BeTrue(
            result.Plan.UnavailableReason ?? "EXPLAIN planned the same text, with the same values bound");
    }

    /// <summary>
    /// A successful read is audited too - not only the refusals. A clause is arbitrary SQL against one
    /// collection that needs no capability at all, so "who read what, with which filter" is exactly the
    /// question an incident asks.
    /// </summary>
    [PostgresFact]
    public async Task A_successful_clause_is_audited_as_9204_with_the_clause_in_the_ring()
    {
        string alias = await PersonAliasAsync();

        await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where data ->> 'Name' = 'Alice'"), Token);

        StudioActionLogEntry entry = harness.Audit.GetLatest()
            .First(x => x.Action == "MartenQuery");

        entry.Succeeded.Should().BeTrue();
        entry.Target.Should().Contain("where data ->> 'Name' = 'Alice'", "the ring records the filter, not the SQL");
        entry.Message.Should().Contain("rows in");

        harness.Logs.Should().Contain(x => x.EventId == 9204);
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
        result.GeneratedSql.Should().EndWith("where 1 = 1\nlimit @limit");
        result.Parameters.Should().Equal("@limit = 2");
    }

    /// <summary>
    /// A <c>limit</c> the visitor wrote is honoured and clamped <em>down</em> to the Rows selector - a
    /// clause is not a way past a server-side cap (plan §4.8).
    /// </summary>
    [PostgresFact]
    public async Task A_limit_and_an_order_by_in_the_clause_are_lifted_out_and_the_limit_is_clamped()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult ordered = await harness.Queries.RunMartenQueryAsync(
            harness.Scope,
            new MartenQueryRequest(alias, "where 1 = 1 order by data ->> 'Name' desc limit 2", PageSize: 50),
            Token);

        ordered.Error.Should().BeNull(ordered.Error?.MessageText ?? string.Empty);
        ordered.Rows.Should().HaveCount(2);
        ordered.Rows[0].Json.Should().Contain("Carla", "the order by was placed on the statement, not dropped");
        ordered.LimitApplied.Should().BeFalse("the clause carried its own limit");
        ordered.RowLimit.Should().Be(2);
        ordered.GeneratedSql.Should().Contain("order by data ->> 'Name' desc\nlimit @limit");

        MartenQueryResult clamped = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where 1 = 1 limit 10000", PageSize: 2), Token);

        clamped.RowLimit.Should().Be(2, "a clause cannot raise the cap");
        clamped.Rows.Should().HaveCount(2);
    }

    [PostgresFact]
    public async Task A_tail_the_composer_cannot_place_is_refused_before_anything_is_sent()
    {
        string alias = await PersonAliasAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where 1 = 1 limit all"), Token);

        result.Rejection.Should().NotBeNull();
        result.Rejection!.Token.Should().Be("limit");
        result.Error.Should().BeNull("nothing was sent");
        result.Rows.Should().BeEmpty();
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
    /// Mode A needs no capability, so a clause carrying a second statement would be an ungated SQL
    /// console - and a read-only transaction refuses writes, not a second `select` that takes a lock. It is
    /// refused before anything is sent, and the table is what it was.
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

    // ------------------------------------------------------------------------------------------------
    // Tenancy and soft delete. This is B1: Marten's own string query composes neither predicate, so the
    // studio composes and runs its own statement. These are the tests that would have caught the leak.
    // ------------------------------------------------------------------------------------------------

    private async Task<string> TicketAliasAsync(StudioScope scope)
    {
        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(scope, Token);

        return types.Single(x => x.TypeName == nameof(QueryTicket)).Alias;
    }

    [PostgresFact]
    public async Task The_picker_says_which_collections_are_tenanted_and_soft_deleted()
    {
        IReadOnlyList<QueryDocumentTypeInfo> types = await harness.Queries.ListDocumentTypesAsync(harness.Scope, Token);

        QueryDocumentTypeInfo ticket = types.Single(x => x.TypeName == nameof(QueryTicket));
        QueryDocumentTypeInfo person = types.Single(x => x.TypeName == nameof(QueryPerson));

        ticket.Conjoined.Should().BeTrue();
        ticket.SoftDeleted.Should().BeTrue();
        person.Conjoined.Should().BeFalse();
        person.SoftDeleted.Should().BeFalse();
    }

    /// <summary>
    /// The live proof. Scoped to <c>acme</c>, the most permissive clause anybody can write returns acme's
    /// live rows and nothing else - not globex's, and not acme's own deleted one. Through Marten's
    /// string-query overload this returned all four.
    /// </summary>
    [PostgresFact]
    public async Task As_one_tenant_a_clause_that_matches_everything_returns_only_that_tenants_live_rows()
    {
        StudioScope acme = QueryHarness.ScopeFor(QueryHarness.Acme);
        string alias = await TicketAliasAsync(acme);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            acme, new MartenQueryRequest(alias, "where 1 = 1"), Token);

        result.Error.Should().BeNull(result.Error?.MessageText ?? string.Empty);
        result.Rows.Select(x => x.Id).Should().Equal(QueryHarness.AcmeLive.ToString());
        result.Rows[0].TenantId.Should().Be(QueryHarness.Acme);
        result.Rows[0].IsDeleted.Should().BeFalse();

        result.ScopeOrPlain.TenantId.Should().Be(QueryHarness.Acme);
        result.ScopeOrPlain.CrossTenant.Should().BeFalse();
        result.GeneratedSql.Should()
            .Contain("and d.\"tenant_id\" = @tenant")
            .And.Contain("and d.\"mt_deleted\" = false");
        result.Parameters.Should().Contain("@tenant = 'acme'");
    }

    /// <summary>
    /// A clause naming another tenant is <c>and</c>-ed with the studio's own predicate rather than
    /// replacing it, so the two together match nothing. This is the shape somebody probes with.
    /// </summary>
    [PostgresFact]
    public async Task As_one_tenant_a_clause_naming_another_tenant_returns_nothing()
    {
        StudioScope acme = QueryHarness.ScopeFor(QueryHarness.Acme);
        string alias = await TicketAliasAsync(acme);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            acme, new MartenQueryRequest(alias, $"where d.tenant_id = '{QueryHarness.Globex}'"), Token);

        result.Error.Should().BeNull(result.Error?.MessageText ?? string.Empty);
        result.Rows.Should().BeEmpty("the scope's own tenant predicate still stands in front of the clause");
    }

    /// <summary>
    /// An <c>or true</c> cannot widen the read either: the visitor's predicate is parenthesised and the
    /// studio's own terms sit on both sides of it, so it can narrow what they let through and never add to
    /// it.
    /// </summary>
    [PostgresFact]
    public async Task As_one_tenant_an_or_true_clause_still_sees_only_that_tenant()
    {
        StudioScope acme = QueryHarness.ScopeFor(QueryHarness.Acme);
        string alias = await TicketAliasAsync(acme);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            acme, new MartenQueryRequest(alias, "where 1 = 2 or true"), Token);

        result.Error.Should().BeNull(result.Error?.MessageText ?? string.Empty);
        result.Rows.Select(x => x.TenantId).Should().AllBe(QueryHarness.Acme);
        result.Rows.Should().ContainSingle();
    }

    [PostgresFact]
    public async Task Include_deleted_shows_only_that_tenants_deleted_rows()
    {
        StudioScope acme = QueryHarness.ScopeFor(QueryHarness.Acme);
        string alias = await TicketAliasAsync(acme);

        MartenQueryResult only = await harness.Queries.RunMartenQueryAsync(
            acme, new MartenQueryRequest(alias, null, null, DeletedFilter.Only), Token);

        only.Error.Should().BeNull(only.Error?.MessageText ?? string.Empty);
        only.Rows.Select(x => x.Id).Should().Equal(QueryHarness.AcmeDeleted.ToString());
        only.Rows[0].IsDeleted.Should().BeTrue();
        only.ScopeOrPlain.Deleted.Should().Be(DeletedFilter.Only);

        MartenQueryResult both = await harness.Queries.RunMartenQueryAsync(
            acme, new MartenQueryRequest(alias, null, null, DeletedFilter.Include), Token);

        both.Rows.Select(x => x.Id).Should().BeEquivalentTo(
            [QueryHarness.AcmeLive.ToString(), QueryHarness.AcmeDeleted.ToString()]);
        both.Rows.Select(x => x.TenantId).Should().AllBe(QueryHarness.Acme, "including deleted is not including tenants");
        both.GeneratedSql.Should().NotContain("mt_deleted\" =");
    }

    /// <summary>
    /// With no tenant in scope the studio reads across every tenant - a cross-tenant view the store policy
    /// may well allow - and <em>says so</em>, because a grid that quietly mixes tenants is the failure the
    /// flag exists for. Deleted rows are still excluded: that is a separate question.
    /// </summary>
    [PostgresFact]
    public async Task With_no_tenant_in_scope_every_tenants_live_rows_appear_and_the_result_says_so()
    {
        string alias = await TicketAliasAsync(harness.Scope);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, "where 1 = 1"), Token);

        result.Error.Should().BeNull(result.Error?.MessageText ?? string.Empty);
        result.Rows.Select(x => x.Id).Should().BeEquivalentTo(
            [QueryHarness.AcmeLive.ToString(), QueryHarness.GlobexLive.ToString()]);

        result.ScopeOrPlain.CrossTenant.Should().BeTrue();
        result.ScopeOrPlain.TenantId.Should().BeNull();
        result.GeneratedSql.Should().NotContain("@tenant");
    }

    /// <summary>
    /// The document is the <c>data</c> column as Postgres holds it, not a round trip through the CLR type -
    /// so nothing can be dropped on the way to the screen.
    /// </summary>
    [PostgresFact]
    public async Task The_row_json_is_the_data_column_itself()
    {
        StudioScope acme = QueryHarness.ScopeFor(QueryHarness.Acme);
        string alias = await TicketAliasAsync(acme);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            acme, new MartenQueryRequest(alias, null), Token);

        result.Rows.Should().ContainSingle();
        result.Rows[0].Json.Should().Contain("acme live");
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

    // ------------------------------------------------------------------------------------------------
    // Escaping the studio's parentheses. The visitor's predicate is spliced between a `(` and a `)` the
    // composer wrote; a clause holding one more `)` than `(` closes the composer's bracket and everything
    // after it lands outside the tenant and soft-delete predicates. The adversarial review measured four
    // rows where the honest answer was one, on this very fixture.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every spelling the review tried, including the one that needs no unbalanced <c>(</c> at all: the
    /// composer puts its closing bracket on a line of its own, so a clause ending in a <c>--</c> comment
    /// supplies the <c>)</c> from the free line that follows.
    /// </summary>
    public static TheoryData<string> UnbalancedClauses() =>
        new()
        {
            "1 = 1) or (1 = 1",
            "where 1 = 1) or (1 = 1",
            "data is not null -- \n) or (true",
            "1=1) or (1=1 order by d.tenant_id limit 500",
            "x = 1) or (1=1 limit 5",
            "subject is not null)",
            "(1 = 1",
        };

    [PostgresTheory]
    [MemberData(nameof(UnbalancedClauses))]
    public async Task As_one_tenant_a_clause_that_escapes_the_studios_bracket_is_refused_and_never_sent(
        string clause)
    {
        StudioScope acme = QueryHarness.ScopeFor(QueryHarness.Acme);
        string alias = await TicketAliasAsync(acme);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            acme, new MartenQueryRequest(alias, clause), Token);

        result.Rejection.Should().NotBeNull(clause);
        result.Rejection!.Token.Should().BeOneOf(")", "(");
        result.Error.Should().BeNull("nothing was sent");
        result.Rows.Should().BeEmpty(clause);

        // The harness grants RunSql, so this also covers the capability level the review probed at: a
        // bracket is not something a capability lifts.
        harness.Audit.GetLatest().First(x => x.Action == "MartenQuery").Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// The anti-vacuity half, and the proof that <em>both</em> halves of the fix are load-bearing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first statement is the shape P6-fix shipped - the studio's terms in front of the visitor's
    /// parenthesised predicate and nowhere else - written out here by hand because the composer no longer
    /// produces it. Spliced with <c>1 = 1) or (1 = 1</c> it returns <b>four</b> rows on a scope narrowed to
    /// <c>acme</c>: acme live, acme deleted, globex live, globex deleted. That is the leak, reproduced.
    /// </para>
    /// <para>
    /// The second is what <see cref="QuerySqlComposer.Compose" /> writes for the same clause today, with
    /// the guard deliberately bypassed. It returns one row, because the terms are repeated <em>after</em>
    /// the predicate and <c>a and (X) and a</c> distributes over the <c>or</c>. So the clause is refused by
    /// the guard <em>and</em> would be harmless if the guard ever missed it.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task The_shape_that_leaked_still_leaks_and_the_shape_the_composer_writes_does_not()
    {
        const string clause = "1 = 1) or (1 = 1";

        DocumentTableInfo table = TicketTable();

        table.TenancyStyle.Should().Be(TenancyStyle.Conjoined, "otherwise this proves nothing");
        table.SoftDeleteEnabled.Should().BeTrue("otherwise this proves nothing");

        string leaking =
            $"select d.\"id\", d.\"tenant_id\", d.\"mt_deleted\"\nfrom {table.QualifiedName} as d\n" +
            "where 1 = 1\n" +
            "  and d.\"tenant_id\" = @tenant\n" +
            "  and d.\"mt_deleted\" = false\n" +
            "  and (\n    " + clause + "\n  )\n" +
            "limit 500";

        (await CountRowsAsync(leaking)).Should().Be(
            4,
            "the pre-fix shape really did put every tenant's rows and every deleted row outside the " +
            "predicates, because `and` binds tighter than `or`");

        ComposedQuery composed = QuerySqlComposer.Compose(table, clause, 500, QueryHarness.Acme);

        (await CountRowsAsync(composed.Statement, QueryHarness.Acme)).Should().Be(
            1,
            "the terms are repeated after the visitor's predicate, so every disjunct is still filtered");

        QuerySqlComposer.CheckClause(clause, allowNestedReads: true).Allowed.Should().BeFalse(
            "and the guard refuses it before either of those statements is composed for real");
    }

    // ------------------------------------------------------------------------------------------------
    // The read-only transaction. Mode A used to run as a plain command on a read connection: no
    // `SET TRANSACTION READ ONLY`, no server-side statement_timeout, no SqlConsoleRole - reached by the
    // mode that needs no capability at all.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The direct proof, and the only one that cannot be explained away by the clause guard: a function
    /// the denylist has never heard of, whose body writes. Outside a read-only transaction the clause
    /// inserts a row; inside one Postgres answers <c>25006</c> and the probe table stays empty.
    /// </summary>
    [PostgresFact]
    public async Task A_clause_that_reaches_a_writing_function_is_refused_by_the_transaction_as_25006()
    {
        string quoted = SqlIdentifier.Quote(harness.Schema);
        string alias = await PersonAliasAsync();

        await ExecuteAsync(
            $"create table {quoted}.ms_probe (n int);\n" +
            $"create function {quoted}.ms_touch() returns int language sql as " +
            $"$fn$ insert into {quoted}.ms_probe values (1); select 1 $fn$;");

        try
        {
            MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
                harness.Scope,
                new MartenQueryRequest(alias, $"where {harness.Schema}.ms_touch() = 1"),
                Token);

            result.Rejection.Should().BeNull(
                "the clause names nothing the guard refuses - the transaction is what has to stop it");
            result.Error.Should().NotBeNull();
            result.Error!.SqlState.Should().Be("25006", "cannot execute INSERT in a read-only transaction");

            (await ScalarAsync($"select count(*) from {quoted}.ms_probe")).Should().Be(0L);
        }
        finally
        {
            await ExecuteAsync(
                $"drop function if exists {quoted}.ms_touch(); drop table if exists {quoted}.ms_probe;");
        }
    }

    /// <summary>
    /// <c>for update</c> reached the server through the <c>order by</c> tail, at both capability levels -
    /// Postgres accepts a locking clause between <c>ORDER BY</c> and <c>LIMIT</c>, so the studio was
    /// handing out row-level write locks from the ungated read mode. It is refused now, and a second
    /// connection with a one-millisecond <c>lock_timeout</c> proves no lock was left behind.
    /// </summary>
    [PostgresTheory]
    [InlineData("where 1 = 1 order by d.id for update")]
    [InlineData("order by 1 for update")]
    [InlineData("where 1 = 1 order by 1 for no key update")]
    public async Task A_locking_clause_in_the_tail_is_refused_and_leaves_no_lock(string clause)
    {
        StudioScope acme = QueryHarness.ScopeFor(QueryHarness.Acme);
        string alias = await TicketAliasAsync(acme);

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            acme, new MartenQueryRequest(alias, clause), Token);

        if (result.Rejection is null)
        {
            result.Error.Should().NotBeNull(clause);
            result.Error!.SqlState.Should().Be("25006", "the read-only transaction is the second boundary");
        }
        else
        {
            result.Rejection.Token.Should().Be("for", clause);
            result.Error.Should().BeNull("nothing was sent");
        }

        await AssertRowIsNotLockedAsync(TicketTable(), QueryHarness.AcmeLive);
    }

    /// <summary>
    /// <c>RunSql</c> used to lift the function denylist entirely. Measured: with the capability granted,
    /// <c>where pg_advisory_lock(42) is not null</c> was <b>allowed</b> - a session-level lock, taken from
    /// a <c>where</c> box, on a pooled connection that outlives the request and that Marten's own daemon
    /// contends for. A read-only transaction does not refuse an advisory lock; only the list does.
    /// </summary>
    [PostgresTheory]
    [InlineData("where pg_advisory_lock(424242) is not null", "pg_advisory_lock")]
    [InlineData("where pg_advisory_lock_shared(4, 2) is not null", "pg_advisory_lock_shared")]
    [InlineData("where set_config('statement_timeout', '0', false) is not null", "set_config")]
    [InlineData("where nextval('nothing') > 0", "nextval")]
    [InlineData("where pg_sleep(30) is null", "pg_sleep")]
    public async Task A_RunSql_holders_clause_is_still_refused_by_the_function_denylist(
        string clause,
        string token)
    {
        string alias = await PersonAliasAsync();

        // The harness grants RunSql and the scope authorizes it, so this is the level the review probed at.
        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            harness.Scope, new MartenQueryRequest(alias, clause), Token);

        result.Rejection.Should().NotBeNull(clause);
        result.Rejection!.Token.Should().Be(token);
        result.Rejection.Message.Should().Contain("whatever capabilities you hold");
        result.Error.Should().BeNull("nothing was sent");
        result.Rows.Should().BeEmpty();

        (await ScalarAsync("select count(*) from pg_locks where locktype = 'advisory'"))
            .Should().Be(0L, "no advisory lock was taken, by this clause or by any that came before it");
    }

    /// <summary>
    /// <b>A behaviour change worth pinning:</b> Mode A now honours
    /// <see cref="MartenStudioOptions.SqlConsoleRole" />, because it runs the same preamble the console
    /// does and that preamble ends in <c>SET LOCAL ROLE</c>. A host that configured a narrow role for the
    /// console gets the ungated <c>where</c> box narrowed to the same thing - which is the direction a
    /// surprise should go, since Mode A needs no capability and the console needs one.
    /// </summary>
    /// <remarks>
    /// The role is proved by taking something away: it is granted <c>usage</c> on the schema and no
    /// <c>select</c> on the table, so a read that really switched to it comes back as <c>42501</c>. The
    /// role is a cluster-level object, so it is named after this schema and dropped in a <c>finally</c>.
    /// </remarks>
    [PostgresFact]
    public async Task Mode_A_runs_as_the_configured_SqlConsoleRole()
    {
        const string schema = "martenqueryliverole";
        const string role = "ms_role_" + schema;

        await using QueryHarness scoped = await QueryHarness.CreateAsync(
            fixture, schema, options => options.SqlConsoleRole = role);

        string quoted = SqlIdentifier.Quote(schema);
        string quotedRole = SqlIdentifier.Quote(role);

        // `drop role` refuses while the role still holds a grant, and `drop owned by` refuses when the role
        // is not there at all - so both, guarded, and run before as well as after in case a previous run
        // was killed between the two.
        string dropRole =
            $"do $do$ begin if exists (select 1 from pg_roles where rolname = '{role}') then " +
            $"drop owned by {quotedRole}; drop role {quotedRole}; end if; end $do$;";

        await ExecuteAsync(dropRole);
        await ExecuteAsync(
            $"create role {quotedRole} nologin;\n" +
            $"grant usage on schema {quoted} to {quotedRole};");

        try
        {
            IReadOnlyList<QueryDocumentTypeInfo> types =
                await scoped.Queries.ListDocumentTypesAsync(scoped.Scope, Token);
            string alias = types.Single(x => x.TypeName == nameof(QueryPerson)).Alias;

            MartenQueryResult result = await scoped.Queries.RunMartenQueryAsync(
                scoped.Scope, new MartenQueryRequest(alias, "where 1 = 1"), Token);

            result.Rejection.Should().BeNull("the clause is a plain filter; the role is what refuses it");
            result.Error.Should().NotBeNull("SET LOCAL ROLE really happened");
            result.Error!.SqlState.Should().Be("42501", "the role was never granted select on the table");
            result.Rows.Should().BeEmpty();
        }
        finally
        {
            await ExecuteAsync(dropRole);
        }
    }

    /// <summary>
    /// A scope the visitor is not authorized for used to leave no trace: the resolve happened before any
    /// audit call, so a <c>StudioNotAuthorizedException</c> went to the browser and nowhere else. Probing
    /// stores, databases and tenants through this page is exactly what an incident asks about, and 9203 is
    /// the entry the console has always written for the same refusal.
    /// </summary>
    [PostgresFact]
    public async Task A_scope_the_visitor_may_not_read_is_audited_as_9203()
    {
        await using QueryHarness denied = await QueryHarness.CreateAsync(
            fixture,
            "martenquerylivescope",
            options => options.StoreAuthorizationPolicy = QueryHarness.DenyWritesPolicy);

        await denied.Queries
            .Invoking(x => x.RunMartenQueryAsync(
                denied.Scope, new MartenQueryRequest("queryperson", "where 1 = 1"), Token))
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        denied.Logs.Should().ContainSingle(x => x.EventId == 9203)
            .Which.Message.Should().Contain(QueryHarness.DenyWritesPolicy, "9203 names the policy that refused");

        StudioActionLogEntry entry = denied.Audit.GetLatest()[0];

        entry.Action.Should().Be("MartenQuery");
        entry.Succeeded.Should().BeFalse();
        entry.Target.Should().Contain("where 1 = 1", "the ring records what was asked for");

        denied.Logs.Should().NotContain(x => x.EventId == 9204, "nothing ran");
    }

    /// <summary>
    /// The other refusal that used to leave no trace: an alias that is not there, or one the host's
    /// <c>IsDocumentTypeVisible</c> hides. Typing an alias into the URL is how somebody looks for a
    /// collection they were not offered.
    /// </summary>
    [PostgresFact]
    public async Task An_unknown_alias_is_audited_as_a_failed_run()
    {
        await harness.Queries
            .Invoking(x => x.RunMartenQueryAsync(
                harness.Scope, new MartenQueryRequest("no-such-alias", "where 1 = 1"), Token))
            .Should().ThrowAsync<KeyNotFoundException>();

        StudioActionLogEntry entry = harness.Audit.GetLatest()[0];

        entry.Action.Should().Be("MartenQuery");
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Contain("no-such-alias");

        harness.Logs.Should().Contain(x => x.EventId == 9205 && x.Message.Contains(
            "no-such-alias", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers for the section above
    // ------------------------------------------------------------------------------------------------

    private DocumentTableInfo TicketTable()
    {
        IDocumentType ticket = harness.Store.Options.AllKnownDocumentTypes()
            .Single(x => x.DocumentType == typeof(QueryTicket));

        return DocumentTableInfo.FromDocumentType(ticket);
    }

    /// <summary>Runs <paramref name="sql" /> outside the studio entirely, and counts what came back.</summary>
    private async Task<int> CountRowsAsync(string sql, string? tenantId = null)
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar)
        {
            Value = tenantId ?? QueryHarness.Acme,
        });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = 500 });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        var rows = 0;

        while (await reader.ReadAsync(Token))
        {
            rows++;
        }

        return rows;
    }

    /// <summary>
    /// Proves nothing holds a row lock, by trying to take one with a <c>lock_timeout</c> short enough that
    /// a lock anybody else were holding would come back as <c>55P03</c> rather than as a wait.
    /// </summary>
    private async Task AssertRowIsNotLockedAsync(DocumentTableInfo table, Guid id)
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync(Token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Token);

        await using (var timeout = new NpgsqlCommand("set local lock_timeout = '1ms'", connection, transaction))
        {
            await timeout.ExecuteNonQueryAsync(Token);
        }

        await using var command = new NpgsqlCommand(
            $"select 1 from {table.QualifiedName} where id = @id for update", connection, transaction);

        command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = id });

        Func<Task> locking = async () => await command.ExecuteScalarAsync(Token);

        await locking.Should().NotThrowAsync("a row somebody else has locked answers 55P03 in one millisecond");

        await transaction.RollbackAsync(Token);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);

        return await command.ExecuteScalarAsync(Token);
    }
}
