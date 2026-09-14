using Bunit;

using MartenStudio.Components.Pages.Query;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;

using QueryPage = MartenStudio.Components.Pages.Query.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The Query page, both modes, through a fake service.
/// </summary>
/// <remarks>
/// The page is the place where the asymmetry between the two modes has to be visible: a where clause is a
/// read anybody with the studio open may make, and the SQL console is the one that is gated, so the tests
/// that matter most here are the ones about what the page does when <c>RunSql</c> is off and what it does
/// with a refusal that came back from the service anyway.
/// </remarks>
public class QueryPageTests
{
    private static StudioComponentContext CreateContext(FakeQueryService service, bool runSql = false)
    {
        var context = new StudioComponentContext();
        context.WithStores("default");
        context.Options.Capabilities.RunSql = runSql;

        // A fallback provider rather than a registration: StudioComponentContext resolves services in its
        // own constructor, which seals bUnit's collection, and that context is shared with every other
        // page suite so this packet does not reach into it.
        context.Services.AddFallbackServiceProvider(new SingleService(typeof(IQueryService), service));

        return context;
    }

    /// <summary>One service, for bUnit's fallback provider.</summary>
    private sealed class SingleService(Type serviceType, object instance) : IServiceProvider
    {
        public object? GetService(Type requested) => requested == serviceType ? instance : null;
    }

    private static FakeQueryService WithPerson() => new FakeQueryService().WithType("person");

    // ------------------------------------------------------------------------------------------------
    // Mode A - the Marten where clause
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_page_opens_on_the_where_clause_mode_and_lists_the_stores_document_types()
    {
        FakeQueryService service = WithPerson().WithType("invoice", "Invoice");
        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();

        page.Find("#ms-query-mode-marten").GetAttribute("aria-selected").Should().Be("true");
        page.SelectorOptions("ms-query-type").Should().Equal("person", "invoice");
        page.Find("#ms-query-where").Should().NotBeNull();
    }

    [Fact]
    public void A_where_clause_runs_and_renders_its_rows_its_sql_and_its_link_to_the_document()
    {
        FakeQueryService service = WithPerson();
        service.MartenResult = new MartenQueryResult(
            "person",
            [new MartenQueryRow("42", """{"Name":"Alice"}""")],
            "select d.id, d.data from \"studio\".\"mt_doc_person\" as d where data ->> 'Name' = 'Alice' limit 50",
            [],
            TimeSpan.FromMilliseconds(4),
            50,
            true,
            null,
            ExplainResult.Unavailable("No plan: fetching one runs EXPLAIN."));

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();
        RunEditor(page, "ms-query-where", "where data ->> 'Name' = 'Alice'");

        service.MartenRequests.Should().ContainSingle();
        service.MartenRequests[0].Alias.Should().Be("person");
        service.MartenRequests[0].WhereClause.Should().Be("where data ->> 'Name' = 'Alice'");
        service.MartenRequests[0].PageSize.Should().Be(context.Options.DefaultPageSize);

        page.Find(".ms-query-sql").TextContent.Should().Contain("mt_doc_person").And.Contain("limit 50");
        page.Find(".ms-query-row-id a").GetAttribute("href").Should().Be("marten/documents/person/doc?id=42");
        page.FindAll(".ms-json-tree").Should().NotBeEmpty("each row renders the document in the JSON viewer");
    }

    /// <summary>
    /// A malformed clause is a value, not a failure: the SQLSTATE, the message, and a caret under the
    /// character Postgres objected to.
    /// </summary>
    [Fact]
    public void A_malformed_clause_comes_back_as_a_postgres_error_with_the_position_highlighted()
    {
        const string clause = "where data ->> 'Name' == 'Alice'";
        const string statement = "select d.id, d.data from \"studio\".\"mt_doc_person\" as d " + clause + " limit 50";

        FakeQueryService service = WithPerson();
        service.MartenResult = new MartenQueryResult(
            "person",
            [],
            statement,
            [],
            TimeSpan.FromMilliseconds(2),
            50,
            true,
            new SqlError("42601", "syntax error at or near \"=\"", statement.IndexOf("==", StringComparison.Ordinal) + 2, null, null),
            null);

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();
        RunEditor(page, "ms-query-where", clause);

        page.Find(".ms-query-error-message").TextContent.Should().Contain("42601").And.Contain("syntax error");

        string caret = page.Find(".ms-query-caret").TextContent;
        caret.Should().Contain(clause);
        caret.Should().Contain("^");

        // The caret sits under the second '=', which is where Postgres pointed - counted from the start of
        // the clause the person typed, not from the start of the statement the studio composed.
        string[] lines = caret.Split('\n');
        lines[1].IndexOf('^').Should().Be(clause.IndexOf("==", StringComparison.Ordinal) + 1);
    }

    [Fact]
    public void The_idle_panel_offers_the_stores_own_examples_and_loading_one_fills_the_editor()
    {
        FakeQueryService service = WithPerson();
        service.Examples = new QueryExamples(
            [new QueryExample(QueryMode.Marten, "Filter on a JSON property", "where data ->> 'Name' = 'x'", "person")],
            [new QueryExample(QueryMode.Sql, "Count", "select count(*) from \"studio\".\"mt_doc_person\"", "person")]);

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();

        page.Find(".ms-query-example-snippet").TextContent.Should().Be("where data ->> 'Name' = 'x'");

        page.Find(".ms-query-example-button").Click();

        page.Find("#ms-query-where").GetAttribute("value").Should().Be("where data ->> 'Name' = 'x'");
    }

    // ------------------------------------------------------------------------------------------------
    // Mode B - the SQL console
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// D4 and D13: the console is off by default, and a disabled control names the exact property a host
    /// would have to set rather than saying "not permitted".
    /// </summary>
    [Fact]
    public void With_RunSql_off_the_console_is_replaced_by_a_panel_naming_the_option()
    {
        using StudioComponentContext context = CreateContext(WithPerson());

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-sql").Click();

        page.Find(".ms-capability-disabled").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.RunSql");
        page.Find(".ms-query-sql-warning").TextContent.Should().Contain("psql");
        page.FindAll("#ms-query-sql").Should().BeEmpty("there is no editor to type into when the console is off");
    }

    [Fact]
    public void With_RunSql_granted_a_statement_runs_and_the_grid_keeps_NULL_apart_from_a_JSON_cell()
    {
        FakeQueryService service = WithPerson();
        service.SqlResult = FakeQueryService.OneRow();

        using StudioComponentContext context = CreateContext(service, runSql: true);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-sql").Click();

        RunEditor(page, "ms-query-sql", "select id, doc, note from x");

        service.Statements.Should().ContainSingle().Which.Should().Be("select id, doc, note from x");

        page.TextOfAll(".ms-query-grid-type").Should().Equal("uuid", "jsonb", "text");
        page.Find(".ms-query-null").TextContent.Should().Be("NULL");
        page.Find(".ms-query-json-chip").Should().NotBeNull();

        page.Find(".ms-query-json-chip").Click();
        page.Find(".ms-query-json-panel").TextContent.Should().Contain("Alice");
    }

    /// <summary>
    /// The guard's refusal is rendered as a sentence with a caret, never as an exception: it is the most
    /// common thing that happens on this page after a typo.
    /// </summary>
    [Fact]
    public void A_statement_the_guard_refuses_is_rendered_with_the_offending_token_pointed_at()
    {
        const string statement = "select 1; drop table x";

        FakeQueryService service = WithPerson();
        service.SqlResult = SqlConsoleResult.Refused(
            new SqlRejection(
                nameof(SqlRejectionReason.MultipleStatements),
                "The SQL console runs one statement at a time.",
                "drop",
                statement.IndexOf("drop", StringComparison.Ordinal)),
            500);

        using StudioComponentContext context = CreateContext(service, runSql: true);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-sql").Click();
        RunEditor(page, "ms-query-sql", statement);

        page.Find(".ms-query-error-message").TextContent.Should().Contain("one statement at a time");

        string[] caret = page.Find(".ms-query-caret").TextContent.Split('\n');
        caret[1].IndexOf('^').Should().Be(statement.IndexOf("drop", StringComparison.Ordinal));
    }

    /// <summary>
    /// The service refuses regardless of what the page rendered (AGENTS.md hard rule 5): a circuit is a
    /// long-lived object a client can drive, so the page has to be able to render the refusal it gets.
    /// </summary>
    [Fact]
    public void A_capability_refusal_from_the_service_is_shown_rather_than_killing_the_circuit()
    {
        FakeQueryService service = WithPerson();
        service.SqlFailure = new StudioCapabilityDeniedException(
            StudioCapability.RunSql, CapabilityDenialReason.Disabled);

        using StudioComponentContext context = CreateContext(service, runSql: true);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-sql").Click();
        RunEditor(page, "ms-query-sql", "select 1");

        page.Find(".ms-query-error-message").TextContent.Should()
            .Contain("MartenStudioOptions.Capabilities.RunSql");
    }

    [Fact]
    public void A_timeout_says_what_it_was_and_which_option_sets_it()
    {
        FakeQueryService service = WithPerson();
        service.SqlResult = new SqlConsoleResult(
            [], [], false, 500, TimeSpan.FromSeconds(30), [],
            new SqlError("57014", "canceling statement due to statement timeout", 0, null, null), null);

        using StudioComponentContext context = CreateContext(service, runSql: true);
        context.Options.QueryTimeout = TimeSpan.FromSeconds(5);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-sql").Click();
        RunEditor(page, "ms-query-sql", "select pg_sleep(60)");

        page.Find(".ms-query-error-message").TextContent.Should()
            .Contain("timed out after 5 s").And.Contain("57014");
    }

    // ------------------------------------------------------------------------------------------------
    // Chrome: stats tabs, the key handler, cancellation
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_stats_panel_has_a_tab_for_the_sql_the_plan_and_the_messages()
    {
        FakeQueryService service = WithPerson();
        service.SqlResult = FakeQueryService.OneRow();

        using StudioComponentContext context = CreateContext(service, runSql: true);

        var page = context.Render<QueryPage>();
        page.Find("#ms-query-mode-sql").Click();
        RunEditor(page, "ms-query-sql", "select 1");

        page.FindAll(".ms-query-tab").Should().HaveCount(3);
        page.TextOfAll(".ms-query-tab")[0].Should().Be("SQL");
        page.TextOfAll(".ms-query-tab")[1].Should().Be("Plan");
        page.TextOfAll(".ms-query-tab")[2].Should().StartWith("Messages");
        page.Find(".ms-query-tab-count").TextContent.Trim().Should().Be("1", "one notice came back");
        page.Find(".ms-query-tab-panel").TextContent.Should().Contain("select 1");

        page.Find("#ms-query-tab-messages").Click();
        page.Find(".ms-query-message").TextContent.Should().Contain("something happened");

        page.Find("#ms-query-tab-plan").Click();
        page.Find(".ms-query-no-plan").TextContent.Should().Contain("EXPLAIN");
    }

    /// <summary>
    /// The chords are registered in the browser rather than bound with <c>@onkeydown</c>, which would send
    /// every keystroke of a query over the circuit.
    /// </summary>
    [Fact]
    public void The_editor_registers_its_key_handler_in_javascript_rather_than_binding_onkeydown()
    {
        using StudioComponentContext context = CreateContext(WithPerson());

        var page = context.Render<QueryPage>();

        context.JSInterop.Invocations["martenStudio.query.enhanceEditor"].Should().ContainSingle(
            "the editor asks the browser to listen for Ctrl+Enter, Esc and Ctrl+S");

        page.Find("#ms-query-where").HasAttribute("onkeydown").Should().BeFalse();
    }

    [Fact]
    public async Task Esc_cancels_a_running_query_through_a_real_cancellation_token()
    {
        FakeQueryService service = WithPerson();
        service.Gate = new TaskCompletionSource();

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();
        var editor = page.FindComponent<QueryEditor>();

        Task running = page.InvokeAsync(() => editor.Instance.RunAsync("where 1 = 1"));

        page.WaitForAssertion(() => page.Find(".ms-query-running").Should().NotBeNull());

        await page.InvokeAsync(() => editor.Instance.CancelAsync());
        await running;

        service.Cancelled.Should().BeTrue("the token the service was handed is the one Esc cancels");
        page.Find(".ms-query-editor-ok").TextContent.Should().Contain("Cancelled");
    }

    [Fact]
    public void Running_a_query_puts_the_mode_the_type_and_the_text_into_the_url()
    {
        FakeQueryService service = WithPerson();

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();
        RunEditor(page, "ms-query-where", "where age > 30");

        context.CurrentUri.Should().Contain("mode=marten")
            .And.Contain("type=person")
            .And.Contain("where=where%20age%20%3E%2030");
    }

    [Fact]
    public void A_deep_link_opens_the_mode_the_type_and_the_text_it_names()
    {
        FakeQueryService service = WithPerson().WithType("invoice", "Invoice");
        service.SqlResult = FakeQueryService.OneRow();

        using StudioComponentContext context = CreateContext(service, runSql: true);
        context.Navigate("marten/query?mode=sql&sql=select+1");

        var page = context.Render<QueryPage>();

        page.Find("#ms-query-mode-sql").GetAttribute("aria-selected").Should().Be("true");
        page.Find("#ms-query-sql").GetAttribute("value").Should().Be("select 1");
    }

    /// <summary>
    /// A query too long for a URL is simply not put in one; what must never happen is the editor being
    /// emptied because the deep link could not carry what was in it.
    /// </summary>
    [Fact]
    public void A_query_too_long_for_a_url_is_left_out_of_it_and_stays_in_the_editor()
    {
        string huge = "where data ->> 'Name' = '" + new string('x', QueryPage.MaxUrlQueryLength) + "'";

        FakeQueryService service = WithPerson();

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();
        RunEditor(page, "ms-query-where", huge);

        context.CurrentUri.Should().NotContain("where=");
        page.Find("#ms-query-where").GetAttribute("value").Should().Be(huge);
        service.MartenRequests.Should().ContainSingle().Which.WhereClause.Should().Be(huge);
    }

    private static void RunEditor(IRenderedComponent<QueryPage> page, string editorId, string text)
    {
        page.Find("#" + editorId).Change(text);
        page.Find(".ms-query-run").Click();
    }
}
