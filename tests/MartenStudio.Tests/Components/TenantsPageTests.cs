using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Services.Tenants;
using MartenStudio.Tests.Database;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-SCOPE-2 T12: the Tenants page reads nothing while it is prerendered and once when the circuit renders it, draws what the service
/// answers - the policy and 200-tenant sentences, the volumes' note, the paging - and links each row to the pages that show it.
/// </summary>
public class TenantsPageTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static async Task<(StudioComponentContext Context, FakeTenantOverviewService Data)> StartAsync(
        Func<StudioScope, TenantOverviewRequest, TenantOverview> answer,
        string query = "")
    {
        FakeTenantOverviewService data = new() { Answer = answer };
        StudioComponentContext context = new(services => services.AddSingleton<ITenantOverviewService>(data));
        await context.ReadyAsync();

        if (query.Length > 0)
        {
            context.Navigate("marten/tenants" + query);
        }

        return (context, data);
    }

    [Fact]
    public async Task Nothing_is_read_while_prerendered_and_the_circuit_reads_once()
    {
        (StudioComponentContext context, FakeTenantOverviewService data) = await StartAsync(static (_, _) => new TenantOverview { Rows = [FakeTenantOverviewService.Row("acme")] });
        await using StudioComponentContext owned = context;

        string html = await DatabasePagePrerenderTests.StaticAsync<Tenants>(context);

        html.Should().Contain("Loading tenants");
        data.Asks.Should().BeEmpty("a prerender reads nothing");

        IRenderedComponent<Tenants> page = context.Render<Tenants>();

        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().ContainSingle());
        data.Asks.Should().ContainSingle("the circuit's first render reads the page, once");
    }

    [Fact]
    public async Task The_rows_show_the_columns_the_service_gave_and_link_to_the_pages_that_show_them()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => new TenantOverview { Rows = [FakeTenantOverviewService.Row("acme")] });
        await using StudioComponentContext owned = context;

        IRenderedComponent<Tenants> page = context.Render<Tenants>();
        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().ContainSingle());

        string[] cells = [.. page.FindAll("tr.ms-table-row td").Take(7).Select(static x => string.Join(' ', x.TextContent.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries)))];
        cells.Should().Equal("acme", "localhost.marten", "Shared tables", "~1,200", "~9,400", "~300", "#12");

        page.FindAll("tr.ms-table-row td")[4].GetAttribute("title").Should().Be("title");
        page.FindAll("th").Select(static x => x.TextContent.Trim()).Should().NotContain("Projections", "no row has a projections cell");

        string[] links = [.. page.FindAll("tr.ms-table-row .ms-tenants-open a").Select(static x => x.GetAttribute("href")!)];
        links.Should().Equal(
            "documents?store=default&db=localhost.marten&tenant=acme",
            "events/streams?store=default&db=localhost.marten&tenant=acme",
            "events/feed?store=default&db=localhost.marten&tenant=acme",
            "projections?store=default&db=localhost.marten&tenant=acme");
    }

    [Fact]
    public async Task Select_on_a_row_moves_the_scope_to_that_tenant()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => new TenantOverview { Rows = [FakeTenantOverviewService.Row("acme")] });
        await using StudioComponentContext owned = context;
        context.Catalog.WithTenants("default", new TenantList(["acme"], false, TenantListSource.Configured));
        await context.State.RefreshAsync(Token);

        IRenderedComponent<Tenants> page = context.Render<Tenants>();
        page.WaitForAssertion(() => page.FindAll(".ms-tenants-select").Should().ContainSingle());

        page.Find(".ms-tenants-select").Click();

        page.WaitForAssertion(() => context.State.ActiveScope!.TenantId.Should().Be("acme"));
    }

    [Fact]
    public async Task Under_a_policy_the_one_sentence_is_said_and_never_the_more_than_two_hundred_one()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => new TenantOverview
        {
            Rows = [FakeTenantOverviewService.Row("acme", database: string.Empty, TenantPlacement.NotPlaced)],
            UnderPolicy = true,
            ListTruncated = true,
            IdsWithoutDatabases = true,
        });
        await using StudioComponentContext owned = context;

        IRenderedComponent<Tenants> page = context.Render<Tenants>();
        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().ContainSingle());

        page.Find(".ms-tenants-policy").TextContent.Should().Be("These are the tenants you may address; there may be others. To open one that is not listed, choose Other… in the header's tenant box.");
        page.FindAll(".ms-tenants-truncated").Should().BeEmpty("a visitor a policy filters is not told whether discovery found more than 200 tenants");
        page.Find(".ms-tenants-unplaced").TextContent.Should().Be("KnownTenantIds names tenants without their databases; pick a database in the header to read one.");
        page.FindAll(".ms-tenants-open a").Should().BeEmpty("a tenant placed in no database has no page to open");
    }

    [Fact]
    public async Task Without_a_policy_the_more_than_two_hundred_sentence_is_said_as_it_always_was()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => new TenantOverview
        {
            Rows = [FakeTenantOverviewService.Row("acme")],
            ListTruncated = true,
        });
        await using StudioComponentContext owned = context;

        IRenderedComponent<Tenants> page = context.Render<Tenants>();
        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().ContainSingle());

        page.Find(".ms-tenants-truncated").TextContent.Should().Be("More than 200 tenants: the first 200 by id are listed. Type any other in the header's tenant box.");
        page.FindAll(".ms-tenants-policy").Should().BeEmpty();
    }

    [Fact]
    public async Task Under_a_policy_the_notes_are_the_same_whether_discovery_found_few_tenants_or_was_cut_off()
    {
        (StudioComponentContext few, _) = await StartAsync(static (_, _) => new TenantOverview { Rows = [FakeTenantOverviewService.Row("acme")], UnderPolicy = true, ListTruncated = false });
        (StudioComponentContext many, _) = await StartAsync(static (_, _) => new TenantOverview { Rows = [FakeTenantOverviewService.Row("acme")], UnderPolicy = true, ListTruncated = true });
        await using StudioComponentContext ownedFew = few;
        await using StudioComponentContext ownedMany = many;

        IRenderedComponent<Tenants> fewPage = few.Render<Tenants>();
        IRenderedComponent<Tenants> manyPage = many.Render<Tenants>();
        fewPage.WaitForAssertion(() => fewPage.FindAll("tr.ms-table-row").Should().ContainSingle());
        manyPage.WaitForAssertion(() => manyPage.FindAll("tr.ms-table-row").Should().ContainSingle());

        manyPage.Find(".ms-tenants-notes").TextContent.Should().Be(fewPage.Find(".ms-tenants-notes").TextContent);
    }

    [Fact]
    public async Task Below_the_activity_gate_the_note_names_the_option()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => new TenantOverview
        {
            Rows = [FakeTenantOverviewService.Row("acme")],
            VolumesShown = false,
            VolumesWithheldReason = "Row estimates and last events compare tenants with one another, so they are shown with ViewDatabaseActivity: MartenStudioOptions.Capabilities.ViewDatabaseActivity.",
        });
        await using StudioComponentContext owned = context;

        IRenderedComponent<Tenants> page = context.Render<Tenants>();

        page.WaitForAssertion(() => page.Find(".ms-tenants-withheld").TextContent.Should().Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity"));
    }

    [Fact]
    public async Task A_store_that_keeps_no_tenants_says_so()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => new TenantOverview { KeepsNoTenants = true });
        await using StudioComponentContext owned = context;

        IRenderedComponent<Tenants> page = context.Render<Tenants>();

        page.WaitForAssertion(() => page.Find(".ms-empty-description").TextContent.Should().Contain("No document type is conjoined").And.Contain("neither is the event store"));
    }

    [Fact]
    public async Task A_refused_scope_is_the_not_authorized_frame()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => TenantOverview.Refused(TenantOverviewRefusal.StorePolicy, "no"));
        await using StudioComponentContext owned = context;

        IRenderedComponent<Tenants> page = context.Render<Tenants>();

        page.WaitForAssertion(() => page.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
    }

    [Fact]
    public async Task Paging_and_the_prefix_travel_in_the_address_and_the_pager_links_keep_both()
    {
        (StudioComponentContext context, FakeTenantOverviewService data) = await StartAsync(
            static (_, request) => new TenantOverview
            {
                Rows = [FakeTenantOverviewService.Row("t" + (request.Page ?? 0))],
                Page = request.Page ?? 0,
                HasNext = (request.Page ?? 0) < 2,
            },
            query: "?filter=t&page=2");
        await using StudioComponentContext owned = context;

        IRenderedComponent<Tenants> page = context.Render<Tenants>();
        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().ContainSingle());

        data.Asks.Should().ContainSingle().Which.Request.Should().BeEquivalentTo(new TenantOverviewRequest { Filter = "t", Page = 1 }, "the address is one-based, the request zero-based");
        page.Find(".ms-tenants-previous").GetAttribute("href").Should().Contain("filter=t").And.Contain("page=1");
        page.Find(".ms-tenants-next").GetAttribute("href").Should().Contain("filter=t").And.Contain("page=3");
    }

    [Fact]
    public async Task The_row_for_the_scopes_tenant_is_highlighted()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => new TenantOverview
        {
            Rows = [FakeTenantOverviewService.Row("acme"), FakeTenantOverviewService.Row("globex")],
        });
        await using StudioComponentContext owned = context;
        context.Catalog.WithTenants("default", new TenantList(["acme", "globex"], false, TenantListSource.Configured));
        await context.State.RefreshAsync(Token);
        await context.State.SetScopeAsync("default", "localhost.marten", "globex", Token);

        IRenderedComponent<Tenants> page = context.Render<Tenants>();
        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().HaveCount(2));

        page.FindAll("tr.ms-tenants-selected").Should().ContainSingle().Which.TextContent.Should().Contain("globex");
    }

    [Fact]
    public async Task A_failure_to_read_is_shown_with_a_retry_and_without_postgres_own_words()
    {
        (StudioComponentContext context, FakeTenantOverviewService data) = await StartAsync(static (_, _) => new TenantOverview());
        await using StudioComponentContext owned = context;
        data.Failure = new Npgsql.PostgresException("relation secret_schema.salaries does not exist", "ERROR", "ERROR", "42P01");

        IRenderedComponent<Tenants> page = context.Render<Tenants>();

        page.WaitForAssertion(() => page.Find(".ms-error-alert").TextContent.Should().Contain("Postgres refused a read (42P01)").And.NotContain("secret_schema"));
    }

    [Fact]
    public async Task Each_row_has_a_key_so_a_tenant_in_two_databases_renders_twice()
    {
        (StudioComponentContext context, _) = await StartAsync(static (_, _) => new TenantOverview
        {
            Rows = [FakeTenantOverviewService.Row("acme", "db-a"), FakeTenantOverviewService.Row("acme", "db-b")],
        });
        await using StudioComponentContext owned = context;

        IRenderedComponent<Tenants> page = context.Render<Tenants>();

        page.WaitForAssertion(() => page.FindAll("tr.ms-table-row").Should().HaveCount(2));
    }
}
