using Bunit;

using MartenStudio.Components.Pages;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-SCOPE-1 S2 and S3: with two or more stores the Overview opens with a one-line strip built from the header's own listing
/// (so it does no I/O), and its bottom region is the selected store's card alone - read with that store's key, and read again
/// when the visitor moves to another store.
/// </summary>
public class OverviewStoresStripTests
{
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_two_stores_the_overview_opens_with_a_strip_naming_each_and_linking_to_all_stores()
    {
        await using StudioComponentContext context = await TwoStoresAsync();

        IRenderedComponent<Overview> page = context.Render<Overview>();

        page.FindAll(".ms-stores-strip-link").Select(static x => x.TextContent.Trim()).Should().Equal("Default", "Other");

        page.FindAll(".ms-stores-strip-link").Select(static x => x.GetAttribute("href")).Should().Equal("?store=default", "?store=other");
        page.Find(".ms-stores-strip-all").GetAttribute("href").Should().Be("stores");
        page.Find(".ms-stores-strip-all").TextContent.Trim().Should().Be("All stores");

        page.FindAll(".ms-stores-strip-link").Single(static x => x.GetAttribute("aria-current") == "true").TextContent.Trim()
            .Should().Be("Default", "the strip marks the store the scope is on");
    }

    [Fact]
    public async Task A_store_that_would_not_build_is_marked_in_the_strip_and_still_links_to_its_overview()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithStore();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithUnavailableStore("broken", "Broken", "no connection string was configured");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<Overview> page = context.Render<Overview>();

        page.FindAll(".ms-stores-strip-link").Select(static x => x.TextContent.Trim()).Should().Equal("Default", "Broken (unavailable)");
        page.FindAll(".ms-stores-strip-link")[1].GetAttribute("href").Should().Be("?store=broken");
    }

    [Fact]
    public async Task With_one_store_there_is_no_strip()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithStore();
        await context.ReadyAsync();

        IRenderedComponent<Overview> page = context.Render<Overview>();

        page.FindAll(".ms-stores-strip").Should().BeEmpty();
    }

    [Fact]
    public async Task The_strip_asks_the_store_info_service_for_nothing()
    {
        await using StudioComponentContext context = await TwoStoresAsync();

        // The premise is the strip's source: the header's listing, not the overview service.
        context.StoreInfo.Gate = new TaskCompletionSource();
        IRenderedComponent<Overview> page = context.Render<Overview>();

        page.FindAll(".ms-stores-strip-link").Should().HaveCount(2, "drawn while the overview's own read is still in flight");
        context.StoreInfo.Gate.SetResult();
    }

    [Fact]
    public async Task The_bottom_region_is_the_selected_stores_card_alone()
    {
        await using StudioComponentContext context = await TwoStoresAsync();

        IRenderedComponent<Overview> page = context.Render<Overview>();

        page.WaitForAssertion(() => page.FindAll(".ms-overview-store").Should().ContainSingle());
        page.Find(".ms-overview-store .ms-section-key").TextContent.Trim().Should().Be("default");
        context.StoreInfo.Keys.Should().Equal("default");
    }

    [Fact]
    public async Task Moving_to_another_store_reads_that_stores_card()
    {
        await using StudioComponentContext context = await TwoStoresAsync();

        IRenderedComponent<Overview> page = context.Render<Overview>();
        page.WaitForAssertion(() => context.StoreInfo.Keys.Should().Equal("default"));

        await page.InvokeAsync(() => context.State.SetScopeAsync("other", string.Empty, tenantId: null, Token));

        page.WaitForAssertion(() =>
        {
            context.StoreInfo.Keys.Should().Equal("default", "other");
            page.Find(".ms-overview-store .ms-section-key").TextContent.Trim().Should().Be("other");
        });
    }

    [Fact]
    public async Task Moving_between_the_databases_of_one_store_reads_no_store_card_again()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithStore("default", "Default");
        context.Catalog
            .WithStore("default", "Default", JasperFx.Descriptors.DatabaseCardinality.StaticMultiple, "db-a", "db-b")
            .WithStore("other", "Other", databaseIdentities: "localhost.other");
        await context.State.EnsureInitializedAsync(Token);

        IRenderedComponent<Overview> page = context.Render<Overview>();
        page.WaitForAssertion(() => page.FindAll(".ms-overview-store").Should().ContainSingle());

        await page.InvokeAsync(() => context.State.SetScopeAsync("default", "db-b", tenantId: null, Token));

        context.State.ActiveScope!.DatabaseId.Should().Be("db-b");
        context.StoreInfo.Keys.Should().Equal("default");
    }

    private static async Task<StudioComponentContext> TwoStoresAsync()
    {
        StudioComponentContext context = new();
        context.StoreInfo
            .WithStore("default", "Default")
            .WithStore("other", "Other", databaseIdentity: "localhost.other");
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");
        context.Catalog.WithStore("other", "Other", databaseIdentities: "localhost.other");
        await context.State.EnsureInitializedAsync(Token);
        return context;
    }
}
