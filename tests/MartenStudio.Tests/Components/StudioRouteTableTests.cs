using MartenStudio.Components;
using MartenStudio.Components.Pages;

using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Components;

/// <summary>
/// The studio's own route matching, which exists because the built-in router can only match the
/// compile-time <c>/marten</c> templates against the browser URL.
/// </summary>
/// <remarks>
/// Every mount is studio-rooted now, so the two base URI shapes are no longer "default path" and "custom
/// path" — they are "static server-side rendering" (the base URI is the application path base, and the
/// relative path still carries the studio prefix) and "the interactive circuit" (the base URI is the
/// rendered studio-rooted <c>&lt;base href&gt;</c>, so it does not). Both happen at both paths, which is
/// exactly why the branch that only did this for a custom path was wrong.
/// </remarks>
public class StudioRouteTableTests
{
    private static MartenStudioOptions Options(string? path = null)
    {
        MartenStudioOptions options = new();
        if (path is not null)
        {
            options.Path = path;
        }

        return options;
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(null, "activity")]
    [InlineData("/ops/marten", "")]
    [InlineData("/ops/marten", "activity")]
    public void A_link_is_the_path_below_the_studio_root_whatever_the_mount(string? path, string subPath)
    {
        StudioLink.To(Options(path), subPath).Should().Be(subPath);
    }

    // -------------------------------------------------------------------------------------------
    // Static server-side rendering: the base URI is the application path base
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "http://host/", "http://host/marten", "")]
    [InlineData(null, "http://host/", "http://host/marten/activity", "activity")]
    [InlineData(null, "http://host/app/", "http://host/app/marten/activity", "activity")]
    [InlineData("/ops/marten", "http://host/", "http://host/ops/marten", "")]
    [InlineData("/ops/marten", "http://host/", "http://host/ops/marten/activity", "activity")]
    public void The_prerendered_shape_strips_the_studio_prefix(string? path, string baseUri, string uri, string expected)
    {
        StudioLink.ToDashboardRelativePath(uri, baseUri, Options(path)).Should().Be(expected);
    }

    // -------------------------------------------------------------------------------------------
    // The circuit: the base URI is the rendered studio-rooted <base href>
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "http://host/marten/", "http://host/marten/", "")]
    [InlineData(null, "http://host/marten/", "http://host/marten", "")]
    [InlineData(null, "http://host/marten/", "http://host/marten/activity", "activity")]
    [InlineData(null, "http://host/marten/", "http://host/marten/activity?store=default", "activity")]
    [InlineData("/ops/marten", "http://host/ops/marten/", "http://host/ops/marten/activity", "activity")]
    public void The_circuit_shape_is_already_studio_rooted(string? path, string baseUri, string uri, string expected)
    {
        StudioLink.ToDashboardRelativePath(uri, baseUri, Options(path)).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "http://host/", "http://host/somewhere-else")]
    [InlineData(null, "http://host/", "http://host/")]
    [InlineData("/ops/marten", "http://host/", "http://host/ops")]
    public void Somewhere_that_is_not_the_studio_is_not_a_studio_location(string? path, string baseUri, string uri)
    {
        StudioLink.ToDashboardRelativePath(uri, baseUri, Options(path)).Should().BeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Matching
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("/ops/marten")]
    public void The_studio_root_matches_the_overview(string? path)
    {
        StudioRouteTable.Match("", Options(path))!.PageType.Should().Be<Overview>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/ops/marten")]
    public void A_leaf_matches_its_page(string? path)
    {
        StudioRouteTable.Match("activity", Options(path))!.PageType.Should().Be<Activity>();
    }

    /// <summary>
    /// The retry that makes the ambiguous case work: when the application's path base happens to end with
    /// the studio path, the prerendered relative path and an interactive leaf look the same, so a failed
    /// match gets a second chance with the prefix stripped. It applies at the default path too now.
    /// </summary>
    [Theory]
    [InlineData(null, "marten/activity")]
    [InlineData("/ops/marten", "ops/marten/activity")]
    public void A_still_prefixed_path_is_resolved_on_the_retry(string? path, string relative)
    {
        MartenStudioOptions options = Options(path);

        StudioRouteTable.ResolveLeaf(relative, options).Should().Be("activity");
        StudioRouteTable.Match(relative, options)!.PageType.Should().Be<Activity>();
    }

    [Fact]
    public void A_path_that_maps_to_nothing_matches_nothing()
    {
        StudioRouteTable.Match("no-such-page", Options()).Should().BeNull();
    }

    [Fact]
    public void The_matched_route_carries_no_values_for_a_literal_page()
    {
        RouteData routeData = StudioRouteTable.Match("activity", Options())!;

        routeData.RouteValues.Should().BeEmpty();
    }
}
