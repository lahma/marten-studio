using DataStudio.Components.Pages;
using DataStudio.Components;
using DataStudio.Services.Settings;
using DataStudio.Marten.Components.Pages;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Marten.Tests.Support;

using Microsoft.AspNetCore.Components;
using DataStudio.Postgres.Components.Pages;

namespace DataStudio.Marten.Tests.Components;

/// <summary>
/// The studio's own route matching, which exists because the built-in router can only match the
/// compile-time templates (<c>/marten/...</c>, <c>/postgres/...</c>) against the browser URL, not against a mount path.
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
    private static DataStudio.Marten.Services.MartenSettings Options(string? path = null)
    {
        DataStudioOptions shell = new();
        if (path is not null)
        {
            shell.Path = path;
        }

        return DataStudio.Marten.Services.MartenSettings.Of(
            new MartenOptions(),
            new DataStudio.Services.Settings.ShellSettings(Microsoft.Extensions.Options.Options.Create(shell)));
    }

    private static StudioRouteTable Routes => MartenSpine.Routes;

    // -------------------------------------------------------------------------------------------
    // Built from the descriptor's (assembly, root) pairs
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The Marten module's pages are rooted at <c>/marten</c> and the shared database pages at <c>/postgres</c>, each kept as the first segment of
    /// its address below the mount path; the shell's own pages (the front page, Activity) are rooted at <c>/</c> and are the shell's to add, not the module's.
    /// </summary>
    [Fact]
    public void The_descriptor_names_both_studio_assemblies_and_the_shells_each_at_its_own_root()
    {
        MartenSpine.Descriptor.PageAssemblies.Should().Equal(
            new StudioPageAssembly(typeof(DataStudio.Marten.Components.Pages.Overview).Assembly, "/marten"),
            new StudioPageAssembly(typeof(StudioHome).Assembly, "/postgres"));

        typeof(StudioHome).Assembly.Should().NotBeSameAs(typeof(DataStudio.Marten.Components.Pages.Overview).Assembly, "the shared pages are an assembly of their own");
        StudioShellDescriptor.ShellPages.Should().Be(new StudioPageAssembly(typeof(Activity).Assembly, "/"), "the shell's pages are the shell's");
    }

    [Fact]
    public void Each_assemblys_pages_are_matched_below_its_own_root_which_is_their_first_segment()
    {
        StudioRouteTable table = new(
        [
            (typeof(Activity), "/"),
            (typeof(DataStudio.Marten.Components.Pages.Projections.Projections), "/marten"),
            (typeof(ElsewherePage), "/elsewhere"),
        ]);

        table.Match("activity")!.PageType.Should().Be<Activity>();
        table.Match("marten/projections")!.PageType.Should().Be<DataStudio.Marten.Components.Pages.Projections.Projections>();
        table.Match("elsewhere/other")!.PageType.Should().Be<ElsewherePage>();
        table.Match("projections").Should().BeNull("a template is stored with its root, never relative to it");
        table.Match("other").Should().BeNull();
        table.Match("postgres/activity").Should().BeNull("the shell's page is at the mount, not under the Postgres module's segment");
    }

    [Fact]
    public void A_route_two_pages_claim_is_refused_when_the_table_is_built()
    {
        Action build = () => _ = new StudioRouteTable([(typeof(ElsewherePage), "/elsewhere"), (typeof(DuplicateOtherPage), "/elsewhere")]);

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*ElsewherePage*DuplicateOtherPage*both claim the route '/elsewhere/other'*");
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
        new StudioRouteTable(MartenSpine.Descriptor.PageAssemblies).Match("postgres/database/object").Should().NotBeNull();
    }

    [Route("/elsewhere/other")]
    private sealed class ElsewherePage : ComponentBase;

    [Route("/elsewhere/other")]
    private sealed class DuplicateOtherPage : ComponentBase;

    [Route("/elsewhere/things/{id}")]
    private sealed class KeyedPage : ComponentBase;

    [Route("/elsewhere/Things/{key}")]
    private sealed class OtherKeyedPage : ComponentBase;

    [Theory]
    [InlineData("")]
    [InlineData("activity")]
    [InlineData("marten/documents")]
    [InlineData("postgres/query")]
    public void A_link_is_the_whole_address_below_the_mount_whatever_the_mount(string subPath)
    {
        StudioLink.To(subPath).Should().Be(subPath);
    }

    // -------------------------------------------------------------------------------------------
    // Static server-side rendering: the base URI is the application path base
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "http://host/", "http://host/studio", "")]
    [InlineData(null, "http://host/", "http://host/studio/activity", "activity")]
    [InlineData(null, "http://host/app/", "http://host/app/studio/activity", "activity")]
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
    [InlineData(null, "http://host/studio/", "http://host/studio/", "")]
    [InlineData(null, "http://host/studio/", "http://host/studio", "")]
    [InlineData(null, "http://host/studio/", "http://host/studio/activity", "activity")]
    [InlineData(null, "http://host/studio/", "http://host/studio/activity?store=default", "activity")]
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
    public void The_mount_is_the_front_page_and_each_modules_home_is_its_own_segment(string? path)
    {
        // The shell's front page is at the mount; the Marten module's overview is its home page, and the Postgres module's home (in a Marten host a redirect
        // to the Marten one) is the shared StudioHome.
        Routes.Match("", Options(path))!.PageType.Should().Be<DataStudio.Components.Pages.Front>();
        Routes.Match("marten", Options(path))!.PageType.Should().Be<Overview>();
        Routes.Match("postgres", Options(path))!.PageType.Should().Be<StudioHome>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/ops/marten")]
    public void The_query_route_matches_the_shared_query_page_whose_body_is_the_descriptors(string? path)
    {
        Routes.Match("postgres/query", Options(path))!.PageType.Should().Be<QueryPage>();
        Routes.Match("query", Options(path)).Should().BeNull("a 1.x address is not a page");
        MartenSpine.Descriptor.QueryComponent.Should().Be<DataStudio.Marten.Components.Pages.Query.Query>();
    }

    /// <summary>X3 (issue #28): the Postgres module's Plan page is below the Postgres module's segment in a Marten host too - <c>postgres/plan</c>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("/ops/marten")]
    public void The_plan_route_matches_the_shared_plan_page_under_the_postgres_segment(string? path)
    {
        Routes.Match("postgres/plan", Options(path))!.PageType.Should().Be<DataStudio.Postgres.Components.Pages.Query.PlanPage>();
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
    [InlineData(null, "studio/activity")]
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
