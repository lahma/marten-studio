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
/// At 390 px the five uppercase labels — STORE, DATABASE, TENANT, TIME ZONE, THEME — were each wider
/// than the control they named, so every one of them broke onto a line of its own and the header became
/// a ragged block four rows deep. Below 900 px the stylesheet hides them and the pickers sit on one row.
/// </para>
/// <para>
/// A stylesheet cannot be rendered by bUnit, so the two halves of that fix are checked in two places:
/// the rule itself in <see cref="Conventions.StylesheetTests" />, and here the markup the rule depends
/// on. The load-bearing half is the <c>aria-label</c>: with the visible label hidden by a media query,
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
    /// The label is kept as well as the <c>aria-label</c>: it is what the control has above 900 px, and
    /// the media query that hides it is written against this class.
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
