using Bunit;

using MartenStudio.Components.Shared;

using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Components;

/// <summary>
/// <c>TableScroll</c>: the scrollable region every wide data table now sits in.
/// </summary>
/// <remarks>
/// It is four attributes and a class, and each one of them is load-bearing: the class is what carries
/// <c>overflow-x: auto</c> and the edge cue, the role and the label are what a screen reader announces
/// instead of an unnamed box, and the <c>tabindex</c> is the only reason the region can be scrolled
/// without a mouse - browsers do not make a scroll container focusable on their own.
/// </remarks>
public class TableScrollTests
{
    [Fact]
    public void The_region_is_labelled_focusable_and_scrollable()
    {
        using var context = new StudioComponentContext();

        var rendered = context.Render<TableScroll>(parameters => parameters
            .Add(x => x.Label, "Event types table")
            .AddChildContent("<table class=\"ms-table\"><tbody><tr><td>x</td></tr></tbody></table>"));

        var region = rendered.Find(".ms-table-scroll");

        region.GetAttribute("role").Should().Be("region");
        region.GetAttribute("aria-label").Should().Be("Event types table");
        region.GetAttribute("tabindex").Should().Be("0");
        region.QuerySelector("table.ms-table").Should().NotBeNull("the table is the region's content");
    }

    /// <summary>
    /// The wrapper draws no border of its own. <c>.ms-table</c> already has one with its own radius, and
    /// a second would read as a frame round a frame - which is the state the documents list was in
    /// before <c>.ms-table-wrap</c> and <c>.ms-table</c> were told apart.
    /// </summary>
    [Fact]
    public void The_region_adds_no_second_class_of_its_own_to_the_table()
    {
        using var context = new StudioComponentContext();

        var rendered = context.Render<TableScroll>(parameters => parameters
            .Add(x => x.Label, "Anything")
            .AddChildContent("<table class=\"ms-table\"></table>"));

        rendered.Find(".ms-table-scroll").ClassList.Should().ContainSingle().Which.Should().Be("ms-table-scroll");
    }

    /// <summary>
    /// No interop, deliberately: the scrolling is the browser's own and the cue is driven by one
    /// document-wide observer in marten-studio.js. A registration per table would be four round trips
    /// per render on the Configuration screen, and a <c>DotNetObjectReference</c> per table to leak.
    /// </summary>
    [Fact]
    public void Rendering_one_asks_the_browser_for_nothing()
    {
        using var context = new StudioComponentContext();

        context.Render<TableScroll>(parameters => parameters
            .Add(x => x.Label, "Anything")
            .AddChildContent("<table class=\"ms-table\"></table>"));

        context.JSInterop.Invocations.Should().BeEmpty();
    }

    /// <summary>
    /// The label is <c>[EditorRequired]</c>, which is a build error in this repository rather than a
    /// runtime one - so this proves the parameter exists and is marked, which is what makes the build
    /// the thing that catches a missing one.
    /// </summary>
    [Fact]
    public void The_label_is_required_at_build_time()
    {
        typeof(TableScroll).GetProperty(nameof(TableScroll.Label))
            .Should().NotBeNull()
            .And.Subject.As<System.Reflection.PropertyInfo>()
            .GetCustomAttributes(typeof(EditorRequiredAttribute), inherit: false)
            .Should().NotBeEmpty("a region with no accessible name is worse than no region");
    }
}
