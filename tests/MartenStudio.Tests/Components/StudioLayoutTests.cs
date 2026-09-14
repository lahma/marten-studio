using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Components.Layout;
using MartenStudio.Services;

using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The page frame: it renders the page only for a scope the visitor may have, and the header stays put
/// either way so they can pick one they may.
/// </summary>
public class StudioLayoutTests
{
    private const string BodyMarker = "the page itself";

    private static readonly RenderFragment Body = builder =>
    {
        builder.OpenElement(0, "p");
        builder.AddAttribute(1, "id", "body-marker");
        builder.AddContent(2, BodyMarker);
        builder.CloseElement();
    };

    private static IRenderedComponent<StudioLayout> RenderLayout(StudioComponentContext context) =>
        context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, Body));

    [Fact]
    public void The_page_is_rendered_when_the_visitor_may_have_the_scope()
    {
        using var context = new StudioComponentContext().WithPolicy("default");
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        var layout = RenderLayout(context);

        layout.Markup.Should().Contain(BodyMarker);
        layout.FindAll(".ms-empty-title").Should().BeEmpty();
    }

    /// <summary>
    /// A page that is never rendered never runs its <c>OnInitializedAsync</c>, so nothing about a store
    /// the visitor may not see is read on their behalf. That is the point of refusing in the frame rather
    /// than in each page.
    /// </summary>
    [Fact]
    public void The_not_authorized_frame_replaces_the_page_when_the_visitor_may_not()
    {
        using var context = new StudioComponentContext().WithPolicy("somebody-elses-store");
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        var layout = RenderLayout(context);

        layout.Markup.Should().NotContain(BodyMarker);
        layout.Find(".ms-empty-title").TextContent.Should().Be("Not authorized");
    }

    [Fact]
    public void A_spinner_stands_in_while_the_policy_has_not_answered()
    {
        using var context = new StudioComponentContext().WithPolicy("default");
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        // The policy is asked and does not answer, which is the state the frame calls "unknown".
        context.AuthorizationService.Gate = new TaskCompletionSource();

        var layout = RenderLayout(context);

        layout.Markup.Should().NotContain(BodyMarker);
        layout.FindAll(".ms-loading-spinner").Should().ContainSingle();

        context.AuthorizationService.Gate.SetResult();
    }

    /// <summary>
    /// No store at all is not a refusal: the page renders and says so for itself, which is the honest
    /// answer both when nothing is registered and when the visitor may see none of it.
    /// </summary>
    [Fact]
    public void With_no_store_at_all_the_page_still_renders()
    {
        using var context = new StudioComponentContext();

        var layout = RenderLayout(context);

        layout.Markup.Should().Contain(BodyMarker);
    }

    [Fact]
    public void The_header_survives_a_refusal_so_another_store_can_be_picked()
    {
        using var context = new StudioComponentContext().WithPolicy("nothing-at-all");
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithStore("invoicing", "Invoicing", databaseIdentities: "localhost.invoicing");

        var layout = RenderLayout(context);

        layout.Find(".ms-header").Should().NotBeNull();
        layout.Find(".ms-sidebar").Should().NotBeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Theme
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_theme_defaults_to_system()
    {
        using var context = new StudioComponentContext();

        RenderLayout(context).ThemeAttribute().Should().Be("system");
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("system")]
    public void The_theme_picker_is_three_way(string theme)
    {
        using var context = new StudioComponentContext();
        var layout = RenderLayout(context);

        layout.Find("#ms-theme-select").Change(theme);

        layout.ThemeAttribute().Should().Be(theme);
        context.State.SelectedTheme.Should().Be(theme);
    }

    [Fact]
    public void An_unknown_theme_falls_back_to_system_rather_than_being_written_into_the_markup()
    {
        using var context = new StudioComponentContext();
        var layout = RenderLayout(context);

        layout.Find("#ms-theme-select").Change("\" onload=\"alert(1)");

        layout.ThemeAttribute().Should().Be("system");
    }

    [Fact]
    public void The_theme_picker_offers_exactly_the_three_choices()
    {
        using var context = new StudioComponentContext();

        RenderLayout(context).SelectorOptions("ms-theme-select").Should().Equal("system", "light", "dark");
    }

    /// <summary>
    /// Seeded from the cookie on the server, so a dark studio renders dark during prerendering and there
    /// is no flash of the light theme when the circuit takes over.
    /// </summary>
    [Fact]
    public void The_theme_is_seeded_from_the_cookie_before_the_first_render()
    {
        using var context = new StudioComponentContext();
        context.State.SelectedTheme = "dark";

        RenderLayout(context).ThemeAttribute().Should().Be("dark");
    }

    [Fact]
    public void The_time_zone_picker_changes_what_timestamps_are_rendered_in()
    {
        using var context = new StudioComponentContext();
        var layout = RenderLayout(context);

        layout.Find("#ms-timezone-select").Change("UTC");

        context.State.SelectedTimeZoneId.Should().Be("UTC");
    }

    // -------------------------------------------------------------------------------------------
    // Navigation
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_nav_lists_every_section_and_marks_the_pages_that_do_not_exist_yet()
    {
        using var context = new StudioComponentContext();
        var layout = RenderLayout(context);

        layout.TextOfAll(".ms-sidebar-section").Should().Equal("Data", "Events", "Operations", "System");

        // Only the pages that have an @page yet are links; the rest are visible and disabled, so the
        // information architecture is legible from the first version. This list grows one packet at a
        // time - Projections joined it in P5, the four Events screens in P4, Schema and Configuration
        // in P7.
        layout.TextOfAll(".ms-nav-link:not(.ms-nav-link-disabled) .ms-nav-link-text")
            .Should().Equal(
                "Overview",
                "Streams",
                "Feed",
                "Event types",
                "Dead letters",
                "Projections",
                "Schema",
                "Configuration",
                "Activity");

        layout.FindAll(".ms-nav-link-disabled").Should().OnlyContain(x =>
            x.GetAttribute("title") == "Coming in a later release" && x.GetAttribute("aria-disabled") == "true");
    }

    [Fact]
    public void The_nav_links_honour_the_configured_path()
    {
        using var context = new StudioComponentContext();
        context.Options.Path = "/ops/marten";

        var layout = RenderLayout(context);

        // In custom-path mode the shell roots the document at the studio itself, so links are relative to
        // the studio root and an empty href resolves to it.
        layout.FindAll("a.ms-nav-link").Select(x => x.GetAttribute("href")).Should().Equal(
            "",
            "events/streams",
            "events/feed",
            "events/types",
            "events/dead-letters",
            "projections",
            "schema",
            "config",
            "activity");
    }

    [Fact]
    public void The_nav_links_carry_the_default_path_when_the_document_is_rooted_at_the_application()
    {
        using var context = new StudioComponentContext();

        var layout = RenderLayout(context);

        layout.FindAll("a.ms-nav-link").Select(x => x.GetAttribute("href")).Should().Equal(
            "marten",
            "marten/events/streams",
            "marten/events/feed",
            "marten/events/types",
            "marten/events/dead-letters",
            "marten/projections",
            "marten/schema",
            "marten/config",
            "marten/activity");
    }

    // -------------------------------------------------------------------------------------------
    // The scope selector, through the layout that hosts it
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void The_store_picker_is_hidden_when_there_is_only_one_store()
    {
        using var context = new StudioComponentContext();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        RenderLayout(context).Selector("ms-store-select").Should().BeNull(
            "a picker with one option is a control that cannot do anything");
    }

    [Fact]
    public void The_store_picker_appears_once_there_is_more_than_one_store()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithStore("IInvoicingStore", "Invoicing Store", databaseIdentities: "localhost.invoicing");

        var layout = RenderLayout(context);

        layout.SelectorOptions("ms-store-select").Should().Equal("default", "IInvoicingStore");
        layout.TextOfAll("#ms-store-select option").Should().Equal("Default", "Invoicing Store");
    }

    /// <summary>
    /// A store the application registered that will not build is offered and greyed out rather than
    /// omitted: leaving it out is what made it look as though it had never been registered.
    /// </summary>
    [Fact]
    public void An_unavailable_store_is_offered_disabled_with_what_it_said()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithUnavailableStore("IInvoicingStore", "Invoicing Store", "no connection string was configured");

        var layout = RenderLayout(context);

        var broken = layout.FindAll("#ms-store-select option").Single(x => x.GetAttribute("value") == "IInvoicingStore");
        broken.HasAttribute("disabled").Should().BeTrue();
        broken.GetAttribute("title").Should().Be("no connection string was configured");
        broken.TextContent.Trim().Should().Be("Invoicing Store (unavailable)");
    }

    [Fact]
    public void The_database_picker_is_hidden_for_a_single_database_store()
    {
        using var context = new StudioComponentContext();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.Single, "localhost.marten");

        RenderLayout(context).Selector("ms-database-select").Should().BeNull();
    }

    [Fact]
    public void The_database_picker_appears_for_a_statically_multi_tenanted_store()
    {
        using var context = new StudioComponentContext();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "srv.one", "srv.two");

        var layout = RenderLayout(context);

        layout.SelectorOptions("ms-database-select").Should().Equal("srv.one", "srv.two");
        layout.FindAll("button").Should().NotContain(x => x.TextContent.Trim() == "Refresh",
            "a static list does not change while the process runs");
    }

    /// <summary>
    /// Dynamic tenancy adds databases while the process runs, so the listing is a snapshot and there has
    /// to be a way to ask again.
    /// </summary>
    [Fact]
    public void The_database_picker_gets_a_refresh_button_for_dynamic_tenancy()
    {
        using var context = new StudioComponentContext();
        context.Catalog.WithStore("default", "Default", DatabaseCardinality.DynamicMultiple, "srv.one");

        var layout = RenderLayout(context);
        var refresh = layout.FindAll("button").Single(x => x.TextContent.Trim() == "Refresh");

        refresh.Click();

        context.Catalog.InvalidateCount.Should().Be(1);
    }

    [Fact]
    public void The_tenant_picker_is_hidden_when_nothing_is_conjoined()
    {
        using var context = new StudioComponentContext();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");

        RenderLayout(context).Selector("ms-tenant-select").Should().BeNull();
    }

    [Fact]
    public void The_tenant_picker_offers_all_tenants_and_then_the_listed_ones()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme", "globex"], IsTruncated: false, TenantListSource.Configured));

        var layout = RenderLayout(context);

        layout.SelectorOptions("ms-tenant-select").Should().Equal("", "acme", "globex");
        layout.Find("#ms-tenant-select option").TextContent.Trim().Should().Be("All tenants");
    }

    /// <summary>
    /// A picker that silently omitted tenants would be worse than none, so a truncated listing becomes a
    /// box that takes the id.
    /// </summary>
    [Fact]
    public void A_truncated_tenant_listing_becomes_a_free_text_box()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme"], IsTruncated: true, TenantListSource.Queried));

        var layout = RenderLayout(context);
        var input = layout.Find("#ms-tenant-select");

        input.NodeName.Should().Be("INPUT");
        input.GetAttribute("placeholder").Should().Be("All tenants");
    }

    [Fact]
    public void Picking_a_tenant_moves_the_active_scope()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme", "globex"], IsTruncated: false, TenantListSource.Configured));

        var layout = RenderLayout(context);

        layout.Find("#ms-tenant-select").Change("globex");

        context.State.ActiveScope!.TenantId.Should().Be("globex");
    }

    /// <summary>
    /// The value arrives from a browser change event, and Blazor does not check that the server offered
    /// it. Without this the client could name any store in the process.
    /// </summary>
    [Fact]
    public void A_tenant_the_listing_did_not_carry_is_ignored()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithTenants("default", new TenantList(["acme"], IsTruncated: false, TenantListSource.Configured));

        var layout = RenderLayout(context);
        layout.Find("#ms-tenant-select").Change("acme");

        layout.Find("#ms-tenant-select").Change("somebody-elses-tenant");

        context.State.ActiveScope!.TenantId.Should().Be("acme", "the previous value stands");
    }

    [Fact]
    public void A_store_the_listing_did_not_carry_is_ignored()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithStore("IInvoicingStore", "Invoicing Store", databaseIdentities: "localhost.invoicing");

        var layout = RenderLayout(context);

        layout.Find("#ms-store-select").Change("not-a-store");

        context.State.ActiveScope!.StoreKey.Should().Be("default");
    }
}
