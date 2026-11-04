using Bunit;

using MartenStudio.Services;

using PostgresStudio.Components.Layout;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-SCOPE-1 S1: a sidebar entry can be shown only for some scope states (<c>StudioNavItem.IsShownInScope</c>), and MartenStudio's
/// Stores entry is shown only while the visitor may see two or more stores - the header's store picker's own test.
/// </summary>
public class NavMenuScopeTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stores_is_absent_with_one_store()
    {
        await using StudioComponentContext context = new();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<NavMenu> nav = context.Render<NavMenu>();

        nav.FindAll("a.ms-nav-link .ms-nav-link-text").Select(static x => x.TextContent.Trim()).Should().NotContain("Stores");
    }

    [Fact]
    public async Task Stores_is_present_with_two_stores_directly_after_Overview_and_carries_the_scope()
    {
        await using StudioComponentContext context = new();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithStore("invoicing", "Invoicing", databaseIdentities: "localhost.invoicing");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<NavMenu> nav = context.Render<NavMenu>();

        string[] labels = [.. nav.FindAll("a.ms-nav-link .ms-nav-link-text").Select(static x => x.TextContent.Trim())];
        labels.Take(3).Should().Equal("Overview", "Stores", "Documents");

        nav.FindAll("a.ms-nav-link").Single(static x => x.QuerySelector(".ms-nav-link-text")!.TextContent.Trim() == "Stores")
            .GetAttribute("href").Should().StartWith("stores?").And.Contain("store=default");
    }

    [Fact]
    public async Task An_unavailable_store_still_counts_as_a_store_the_visitor_may_see()
    {
        await using StudioComponentContext context = new();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithUnavailableStore("invoicing", "Invoicing", "no connection string was configured");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<NavMenu> nav = context.Render<NavMenu>();

        nav.FindAll("a.ms-nav-link .ms-nav-link-text").Select(static x => x.TextContent.Trim()).Should().Contain("Stores",
            "the Stores page is where an unbuildable store is explained, and the header's picker lists it too");
    }

    [Fact]
    public async Task One_listed_store_and_one_whose_databases_could_not_be_listed_are_two_stores()
    {
        await using StudioComponentContext context = new();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithUnlistableStore("master", "Master table store");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<NavMenu> nav = context.Render<NavMenu>();

        nav.FindAll("a.ms-nav-link .ms-nav-link-text").Select(static x => x.TextContent.Trim()).Should().Contain("Stores");
    }

    [Fact]
    public void The_stores_entry_carries_a_scope_condition_and_the_overview_does_not()
    {
        StudioNavItem[] items = [.. MartenStudioDescriptor.Instance.Navigation.SelectMany(static x => x.Items)];

        items.Single(static x => x.Label == "Stores").IsShownInScope.Should().NotBeNull();
        items.Single(static x => x.Label == "Overview").IsShownInScope.Should().BeNull();
    }
}
