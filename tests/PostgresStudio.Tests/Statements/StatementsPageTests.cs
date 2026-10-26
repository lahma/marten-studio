using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Statements;
using PostgresStudio.Tests.Components;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

using StatementsPage = PostgresStudio.Components.Pages.Statements.Statements;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// Operations › Statements rendered over a fake service (W5-Q1): its gate states - the capability's notice naming the option,
/// a policy's sentence, the extension's status - the withheld sentence in place of a text, the top N and the group view, "rank
/// by storage reads" only where Aurora's are read, every table in a scroll region, and no drill-in.
/// </summary>
public class StatementsPageTests
{
    private sealed class FakeStatements(StatementsView view) : IStatementsService
    {
        public List<StudioScope> Reads { get; } = [];

        public List<StudioScope> Polls { get; } = [];

        public Task<StatementsView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Reads.Add(scope);
            return Task.FromResult(view);
        }

        public Task<StatementsView> GetLatestAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Polls.Add(scope);
            return Task.FromResult(view);
        }
    }

    private static async Task<(PostgresStudioComponentContext Context, FakeStatements Service)> ContextAsync(StatementsView view)
    {
        FakeStatements service = new(view);
        PostgresStudioComponentContext context = new(services => services.AddSingleton<IStatementsService>(service));
        context.WithAllCapabilities();
        return (await context.ReadyAsync(), service);
    }

    private static StatementsView Shown(StatementFacts facts) =>
        new StatementsService(
                new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
                null!, null!, null!, null!, null!, new StudioLogThrottle(TimeProvider.System), NullLogger<StatementsService>.Instance)
            .Show(facts, AllSchemas()) with
        {
            StudioCost = new StudioCost(TimeSpan.FromMilliseconds(1_500), 12, TimeSpan.FromMinutes(5)),
        };

    /// <summary>A visitor's gate that withholds nothing: <c>public</c> and <c>hr</c> both admitted.</summary>
    private static DatabaseGateRead AllSchemas() => new(
        new DatabaseGate(
            ["public"],
            [new CatalogSchema("public", true, false), new CatalogSchema("hr", true, false)],
            ["public", "hr"],
            capabilityEnabled: true,
            readOnly: false,
            authorized: true,
            new DatabaseObjectClassifier([])),
        DatabaseRefusal.None,
        null);

    private static StatementFacts ThreeStatements(bool aurora = false) => Facts(Read(
        1_000,
        Statement(1, "select * from public.orders where id = $1", 100, 600, [Orders], storage: aurora ? 10 : null),
        Statement(2, "select s.amount from hr.salaries s", 10, 300, [Salaries], storage: aurora ? 900 : null),
        Statement(3, "copy (select 'x') to stdout", 1, 100)));

    [Fact]
    public async Task With_the_capability_off_the_page_names_the_option_and_shows_no_statement()
    {
        (PostgresStudioComponentContext context, FakeStatements service) = await ContextAsync(
            StatementsView.Refused(DatabaseRefusal.CapabilityOff, PostgresStudioText.Instance.StatementsCapabilityDenial));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-capability-disabled").Should().ContainSingle());

        page.Find(".ms-capability-disabled").TextContent.Should().Contain("Statements is not enabled")
            .And.Contain("PostgresStudioOptions.Capabilities.ViewDatabaseActivity");
        page.FindAll("table").Should().BeEmpty();
        service.Reads.Should().ContainSingle();
    }

    /// <summary>
    /// W5-Q1-fix F6: the load and Refresh may run a statement sweep (GetAsync); the page's polls only look at the last one
    /// (GetLatestAsync), so a tab left open never plans T02's statements on nobody's behalf.
    /// </summary>
    [Fact]
    public async Task The_load_and_Refresh_read_and_the_polls_only_look()
    {
        (PostgresStudioComponentContext context, FakeStatements service) = await ContextAsync(Shown(ThreeStatements()));
        await using PostgresStudioComponentContext owned = context;
        context.Options.RefreshInterval = TimeSpan.FromMilliseconds(20);
        context.Options.HealthInterval = TimeSpan.FromMilliseconds(20);

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => service.Polls.Should().HaveCountGreaterThanOrEqualTo(2), TimeSpan.FromSeconds(10));
        service.Reads.Should().ContainSingle("only the load read; every interval since only looked");

        await page.InvokeAsync(() => page.Find(".ms-stmts-actions button").Click());
        page.WaitForAssertion(() => service.Reads.Should().HaveCount(2, "Refresh is a read, as the load is"));
    }

    [Fact]
    public async Task A_policys_refusal_is_its_own_sentence()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(
            StatementsView.Refused(DatabaseRefusal.WritePolicy, PostgresStudioText.Instance.StatementsWritePolicyDenial));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.Find(".ms-stmts-refusal").TextContent.Should().Contain("refused ViewDatabaseActivity"));
        page.FindAll("table").Should().BeEmpty();
    }

    [Fact]
    public async Task An_extension_that_cannot_be_read_is_its_status_naming_who_can_fix_it()
    {
        StatementFacts facts = StatementFacts.Build(
            "default", "default", Now,
            new StatementReadOutcome(StatementReadState.NotPreloaded, Pg17, null) { Platform = PostgresStudio.Services.Health.PostgresPlatform.SelfHosted },
            null, PostgresStudio.Services.Health.HealthHistoryView.Empty, null, StatementsTestFacts.Tables, null);
        (PostgresStudioComponentContext context, _) = await ContextAsync(Shown(facts));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.Find(".ms-stmts-status").TextContent.Should()
            .Contain("its library is not loaded").And.Contain("postgresql.conf"));
        page.FindAll("table").Should().BeEmpty();
        page.Find(".ms-stmts-studio").TextContent.Should().Contain("The studio itself: 1.50 s of database time over 12 connections");
    }

    [Fact]
    public async Task The_statements_are_drawn_in_a_scroll_region_with_their_groups_and_the_withheld_sentence()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(Shown(ThreeStatements()));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-stmts-table tbody tr").Should().HaveCount(3));

        page.Find(".ms-page-title").TextContent.Should().Be("Statements");
        page.FindAll("table.ms-table").Should().OnlyContain(static x => x.ParentElement != null && x.ParentElement.GetAttribute("role") == "region");

        IReadOnlyList<IElement> rows = page.FindAll(".ms-stmts-table tbody tr");
        rows[0].QuerySelector(".ms-stmts-group")!.TextContent.Should().Be("select · public.orders");
        rows[0].QuerySelector(".ms-stmts-share-value")!.TextContent.Should().Be("60%");
        rows[0].QuerySelector(".ms-stmts-bar")!.GetAttribute("style").Should().Be("--ms-stmts-share: 60%", "the share is a bar as well as a number");
        rows[0].QuerySelector("pre")!.TextContent.Should().Be("select * from public.orders where id = $1");

        IElement withheld = rows.Single(static x => x.TextContent.Contains("queryid 3", StringComparison.Ordinal));
        withheld.QuerySelector("pre").Should().BeNull("a withheld text is never drawn");
        withheld.QuerySelector(".ms-stmts-note")!.TextContent.Should().Be(StatementReads.WithheldUtility);

        page.Find(".ms-stmts-window").TextContent.Should().StartWith("Each statement's totals since its counters began");
        page.Find(".ms-stmts-caveat").TextContent.Should().Contain("as the data source's own Postgres role");
        page.Find(".ms-stmts-studio").TextContent.Should().Contain("With no SqlConsoleRole set, every statement the studio runs here runs as");

        page.FindAll("a").Should().NotContain(static x => (x.GetAttribute("href") ?? string.Empty).Contains("statements/statement", StringComparison.Ordinal),
            "the drill-in is 0.6's: no statement links to a detail page");
        page.FindAll(".ms-stmts-rank-storage").Should().BeEmpty("storage reads are Aurora's, and this is not Aurora");
    }

    [Fact]
    public async Task The_group_view_sums_the_top_statements_by_group()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(Shown(ThreeStatements()));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-stmts-table tbody tr").Should().HaveCount(3));

        page.Find(".ms-stmts-view-groups").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-stmts-groups tbody tr").Should().HaveCount(3));
        page.FindAll(".ms-stmts-groups .ms-stmts-group").Select(static x => x.TextContent)
            .Should().Equal("select · public.orders", "select · hr.salaries", "copy · other");
    }

    [Fact]
    public async Task On_Aurora_the_statements_can_be_ranked_by_storage_reads()
    {
        (PostgresStudioComponentContext context, _) = await ContextAsync(Shown(ThreeStatements(aurora: true)));
        await using PostgresStudioComponentContext owned = context;

        var page = context.Render<StatementsPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-stmts-table tbody tr").Should().HaveCount(3));

        page.Find(".ms-stmts-rank-storage").Click();

        page.WaitForAssertion(() => page.FindAll(".ms-stmts-table tbody tr").Should().HaveCount(2, "the statement with no storage reads is not among them"));
        page.FindAll(".ms-stmts-table tbody tr")[0].TextContent.Should().Contain("queryid 2", "900 storage reads rank above 10");
        page.FindAll(".ms-stmts-table th").Select(static x => x.TextContent.Trim()).Should().Contain("Storage reads");
    }
}
