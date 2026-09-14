using Bunit;
using MartenStudio.Components.Json;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Json;

/// <summary>The search box's own interop: moving focus into it, which is all it ever asks the browser for.</summary>
public class JsonSearchBarTests
{
    [Fact]
    public async Task A_lost_circuit_focusing_the_search_box_does_not_escape_the_component()
    {
        using var context = new JsonTestContext();
        context.JSInterop.DisconnectVoid("Blazor._internal.domWrapper.focus");

        var view = context.Render<JsonSearchBar>();

        Func<Task> focus = () => view.InvokeAsync(() => view.Instance.FocusAsync());

        await focus.Should().NotThrowAsync(
            "JSDisconnectedException is not a JSException, and the filter must still catch it");
    }
}
