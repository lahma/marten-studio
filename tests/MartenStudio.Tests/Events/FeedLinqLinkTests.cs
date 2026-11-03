using AngleSharp.Dom;

using Bunit;

using JasperFx.Descriptors;

using MartenStudio.Components.Pages.Events;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Query;

using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The Feed's "Query these events with LINQ" link (1.1): a link and nothing more, shown only where LINQ mode could run, carrying
/// the scope and the feed's own filters as a predicate for the Query page's box.
/// </summary>
public class FeedLinqLinkTests
{
    private const string LinkSelector = "a.ms-feed-linq-link";

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_link_is_shown_under_RunLinqQueries()
    {
        using StudioComponentContext context = await NewContextAsync(static options => options.Capabilities.RunLinqQueries = true);

        IRenderedComponent<Feed> page = context.Render<Feed>();

        IElement link = page.Find(LinkSelector);
        link.TextContent.Trim().Should().Be("Query these events with LINQ");
        link.GetAttribute("href").Should().StartWith("query?");
    }

    [Fact]
    public async Task The_link_is_hidden_when_the_capability_is_off()
    {
        using StudioComponentContext context = await NewContextAsync(static options => options.Capabilities = new MartenStudioCapabilities());

        context.Render<Feed>().FindAll(LinkSelector).Should().BeEmpty();
    }

    [Fact]
    public async Task The_link_is_hidden_under_ReadOnly_whatever_the_capabilities_say()
    {
        using StudioComponentContext context = await NewContextAsync(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
        });

        context.Render<Feed>().FindAll(LinkSelector).Should().BeEmpty();
    }

    [Fact]
    public async Task The_href_carries_the_scope_and_the_feeds_filters_as_a_prefilled_predicate()
    {
        using StudioComponentContext context = await NewContextAsync(
            static options => options.Capabilities.RunLinqQueries = true,
            tenant: "acme");
        context.Navigate("marten/events/feed?types=OrderPlaced&stream=abc&archived=1");

        string href = context.Render<Feed>().Find(LinkSelector).GetAttribute("href")!;

        Dictionary<string, string> query = Parse(href);
        query["mode"].Should().Be("linq");
        query["target"].Should().Be("events");
        query["store"].Should().Be("default");
        query["db"].Should().Be("localhost.marten");
        query["tenant"].Should().Be("acme");
        query["archived"].Should().Be("include");
        query["predicate"].Should().Contain("StreamId == \"abc\"").And.Contain("EventTypeName == \"OrderPlaced\"");
    }

    [Fact]
    public async Task The_link_never_runs_anything_not_on_render_and_not_on_a_click()
    {
        var linq = new FakeLinqQueryService();

        using StudioComponentContext context = await NewContextAsync(
            static options => options.Capabilities.RunLinqQueries = true,
            configure: services => services.AddSingleton<MartenStudio.Services.Query.ILinqQueryService>(linq));

        IRenderedComponent<Feed> page = context.Render<Feed>();
        IElement link = page.Find(LinkSelector);

        link.GetAttribute("href").Should().NotBeNullOrEmpty();

        // A plain anchor: there is no handler to dispatch a click to, so following it only navigates to the Query page.
        Func<Task> click = () => link.ClickAsync(new MouseEventArgs());
        await click.Should().ThrowAsync<MissingEventHandlerException>();
        linq.Requests.Should().BeEmpty("the link only fills the Query page's boxes; running is a button there");
    }

    private static Dictionary<string, string> Parse(string href)
    {
        string query = href[(href.IndexOf('?', StringComparison.Ordinal) + 1)..];

        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .ToDictionary(
                static parts => Uri.UnescapeDataString(parts[0]),
                static parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1].Replace('+', ' ') : string.Empty));
    }

    private static async Task<StudioComponentContext> NewContextAsync(
        Action<MartenStudioOptions> options,
        string? tenant = null,
        Action<IServiceCollection>? configure = null)
    {
        var context = new StudioComponentContext(configure);
        options(context.Options);

        if (tenant is null)
        {
            await context.ReadyAsync();
            return context;
        }

        context.Catalog
            .WithStore("default", "Default", DatabaseCardinality.Single, "localhost.marten")
            .WithTenants("default", new TenantList([tenant, "globex"], IsTruncated: false, TenantListSource.Configured));

        await context.State.EnsureInitializedAsync(Token);
        await context.State.SetScopeAsync("default", "localhost.marten", tenant, Token);

        return context;
    }
}
