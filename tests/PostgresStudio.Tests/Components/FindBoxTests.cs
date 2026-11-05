using AngleSharp.Dom;

using Bunit;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

using PostgresStudio.Components.Layout;
using PostgresStudio.Services;
using PostgresStudio.Services.Find;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-SCOPE-3: the header's Find box. It is drawn only with a scope and a kind to search, it runs on Enter (which script sends to
/// <c>FindAsync</c>), one hit in the scope on screen opens by itself and any other is a link, and nothing it does touches the scope.
/// </summary>
public class FindBoxTests
{
    private static readonly StudioScope Active = new("default", "default", null);

    private static PostgresStudioComponentContext Context(params FakeFindContributor[] contributors)
    {
        PostgresStudioComponentContext context = new(services =>
        {
            foreach (FakeFindContributor contributor in contributors)
            {
                services.AddSingleton<IFindContributor>(contributor);
            }
        });

        context.JSInterop.Setup<bool>("martenStudio.find.enhance", _ => true).SetResult(true);
        return context;
    }

    private static async Task<IRenderedComponent<FindBox>> RenderReadyAsync(PostgresStudioComponentContext context)
    {
        await context.ReadyAsync();
        return context.Render<FindBox>();
    }

    [Fact]
    public async Task With_no_kind_registered_nothing_is_drawn_and_no_script_is_asked_for()
    {
        using PostgresStudioComponentContext context = Context();

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);

        box.FindAll(".ms-header-find").Should().BeEmpty();
        context.JSInterop.Invocations.Should().BeEmpty();
    }

    [Fact]
    public void With_no_scope_nothing_is_drawn_and_no_script_is_asked_for()
    {
        using PostgresStudioComponentContext context = Context(new FakeFindContributor(10, "Any"));

        IRenderedComponent<FindBox> box = context.Render<FindBox>();

        box.FindAll(".ms-header-find").Should().BeEmpty();
        context.JSInterop.Invocations.Should().BeEmpty("the script is asked for after a render with a field to enhance, never before");
    }

    [Fact]
    public async Task The_box_is_a_search_with_a_name_a_hint_and_a_magnifier_for_narrow_screens()
    {
        using PostgresStudioComponentContext context = Context(new FakeFindContributor(10, "Any"));

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);

        box.Find(".ms-header-find").GetAttribute("role").Should().Be("search");

        IElement input = box.Find("input#ms-find-input");
        input.GetAttribute("type").Should().Be("search");
        input.GetAttribute("aria-label").Should().Be("Find by id");
        input.GetAttribute("placeholder").Should().Be("Find by id (/)");
        input.GetAttribute("autocomplete").Should().Be("off");
        input.HasAttribute("value").Should().BeFalse("the field is never bound, so a render never touches what was typed");

        string hintId = input.GetAttribute("aria-describedby")!;
        box.Find("#" + hintId).TextContent.Should().Be("A document or stream id, a natural key, #sequence, an exact table or view name or a queryid");

        IElement toggle = box.Find("button.ms-find-toggle");
        toggle.GetAttribute("popovertarget").Should().Be("ms-find-popover");
        toggle.GetAttribute("aria-label").Should().Be("Find by id");
        box.Find("#ms-find-popover").GetAttribute("popover").Should().Be("auto");

        box.Find(".ms-find-status").GetAttribute("aria-live").Should().Be("polite");
        box.FindAll("form").Should().BeEmpty("the studio has no forms (D23): Enter is handled in script");
    }

    [Fact]
    public async Task The_script_enhances_the_field_once_after_a_render_that_has_one()
    {
        using PostgresStudioComponentContext context = Context(new FakeFindContributor(10, "Any"));

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.CloseAsync());

        context.JSInterop.VerifyInvoke("martenStudio.find.enhance", 1);
    }

    [Fact]
    public async Task Several_hits_are_listed_by_kind_as_links_and_nothing_is_opened()
    {
        FakeFindContributor documents = new FakeFindContributor(10, "Documents").Returns(
            "2 document types",
            null,
            FakeFindContributor.Hit(Active, "Document", "1", tenantChip: "tenant acme"),
            FakeFindContributor.Hit(Active, "Document", "2", mark: "deleted"));
        FakeFindContributor streams = new FakeFindContributor(20, "Streams").Returns("streams", null, FakeFindContributor.Hit(Active, "Stream", "1"));
        using PostgresStudioComponentContext context = Context(documents, streams);
        string before = context.CurrentUri;

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync("1"));

        box.FindAll(".ms-find-group-title").Select(static x => x.TextContent).Should().Equal("Document", "Stream");
        IReadOnlyList<IElement> links = box.FindAll("a.ms-find-hit");
        links.Select(static x => x.GetAttribute("href")).Should().Equal(
            "/postgres/document?id=1", "/postgres/document?id=2", "/postgres/stream?id=1");
        links[0].QuerySelector(".ms-find-chip")!.TextContent.Should().Be("tenant acme");
        links[1].QuerySelector(".ms-chip-warning")!.TextContent.Should().Be("deleted");
        box.Find(".ms-find-status").TextContent.Should().Be("3 results");
        context.CurrentUri.Should().Be(before, "several hits are for the visitor to choose between");
    }

    [Fact]
    public async Task One_hit_in_the_scope_on_screen_is_opened_by_itself_and_the_value_stays()
    {
        FakeFindContributor documents = new FakeFindContributor(10, "Documents").Returns("documents", null, FakeFindContributor.Hit(Active, "Document", "42"));
        using PostgresStudioComponentContext context = Context(documents);

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync("42"));

        context.CurrentUri.Should().EndWith("/postgres/document?id=42");
        box.Find("input#ms-find-input").HasAttribute("value").Should().BeFalse("the typed text is the browser's, not the component's");
        context.JSInterop.VerifyInvoke("martenStudio.find.dismiss", 1);
    }

    [Theory]
    [InlineData("other", "default", null)]
    [InlineData("default", "elsewhere", null)]
    [InlineData("default", "default", "acme")]
    public async Task One_hit_in_another_store_database_or_tenant_is_a_link_the_visitor_clicks_and_is_never_pushed(string store, string database, string? tenant)
    {
        StudioScope elsewhere = new(store, database, tenant);
        FakeFindContributor documents = new FakeFindContributor(10, "Documents").Returns("documents", null, FakeFindContributor.Hit(elsewhere, "Document", "42"));
        using PostgresStudioComponentContext context = Context(documents);
        string before = context.CurrentUri;

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync("42"));

        context.CurrentUri.Should().Be(before, "the shell follows the scope of a link that was clicked, never of one that was pushed");
        box.Find("a.ms-find-hit").GetAttribute("href").Should().Be("/postgres/document?id=42");
        box.Find(".ms-find-status").TextContent.Should().Be("1 result");
        context.State.ActiveScope.Should().Be(Active, "Find never moves the scope");
    }

    [Fact]
    public async Task Nothing_found_says_where_it_looked_what_it_searched_and_what_it_did_not()
    {
        FakeFindContributor documents = new FakeFindContributor(10, "Documents").Returns(
            "47 document types",
            ["2 document types partitioned by tenant were not searched: select a tenant."]);
        using PostgresStudioComponentContext context = Context(documents);

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync("nope"));

        box.Find(".ms-find-status").TextContent.Should().Be("Nothing found");
        string panel = box.Find("[data-ms-find-panel]").TextContent;
        panel.Should().Contain("Nothing in").And.Contain("matched \"nope\".");
        panel.Should().Contain("Searched: 47 document types.");
        panel.Should().Contain("2 document types partitioned by tenant were not searched: select a tenant.");
        box.FindAll("a.ms-find-hit").Should().BeEmpty();
    }

    [Fact]
    public async Task A_part_of_a_name_that_matched_nothing_offers_the_browsers_name_filter()
    {
        FakeFindContributor documents = new FakeFindContributor(10, "Tables").Returns("tables and views");
        using PostgresStudioComponentContext context = Context(documents);

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync("qrtz"));

        string panel = box.Find("[data-ms-find-panel]").TextContent;
        panel.Should().Contain("Find matches whole names and ids.");
        IElement link = box.Find("a.ms-find-browse");
        link.TextContent.Should().Be("Filter Database › Tables by \"qrtz\"");
        link.GetAttribute("href").Should().Contain("database").And.Contain("q=qrtz").And.Contain("store=default");
    }

    [Theory]
    [InlineData("2f6c0e0a-1b2c-4d3e-8f90-123456789abc")]
    [InlineData("#12")]
    [InlineData("1234")]
    public async Task A_value_that_could_not_be_a_name_offers_no_name_filter(string typed)
    {
        using PostgresStudioComponentContext context = Context(new FakeFindContributor(10, "Tables").Returns("tables and views"));

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync(typed));

        box.FindAll("a.ms-find-browse").Should().BeEmpty();
        box.Find("[data-ms-find-panel]").TextContent.Should().NotContain("Find matches whole names");
    }

    [Fact]
    public async Task A_name_that_was_found_offers_no_name_filter()
    {
        FakeFindContributor tables = new FakeFindContributor(10, "Tables").Returns("tables and views", null, FakeFindContributor.Hit(Active, "Table", "public.orders"), FakeFindContributor.Hit(Active, "Table", "other.orders"));
        using PostgresStudioComponentContext context = Context(tables);

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync("orders"));

        box.FindAll("a.ms-find-browse").Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_with_no_tenants_never_says_all_tenants()
    {
        using PostgresStudioComponentContext context = Context(new FakeFindContributor(10, "Tables").Returns("tables and views"));

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync("nope"));

        context.State.ShowTenantSelector.Should().BeFalse("the premise: a Postgres Studio source has no tenants");
        string panel = box.Find("[data-ms-find-panel]").TextContent;
        panel.Should().Contain("Nothing in ").And.Contain("matched \"nope\".");
        panel.Should().NotContain("tenant");
    }

    [Fact]
    public async Task A_value_that_is_too_long_is_refused_in_the_panel_and_nothing_is_asked()
    {
        FakeFindContributor documents = new(10, "Documents");
        using PostgresStudioComponentContext context = Context(documents);

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync(new string('x', 600)));

        box.Find(".ms-find-status").TextContent.Should().Be("Too long to be an id (512 characters at most).");
        box.Find("[data-ms-find-panel]").TextContent.Should().Contain("Too long to be an id");
        documents.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Escape_closes_the_panel_and_clears_the_announcement()
    {
        FakeFindContributor documents = new FakeFindContributor(10, "Documents").Returns(
            "documents", null, FakeFindContributor.Hit(Active, "Document", "1"), FakeFindContributor.Hit(Active, "Document", "2"));
        using PostgresStudioComponentContext context = Context(documents);

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);
        await box.InvokeAsync(() => box.Instance.FindAsync("x"));
        box.FindAll("a.ms-find-hit").Should().HaveCount(2);

        await box.InvokeAsync(() => box.Instance.CloseAsync());

        box.FindAll("[data-ms-find-panel]").Should().BeEmpty();
        box.Find(".ms-find-status").TextContent.Should().BeEmpty();
    }

    [Fact]
    public async Task Disposing_after_the_circuit_is_gone_swallows_the_lost_interop()
    {
        using PostgresStudioComponentContext context = Context(new FakeFindContributor(10, "Any"));
        context.JSInterop.SetupVoid("martenStudio.find.release", _ => true).SetException(new JSDisconnectedException("the circuit closed"));

        IRenderedComponent<FindBox> box = await RenderReadyAsync(context);

        Func<Task> dispose = async () => await box.Instance.DisposeAsync();
        await dispose.Should().NotThrowAsync();
        context.JSInterop.VerifyInvoke("martenStudio.find.release", 1);
    }

    [Fact]
    public async Task The_layout_draws_the_box_between_the_scope_and_the_actions()
    {
        using PostgresStudioComponentContext context = Context(new FakeFindContributor(10, "Any"));
        await context.ReadyAsync();

        IRenderedComponent<StudioLayout> layout = context.Render<StudioLayout>(parameters => parameters.Add(x => x.Body, static builder => builder.AddContent(0, string.Empty)));

        layout.WaitForAssertion(() =>
            layout.Find("header.ms-header").Children.Select(static x => x.ClassList[0]).Should().Equal("ms-header-left", "ms-header-find", "ms-header-right"));
    }
}
