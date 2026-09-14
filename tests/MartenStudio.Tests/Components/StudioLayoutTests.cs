using AngleSharp.Dom;

using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Components.Layout;
using MartenStudio.Services;
using MartenStudio.Tests.Support;

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

    /// <summary>
    /// The first render reads <c>localStorage</c> through <c>martenStudio.prefs.get</c>, and a closed
    /// browser tab throws <see cref="Microsoft.JSInterop.JSDisconnectedException" /> out of that call - a
    /// type that derives from <see cref="Exception" /> rather than from
    /// <see cref="Microsoft.JSInterop.JSException" />. The layout has to render anyway rather than take
    /// the rest of the circuit down with it.
    /// </summary>
    [Fact]
    public void A_lost_circuit_reading_stored_preferences_does_not_escape_the_layout()
    {
        using var context = new StudioComponentContext();
        context.JSInterop.Disconnect<string?>("martenStudio.prefs.get");

        Action render = () => RenderLayout(context);

        render.Should().NotThrow();
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
        // in P7, Documents in P2, Query in P6.
        layout.TextOfAll(".ms-nav-link:not(.ms-nav-link-disabled) .ms-nav-link-text")
            .Should().Equal(
                "Overview",
                "Documents",
                "Query",
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

        // The shell roots the document at the studio itself, so links are relative to the studio root and
        // an empty href resolves to it.
        layout.FindAll("a.ms-nav-link").Select(x => x.GetAttribute("href")).Should().Equal(
            "",
            "documents",
            "query",
            "events/streams",
            "events/feed",
            "events/types",
            "events/dead-letters",
            "projections",
            "schema",
            "config",
            "activity");
    }

    /// <summary>
    /// The default mount is studio-rooted too, so its links have exactly the shape a custom path's do.
    /// There is no second link shape any more: that was the branch nobody ran locally.
    /// </summary>
    [Fact]
    public void The_nav_links_have_the_same_shape_at_the_default_path()
    {
        using var context = new StudioComponentContext();

        var layout = RenderLayout(context);

        layout.FindAll("a.ms-nav-link").Select(x => x.GetAttribute("href")).Should().Equal(
            "",
            "documents",
            "query",
            "events/streams",
            "events/feed",
            "events/types",
            "events/dead-letters",
            "projections",
            "schema",
            "config",
            "activity");
    }

    // -------------------------------------------------------------------------------------------
    // Navigation badges
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Dead_letters_wear_a_red_count_when_there_are_any()
    {
        using var context = new StudioComponentContext();
        context.NavBadges.DeadLetters = 12;

        var layout = RenderLayout(context);

        IElement badge = Badge(layout, "Dead letters")!;

        badge.TextContent.Trim().Should().Be("12");
        badge.ClassList.Should().Contain("ms-nav-badge-danger");
        badge.GetAttribute("title").Should().Contain("could not apply");
    }

    /// <summary>
    /// The count is a decoration on the link, never part of it: the link's text is what a screen reader
    /// announces and what every navigation test matches on, and a badge folded into it would rename the
    /// page.
    /// </summary>
    [Fact]
    public void A_badge_never_changes_the_text_of_the_link_it_decorates()
    {
        using var context = new StudioComponentContext();
        context.NavBadges.DeadLetters = 12;
        context.NavBadges.ProjectionsNeedAttention = true;

        var layout = RenderLayout(context);

        layout.TextOfAll(".ms-nav-link:not(.ms-nav-link-disabled) .ms-nav-link-text")
            .Should().Contain("Dead letters").And.Contain("Projections");

        layout.FindAll("a.ms-nav-link").Select(x => x.GetAttribute("href")).Should()
            .Contain("events/dead-letters").And.Contain("projections");
    }

    /// <summary>
    /// Zero dead letters is good news, and "nobody could tell" is not news at all. Neither draws a badge -
    /// a red nought would be an alarm about the absence of a problem.
    /// </summary>
    [Theory]
    [InlineData(0L)]
    [InlineData(null)]
    public void No_badge_is_drawn_for_zero_or_for_an_unreadable_count(long? count)
    {
        using var context = new StudioComponentContext();
        context.NavBadges.DeadLetters = count;

        var layout = RenderLayout(context);

        Badge(layout, "Dead letters").Should().BeNull();
    }

    [Fact]
    public void Projections_wear_an_amber_dot_with_the_reason_in_its_tooltip()
    {
        using var context = new StudioComponentContext();
        context.NavBadges.ProjectionsNeedAttention = true;
        context.NavBadges.ProjectionsExplanation = "OrderSummary:All is 5,000 events behind";

        var layout = RenderLayout(context);

        IElement badge = Badge(layout, "Projections")!;

        badge.ClassList.Should().Contain("ms-nav-badge-dot").And.Contain("ms-nav-badge-warning");
        badge.TextContent.Trim().Should().BeEmpty("a count of 'needs attention' would mean nothing");
        badge.GetAttribute("title").Should().Be("OrderSummary:All is 5,000 events behind");
        badge.GetAttribute("aria-label").Should().Be("OrderSummary:All is 5,000 events behind");
    }

    [Fact]
    public void Projections_wear_nothing_while_every_shard_is_keeping_up()
    {
        using var context = new StudioComponentContext();

        var layout = RenderLayout(context);

        Badge(layout, "Projections").Should().BeNull();
    }

    /// <summary>
    /// Schema deliberately has no badge: finding drift costs a <c>CreateMigrationAsync</c>, which hard
    /// rule 14 keeps off every navigation path, so it stays behind the button that says what it may
    /// create.
    /// </summary>
    [Fact]
    public void Schema_carries_no_badge_because_drift_cannot_be_found_without_building_schema()
    {
        using var context = new StudioComponentContext();
        context.NavBadges.DeadLetters = 3;
        context.NavBadges.ProjectionsNeedAttention = true;

        var layout = RenderLayout(context);

        Badge(layout, "Schema").Should().BeNull();
    }

    /// <summary>
    /// The one thing the sidebar must never do is stop rendering. A badge is a decoration, and a
    /// decoration that could take navigation down with it would be worse than no badge at all.
    /// </summary>
    [Fact]
    public void An_indicator_service_that_throws_costs_the_badges_and_nothing_else()
    {
        using var context = new StudioComponentContext();
        context.NavBadges.Failure = new InvalidOperationException("the database is gone");

        var layout = RenderLayout(context);

        layout.FindAll(".ms-nav-badge").Should().BeEmpty();
        layout.FindAll("a.ms-nav-link").Should().HaveCount(11);
        layout.Markup.Should().Contain(BodyMarker);
    }

    /// <summary>The badge on the nav entry with this label, or <see langword="null" /> when it has none.</summary>
    private static IElement? Badge(IRenderedComponent<StudioLayout> layout, string label)
    {
        foreach (IElement link in layout.FindAll(".ms-nav-link"))
        {
            if (string.Equals(link.QuerySelector(".ms-nav-link-text")?.TextContent.Trim(), label, StringComparison.Ordinal))
            {
                return link.QuerySelector(".ms-nav-badge");
            }
        }

        throw new InvalidOperationException($"The sidebar has no entry called '{label}'.");
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

    // -------------------------------------------------------------------------------------------
    // The pickers actually reach the server, and what is on screen is in the URL (plan D9)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The header's three pickers are the studio's only controls that are not on a page, and a browser
    /// check found them doing nothing at all. They are written with an explicit <c>value</c> plus
    /// <c>@onchange</c> rather than <c>@bind</c>, so this pins that the handler runs and that the selected
    /// option is marked in the markup - during static server-side rendering <c>value</c> on a
    /// <c>&lt;select&gt;</c> is an attribute browsers ignore, so without <c>selected</c> the prerendered
    /// picker showed its first option whatever the state said.
    /// </summary>
    [Fact]
    public void The_theme_picker_marks_the_selected_option_and_moves_the_state()
    {
        using var context = new StudioComponentContext();
        var layout = RenderLayout(context);

        layout.Find("#ms-theme-select").Change("dark");

        context.State.SelectedTheme.Should().Be("dark");
        layout.ThemeAttribute().Should().Be("dark");
        layout.Find("#ms-theme-select option[value=dark]").HasAttribute("selected").Should().BeTrue();
        layout.Find("#ms-theme-select option[value=light]").HasAttribute("selected").Should().BeFalse();
    }

    [Fact]
    public void The_time_zone_picker_marks_the_selected_option()
    {
        using var context = new StudioComponentContext();
        var layout = RenderLayout(context);

        layout.Find("#ms-timezone-select").Change("UTC");

        layout.FindAll("#ms-timezone-select option")
            .Where(x => x.HasAttribute("selected"))
            .Select(x => x.GetAttribute("value"))
            .Should().Equal("UTC");
    }

    [Fact]
    public void The_store_picker_marks_the_active_store()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithStore("IInvoicingStore", "Invoicing Store", databaseIdentities: "localhost.invoicing");

        var layout = RenderLayout(context);
        layout.Find("#ms-store-select").Change("IInvoicingStore");

        layout.FindAll("#ms-store-select option")
            .Where(x => x.HasAttribute("selected"))
            .Select(x => x.GetAttribute("value"))
            .Should().Equal("IInvoicingStore");
    }

    /// <summary>
    /// Plan D9: the scope lives in the URL for the same reason a document id does — a link someone pastes
    /// into a chat has to reopen the same thing. It never did; the address bar stayed on the bare page.
    /// </summary>
    [Fact]
    public void Changing_the_scope_writes_it_into_the_query_string()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithStore("IInvoicingStore", "Invoicing Store", databaseIdentities: "localhost.invoicing");

        var layout = RenderLayout(context);
        layout.Find("#ms-store-select").Change("IInvoicingStore");

        context.CurrentUri.Should().Contain("store=IInvoicingStore").And.Contain("db=localhost.invoicing");
    }

    [Fact]
    public void The_scope_in_the_query_string_is_what_the_studio_opens_on()
    {
        using var context = new StudioComponentContext();
        context.Catalog
            .WithStore("default", "Default", databaseIdentities: "localhost.marten")
            .WithStore("IInvoicingStore", "Invoicing Store", databaseIdentities: "localhost.invoicing");
        context.Navigate("marten?store=IInvoicingStore");

        RenderLayout(context);

        context.State.ActiveScope!.StoreKey.Should().Be("IInvoicingStore");
    }

    [Fact]
    public void A_store_the_query_string_names_but_the_listing_does_not_carry_is_ignored()
    {
        using var context = new StudioComponentContext();
        context.Catalog.WithStore("default", "Default", databaseIdentities: "localhost.marten");
        context.Navigate("marten?store=somebody-elses-store");

        RenderLayout(context);

        context.State.ActiveScope!.StoreKey.Should().Be("default", "a URL is something anyone can type");
    }

    // -------------------------------------------------------------------------------------------
    // The "served to anyone" banner
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void An_anonymous_studio_says_so_on_every_page()
    {
        using var context = new StudioComponentContext();
        context.ServedAnonymously();

        var layout = RenderLayout(context);

        layout.Find(".ms-anonymous-banner").TextContent
            .Should().Contain("served to anyone").And.Contain("AllowAnonymous");
    }

    [Fact]
    public void An_authorized_studio_draws_no_banner()
    {
        using var context = new StudioComponentContext();

        RenderLayout(context).FindAll(".ms-anonymous-banner").Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------
    // The prerendered theme survives the circuit
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="StudioState" /> is scoped and the circuit's scope has no <c>HttpContext</c>, so the
    /// cookie that themed the prerendered page is invisible to it: a dark studio flashed to light the
    /// moment the circuit attached, and an interop round trip to fix it would only have made the flash
    /// shorter. The prerender scope persists what it knew and the circuit reads it before its first
    /// render.
    /// </summary>
    [Fact]
    public void The_circuit_opens_on_the_theme_the_prerender_rendered()
    {
        using var context = new StudioComponentContext();
        context.WithPersistedState(
            "MartenStudio.Preferences",
            new { Theme = "dark", TimeZoneId = "UTC" });

        var layout = RenderLayout(context);

        layout.ThemeAttribute().Should().Be("dark");
        context.State.SelectedTheme.Should().Be("dark");
        context.State.SelectedTimeZoneId.Should().Be("UTC");
    }

    [Fact]
    public void Nothing_persisted_leaves_the_state_as_the_scope_found_it()
    {
        using var context = new StudioComponentContext();
        context.State.SelectedTheme = "light";

        RenderLayout(context).ThemeAttribute().Should().Be("light");
    }
}
