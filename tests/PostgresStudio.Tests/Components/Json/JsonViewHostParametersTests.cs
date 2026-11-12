using Bunit;

using PostgresStudio.Components.Json;

namespace PostgresStudio.Tests.Components.Json;

/// <summary>
/// The three parameters a host that is not a Postgres studio sets on <c>JsonView</c> (KStudio shows a Kafka value with it): no download, no Postgres
/// expressions in the node menu, and a "Copy as search" the host answers. Their defaults leave both studios as they were.
/// </summary>
public class JsonViewHostParametersTests
{
    private const string Sample = """{"customer":{"id":"customer-042","email":"a@example.com"},"total":12.5}""";

    private static IRenderedComponent<JsonView> Render(PostgresStudioComponentContext context, Action<ComponentParameterCollectionBuilder<JsonView>>? configure = null) =>
        context.Render<JsonView>(p =>
        {
            p.Add(x => x.Json, Sample);
            configure?.Invoke(p);
        });

    private static void OpenMenuOfId(IRenderedComponent<JsonView> view) =>
        view.FindAll("[role=treeitem]").First(static r => r.TextContent.Contains("customer-042", StringComparison.Ordinal)).QuerySelector(".ms-json-key")!.Click();

    [Fact]
    public void Download_is_hidden_when_not_allowed()
    {
        using PostgresStudioComponentContext context = new();

        Render(context).Markup.Should().Contain("Download", "by default a document can be downloaded, as before");
        IRenderedComponent<JsonView> hidden = Render(context, p => p.Add(x => x.AllowDownload, false));

        hidden.Markup.Should().NotContain("Download").And.Contain("Copy JSON");
    }

    [Fact]
    public void Postgres_items_are_hidden_when_asked()
    {
        using PostgresStudioComponentContext context = new();

        IRenderedComponent<JsonView> standard = Render(context);
        OpenMenuOfId(standard);
        standard.FindAll(".ms-copy-menu .ms-menu-item-label").Select(static x => x.TextContent).Should().Equal(
            "Copy value", "Copy JSONPath", "Copy Postgres expression", "Copy Postgres #>> path", "Copy Marten LINQ path", "Copy containment filter");

        IRenderedComponent<JsonView> plain = Render(context, p => p.Add(x => x.ShowPostgresCopyItems, false));
        OpenMenuOfId(plain);
        plain.FindAll(".ms-copy-menu .ms-menu-item-label").Select(static x => x.TextContent).Should().Equal("Copy value", "Copy JSONPath");
    }

    [Fact]
    public void Copy_as_search_appears_only_when_the_delegate_answers()
    {
        using PostgresStudioComponentContext context = new();
        List<string> asked = [];

        string? Answer(IReadOnlyList<(string? Key, int Index)> segments, string valueJson)
        {
            asked.Add(string.Join('/', segments.Select(static x => x.Key ?? "[" + x.Index + "]")) + "=" + valueJson);
            return segments is [("customer", _), ("id", _)] ? "$.customer.id = 'customer-042'" : null;
        }

        IRenderedComponent<JsonView> view = Render(context, p => p.Add(x => x.CopyAsSearch, Answer));

        OpenMenuOfId(view);
        view.FindAll(".ms-copy-menu .ms-menu-item-label").Select(static x => x.TextContent).Should().Contain("Copy as search");
        view.FindAll(".ms-copy-menu .ms-menu-item").First(static x => x.TextContent.Contains("Copy as search", StringComparison.Ordinal)).Click();
        context.JSInterop.Invocations["martenStudio.clipboard.copyText"].Should().ContainSingle().Which.Arguments[0].Should().Be("$.customer.id = 'customer-042'");
        asked.Should().Contain("customer/id=\"customer-042\"");

        view.FindAll("[role=treeitem]").First(static r => r.TextContent.Contains("12.5", StringComparison.Ordinal)).QuerySelector(".ms-json-key")!.Click();
        view.FindAll(".ms-copy-menu .ms-menu-item-label").Select(static x => x.TextContent).Should().NotContain("Copy as search", "the delegate answered null for this node");

        IRenderedComponent<JsonView> without = Render(context);
        OpenMenuOfId(without);
        without.FindAll(".ms-copy-menu .ms-menu-item-label").Select(static x => x.TextContent).Should().NotContain("Copy as search");
    }
}
