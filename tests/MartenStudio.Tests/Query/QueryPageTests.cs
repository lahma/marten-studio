using System.Reflection;

using Bunit;

using MartenStudio.Components.Pages.Query;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Conventions;

using Microsoft.JSInterop;

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
            "select d.\"id\", d.\"data\"::text\nfrom \"studio\".\"mt_doc_person\" as d\nwhere 1 = 1\n" +
            "  and (\n    data ->> 'Name' = 'Alice'\n  )\nlimit @limit",
            ["@limit = 50"],
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
        service.MartenRequests[0].IncludeDeleted.Should().Be(DeletedFilter.Exclude, "that is the default");

        page.Find(".ms-query-sql").TextContent.Should().Contain("mt_doc_person").And.Contain("limit @limit");
        page.Find(".ms-query-sql-note").TextContent.Should().Contain("@limit = 50", "the bindings are listed");
        page.Find(".ms-query-row-id a").GetAttribute("href").Should().Be("documents/person/doc?id=42");
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
        const string predicate = "data ->> 'Name' == 'Alice'";
        const string statement = "select d.\"id\", d.\"data\"::text\nfrom \"studio\".\"mt_doc_person\" as d\n" +
            "where 1 = 1\n  and (\n    " + predicate + "\n  )\nlimit @limit";

        FakeQueryService service = WithPerson();
        service.MartenResult = new MartenQueryResult(
            "person",
            [],
            statement,
            ["@limit = 50"],
            TimeSpan.FromMilliseconds(2),
            50,
            true,
            new SqlError("42601", "syntax error at or near \"=\"", statement.IndexOf("==", StringComparison.Ordinal) + 2, null, null),
            null,
            null,
            predicate);

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
    /// <b>A deep link is attacker-controlled text</b>, and it is the cheapest way to hand somebody a clause
    /// they did not write: a link in a chat window opens the Query page with the <c>where</c> box already
    /// filled in. So the one character that defeated three rounds of review has to survive the URL, the
    /// parameter binding, the editor and the request <em>unchanged</em>, and be refused at the far end.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>%0D</c> is a carriage return, which ends a <c>--</c> comment to Postgres (its lexer's
    /// <c>non_newline</c> is <c>[^\n\r]</c>) and to nothing else. Everything after it is live SQL: this
    /// clause closes the bracket the composer wrote and lands the rest of itself outside the tenant and
    /// soft-delete predicates. A router, a query-string decoder or a textarea that turned the CR into an LF
    /// on the way would hand the guard a different clause from the one Postgres would run - which is the
    /// exact shape of the defect this packet exists to close - so the assertion is byte-for-byte equality
    /// and not "something was sent".
    /// </para>
    /// <para>
    /// The fake runs the real <c>QuerySqlComposer.CheckClause</c> here rather than answering with a canned
    /// result, so the refusal on screen is the guard's own and the test would fail if the guard ever
    /// stopped refusing it.
    /// </para>
    /// <para>
    /// The expected clause is written with <c>\r</c> and never as a literal carriage return: the repository
    /// normalises line endings to LF on checkout, so a literal would silently become something else and the
    /// test would pass for the wrong reason.
    /// </para>
    /// <para>
    /// <b>The assertion is on the request and not on the rendered <c>value</c> attribute</b>, and that is
    /// not a convenience. HTML attribute-value normalisation turns a carriage return into a line feed when
    /// markup is parsed, so the attribute in the rendered DOM really does read <c>--\n)</c> - in bUnit, and
    /// in a browser parsing the prerendered page. That normalisation only ever weakens the text (a comment
    /// ends at a line feed too, so the guard reads <c>\n</c> the same way), and it never reaches the run
    /// path: <c>QueryEditor</c>'s Run button sends the component's own <c>Value</c>, which is the server's
    /// string, not the textarea's DOM value. Asserting the attribute would have been asserting AngleSharp's
    /// parser; asserting the request is asserting the studio.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_carriage_return_in_a_where_deep_link_reaches_the_guard_unchanged_and_is_refused()
    {
        const string clause = "data is not null --\r) or (true";

        FakeQueryService service = WithPerson();
        service.UseRealClauseGuard = true;

        using StudioComponentContext context = CreateContext(service);
        context.Navigate("marten/query?type=person&where=" + Uri.EscapeDataString(clause));

        context.CurrentUri.Should().Contain("%0D", "the carriage return travels percent-encoded");

        var page = context.Render<QueryPage>();

        page.Find(".ms-query-run").Click();

        service.MartenRequests.Should().ContainSingle()
            .Which.WhereClause.Should().Be(
                clause, "the carriage return survived the URL, the parameter binding and the request");

        page.Find(".ms-query-error-message").TextContent.Should()
            .Contain("closes a bracket it never opened", "the real guard refused it")
            .And.Contain("no capability lifts this one");
    }

    /// <summary>
    /// The other half of the same claim: the guard is reached for <em>every</em> clause, and one that is a
    /// plain filter still runs. Without this the test above would pass just as well against a page that
    /// refused everything.
    /// </summary>
    [Fact]
    public void A_deep_link_carrying_an_ordinary_clause_is_not_refused()
    {
        const string clause = "where data ->> 'Name' = 'Alice'";

        FakeQueryService service = WithPerson();
        service.UseRealClauseGuard = true;

        using StudioComponentContext context = CreateContext(service);
        context.Navigate("marten/query?type=person&where=" + Uri.EscapeDataString(clause));

        var page = context.Render<QueryPage>();

        page.Find(".ms-query-run").Click();

        service.MartenRequests.Should().ContainSingle().Which.WhereClause.Should().Be(clause);
        page.FindAll(".ms-query-error-message").Should().BeEmpty();
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

    // ------------------------------------------------------------------------------------------------
    // Tenancy and soft delete - the half of a Mode A result that is about who may see what
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The tri-state is offered only where there is something to ask about, and what it is set to reaches
    /// the service rather than only the screen.
    /// </summary>
    [Fact]
    public void The_deleted_tri_state_appears_only_for_a_soft_deleted_type_and_travels_with_the_request()
    {
        FakeQueryService service = new FakeQueryService()
            .WithType("person")
            .WithType("invoice", "Invoice", softDeleted: true);

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();

        page.FindAll("#ms-query-deleted").Should().BeEmpty("person is not soft-deleted");

        page.Find("#ms-query-type").Change("invoice");
        page.Find("#ms-query-deleted").Change(nameof(DeletedFilter.Only));
        RunEditor(page, "ms-query-where", "where 1 = 1");

        service.MartenRequests.Should().ContainSingle()
            .Which.IncludeDeleted.Should().Be(DeletedFilter.Only);
    }

    /// <summary>
    /// A grid that is quietly showing every tenant is the failure the chips exist to prevent, so the page
    /// says it twice: as a warning chip beside the row count, and as a message in the Messages tab.
    /// </summary>
    [Fact]
    public void A_cross_tenant_result_says_so_in_the_header_and_in_the_messages()
    {
        FakeQueryService service = new FakeQueryService().WithType("person", conjoined: true);
        service.MartenResult = new MartenQueryResult(
            "person",
            [new MartenQueryRow("42", """{"Name":"Alice"}""", "acme"), new MartenQueryRow("43", "{}", "globex")],
            "select …",
            [],
            TimeSpan.FromMilliseconds(1),
            50,
            true,
            null,
            null,
            new MartenQueryScope(null, CrossTenant: true, DeletedFilter.Exclude, SoftDeleted: false));

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();
        RunEditor(page, "ms-query-where", "where 1 = 1");

        page.Find(".ms-query-scope-chip").TextContent.Trim().Should().Be("all tenants");
        page.Find(".ms-query-scope-chip").ClassList.Should().Contain("ms-chip-warning");
        page.FindAll(".ms-query-row-tenant").Select(x => x.TextContent).Should().Equal("acme", "globex");
    }

    [Fact]
    public void A_tenanted_result_names_the_tenant_it_was_filtered_to()
    {
        FakeQueryService service = new FakeQueryService().WithType("person", conjoined: true, softDeleted: true);
        service.MartenResult = new MartenQueryResult(
            "person",
            [new MartenQueryRow("42", "{}", "acme", IsDeleted: true)],
            "select …",
            ["@tenant = 'acme'", "@limit = 50"],
            TimeSpan.FromMilliseconds(1),
            50,
            true,
            null,
            null,
            new MartenQueryScope("acme", CrossTenant: false, DeletedFilter.Only, SoftDeleted: true));

        using StudioComponentContext context = CreateContext(service);

        var page = context.Render<QueryPage>();
        RunEditor(page, "ms-query-where", "where 1 = 1");

        page.FindAll(".ms-query-scope-chip").Select(x => x.TextContent.Trim())
            .Should().Equal("tenant acme", "deleted only");
        page.Find(".ms-query-row-deleted").TextContent.Should().Be("deleted");
    }

    // ------------------------------------------------------------------------------------------------
    // Losing the browser
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The editor is disposed when its page closes, which is exactly when the circuit has usually already
    /// gone - and a disconnected circuit answers interop with <c>JSDisconnectedException</c>, which derives
    /// from <see cref="Exception" /> and from none of the types a <c>catch (JSException)</c> list names.
    /// Unhandled, it escapes <c>DisposeAsync</c> and takes the <see cref="DotNetObjectReference{T}" /> with
    /// it - an interop registration leaked per page view, for a page that was only being closed.
    /// </summary>
    [Fact]
    public async Task The_editor_survives_a_circuit_that_disconnected_before_it_was_disposed()
    {
        using StudioComponentContext context = CreateContext(WithPerson());

        context.JSInterop.Setup<bool>("martenStudio.query.enhanceEditor", _ => true).SetResult(true);
        context.JSInterop.SetupVoid("martenStudio.query.releaseEditor", _ => true)
            .SetException(new JSDisconnectedException("The circuit has disconnected."));

        IRenderedComponent<QueryEditor> editor = context.Render<QueryEditor>(
            parameters => parameters.Add(p => p.EditorId, "ms-query-where"));

        QueryEditor instance = editor.Instance;

        Func<Task> dispose = context.DisposeComponentsAsync;

        await dispose.Should().NotThrowAsync(
            "a page being closed must not turn into an unhandled exception on the server");

        context.JSInterop.Invocations["martenStudio.query.releaseEditor"].Should().ContainSingle(
            "otherwise this test never reaches the throwing call and proves nothing");

        SelfReferenceOf(instance).Should().BeNull(
            "the DotNetObjectReference is released in a finally, whatever JavaScript did");
    }

    /// <summary>
    /// The same, for the registration call: a circuit can disconnect between the render and the first
    /// <c>OnAfterRenderAsync</c>, and the only cost of that is keyboard chords the page never promised.
    /// </summary>
    [Fact]
    public void The_editor_still_renders_when_registering_its_key_handler_disconnects()
    {
        using StudioComponentContext context = CreateContext(WithPerson());

        context.JSInterop.Setup<bool>("martenStudio.query.enhanceEditor", _ => true)
            .SetException(new JSDisconnectedException("The circuit has disconnected."));

        var page = context.Render<QueryPage>();

        page.Find("#ms-query-where").Should().NotBeNull("the textarea and its buttons still work");
    }

    /// <summary>
    /// The editor survives every shape of "the browser is not there", and it does so through the
    /// <em>shared</em> predicate. It used to carry a private copy, which had already drifted - it named
    /// <c>ObjectDisposedException</c> and swapped <c>TaskCanceledException</c> for
    /// <c>OperationCanceledException</c> - on the stated premise that
    /// <c>StudioLiveUpdates.IsInteropUnavailable</c> was private, which it never was. AGENTS.md hard rule
    /// 15 says there is one predicate; <see cref="QueryComponentsUseTheSharedInteropPredicate" /> is what
    /// keeps the copy from coming back, and this is what proves the shared one does the job.
    /// </summary>
    [Theory]
    [MemberData(nameof(InteropLossCases))]
    public async Task The_editor_survives_every_shape_of_a_browser_that_is_not_there(
        string name,
        Exception thrown)
    {
        name.Should().NotBeNullOrEmpty();

        using StudioComponentContext context = CreateContext(WithPerson());

        context.JSInterop.Setup<bool>("martenStudio.query.enhanceEditor", _ => true).SetResult(true);
        context.JSInterop.SetupVoid("martenStudio.query.releaseEditor", _ => true).SetException(thrown);

        IRenderedComponent<QueryEditor> editor = context.Render<QueryEditor>(
            parameters => parameters.Add(p => p.EditorId, "ms-query-where"));

        QueryEditor instance = editor.Instance;
        Func<Task> dispose = context.DisposeComponentsAsync;

        await dispose.Should().NotThrowAsync(name);

        SelfReferenceOf(instance).Should().BeNull(
            "the DotNetObjectReference is released in a finally, whatever JavaScript did");
    }

    /// <summary>Every exception the shared predicate names, plus the one the private copy was added for.</summary>
    public static TheoryData<string, Exception> InteropLossCases() =>
        new()
        {
            { "the tab closed", new JSDisconnectedException("The circuit has disconnected.") },
            { "the function is missing", new JSException("martenStudio.query is undefined") },
            { "prerendering", new InvalidOperationException("JavaScript interop calls cannot be issued.") },
            { "the call was cancelled", new TaskCanceledException("The operation was canceled.") },
        };

    /// <summary>
    /// AGENTS.md hard rule 15, as a scan rather than as a review note: no component under
    /// <c>Components/Pages/Query</c> declares an interop predicate of its own. A second copy is not merely
    /// duplication - the one that was here had already drifted from the shared one in two places, so the
    /// two disagreed about which exceptions mean "the browser is gone", and only one of them was the
    /// answer the rest of the studio uses.
    /// </summary>
    [Fact]
    public void QueryComponentsUseTheSharedInteropPredicate()
    {
        var directory = RepositoryRoot.Combine("src", "MartenStudio", "Components", "Pages", "Query");

        string[] files = [.. Directory.EnumerateFiles(directory, "*.razor", SearchOption.AllDirectories)];

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        List<string> offenders = [];
        var callers = 0;

        foreach (string file in files)
        {
            string text = File.ReadAllText(file);

            if (text.Contains("bool IsInteropUnavailable(", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }

            if (text.Contains("StudioLiveUpdates.IsInteropUnavailable(", StringComparison.Ordinal))
            {
                callers++;
            }
        }

        offenders.Should().BeEmpty(
            "hard rule 15: there is one interop predicate, StudioLiveUpdates.IsInteropUnavailable. " +
            "Offenders: " + string.Join(", ", offenders));

        callers.Should().BeGreaterThan(0, "otherwise this scan would pass on a page with no interop at all");
    }

    private static object? SelfReferenceOf(QueryEditor editor) =>
        typeof(QueryEditor)
            .GetField("selfReference", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor);

    private static void RunEditor(IRenderedComponent<QueryPage> page, string editorId, string text)
    {
        page.Find("#" + editorId).Change(text);
        page.Find(".ms-query-run").Click();
    }
}
