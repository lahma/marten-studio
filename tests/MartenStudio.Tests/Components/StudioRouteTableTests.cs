using MartenStudio.Components;
using MartenStudio.Components.Pages;
using PostgresStudio.Services.Settings;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components;
using PostgresStudio.Components;
using PostgresStudio.Components.Pages;

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
    private static MartenStudio.Services.MartenStudioSettings Options(string? path = null)
    {
        MartenStudioOptions options = new();
        if (path is not null)
        {
            options.Path = path;
        }

        return MartenSpine.Settings(options);
    }

    private static StudioRouteTable Routes => MartenSpine.Routes;

    // -------------------------------------------------------------------------------------------
    // Built from the descriptor's (assembly, root) pairs
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// MartenStudio's pages are rooted at <c>/marten</c> and the shared studio's at <c>/postgres</c>, each re-rooted at the
    /// mount path from its own root (design §2.5) - two assemblies since the move.
    /// </summary>
    [Fact]
    public void The_descriptor_names_both_studio_assemblies_each_at_its_own_root()
    {
        MartenSpine.Descriptor.PageAssemblies.Should().Equal(
            new StudioPageAssembly(typeof(MartenStudioApp).Assembly, "/marten"),
            new StudioPageAssembly(typeof(StudioHome).Assembly, "/postgres"));

        typeof(StudioHome).Assembly.Should().NotBeSameAs(typeof(MartenStudioApp).Assembly, "the shared pages are an assembly of their own");
    }

    [Fact]
    public void Each_assembly_s_pages_are_matched_below_its_own_root()
    {
        StudioRouteTable table = new(
        [
            (typeof(Activity), "/postgres"),
            (typeof(MartenStudio.Components.Pages.Projections.Projections), "/marten"),
            (typeof(ElsewherePage), "/elsewhere"),
        ]);

        table.Match("activity")!.PageType.Should().Be<Activity>();
        table.Match("projections")!.PageType.Should().Be<MartenStudio.Components.Pages.Projections.Projections>();
        table.Match("other")!.PageType.Should().Be<ElsewherePage>();
        table.Match("elsewhere/other").Should().BeNull("a template is stored relative to its own root");
        table.Match("postgres/activity").Should().BeNull("the shared studio's root is stripped like any other");
    }

    [Fact]
    public void A_route_two_pages_claim_is_refused_when_the_table_is_built()
    {
        Action build = () => _ = new StudioRouteTable([(typeof(Activity), "/postgres"), (typeof(DuplicateActivityPage), "/elsewhere")]);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*Activity*DuplicateActivityPage*both claim the route '/activity'*");
    }

    [Fact]
    public void A_route_claimed_twice_with_differently_named_parameters_is_one_route()
    {
        Action build = () => _ = new StudioRouteTable([(typeof(KeyedPage), "/elsewhere"), (typeof(OtherKeyedPage), "/elsewhere")]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*both claim the route*");
    }

    [Fact]
    public void A_template_outside_its_assembly_s_root_is_refused()
    {
        Action build = () => _ = new StudioRouteTable([(typeof(ElsewherePage), "/marten")]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*'/elsewhere/other'*not rooted at '/marten'*");
    }

    [Fact]
    public void Marten_studio_s_own_pages_build_one_table_with_no_duplicate()
    {
        new StudioRouteTable(MartenSpine.Descriptor.PageAssemblies).Match("database/object").Should().NotBeNull();
    }

    [Route("/elsewhere/other")]
    private sealed class ElsewherePage : ComponentBase;

    [Route("/elsewhere/activity")]
    private sealed class DuplicateActivityPage : ComponentBase;

    [Route("/elsewhere/things/{id}")]
    private sealed class KeyedPage : ComponentBase;

    [Route("/elsewhere/Things/{key}")]
    private sealed class OtherKeyedPage : ComponentBase;

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
        // The shared root page, whose body is the descriptor's overview (PS-1d); the overview itself has no @page.
        Routes.Match("", Options(path))!.PageType.Should().Be<StudioHome>();
        MartenSpine.Descriptor.OverviewComponent.Should().Be<Overview>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/ops/marten")]
    public void The_query_route_matches_the_shared_query_page_whose_body_is_the_descriptors(string? path)
    {
        Routes.Match("query", Options(path))!.PageType.Should().Be<QueryPage>();
        MartenSpine.Descriptor.QueryComponent.Should().Be<MartenStudio.Components.Pages.Query.Query>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/ops/marten")]
    public void A_leaf_matches_its_page(string? path)
    {
        Routes.Match("activity", Options(path))!.PageType.Should().Be<Activity>();
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
        IStudioSettings options = Options(path);

        Routes.ResolveLeaf(relative, options).Should().Be("activity");
        Routes.Match(relative, options)!.PageType.Should().Be<Activity>();
    }

    [Fact]
    public void A_path_that_maps_to_nothing_matches_nothing()
    {
        Routes.Match("no-such-page", Options()).Should().BeNull();
    }

    [Fact]
    public void The_matched_route_carries_no_values_for_a_literal_page()
    {
        RouteData routeData = Routes.Match("activity", Options())!;

        routeData.RouteValues.Should().BeEmpty();
    }
}
