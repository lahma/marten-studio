using DataStudio.Components;
using Microsoft.AspNetCore.Components;

namespace DataStudio.Tests.Modules;

/// <summary>
/// D34 and v2 step 5: every assembly's pages keep their root as their first segment, so a module's <c>/orders/list</c> is <c>orders/list</c> and never lands
/// over another module's page, and the shell's own <c>/activity</c> (root <c>/</c>) is <c>activity</c>; the root check and the duplicate check stand.
/// </summary>
public class StudioRouteTableModuleTests
{
    [Route("/orders")]
    private sealed class IndexPage : ComponentBase
    {
    }

    [Route("/orders/list")]
    private sealed class ListPage : ComponentBase
    {
    }

    [Route("/orders/{id}")]
    private sealed class DetailPage : ComponentBase
    {
    }

    [Route("/elsewhere/list")]
    private sealed class OutsidePage : ComponentBase
    {
    }

    [Route("/")]
    private sealed class FrontPage : ComponentBase
    {
    }

    [Route("/activity")]
    private sealed class ShellActivityPage : ComponentBase
    {
    }

    [Route("/orders/list")]
    private sealed class OtherOrdersListPage : ComponentBase
    {
    }

    [Fact]
    public void An_assembly_routes_below_its_own_segment()
    {
        StudioRouteTable table = new([(typeof(IndexPage), "/orders"), (typeof(ListPage), "/orders"), (typeof(DetailPage), "/orders")]);

        table.Match("orders")!.PageType.Should().Be<IndexPage>();
        table.Match("orders/list")!.PageType.Should().Be<ListPage>("a literal segment beats a parameter");
        RouteData detail = table.Match("orders/7")!;
        detail.PageType.Should().Be<DetailPage>();
        detail.RouteValues["id"].Should().Be("7");
        table.Match("list").Should().BeNull("the root is kept, so the page is not at the studio root");
        table.Match(string.Empty).Should().BeNull();
    }

    [Fact]
    public void The_shells_own_root_is_the_mount_itself_and_every_address_is_below_it()
    {
        StudioRouteTable table = new([(typeof(FrontPage), "/"), (typeof(ShellActivityPage), "/"), (typeof(ListPage), "/orders")]);

        table.Match(string.Empty)!.PageType.Should().Be<FrontPage>("the mount is the front page");
        table.Match("activity")!.PageType.Should().Be<ShellActivityPage>();
        table.Match("orders/list")!.PageType.Should().Be<ListPage>();
        table.Match("list").Should().BeNull();
    }

    [Fact]
    public void A_template_outside_its_root_is_refused()
    {
        Action build = () => _ = new StudioRouteTable([(typeof(OutsidePage), "/orders")]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*not rooted at '/orders'*");
    }

    [Fact]
    public void A_route_two_assemblies_claim_is_refused()
    {
        Action build = () => _ = new StudioRouteTable([(typeof(OtherOrdersListPage), "/orders"), (typeof(ListPage), "/orders")]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*both claim the route '/orders/list'*");
    }

    [Fact]
    public void A_pre_2_address_is_not_a_page()
    {
        // A page of the module that owns /orders is at orders/..., never at the bare name a 1.x studio served it at.
        StudioRouteTable table = new([(typeof(ListPage), "/orders")]);

        table.Match("list").Should().BeNull();
        table.Match("orders/list")!.PageType.Should().Be<ListPage>();
    }
}
