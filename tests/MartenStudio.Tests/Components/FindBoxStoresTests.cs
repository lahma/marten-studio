using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Layout;
using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-SCOPE-3: the header's Find box beside other stores. Nothing found offers the stores the visitor may select, and a search there
/// is the same Find against that store's first database - whose hits are links the visitor clicks, never pushes, so the shell follows
/// them the way it follows any clicked link.
/// </summary>
public class FindBoxStoresTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private sealed class Recording : IFindContributor
    {
        public List<(StudioScope Scope, string Value)> Calls { get; } = [];

        public Func<StudioScope, FindContribution> Answer { get; set; } = static _ => new FindContribution([], "documents", []);

        public int Order => 10;

        public string Noun => "Documents";

        public Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
        {
            Calls.Add((scope, query.Value));
            return Task.FromResult(Answer(scope));
        }
    }

    private static StudioComponentContext Context(Recording contributor) => new(services =>
    {
        services.AddSingleton<IFindContributor>(contributor);
    });

    private static async Task<StudioComponentContext> ReadyAsync(Recording contributor, Action<FakeStudioScopeCatalog> stores)
    {
        StudioComponentContext context = Context(contributor);
        stores(context.Catalog);
        context.JSInterop.Setup<bool>("martenStudio.find.enhance", _ => true).SetResult(true);
        await context.State.EnsureInitializedAsync(Token);
        return context;
    }

    private static void TwoStores(FakeStudioScopeCatalog catalog) => catalog
        .WithStore("default", "Default", DatabaseCardinality.Single, "primary")
        .WithStore("invoicing", "Invoicing", DatabaseCardinality.Single, "billing")
        .WithStore("audit", "Audit", DatabaseCardinality.Single, "trail")
        .WithUnavailableStore("broken", "Broken", "no connection string");

    [Fact]
    public async Task Nothing_found_offers_the_other_stores_the_visitor_may_select_and_not_the_one_just_searched_or_one_that_will_not_build()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, TwoStores);
        IRenderedComponent<FindBox> box = context.Render<FindBox>();

        await box.InvokeAsync(() => box.Instance.FindAsync("nope"));

        box.Find(".ms-find-status").TextContent.Should().Be("Nothing found");
        box.FindAll(".ms-find-other-store").Select(static x => x.TextContent.Trim()).Should().Equal("Invoicing", "Audit");
        box.Find(".ms-find-other-label").TextContent.Should().Be("Search in");
    }

    [Fact]
    public async Task Searching_in_another_store_runs_the_same_find_against_that_stores_first_database_and_changes_no_scope()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, TwoStores);
        StudioScope before = context.State.ActiveScope!;
        string uriBefore = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;
        IRenderedComponent<FindBox> box = context.Render<FindBox>();
        await box.InvokeAsync(() => box.Instance.FindAsync("INV-77"));

        await box.InvokeAsync(() => box.FindAll(".ms-find-other-store").Single(static x => x.TextContent.Trim() == "Invoicing").Click());

        contributor.Calls.Should().HaveCount(2);
        contributor.Calls[1].Should().Be((new StudioScope("invoicing", "billing", null), "INV-77"), "the value the visitor typed, in the first database of the store they chose that the catalog lists for them");
        context.State.ActiveScope.Should().Be(before, "Find never moves the scope");
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.Should().Be(uriBefore);
    }

    [Fact]
    public async Task A_hit_in_another_store_is_a_link_that_is_never_opened_by_itself()
    {
        StudioScope invoicing = new("invoicing", "billing", null);
        Recording contributor = new()
        {
            Answer = scope => scope.StoreKey == "invoicing"
                ? new FindContribution([new FindHit("Document", "invoice INV-77", "/marten/documents/invoice/doc?id=INV-77&store=invoicing&db=billing", invoicing, "invoice\u001fINV-77")], "1 document type", [])
                : new FindContribution([], "documents", []),
        };
        await using StudioComponentContext context = await ReadyAsync(contributor, TwoStores);
        string uriBefore = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;
        IRenderedComponent<FindBox> box = context.Render<FindBox>();
        await box.InvokeAsync(() => box.Instance.FindAsync("INV-77"));

        await box.InvokeAsync(() => box.FindAll(".ms-find-other-store").Single(static x => x.TextContent.Trim() == "Invoicing").Click());

        box.Find("a.ms-find-hit").GetAttribute("href").Should().Be("/marten/documents/invoice/doc?id=INV-77&store=invoicing&db=billing");
        context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri.Should().Be(uriBefore, "a pushed address would be built from a scope the shell is not on");
        context.State.ActiveScope!.StoreKey.Should().Be("default");
    }

    [Fact]
    public async Task Searching_in_another_store_picks_the_first_database_the_catalog_admits_not_the_first_the_store_has()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, catalog => catalog
            .WithStore("default", "Default", DatabaseCardinality.Single, "primary")
            .WithStore("invoicing", "Invoicing", DatabaseCardinality.StaticMultiple, "archive", "billing", "reporting"));

        // The policy-filtered listing the header offers: the store's first database is not among the ones this visitor may see.
        context.Catalog.Databases["invoicing"].RemoveAt(0);
        IRenderedComponent<FindBox> box = context.Render<FindBox>();
        await box.InvokeAsync(() => box.Instance.FindAsync("INV-77"));

        await box.InvokeAsync(() => box.FindAll(".ms-find-other-store").Single().Click());

        contributor.Calls[^1].Scope.Should().Be(new StudioScope("invoicing", "billing", null));
    }

    [Fact]
    public async Task A_store_with_no_database_the_visitor_may_see_has_nothing_to_search_in_and_nothing_is_asked()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, catalog => catalog
            .WithStore("default", "Default", DatabaseCardinality.Single, "primary")
            .WithStore("invoicing", "Invoicing", DatabaseCardinality.Single, "billing"));
        context.Catalog.Databases["invoicing"].Clear();
        IRenderedComponent<FindBox> box = context.Render<FindBox>();
        await box.InvokeAsync(() => box.Instance.FindAsync("INV-77"));
        int asked = contributor.Calls.Count;

        await box.InvokeAsync(() => box.FindAll(".ms-find-other-store").Single().Click());

        contributor.Calls.Should().HaveCount(asked);
        box.Find(".ms-find-status").TextContent.Should().Be("Nothing found");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_store_whose_databases_could_not_be_listed_is_a_failure_the_box_says_so_about_not_nothing_found(bool policyThrows)
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, TwoStores);
        (policyThrows ? context.Catalog.ListingThrows : context.Catalog.EnumerationFails).Add("invoicing");
        IRenderedComponent<FindBox> box = context.Render<FindBox>();
        await box.InvokeAsync(() => box.Instance.FindAsync("INV-77"));
        int asked = contributor.Calls.Count;

        await box.InvokeAsync(() => box.FindAll(".ms-find-other-store").Single(static x => x.TextContent.Trim() == "Invoicing").Click());

        box.Find(".ms-find-error").TextContent.Should().Be("Find could not search Invoicing. Try again.");
        box.Find(".ms-find-status").TextContent.Should().BeEmpty("it is not 'Nothing found'");
        contributor.Calls.Should().HaveCount(asked);
    }

    [Fact]
    public async Task Marten_Studios_empty_message_says_all_tenants_for_a_store_with_a_tenant_selector_and_the_picker_is_a_store()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, catalog =>
        {
            TwoStores(catalog);
            catalog.WithTenants("default", new TenantList(["acme"], false, TenantListSource.Configured));
        });
        await context.State.RefreshAsync(Token);
        IRenderedComponent<FindBox> box = context.Render<FindBox>();

        await box.InvokeAsync(() => box.Instance.FindAsync("nope"));

        box.Find("[data-ms-find-panel]").TextContent.Should().Contain("Nothing in Default › primary (all tenants) matched \"nope\".");
        box.Find(".ms-find-other").GetAttribute("aria-label").Should().Be("Search in another store");
    }

    [Fact]
    public async Task A_store_with_no_tenant_selector_does_not_say_all_tenants_in_either_studio()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, TwoStores);
        IRenderedComponent<FindBox> box = context.Render<FindBox>();

        await box.InvokeAsync(() => box.Instance.FindAsync("nope"));

        string panel = box.Find("[data-ms-find-panel]").TextContent;
        panel.Should().Contain("Nothing in Default › primary matched \"nope\".");
        panel.Should().NotContain("tenants");
    }

    [Fact]
    public async Task At_most_eight_other_stores_are_offered()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, catalog =>
        {
            catalog.WithStore("default", "Default", DatabaseCardinality.Single, "primary");
            for (int i = 1; i <= 12; i++)
            {
                catalog.WithStore("s" + i, "Store " + i, DatabaseCardinality.Single, "db" + i);
            }
        });
        IRenderedComponent<FindBox> box = context.Render<FindBox>();

        await box.InvokeAsync(() => box.Instance.FindAsync("nope"));

        box.FindAll(".ms-find-other-store").Should().HaveCount(8);
    }

    [Fact]
    public async Task A_single_store_has_nobody_to_offer()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, static catalog => catalog.WithStore("default", "Default", DatabaseCardinality.Single, "primary"));
        IRenderedComponent<FindBox> box = context.Render<FindBox>();

        await box.InvokeAsync(() => box.Instance.FindAsync("nope"));

        box.FindAll(".ms-find-other").Should().BeEmpty();
    }

    [Fact]
    public async Task What_was_found_does_not_outlive_the_scope_it_was_found_in()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, static catalog => catalog
            .WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "primary", "replica"));
        IRenderedComponent<FindBox> box = context.Render<FindBox>();
        await box.InvokeAsync(() => box.Instance.FindAsync("nope"));
        box.FindAll("[data-ms-find-panel]").Should().ContainSingle();

        await box.InvokeAsync(() => context.State.SetScopeAsync("default", "replica", null, Token));

        box.FindAll("[data-ms-find-panel]").Should().BeEmpty("what was found was found in another database");
        box.Find(".ms-find-status").TextContent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_box_is_drawn_in_Marten_Studios_header_with_a_store_in_scope()
    {
        Recording contributor = new();
        await using StudioComponentContext context = await ReadyAsync(contributor, TwoStores);

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, static builder => builder.AddContent(0, string.Empty)));

        layout.WaitForAssertion(() => layout.Find(".ms-header > .ms-header-find input#ms-find-input").Should().NotBeNull());
    }
}
