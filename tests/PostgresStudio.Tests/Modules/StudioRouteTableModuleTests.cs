using Microsoft.AspNetCore.Components;

using PostgresStudio.Components;

namespace PostgresStudio.Tests.Modules;

/// <summary>
/// D34: a root-keeping assembly's pages stay below their own segment in the relative route space, so a module's <c>/orders/list</c> is
/// <c>orders/list</c> and never lands over a host page; the root check and the duplicate check are unchanged.
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

    [Route("/host/orders/list")]
    private sealed class HostListPage : ComponentBase
    {
    }

    [Route("/host")]
    private sealed class HostIndexPage : ComponentBase
    {
    }

    [Fact]
    public void A_root_keeping_assembly_routes_below_its_own_segment()
    {
        StudioRouteTable table = new([(typeof(IndexPage), "/orders", true), (typeof(ListPage), "/orders", true), (typeof(DetailPage), "/orders", true)]);

        table.Match("orders")!.PageType.Should().Be<IndexPage>();
        table.Match("orders/list")!.PageType.Should().Be<ListPage>("a literal segment beats a parameter");
        RouteData detail = table.Match("orders/7")!;
        detail.PageType.Should().Be<DetailPage>();
        detail.RouteValues["id"].Should().Be("7");
        table.Match("list").Should().BeNull("the root is kept, so the page is not at the studio root");
        table.Match(string.Empty).Should().BeNull();
    }

    [Fact]
    public void A_root_dropping_assembly_is_routed_as_it_always_was()
    {
        StudioRouteTable table = new([(typeof(HostIndexPage), "/host", false), (typeof(HostListPage), "/host", false)]);

        table.Match(string.Empty)!.PageType.Should().Be<HostIndexPage>();
        table.Match("orders/list")!.PageType.Should().Be<HostListPage>();
    }

    [Fact]
    public void A_root_keeping_template_outside_its_root_is_refused()
    {
        Action build = () => _ = new StudioRouteTable([(typeof(OutsidePage), "/orders", true)]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*not rooted at '/orders'*");
    }

    [Fact]
    public void A_module_route_equal_to_a_host_route_is_refused()
    {
        // The host's /host/orders/list is orders/list below its root; the module's /orders/list keeps its root and is orders/list too.
        Action build = () => _ = new StudioRouteTable([(typeof(HostListPage), "/host", false), (typeof(ListPage), "/orders", true)]);

        build.Should().Throw<InvalidOperationException>().WithMessage("*both claim the route '/orders/list'*");
    }

    [Fact]
    public void The_two_tuple_constructor_keeps_no_root()
    {
        StudioRouteTable table = new([(typeof(ListPage), "/orders")]);

        table.Match("list")!.PageType.Should().Be<ListPage>();
    }
}
