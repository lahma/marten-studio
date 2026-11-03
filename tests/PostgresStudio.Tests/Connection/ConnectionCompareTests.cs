using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;

using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Components.Shared;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Tests.Components;

using static PostgresStudio.Tests.Connection.ConnectionTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// "Compare a connection string" (1.1-PASTE): the disclosure and its promise, the box that is never bound, the Pasted column and the
/// rows that differ, the advice labelled as what it would be, the refusals that say a name and nothing else, the gate's word, and the
/// sentinel test for the surfaces this component adds (markup, log lines at Trace, the audit ring, the model, what is sent to the
/// browser). The service is faked by the real sanitiser and comparer over stated facts.
/// </summary>
public sealed class ConnectionCompareTests
{
    private const string Sentinel = "S3NT1NEL";

    private const string InEffect = "Host=db.example;Port=5432;Database=app;Username=svc;SSL Mode=Prefer;Maximum Pool Size=100;Application Name=billing";

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Dump = new() { IncludeFields = true, MaxDepth = 64 };

    private static async Task<PostgresStudioComponentContext> ContextAsync(Action<IServiceCollection>? configure = null, string current = InEffect)
    {
        PostgresStudioComponentContext context = new(configure);
        ConnectionFacts facts = Facts(current, Observed());

        context.ConnectionCompare.Answer = text => ConnectionCompareService.Compare(text, facts, new FakeEnvironment(), Now);

        return await context.ReadyAsync();
    }

    private static IRenderedComponent<ConnectionCompare> Open(PostgresStudioComponentContext context)
    {
        IRenderedComponent<ConnectionCompare> page = context.Render<ConnectionCompare>();
        page.Find("#conn-compare-input");
        return page;
    }

    /// <summary>What studio.js does when Compare is pressed: hands the text over in chunks, then answers the length it sent.</summary>
    private static async Task PasteAsync(PostgresStudioComponentContext context, IRenderedComponent<ConnectionCompare> page, string text)
    {
        JSRuntimeInvocationHandler<int> sending = context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true);

        ConnectionCompare compare = page.Instance;
        page.Find(".ms-conn-compare-run").Click();

        await page.InvokeAsync(() => compare.BeginPaste());

        for (int i = 0; i < text.Length; i += 1000)
        {
            int start = i;
            await page.InvokeAsync(() => compare.PasteChunk(text.Substring(start, Math.Min(1000, text.Length - start))));
        }

        sending.SetResult(text.Length);
    }

    private static void Wait(Action assertion, IRenderedComponent<ConnectionCompare> page) => page.WaitForAssertion(assertion, TimeSpan.FromSeconds(10));

    // -------------------------------------------------------------------------------------------------
    // The disclosure, the promise and the box
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task It_is_a_disclosure_with_a_heading_and_says_what_happens_to_a_password_above_the_box()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        IElement details = page.Find("section#conn-compare details.ms-conn-compare-details");
        details.HasAttribute("open").Should().BeFalse("a disclosure starts closed");
        details.QuerySelector("summary h2.ms-section-title")!.TextContent.Should().Be("Compare a connection string");
        page.Find("#conn-compare-promise").TextContent.Should().Be("Passwords are removed before anything is read, and the studio keeps and logs nothing you paste.");
        page.Find("label.ms-conn-compare-label").GetAttribute("for").Should().Be("conn-compare-input");
        page.Find("#conn-compare-help").TextContent.Should().Contain("not audited").And.Contain("Nothing is connected to")
            .And.Contain("If your application logs ASP.NET Core's SignalR at Debug level, that log records what the browser sends, as it does for every page.");
        page.FindAll(".ms-conn-compare-result").Should().BeEmpty();
    }

    [Fact]
    public async Task The_box_is_not_bound_is_not_remembered_by_the_browser_and_is_described_by_the_promise()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        IElement box = page.Find("#conn-compare-input");
        box.Attributes.Select(static x => x.Name).Should().NotContain(static x => x.StartsWith("blazor:on", StringComparison.Ordinal), "the text goes over when Compare is pressed, never in a change event");
        box.GetAttribute("autocomplete").Should().Be("off");
        box.GetAttribute("spellcheck").Should().Be("false");
        box.GetAttribute("aria-describedby").Should().Contain("conn-compare-promise");
        box.HasAttribute("name").Should().BeFalse("a form-less textarea with no name is not offered to a browser's autofill history");
    }

    [Fact]
    public async Task The_connection_page_draws_the_disclosure_between_the_settings_and_what_the_server_saw()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        context.Connection.View = View(Facts(InEffect, Observed()), null, GateClosed);
        await context.ReadyAsync();

        IRenderedComponent<ConnectionPage> page = context.Render<ConnectionPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-conn-strip").Should().ContainSingle());

        page.FindAll("h2.ms-section-title").Select(static x => x.TextContent.Trim().Split('\n')[0].Trim())
            .Should().ContainInOrder("Settings", "Compare a connection string", "What the server saw");
    }

    [Fact]
    public async Task A_refused_visitor_is_drawn_no_disclosure()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        context.Connection.View = ConnectionPageView.Refused(ConnectionRefusal.StorePolicy, "Your account may not see how this application connects.");
        await context.ReadyAsync();

        IRenderedComponent<ConnectionPage> page = context.Render<ConnectionPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-conn-refusal").Should().ContainSingle());

        page.FindAll("#conn-compare, #conn-compare-input").Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // The comparison
    // -------------------------------------------------------------------------------------------------

    private const string Candidate = "Host=db.example;Port=5432;Database=app;Username=svc;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem;"
        + "Maximum Pool Size=50;Application Name=billing;Password=S3NT1NEL";

    [Fact]
    public async Task The_pasted_column_shows_the_pasted_value_and_the_rows_that_differ_are_marked_in_words()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, Candidate);

        Wait(() => page.FindAll(".ms-conn-compare-table").Should().NotBeEmpty(), page);
        page.FindAll(".ms-conn-compare-table thead th").Select(static x => x.TextContent).Distinct().Should().Equal("Setting", "In effect now", "Pasted", "Change");

        IElement ssl = page.Find("tr#conn-cmp-row-ssl-mode");
        ssl.ClassList.Should().Contain("ms-conn-compare-diff");
        ssl.QuerySelectorAll("td")[0].TextContent.Should().Contain("Prefer").And.Contain("connection string");
        ssl.QuerySelectorAll("td")[1].TextContent.Should().Contain("VerifyFull").And.Contain("pasted");
        ssl.QuerySelector(".ms-conn-compare-mark")!.TextContent.Should().Be("changed", "the word carries the meaning, the tint only repeats it");

        page.Find("tr#conn-cmp-row-maximum-pool-size .ms-conn-compare-mark").TextContent.Should().Be("changed");
        page.FindAll("tr#conn-cmp-row-host").Should().BeEmpty("only what differs is shown at first");
        page.Find("#conn-compare-summary").TextContent.Should().Contain("settings differ").And.Contain("from the connection in effect");
    }

    [Fact]
    public async Task Showing_every_setting_adds_the_ones_that_do_not_differ_marked_the_same()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);
        await PasteAsync(context, page, Candidate);
        Wait(() => page.FindAll(".ms-conn-compare-table").Should().NotBeEmpty(), page);

        page.Find("button.ms-conn-compare-toggle").GetAttribute("aria-pressed").Should().Be("false");
        page.Find("button.ms-conn-compare-toggle").Click();

        page.Find("button.ms-conn-compare-toggle").GetAttribute("aria-pressed").Should().Be("true");
        IElement host = page.Find("tr#conn-cmp-row-host");
        host.ClassList.Should().NotContain("ms-conn-compare-diff");
        host.QuerySelector(".ms-conn-compare-mark")!.TextContent.Should().Be("same");
        page.FindAll(".ms-conn-compare-diff").Should().NotBeEmpty();
    }

    [Fact]
    public async Task Pasting_the_same_settings_says_nothing_differs()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, InEffect + ";Maximum Pool Size=100");

        Wait(() => page.Find("#conn-compare-summary").TextContent.Should().Contain("sets nothing differently"), page);
        page.FindAll(".ms-conn-compare-table").Should().BeEmpty();
    }

    [Fact]
    public async Task The_advice_is_labelled_as_what_it_would_be_with_the_pasted_settings_and_says_whats_new_and_what_stops_applying()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(current: "Host=db.example;SSL Mode=Prefer;Application Name=a");
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, "Host=a.example,b.example;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem;Application Name=a");

        Wait(() => page.FindAll("#conn-compare-advice-title").Should().ContainSingle(), page);
        page.Find("#conn-compare-advice-title").TextContent.Should().Be("If you switched to the pasted settings");

        IElement card = page.Find("article.ms-conn-compare-card");
        card.QuerySelector(".ms-conn-kind")!.TextContent.Should().Be("New");
        card.QuerySelector("h4")!.TextContent.Should().Be("Writes can land on a standby");
        card.QuerySelectorAll(".ms-health-remedy-title").Select(static x => x.TextContent).Should().Contain("Connection string");
        page.FindComponents<CopyButton>().Select(static x => x.Instance.Value).Should().Contain(static x => x.Contains("Target Session Attributes", StringComparison.Ordinal));
        page.Find(".ms-conn-compare-resolved").TextContent.Should().Contain("No longer applies").And.Contain("certificate is not verified");
        page.Find(".ms-conn-compare-limits").TextContent.Should().Contain("health advisor's findings run on the application's real data source only");
        page.FindAll("[id]").Select(static x => x.Id ?? string.Empty).Where(static x => x.StartsWith("conn-card-", StringComparison.Ordinal)).Should().BeEmpty("the page's own ids are never repeated");
    }

    [Fact]
    public async Task A_row_with_advice_links_to_its_card_in_the_comparison_and_the_card_is_there()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, "Host=a.example,b.example;SSL Mode=Require");

        Wait(() => page.FindAll("tr#conn-cmp-row-ssl-mode").Should().ContainSingle(), page);
        IElement marker = page.Find("tr#conn-cmp-row-ssl-mode a.ms-conn-advice");
        string id = Uri.UnescapeDataString(marker.GetAttribute("href")![(marker.GetAttribute("href")!.IndexOf('#', StringComparison.Ordinal) + 1)..]);
        page.FindAll("#" + id).Should().ContainSingle();
    }

    [Fact]
    public async Task A_removed_secret_is_said_to_have_been_in_the_text_by_its_keyword_and_nothing_else()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, Candidate);

        Wait(() => page.Find(".ms-conn-compare-removed").TextContent.Should().Contain("a secret (Password)").And.Contain("Removed from what the studio reads; the text stays in this box until you press Clear or leave the page."), page);
    }

    [Fact]
    public async Task The_result_opens_with_one_verdict_sentence_above_the_count_and_the_tables_are_labelled_for_the_phone()
    {
        await using PostgresStudioComponentContext context = await ContextAsync(current: "Host=db.example;SSL Mode=Prefer;Persist Security Info=true;Username=svc");
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, "Host=db.example;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem;Username=svc");

        Wait(() => page.FindAll("#conn-compare-verdict").Should().ContainSingle(), page);
        page.Find("#conn-compare-verdict").TextContent.Should().StartWith(
            "The pasted settings check the server's certificate (SSL Mode=VerifyFull) and no longer keep the password readable (Persist Security Info).");

        IReadOnlyList<IElement> result = [.. page.Find("#conn-compare-result").Children];
        result[0].Id.Should().Be("conn-compare-verdict", "the verdict is first");
        result[1].Id.Should().Be("conn-compare-summary");

        // The changed rows stay (Port, Persist Security Info and Options are real differences), and each cell says what it is.
        page.FindAll("tr#conn-cmp-row-persist-security-info").Should().ContainSingle();
        IElement table = page.Find("table.ms-conn-compare-table");
        table.QuerySelectorAll("colgroup > col").Select(static x => x.ClassName).Should().Equal(
            "ms-conn-col-setting", "ms-conn-col-now", "ms-conn-col-pasted", "ms-conn-col-change");
        table.QuerySelectorAll("tbody tr")[0].QuerySelectorAll("td").Select(static x => x.GetAttribute("data-label")).Should().Equal("In effect now", "Pasted", "Change");
    }

    [Fact]
    public async Task A_comparison_with_the_same_advice_says_so_in_its_verdict()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, InEffect + ";Maximum Pool Size=100");

        Wait(() => page.Find("#conn-compare-verdict").TextContent.Should().Be("The page's hints are the same for both. Health findings such as S05 or S12 are judged only for the connection in effect."), page);
    }

    [Fact]
    public async Task When_the_connection_in_effect_cannot_be_read_no_difference_count_is_claimed()
    {
        // A connection string Npgsql cannot read: the first column is empty, so "N settings differ" would be a count of everything.
        await using PostgresStudioComponentContext context = await ContextAsync(current: "Port=not-a-port");
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, "Host=db.example;Database=app");

        Wait(() => page.FindAll("#conn-compare-summary").Should().ContainSingle(), page);
        string summary = page.Find("#conn-compare-summary").TextContent;
        summary.Should().Contain("could not be read").And.Contain("every setting is listed");
        summary.Should().NotContain("differ");
    }

    [Fact]
    public async Task An_unquoted_removed_password_adds_the_clause_about_quoting_and_a_quoted_one_does_not()
    {
        const string clause = "A password with ';' in it must be quoted, or Npgsql reads what follows as settings.";

        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, "Host=a.example;Password=" + Sentinel);
        Wait(() => page.Find(".ms-conn-compare-removed").TextContent.Should().Contain("until you press Clear or leave the page. " + clause), page);

        await using PostgresStudioComponentContext quoted = await ContextAsync();
        IRenderedComponent<ConnectionCompare> second = Open(quoted);

        await PasteAsync(quoted, second, "Host=a.example;Password='" + Sentinel + "'");
        Wait(() => second.Find(".ms-conn-compare-removed").TextContent.Should().Contain("until you press Clear or leave the page.").And.NotContain("must be quoted"), second);
    }

    // -------------------------------------------------------------------------------------------------
    // Refusals
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_keyword_refused_after_an_unquoted_password_says_the_unnamed_sentence_on_the_page()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, "Host=a;Password=abc;def" + Sentinel + "=ghi");

        Wait(() => page.Find(".ms-conn-compare-refusal").TextContent.Should()
            .Be("The keyword after the password could not be read. It is not named, in case it is part of the password; a password with ';' in it must be quoted."), page);
        page.Markup.Should().NotContain(Sentinel);
    }

    [Fact]
    public async Task A_keyword_npgsql_rejects_is_refused_by_name_and_the_value_is_never_on_the_page()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, "Host=a;Bogus Keyword=" + Sentinel + "-bad;Port=" + Sentinel + "-port;Password=" + Sentinel);

        Wait(() => page.Find(".ms-conn-compare-refusal").TextContent.Should()
            .Be("Npgsql does not know the keyword \"Bogus Keyword\". Npgsql did not accept the value given for \"Port\"."), page);
        page.FindAll(".ms-conn-compare-table").Should().BeEmpty("a refused string is not read at all");
        page.Markup.Should().NotContain(Sentinel);
    }

    [Fact]
    public async Task Text_npgsql_cannot_read_says_only_that_and_echoes_nothing_of_it()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, "postgresql://svc:" + Sentinel + "@db.example/app?sslmode=require");

        Wait(() => page.Find(".ms-conn-compare-refusal").TextContent.Should().Be("Npgsql could not read this connection string."), page);
        page.Markup.Should().NotContain(Sentinel).And.NotContain("postgresql://");
    }

    [Fact]
    public async Task An_empty_box_asks_for_a_string_and_asks_the_service_nothing()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);
        context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true).SetResult(0);

        page.Find(".ms-conn-compare-run").Click();

        Wait(() => page.Find(".ms-conn-compare-refusal").TextContent.Should().Be("Paste a connection string first."), page);
        context.ConnectionCompare.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Text_longer_than_the_limit_is_refused_by_the_browsers_helper_before_any_of_it_is_sent()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);
        context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true).SetResult(ConnectionCompare.PastedTooLarge);

        page.Find(".ms-conn-compare-run").Click();

        Wait(() => page.Find(".ms-conn-compare-refusal").TextContent.Should().Contain("more than 8,000 characters"), page);
        context.ConnectionCompare.Requests.Should().BeEmpty();
        context.JSInterop.Invocations["martenStudio.query.sendInChunks"].Single().Arguments.Should().Contain(6000).And.Contain(8000, "the helper is told the bound and the chunk size");
    }

    [Fact]
    public async Task A_refusal_by_the_gate_is_the_services_sentence_in_a_warning()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        context.ConnectionCompare.Answer = null;
        context.ConnectionCompare.View = ConnectionCompareView.Of(ConnectionCompareOutcome.Refused, "Your account may not see how this application connects.");
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, Candidate);

        Wait(() => page.Find(".ms-conn-compare-refusal").TextContent.Should().Be("Your account may not see how this application connects."), page);
        page.Find(".ms-conn-compare-refusal").ClassList.Should().Contain("ms-alert-warning");
        context.ConnectionCompare.Requests.Select(static x => x.Scope).Should().ContainSingle().Which.Should().Be(context.State.ActiveScope);
    }

    [Fact]
    public async Task A_service_that_fails_is_a_message_on_the_page_with_the_exceptions_type_and_nothing_it_says()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        context.ConnectionCompare.Failure = new InvalidOperationException("the text was " + Sentinel);
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, Candidate);

        Wait(() => page.Find(".ms-conn-compare-error").TextContent.Should().Be("The comparison failed (InvalidOperationException)."), page);
        page.Markup.Should().NotContain(Sentinel);
        page.Find(".ms-conn-compare-run").HasAttribute("disabled").Should().BeFalse("a failure leaves the button usable");
    }

    [Fact]
    public async Task A_browser_that_went_away_mid_hand_over_is_said_in_a_sentence()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);
        context.JSInterop.Setup<int>("martenStudio.query.sendInChunks", static _ => true).SetException(new Microsoft.JSInterop.JSDisconnectedException("circuit"));

        page.Find(".ms-conn-compare-run").Click();

        Wait(() => page.Find(".ms-conn-compare-error").TextContent.Should().Contain("browser's connection to it ended"), page);
    }

    // -------------------------------------------------------------------------------------------------
    // Nothing is kept
    // -------------------------------------------------------------------------------------------------

    private static string Buffered(IRenderedComponent<ConnectionCompare> page) =>
        ((StringBuilder) typeof(ConnectionCompare).GetField("received", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page.Instance)!).ToString();

    [Fact]
    public async Task The_component_keeps_no_copy_of_the_text_after_the_call_and_clear_drops_the_answer_and_the_box()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);
        IElement before = page.Find("#conn-compare-input");

        await PasteAsync(context, page, Candidate);
        Wait(() => page.FindAll(".ms-conn-compare-table").Should().NotBeEmpty(), page);

        Buffered(page).Should().BeEmpty("the chunks are emptied as the call returns");
        context.ConnectionCompare.Requests.Should().ContainSingle().Which.Text.Should().Be(Candidate, "the text reaches the service as the call's argument, once");

        page.Find(".ms-conn-compare-clear").Click();

        page.FindAll(".ms-conn-compare-result").Should().BeEmpty();
        page.Find("#conn-compare-input").Should().NotBeSameAs(before, "clearing replaces the textarea: it is not bound, so the browser's copy is thrown away with the element");
    }

    [Fact]
    public async Task Leaving_the_page_drops_a_half_received_text_and_the_answer()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);
        ConnectionCompare compare = page.Instance;

        await page.InvokeAsync(() => compare.BeginPaste());
        await page.InvokeAsync(() => compare.PasteChunk("Host=half;Password=" + Sentinel));
        Buffered(page).Should().Contain(Sentinel, "the guard of the next assertion is not vacuous");

        await page.InvokeAsync(() => compare.DisposeAsync().AsTask());

        Buffered(page).Should().BeEmpty();
    }

    [Fact]
    public async Task A_chunk_past_the_bound_is_not_kept()
    {
        await using PostgresStudioComponentContext context = await ContextAsync();
        IRenderedComponent<ConnectionCompare> page = Open(context);
        ConnectionCompare compare = page.Instance;

        await page.InvokeAsync(() => compare.BeginPaste());
        await page.InvokeAsync(() => compare.PasteChunk(new string('a', 8_000 + ConnectionCompare.ChunkCharacters)));
        await page.InvokeAsync(() => compare.PasteChunk("more"));

        Buffered(page).Length.Should().Be(8_000 + ConnectionCompare.ChunkCharacters, "the browser's helper is not trusted to stop at the bound");
    }

    // -------------------------------------------------------------------------------------------------
    // The sentinel test: every surface this component adds
    // -------------------------------------------------------------------------------------------------

    private sealed class CapturingProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capture(CapturingProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue(category + ": " + formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
        }
    }

    // -------------------------------------------------------------------------------------------------
    // The snapshot cannot reach the comparison: structural, because no state of the comparison exists to put in a file
    // -------------------------------------------------------------------------------------------------

    private static bool IsPastedType(Type type)
    {
        Type element = type.HasElementType ? type.GetElementType()! : type;

        return element == typeof(IConnectionCompareService)
            || element == typeof(ConnectionCompareService)
            || element == typeof(ConnectionCompareView)
            || element == typeof(ConnectionCompareRow)
            || element == typeof(ConnectionCompareCard)
            || element == typeof(PastedConnection)
            || element == typeof(PastedRejection)
            || element == typeof(PastedStrip)
            || element.Name.StartsWith("Pasted", StringComparison.Ordinal)
            || element.Name.StartsWith("ConnectionCompare", StringComparison.Ordinal);
    }

    [Fact]
    public void The_visitor_snapshot_builder_takes_nothing_of_the_comparison_in_its_constructor_or_its_fields()
    {
        Type builder = typeof(VisitorSnapshotBuilder);
        BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        IEnumerable<Type> constructed = builder.GetConstructors(all).SelectMany(static x => x.GetParameters()).Select(static x => x.ParameterType);
        IEnumerable<Type> held = builder.GetFields(all).Select(static x => x.FieldType);
        IEnumerable<Type> returned = builder.GetMethods(all).Select(static x => x.ReturnType);

        foreach (Type type in constructed.Concat(held).Concat(returned))
        {
            // Nor a generic wrapper of one (Task<ConnectionCompareView>, IReadOnlyList<PastedRejection>).
            IsPastedType(type).Should().BeFalse(type + " is, or is the comparison's, and a snapshot is built from the visitor's own gated views only");
            type.GenericTypeArguments.Should().NotContain(static x => IsPastedType(x), type + " wraps the comparison");
        }

        // The guard is not vacuous: it knows the type the builder does take for the Connection page.
        constructed.Should().Contain(typeof(IConnectionInsightService));
    }

    [Fact]
    public void The_comparison_service_holds_services_only_in_readonly_fields_and_never_the_text_or_the_answer()
    {
        FieldInfo[] fields = typeof(ConnectionCompareService).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        fields.Should().NotBeEmpty("not vacuous");

        foreach (FieldInfo field in fields)
        {
            field.IsInitOnly.Should().BeTrue(field.Name + " is mutable, so it could hold the text between calls");
            (field.FieldType == typeof(string) || field.FieldType == typeof(StringBuilder) || field.FieldType == typeof(ReadOnlyMemory<char>) || field.FieldType.IsArray)
                .Should().BeFalse(field.Name + " (" + field.FieldType + ") could hold the text");
            IsPastedType(field.FieldType).Should().BeFalse(field.Name + " holds the comparison's own types");
            (field.FieldType.IsInterface || field.FieldType == typeof(PostgresStudio.Services.StudioAuthorization) || field.FieldType == typeof(PostgresStudio.Services.Sources.StudioSourceResolver) || field.FieldType == typeof(TimeProvider))
                .Should().BeTrue(field.Name + " (" + field.FieldType + ") is not a service");
        }

        typeof(ConnectionCompareService).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().BeEmpty("a property is a field with a name: the service keeps nothing between calls");
    }

    private const string Everything =
        "Host=db.example;Database=app;Username=svc;SSL Mode=Require;Maximum Pool Size=20;Application Name=billing;"
        + "Password=" + Sentinel + "-password;PWD=" + Sentinel + "-PWD;Pwd=" + Sentinel + "-Pwd;PSW=" + Sentinel + "-PSW;SSL Password=" + Sentinel + "-ssl;"
        + "Options='-c app.secret=" + Sentinel + "-opt -c search_path=" + Sentinel + "-sp'";

    [Theory]
    [InlineData(Everything, "Compared")]
    [InlineData(Everything + ";Bogus=" + Sentinel + "-bogus", "Rejected")]
    [InlineData(Everything + ";Port=" + Sentinel + "-port", "Rejected")]
    [InlineData("postgres://svc:" + Sentinel + "-url@db.example/app", "Unreadable")]
    [InlineData(Everything + ";" + Sentinel + "-stray", "Unreadable")]
    [InlineData("Host=x;Password=abc;def" + Sentinel + "-tail=ghi", "Rejected")]
    public async Task No_sentinel_is_in_the_markup_a_log_line_at_trace_the_audit_ring_the_model_or_anything_sent_to_the_browser(string pasted, string outcome)
    {
        ConnectionCompareOutcome expected = Enum.Parse<ConnectionCompareOutcome>(outcome);

        CapturingProvider logs = new();
        await using PostgresStudioComponentContext context = await ContextAsync(services => services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs)));
        IRenderedComponent<ConnectionCompare> page = Open(context);

        await PasteAsync(context, page, pasted);
        Wait(() => page.FindAll(".ms-conn-compare-result").Should().ContainSingle(), page);
        if (expected == ConnectionCompareOutcome.Compared)
        {
            page.Find("button.ms-conn-compare-toggle").Click();
        }

        // The model, however it is dumped.
        ConnectionCompareView model = ConnectionCompareService.Compare(pasted, Facts(InEffect, Observed()), new FakeEnvironment(), Now);
        model.Outcome.Should().Be(expected);
        string dumped = JsonSerializer.Serialize(model, Dump)
            + string.Join('\n', model.Rows.Select(static x => x.Keyword + "=" + x.Pasted.Value + " " + x.Pasted.Note + " " + x.Current?.Value))
            + string.Join('\n', model.Cards.Concat(model.Notes).Select(static x => x.Card.Heading + x.Card.Text + string.Join('|', x.Card.Fixes.Select(static f => f.Code + f.Note))))
            + model.Reason;

        dumped.Should().NotContain(Sentinel);
        page.Markup.Should().NotContain(Sentinel);
        string.Join('\n', logs.Lines).Should().NotContain(Sentinel);

        context.Services.GetRequiredService<PostgresStudio.Services.StudioActionLogService>().GetLatest().Should().BeEmpty("a comparison is not audited");

        // What .NET sent to the browser: element references, a reference to itself and two numbers - and no local storage write.
        context.JSInterop.Invocations.Should().NotContain(static x => x.Identifier == "martenStudio.prefs.set");
        context.JSInterop.Invocations.SelectMany(static x => x.Arguments).Select(static x => x?.ToString() ?? string.Empty).Should().NotContain(static x => x.Contains(Sentinel, StringComparison.Ordinal));

        if (expected == ConnectionCompareOutcome.Compared)
        {
            page.Find("tr#conn-cmp-row-password .ms-conn-value:nth-child(3)").TextContent.Should().Contain("set").And.NotContain(Sentinel);
            page.Find("tr#conn-cmp-row-options").TextContent.Should().Contain("app.secret=(value withheld)");
            page.Find("tr#conn-cmp-row-application-name").TextContent.Should().Contain("billing", "the dump is not vacuous: a value that may be shown is on the page");
        }
    }
}
