using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages;
using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Components.Shared;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Tests.Components;

using static PostgresStudio.Tests.Connection.ConnectionTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// Database › Connection (NP3): each section, the two gates' states, the toggle and what it remembers, the cards that link to rows,
/// what the copy buttons copy, and the Overview's line. The service is faked; the model is the real builder's over stated facts.
/// </summary>
public sealed class ConnectionPageTests
{
    private const string Policy = "Host=db.example;Database=app;Username=svc;SSL Mode=Prefer;Maximum Pool Size=100";

    private static ConnectionPageView DefaultView(SessionDetailsAccess? activity = null, HealthReport? report = null) =>
        View(Facts(Policy, Observed()), report, activity ?? GateClosed);

    private static async Task<PostgresStudioComponentContext> ContextAsync(ConnectionPageView view, ConnectionApplicationsView? applications = null)
    {
        PostgresStudioComponentContext context = new();
        context.Connection.View = view;

        if (applications is not null)
        {
            context.Connection.Applications = applications;
        }

        return await context.ReadyAsync();
    }

    private static IRenderedComponent<ConnectionPage> Open(PostgresStudioComponentContext context)
    {
        IRenderedComponent<ConnectionPage> page = context.Render<ConnectionPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-conn-strip").Should().ContainSingle());
        return page;
    }

    // -------------------------------------------------------------------------------------------------
    // The sections
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_draws_the_strip_then_the_sections_an_admin_reads_in_order()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.Find(".ms-page-title").TextContent.Should().Be("Connection");
        page.FindAll("h2.ms-section-title").Select(static x => x.TextContent.Trim().Split('\n')[0].Trim())
            .Should().Equal("Worth a look", "Settings", "Compare a connection string", "What the server saw", "This application's sessions", "Not visible from here");

        page.FindAll(".ms-conn-fact").Select(static x => x.QuerySelector("dt")!.TextContent)
            .Should().Equal("Server", "Database", "Role", "TLS", "Pool", "Npgsql", "Platform");
        page.Find(".ms-conn-fact-warn").TextContent.Should().Contain("encrypted, not verified", "the words carry the meaning, the edge only repeats it");
        page.FindAll(".ms-conn-fact a").Should().OnlyContain(static x => x.GetAttribute("href")!.Contains('#'), "each fact links to its detail");
        page.Markup.Should().Contain("Read ").And.Contain("Show SQL for what the server saw");
    }

    [Fact]
    public async Task Each_fact_links_to_a_row_or_section_the_page_has()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        foreach (IElement link in page.FindAll(".ms-conn-fact a"))
        {
            string id = Uri.UnescapeDataString(link.GetAttribute("href")![(link.GetAttribute("href")!.IndexOf('#', StringComparison.Ordinal) + 1)..]);
            page.FindAll("#" + id).Should().NotBeEmpty("the fact " + link.TextContent + " links to #" + id);
        }
    }

    [Fact]
    public async Task With_nothing_to_say_the_block_says_nothing_to_change()
    {
        ConnectionPageView view = View(Facts("Host=db.example;SSL Mode=VerifyFull", Observed()));
        await using PostgresStudioComponentContext context = await ContextAsync(view);
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.Find(".ms-conn-nothing").TextContent.Should().Be("Nothing to change.");
        page.FindAll(".ms-conn-worth .ms-tab-count").Should().BeEmpty();
    }

    [Fact]
    public async Task Findings_a_visitor_is_not_shown_are_counted_and_the_block_does_not_claim_there_are_none()
    {
        ConnectionPageView view = View(Facts("Host=db.example;SSL Mode=VerifyFull", Observed()), Report(3, "They need Capabilities.ViewDatabaseActivity."));
        await using PostgresStudioComponentContext context = await ContextAsync(view);
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.Find(".ms-conn-nothing").TextContent.Should().Be("Nothing to change among what you are shown.");
        page.Find(".ms-conn-withheld").TextContent.Should().Contain("3 findings").And.Contain("Capabilities.ViewDatabaseActivity");
    }

    [Fact]
    public async Task A_finding_is_a_card_with_its_severity_its_code_its_remedy_and_a_link_to_the_health_page()
    {
        HealthReport report = Report(0, null, Finding("S11", HealthSeverity.Warning, "The server's certificate is not verified"));
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView(report: report));
        IRenderedComponent<ConnectionPage> page = Open(context);

        IElement card = page.Find("article#conn-card-finding-s11");
        card.GetAttribute("aria-labelledby").Should().Be("conn-card-finding-s11-title");
        card.QuerySelector("h3")!.TextContent.Should().Be("The server's certificate is not verified");
        card.QuerySelector(".ms-health-severity")!.TextContent.Should().Be("Warning");
        card.QuerySelector(".ms-health-code")!.TextContent.Should().Be("S11");
        card.QuerySelector(".ms-health-remedy-code")!.TextContent.Should().Be("opts.Something = true;");
        card.QuerySelector(".ms-health-link a")!.GetAttribute("href").Should().Contain("health").And.EndWith("#finding-s11");
        card.ClassList.Should().Contain("ms-health-finding-warning");
        page.Find(".ms-conn-worth .ms-tab-count").TextContent.Should().Be("1", "the finding says what the page's own certificate hint would, so the hint is left out");
        page.FindAll("#conn-card-hint-tls").Should().BeEmpty();
    }

    [Fact]
    public async Task A_hint_is_a_card_with_every_form_of_its_fix_and_the_copy_buttons_copy_exactly_what_is_shown()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        IElement card = page.Find("article#conn-card-hint-tls");
        card.QuerySelector(".ms-conn-kind")!.TextContent.Should().Be("Hint", "a badge with text, not colour alone");
        card.QuerySelectorAll(".ms-health-remedy-title").Select(static x => x.TextContent).Should().Equal("Connection string", "NpgsqlDataSourceBuilder");

        string[] shown = [.. card.QuerySelectorAll(".ms-health-remedy-code").Select(static x => x.TextContent)];
        shown[0].Should().StartWith("SSL Mode=VerifyFull");
        shown[1].Should().Contain("SslMode.VerifyFull");

        IReadOnlyList<IRenderedComponent<CopyButton>> buttons = page.FindComponents<CopyButton>();
        buttons.Select(static x => x.Instance.Value).Should().Contain(shown, "what the button puts on the clipboard is what the block shows");
        buttons.Select(static x => x.Instance.Label).Should().Contain("Copy Connection string (TLS)").And.Contain("Copy NpgsqlDataSourceBuilder (TLS)");
    }

    [Fact]
    public async Task A_card_anchors_to_the_rows_it_is_about_and_each_row_links_back_to_its_card()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.FindAll("#conn-card-hint-tls .ms-conn-about-link").Select(static x => x.TextContent).Should().Contain(["SSL Mode", "Root Certificate"]);
        page.FindAll("#conn-card-hint-tls .ms-conn-about-link").Select(static x => x.GetAttribute("href")!)
            .Should().Contain(static x => x.EndsWith("#conn-row-ssl-mode", StringComparison.Ordinal));
        page.FindAll("tr#conn-row-ssl-mode").Should().ContainSingle();

        IElement marker = page.Find("tr#conn-row-ssl-mode a.ms-conn-advice");
        marker.TextContent.Should().Be("advice", "text, not an icon");
        marker.GetAttribute("href").Should().EndWith("#conn-card-hint-tls");
        marker.GetAttribute("aria-label").Should().Contain("SSL Mode").And.Contain("The server's certificate is not verified");
    }

    [Fact]
    public async Task A_note_is_good_to_know_and_not_a_thing_worth_a_look()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(View(Facts("Host=/var/run/postgresql;SSL Mode=Disable", Observed(ssl: false))));
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.Find(".ms-conn-nothing").TextContent.Should().Be("Nothing to change.");
        page.Find("#conn-notes-title").TextContent.Should().Be("Good to know");
        page.FindAll(".ms-conn-card-note").Select(static x => x.QuerySelector(".ms-conn-kind")!.TextContent).Should().OnlyContain(static x => x == "Note");
        page.FindAll(".ms-conn-card-note").Select(static x => x.Id).Should().Contain("conn-card-hint-local");
        page.FindAll("#conn-card-hint-enlist").Should().BeEmpty("that note is for the all-settings view");
    }

    [Fact]
    public async Task A_finding_muted_in_this_browser_is_not_drawn_and_is_counted_in_a_line()
    {
        HealthReport report = Report(0, null, Finding("S11", HealthSeverity.Warning, "The server's certificate is not verified"));
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView(report: report));
        await context.Services.GetRequiredService<HealthMutes>().SetAsync(context.JSInterop.JSRuntime, report.Findings[0].MuteKey, true, Xunit.TestContext.Current.CancellationToken);

        IRenderedComponent<ConnectionPage> page = Open(context);

        page.FindAll("#conn-card-finding-s11").Should().BeEmpty();
        page.Find(".ms-conn-worth").TextContent.Should().Contain("1 finding muted in this browser");
    }

    [Fact]
    public async Task A_finding_the_host_accepted_is_a_line_and_no_card()
    {
        HealthReport report = Report(0, null, Finding("S11", HealthSeverity.Warning, "The server's certificate is not verified", accepted: true));
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView(report: report));
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.FindAll("#conn-card-finding-s11").Should().BeEmpty();
        page.Find(".ms-conn-worth").TextContent.Should().Contain("1 finding accepted by the host");
    }

    // -------------------------------------------------------------------------------------------------
    // The settings table and its toggle
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_settings_are_grouped_by_category_each_in_a_labelled_scroll_region_with_the_four_columns()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.FindAll(".ms-conn-group-button").Select(static x => x.TextContent).Should().Contain(static x => x.Contains("Pooling", StringComparison.Ordinal));
        page.FindAll(".ms-conn-table thead th").Select(static x => x.TextContent).Distinct().Should().Equal("Setting", "In effect", "From", "Default");
        page.FindAll("table.ms-table").Should().OnlyContain(static x => x.ParentElement!.ClassList.Contains("ms-table-scroll"), "D25");
        page.FindAll(".ms-table-scroll").Should().OnlyContain(static x => x.GetAttribute("role") == "region" && !string.IsNullOrEmpty(x.GetAttribute("aria-label")));
    }

    [Fact]
    public async Task A_row_shows_the_value_the_from_badge_the_default_and_the_builder_property_in_a_tooltip()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        IElement row = page.Find("tr#conn-row-maximum-pool-size");
        row.QuerySelector("th")!.GetAttribute("title").Should().Be("NpgsqlConnectionStringBuilder.MaxPoolSize");
        row.QuerySelector(".ms-conn-value")!.TextContent.Should().Contain("100").And.Contain("The same as the default");
        row.QuerySelector(".ms-conn-from")!.TextContent.Should().Be("connection string");
        row.QuerySelector(".ms-conn-default")!.TextContent.Should().Be("100");
        page.Find("tr#conn-row-password .ms-conn-value").TextContent.Should().Contain("not visible: Npgsql removes it");
    }

    [Fact]
    public async Task The_toggle_is_on_by_default_hides_defaults_and_shows_them_when_turned_off_and_remembers_the_choice()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        IElement toggle = page.Find("button.ms-conn-toggle");
        toggle.TextContent.Trim().Should().Be("Only what differs from the default");
        toggle.GetAttribute("aria-pressed").Should().Be("true");
        page.FindAll("tr#conn-row-connection-pruning-interval").Should().BeEmpty("it is at its default");
        page.FindAll("tr#conn-row-host").Should().ContainSingle("the rows that say what the application connects with stay");

        toggle.Click();

        page.WaitForAssertion(() => page.FindAll("tr#conn-row-connection-pruning-interval").Should().ContainSingle());
        page.Find("button.ms-conn-toggle").GetAttribute("aria-pressed").Should().Be("false");
        page.Find("#conn-card-hint-enlist").QuerySelector(".ms-conn-kind")!.TextContent.Should().Be("Note", "the ambient-transaction note belongs to this view");

        context.JSInterop.Invocations.Should().Contain(static x => x.Identifier == "martenStudio.prefs.set"
            && x.Arguments.Count == 2 && (string?) x.Arguments[0] == "ms_conn_only_differs" && (string?) x.Arguments[1] == "0");
    }

    [Fact]
    public async Task A_browser_that_remembered_all_settings_starts_with_them_and_a_closed_group_stays_closed()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", static x => x.Arguments.Count == 1 && (string?) x.Arguments[0] == "ms_conn_only_differs").SetResult("0");
        context.JSInterop.Setup<string?>("martenStudio.prefs.get", static x => x.Arguments.Count == 1 && (string?) x.Arguments[0] == "ms_conn_collapsed").SetResult("Pooling");

        IRenderedComponent<ConnectionPage> page = Open(context);

        page.WaitForAssertion(() => page.Find("button.ms-conn-toggle").GetAttribute("aria-pressed").Should().Be("false"));
        page.FindAll("tr#conn-row-connection-pruning-interval").Should().BeEmpty("its group is closed");
        page.FindAll(".ms-conn-group-button").Single(static x => x.TextContent.Contains("Pooling", StringComparison.Ordinal))
            .GetAttribute("aria-expanded").Should().Be("false");

        page.FindAll(".ms-conn-group-button").Single(static x => x.TextContent.Contains("Pooling", StringComparison.Ordinal)).Click();

        page.WaitForAssertion(() => page.FindAll("tr#conn-row-connection-pruning-interval").Should().ContainSingle());
        context.JSInterop.Invocations.Should().Contain(static x => x.Identifier == "martenStudio.prefs.set" && (string?) x.Arguments[0] == "ms_conn_collapsed");
    }

    [Fact]
    public async Task A_browser_that_refuses_to_remember_is_drawn_the_default()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        context.JSInterop.SetupVoid("martenStudio.prefs.set", _ => true).SetException(new Microsoft.JSInterop.JSException("storage refused"));

        IRenderedComponent<ConnectionPage> page = Open(context);
        page.Find("button.ms-conn-toggle").Click();

        // The choice lasts for the page even when the browser cannot keep it.
        page.WaitForAssertion(() => page.Find("button.ms-conn-toggle").GetAttribute("aria-pressed").Should().Be("false"));
    }

    // -------------------------------------------------------------------------------------------------
    // What the server saw, what no page can see, and the slot
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task What_the_server_saw_says_whose_connection_it_is_before_it_shows_anything()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        IElement section = page.Find("#conn-observed");
        section.QuerySelector(".ms-conn-note")!.TextContent.Should().Contain("studio's own connection").And.Contain("same pool");
        section.QuerySelectorAll("dt").Select(static x => x.TextContent).Should().Contain(["Logged in as", "Database", "statement_timeout"]);
    }

    [Fact]
    public async Task A_server_that_could_not_be_asked_is_a_sentence_and_the_rest_of_the_page_stands()
    {
        ConnectionPageView view = View(Facts(Policy, observed: null, observedNotRead: "the connection read failed (SQLSTATE 28P01)"));
        await using PostgresStudioComponentContext context = await ContextAsync(view);
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.Find("#conn-observed .ms-conn-unavailable").TextContent.Should().Contain("28P01");
        page.FindAll(".ms-conn-fact").Single(static x => x.QuerySelector("dt")!.TextContent == "TLS").TextContent.Should().Contain("not read");
        page.FindAll("#conn-settings").Should().ContainSingle();
    }

    [Fact]
    public async Task What_no_page_can_see_is_short_and_there()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.FindAll("#conn-not-visible dt").Select(static x => x.TextContent).Should().Contain(["Passwords supplied by code", "The default password file"]);
    }

    [Fact]
    public async Task The_contributors_slot_is_empty_in_postgres_studio_and_drawn_when_a_studio_fills_it()
    {
        await using PostgresStudioComponentContext empty = await ContextAsync(DefaultView());
        Open(empty).FindAll(".ms-conn-contributed").Should().BeEmpty();

        ConnectionContribution contribution = new()
        {
            Sections =
            [
                new(
                    "conn-marten",
                    "Marten",
                    "What the store adds.",
                    [new("UseStickyConnectionLifetimes", "false")],
                    [new("conn-card-marten", ConnectionCardKind.Contributed, "Marten's timeout wins", "text", ["MARTEN"], [], [new("StoreOptions", "options.CommandTimeout = 30;")])]),
            ],
        };

        await using PostgresStudioComponentContext filled = await ContextAsync(View(Facts(Policy, Observed()), contributions: contribution));
        IRenderedComponent<ConnectionPage> page = Open(filled);

        page.Find("#conn-marten h2").TextContent.Should().Be("Marten");
        page.Find("#conn-marten dt").TextContent.Should().Be("UseStickyConnectionLifetimes");
        page.FindComponents<CopyButton>().Select(static x => x.Instance.Value).Should().Contain("options.CommandTimeout = 30;");
    }

    [Fact]
    public async Task A_contributor_that_repeats_a_label_an_id_or_a_fix_cannot_end_the_circuit()
    {
        ConnectionCard card(string id) => new(id, ConnectionCardKind.Contributed, "Advice", "text", ["X"], ["Command Timeout", "Command Timeout"], [new("StoreOptions", "a = 1;"), new("StoreOptions", "b = 2;")]);
        ConnectionContribution contribution = new()
        {
            Cards = [card("conn-card-dup"), card("conn-card-dup")],
            Sections =
            [
                new("conn-dup", "One", null, [new("Same", "1"), new("Same", "2")], [card("conn-card-dup")]),
                new("conn-dup", "Two", null, [], []),
            ],
        };

        await using PostgresStudioComponentContext context = await ContextAsync(View(Facts(Policy, Observed()), contributions: contribution));
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.FindAll(".ms-conn-contributed").Should().HaveCount(2);
        page.FindAll("#conn-dup, #conn-dup-2").Should().HaveCount(2, "the second section took another id");
        page.FindAll("dt").Count(static x => x.TextContent == "Same").Should().Be(2);
        page.FindAll("[id^='conn-card-dup']").Select(static x => x.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_lead_says_what_refresh_does_and_how_often_the_server_is_asked()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView());
        IRenderedComponent<ConnectionPage> page = Open(context);

        Normalize(page.Find(".ms-conn-lead").TextContent).Should().Contain("Refresh shows the latest read; the server is asked at most once per health interval.");
    }

    [Fact]
    public async Task The_strip_calls_gssapi_encryption_encryption()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(View(Facts(Policy, Observed(ssl: false, gssEncrypted: true))));
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.FindAll(".ms-conn-fact dt").Select(static x => x.TextContent).Should().Contain("Encryption").And.NotContain("TLS");
        page.FindAll(".ms-conn-fact").Single(static x => x.QuerySelector("dt")!.TextContent == "Encryption").TextContent.Should().Contain("GSSAPI");
    }

    [Fact]
    public async Task A_connection_string_that_could_not_be_read_is_an_alert_and_the_server_section_stays()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(View(Facts("Host=db.example;Maximum Pool Size=not-a-number", Observed())));
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.Find(".ms-conn-unreadable").TextContent.Should().Contain("could not be read");
        page.FindAll("#conn-settings").Should().BeEmpty();
        page.FindAll("#conn-observed").Should().ContainSingle();
    }

    // -------------------------------------------------------------------------------------------------
    // The two gates
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_visitor_the_database_gate_refuses_sees_the_sentence_and_no_section()
    {
        ConnectionPageView refused = ConnectionPageView.Refused(ConnectionRefusal.StorePolicy, "Your account may not see how this application connects.");
        await using PostgresStudioComponentContext context = await ContextAsync(refused);

        IRenderedComponent<ConnectionPage> page = context.Render<ConnectionPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-conn-refusal").Should().ContainSingle());

        page.Find(".ms-conn-refusal").TextContent.Should().Contain("may not see how this application connects");
        page.FindAll(".ms-section").Should().BeEmpty();
        page.FindAll(".ms-conn-strip").Should().BeEmpty();
        context.Connection.ApplicationRequests.Should().BeEmpty("a refused visitor is asked nothing further");
    }

    [Fact]
    public async Task A_visitor_short_of_the_activity_gate_sees_everything_else_and_a_sentence_naming_the_option()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView(GateClosed));
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.FindAll("#conn-settings").Should().ContainSingle();
        page.FindAll("#conn-observed").Should().ContainSingle();
        page.Find("#conn-sessions .ms-capability-disabled").TextContent.Should().Contain("Capabilities.ViewDatabaseActivity");
        page.FindAll("#conn-sessions table").Should().BeEmpty();
        context.Connection.ApplicationRequests.Should().BeEmpty("a closed gate is never asked, so it refuses and audits nothing");
    }

    [Fact]
    public async Task A_policy_refusal_of_the_activity_gate_is_the_policys_sentence()
    {
        SessionDetailsAccess denied = new(true, false, false, DatabaseRefusal.WritePolicy, "Your account may not see who is using this database.");
        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView(denied));
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.Find("#conn-sessions").TextContent.Should().Contain("not shown").And.Contain("may not see who is using this database");
        context.Connection.ApplicationRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Through_the_activity_gate_the_sessions_are_listed_with_their_age_their_tls_and_links_to_sessions_and_holders()
    {
        ConnectionApplicationsView applications = new()
        {
            Role = "svc",
            MeasuredAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero),
            ApplicationCount = 2,
            Rows =
            [
                new ConnectionApplicationRow("billing-api", 12, new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.Zero), 12, 12),
                new ConnectionApplicationRow(string.Empty, 2, null, 0, 0),
            ],
        };

        await using PostgresStudioComponentContext context = await ContextAsync(DefaultView(GateOpen), applications);
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.WaitForAssertion(() => page.FindAll("#conn-sessions tbody tr").Should().HaveCount(2));
        string[] first = [.. page.FindAll("#conn-sessions tbody tr")[0].QuerySelectorAll("td").Select(static x => x.TextContent)];
        first[0].Should().Be("billing-api");
        first[1].Should().Be("12");
        first[3].Should().Be("12 of 12");

        string[] second = [.. page.FindAll("#conn-sessions tbody tr")[1].QuerySelectorAll("td").Select(static x => x.TextContent)];
        second[0].Should().Be("(none set)");
        second[2].Should().Contain("not visible to this role");
        second[3].Should().Be("not visible to this role");

        page.Find("#conn-sessions").TextContent.Should().Contain("How sure").And.Contain("svc");
        page.FindAll("#conn-sessions a").Select(static x => x.GetAttribute("href")!).Should().Contain(static x => x.Contains("database/sessions", StringComparison.Ordinal) && !x.Contains("view=", StringComparison.Ordinal))
            .And.Contain(static x => x.Contains("view=holders", StringComparison.Ordinal));
        context.Connection.ApplicationRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_read_of_the_sessions_is_a_sentence_and_not_an_empty_table()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(
            DefaultView(GateOpen),
            ConnectionApplicationsView.Refused(DatabaseRefusal.Unavailable, "The role the studio reads as may not read the database's sessions (SQLSTATE 42501)."));
        IRenderedComponent<ConnectionPage> page = Open(context);

        page.WaitForAssertion(() => page.Find("#conn-sessions .ms-conn-unavailable").TextContent.Should().Contain("42501"));
        page.FindAll("#conn-sessions table").Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // The Overview's line
    // -------------------------------------------------------------------------------------------------

    private static ConnectionPageView TwoThings() => View(
        Facts("Host=db.example;SSL Mode=VerifyFull;Maximum Pool Size=100", Observed()),
        Report(0, null,
            Finding("C16", HealthSeverity.Warning, "Idle pooled connections may be ended first"),
            Finding("K08", HealthSeverity.Info, "The server ends idle sessions")));

    [Fact]
    public async Task The_overviews_line_says_tls_the_pool_and_how_many_things_are_worth_a_look_and_links_to_the_page()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(TwoThings());

        IRenderedComponent<ConnectionGlanceLine> line = context.Render<ConnectionGlanceLine>(p => p.Add(static x => x.Revision, DateTimeOffset.UnixEpoch));

        line.WaitForAssertion(() => line.Find(".ms-overview-connection").TextContent.Should().Contain("Connection"));
        Normalize(line.Find(".ms-overview-connection").TextContent).Should().Be("Connection: TLS verified (VerifyFull) · pool 100 · 2 things worth a look");
        line.Find(".ms-overview-connection a").GetAttribute("href").Should().Contain("database/connection");
        context.Connection.GlanceRequests.Should().NotBeEmpty("the line asks for the glance, which shows no peak and so neither reads nor records one");
        context.Connection.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_overviews_line_calls_gssapi_encryption_encryption_and_not_tls()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(View(
            Facts("Host=db.example", Observed(ssl: false, gssEncrypted: true)),
            Report(),
            pool: null));

        IRenderedComponent<ConnectionGlanceLine> line = context.Render<ConnectionGlanceLine>(p => p.Add(static x => x.Revision, DateTimeOffset.UnixEpoch));

        line.WaitForAssertion(() => Normalize(line.Find(".ms-overview-connection").TextContent).Should().StartWith("Connection: Encryption: GSSAPI"));
        Normalize(line.Find(".ms-overview-connection").TextContent).Should().NotContain("TLS");
    }

    [Fact]
    public async Task The_overviews_line_leaves_out_what_this_browser_muted()
    {
        ConnectionPageView view = TwoThings();
        await using PostgresStudioComponentContext context = await ContextAsync(view);
        await context.Services.GetRequiredService<HealthMutes>().SetAsync(context.JSInterop.JSRuntime, view.Cards[0].Finding!.MuteKey, true, Xunit.TestContext.Current.CancellationToken);

        IRenderedComponent<ConnectionGlanceLine> line = context.Render<ConnectionGlanceLine>(p => p.Add(static x => x.Revision, DateTimeOffset.UnixEpoch));

        line.WaitForAssertion(() => Normalize(line.Find(".ms-overview-connection").TextContent).Should().EndWith("· 1 thing worth a look"));
    }

    [Fact]
    public async Task The_overviews_line_says_nothing_to_change_when_there_is_nothing_and_is_absent_for_a_visitor_the_page_refuses()
    {
        await using PostgresStudioComponentContext clear = await ContextAsync(View(Facts("Host=db.example;SSL Mode=VerifyFull", Observed())));
        IRenderedComponent<ConnectionGlanceLine> line = clear.Render<ConnectionGlanceLine>(p => p.Add(static x => x.Revision, DateTimeOffset.UnixEpoch));
        line.WaitForAssertion(() => Normalize(line.Find(".ms-overview-connection").TextContent).Should().EndWith("· nothing to change"));

        await using PostgresStudioComponentContext refused = await ContextAsync(ConnectionPageView.Refused(ConnectionRefusal.StorePolicy, "no"));
        IRenderedComponent<ConnectionGlanceLine> none = refused.Render<ConnectionGlanceLine>(p => p.Add(static x => x.Revision, DateTimeOffset.UnixEpoch));
        refused.Connection.GlanceRequests.Should().NotBeEmpty();
        none.FindAll(".ms-overview-connection").Should().BeEmpty();
    }

    [Fact]
    public async Task The_postgres_overview_draws_the_line_once_it_is_interactive()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(TwoThings());

        IRenderedComponent<StudioHome> overview = context.Render<StudioHome>();

        overview.WaitForAssertion(() => overview.FindAll(".ms-overview-connection").Should().ContainSingle());
        Normalize(overview.Find(".ms-overview-connection").TextContent).Should().Contain("TLS verified (VerifyFull)");
    }

    private static string Normalize(string text) => string.Join(' ', text.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
}
