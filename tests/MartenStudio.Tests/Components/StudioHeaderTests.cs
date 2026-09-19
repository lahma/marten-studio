using AngleSharp.Dom;

using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Components.Layout;
using MartenStudio.Services;

using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The header's markup, which is what the phone layout is built out of.
/// </summary>
/// <remarks>
/// <para>
/// At 390 px the uppercase labels — STORE, DATABASE, TENANT, TIME ZONE, THEME — were each wider than the
/// control they named, so every one of them broke onto a line of its own and the header became a ragged
/// block four rows deep, with the page title below it. Two things fixed that, and this file is about the
/// markup half of both: the labels are hidden by width below 900 px, and the two pickers that are
/// preferences rather than context — theme and time zone — moved into a popover behind one gear, at
/// every width (UX-5).
/// </para>
/// <para>
/// A stylesheet cannot be rendered by bUnit, so the rules themselves are asserted in
/// <see cref="Conventions.StylesheetTests" /> and the markup those rules are written against is asserted
/// here. The load-bearing half is the <c>aria-label</c>: with the visible label hidden by a media query,
/// it is the accessible name that no stylesheet can take away, and a control that lost it would be an
/// unnamed <c>select</c> on a phone and nowhere else — the kind of regression that only a device shows.
/// </para>
/// </remarks>
public class StudioHeaderTests
{
    private static readonly RenderFragment Body = builder => builder.AddContent(0, string.Empty);

    private static IRenderedComponent<StudioLayout> RenderLayout(StudioComponentContext context) =>
        context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, Body));

    /// <summary>
    /// Every scope and preference picker in the header, by the id the label's <c>for</c> names, with the
    /// accessible name it has to keep whether or not that label is painted.
    /// </summary>
    public static TheoryData<string, string> Pickers() => new()
    {
        { "ms-store-select", "Store" },
        { "ms-database-select", "Database" },
        { "ms-tenant-select", "Tenant" },
        { "ms-timezone-select", "Time zone" },
        { "ms-theme-select", "Theme" },
    };

    [Theory]
    [MemberData(nameof(Pickers))]
    public void Every_header_control_carries_its_own_aria_label(string id, string expected)
    {
        using var context = HeaderWithEveryControl();

        var layout = RenderLayout(context);

        IElement? control = layout.Selector(id);
        control.Should().NotBeNull($"the header should render '{id}'");
        control!.GetAttribute("aria-label").Should().Be(
            expected,
            "below 900 px the visible label is hidden, and the aria-label is the name that survives it");
    }

    /// <summary>
    /// The label is kept as well as the <c>aria-label</c>: it is what the scope pickers have above 900 px
    /// and what the two preference pickers have inside the popover, where there is room for it.
    /// </summary>
    [Fact]
    public void Every_header_control_still_has_a_visible_label_bound_to_it()
    {
        using var context = HeaderWithEveryControl();

        var layout = RenderLayout(context);

        List<string> labelled = [.. layout
            .FindAll(".ms-header-control-label")
            .Select(x => x.GetAttribute("for") ?? string.Empty)];

        labelled.Should().BeEquivalentTo(
            ["ms-store-select", "ms-database-select", "ms-tenant-select", "ms-timezone-select", "ms-theme-select"],
            "the stylesheet hides .ms-header-control-label below 900 px, so every label has to be one");
    }

    /// <summary>
    /// The structure the media query is written against: two regions inside one header, each control in
    /// its own <c>.ms-header-control</c>. A rearrangement that moved a picker out of one of these would
    /// leave the desktop header intact and the phone header broken.
    /// </summary>
    [Fact]
    public void The_header_keeps_the_two_regions_and_one_wrapper_per_control()
    {
        using var context = HeaderWithEveryControl();

        var layout = RenderLayout(context);

        layout.FindAll(".ms-header").Should().ContainSingle();
        layout.FindAll(".ms-header > .ms-header-left").Should().ContainSingle();
        layout.FindAll(".ms-header > .ms-header-right").Should().ContainSingle();
        layout.FindAll(".ms-header-left .ms-scope-selector").Should().ContainSingle();
        layout.FindAll(".ms-header-control").Should().HaveCount(5);
    }

    // -----------------------------------------------------------------------------------------------
    // The preferences popover (UX-5)
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// The header row itself holds the scope, the capability chip and one 32 px button — and nothing
    /// else. This is the assertion that keeps the phone header one row: every picker the header row
    /// gains is another line at 390 px.
    /// </summary>
    [Fact]
    public void The_header_row_holds_the_scope_the_chip_and_the_preferences_button_and_no_other_picker()
    {
        using var context = HeaderWithEveryControl();

        var layout = RenderLayout(context);

        layout.FindAll(".ms-header-left .ms-header-control").Should().HaveCount(
            3,
            "store, database and tenant are the context of the screen and stay in the header row");

        layout.FindAll(".ms-header-right > .ms-header-control").Should().BeEmpty(
            "theme and time zone are behind the gear, not beside the chip");

        layout.FindAll(".ms-preferences-popover .ms-header-control").Should().HaveCount(
            2, "and that is where they went");

        layout.FindAll(".ms-header-right > .ms-chip").Should().ContainSingle(
            "the capability chip stays in the header");
        layout.FindAll(".ms-header-right > .ms-preferences-button").Should().ContainSingle();
    }

    /// <summary>
    /// The button names itself, says what it opens and what state it is in. An icon-only control with no
    /// accessible name is a control a screen reader announces as "button".
    /// </summary>
    [Fact]
    public void The_preferences_button_is_named_and_says_what_it_opens()
    {
        using var context = HeaderWithEveryControl();

        var layout = RenderLayout(context);

        IElement button = layout.Find(".ms-preferences-button");

        button.GetAttribute("type").Should().Be("button");
        button.GetAttribute("aria-label").Should().Be("Preferences");
        button.GetAttribute("title").Should().Be(
            "Preferences: theme, time zone",
            "a gear on its own says nothing about which preferences");
        button.GetAttribute("popovertarget").Should().Be("ms-preferences-popover");
        button.QuerySelectorAll("svg").Should().ContainSingle("the icon is inline SVG, never a font or a library");
        button.TextContent.Trim().Should().BeEmpty("the label is the aria-label; the button is the glyph");
    }

    /// <summary>
    /// The button is wired to the popover the one way the platform understands, and states nothing about
    /// it that the platform is already responsible for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>popovertarget</c> is the whole wiring: the user agent opens the menu, reports the invoker's
    /// expanded state and the controls relationship, and does all of it with no script at all.
    /// </para>
    /// <para>
    /// This used to carry a hand-written <c>aria-expanded</c> driven by an <c>@ontoggle</c> handler, and
    /// the failure is worth the test. <c>popovertarget</c> works during <em>prerendering</em> — before
    /// the circuit exists and before Blazor has attached any listener — while the attribute could only
    /// move when a <c>toggle</c> event arrived over that circuit. A tap in the window between the two
    /// opened the menu without moving the attribute, and because the only way to track a state Blazor
    /// surfaces as a bare <see cref="EventArgs" /> is to invert a flag per event, it stayed inverted for
    /// the rest of the session: a screen reader was told the opposite of the truth on every open from
    /// then on. So the assertion is that these attributes are <em>absent</em>, not that they are right.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_button_points_at_the_popover_and_leaves_its_state_to_the_platform()
    {
        using var context = HeaderWithEveryControl();

        var layout = RenderLayout(context);

        IElement button = layout.Find(".ms-preferences-button");
        IElement popover = layout.Find("#ms-preferences-popover");

        button.GetAttribute("popovertarget").Should().Be(
            popover.Id,
            "the invoker names the popover it opens, and that is the whole wiring");

        button.HasAttribute("aria-expanded").Should().BeFalse(
            "a popover invoker's expanded state is the user agent's to report; one written here goes "
            + "stale the first time the menu is opened before the circuit attaches");
        button.HasAttribute("aria-controls").Should().BeFalse(
            "the controls relationship comes from popovertarget too, the same as CapabilityBanner's "
            + "chip and JsonViewRow's key button");
    }

    /// <summary>
    /// The popover is a native one, and its contents are rendered whether or not it is open.
    /// </summary>
    /// <remarks>
    /// Both halves matter. No positioning library and no third-party menu component (AGENTS.md hard
    /// rule 3) is the first; the second is that <c>popover</c> hides the box with the user agent's own
    /// <c>display: none</c>, so the selects exist in the document from the first render — which is what
    /// the browser suite drives, and what stops the circuit rebuilding 400 time-zone options every time
    /// somebody opens the menu.
    /// </remarks>
    [Fact]
    public void The_popover_is_native_and_its_contents_are_always_rendered()
    {
        using var context = HeaderWithEveryControl();

        var layout = RenderLayout(context);

        IElement popover = layout.Find("#ms-preferences-popover");

        popover.GetAttribute("popover").Should().Be("auto");
        popover.ClassList.Should().Contain("ms-popover", "it is the same box the capability list uses");

        // Nothing has opened it, and the selects are in the document all the same — which is the half
        // of this that matters, because `popover` hides them with the user agent's own `display: none`
        // and the browser suite drives them through it.
        popover.QuerySelector("#ms-timezone-select").Should().NotBeNull();
        popover.QuerySelector("#ms-theme-select").Should().NotBeNull();
        popover.QuerySelectorAll("label[for='ms-timezone-select']").Should().ContainSingle();
        popover.QuerySelectorAll("label[for='ms-theme-select']").Should().ContainSingle();
    }

    /// <summary>
    /// The pickers still work from inside the popover: the handlers stayed in <c>StudioLayout</c>, where
    /// the preference-race logic they are one half of lives, and the markup moved without them.
    /// </summary>
    [Fact]
    public void A_theme_picked_inside_the_popover_still_reaches_the_shell()
    {
        using var context = HeaderWithEveryControl();

        var layout = RenderLayout(context);
        layout.WaitForState(() => layout.Instance.PreferencesLoaded);

        layout.Find("#ms-preferences-popover #ms-theme-select").Change("dark");

        layout.WaitForAssertion(() =>
        {
            layout.ThemeAttribute().Should().Be("dark");
            context.State.SelectedTheme.Should().Be("dark");
        });
    }

    /// <summary>
    /// A header with all five controls at once: two stores, two databases in the selected one, and a
    /// tenant list. Anything less and the pickers that are conditional simply are not there.
    /// </summary>
    private static StudioComponentContext HeaderWithEveryControl()
    {
        StudioComponentContext context = new();
        context.Catalog
            .WithStore("default", "Default", DatabaseCardinality.StaticMultiple, "localhost.marten", "localhost.marten2")
            .WithStore("invoicing", "Invoicing", DatabaseCardinality.Single, "localhost.invoicing")
            .WithTenants("default", new TenantList(["acme", "globex"], IsTruncated: false, TenantListSource.Configured));

        return context;
    }
}
