using Bunit;
using MartenStudio.Components.Json;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Json;

/// <summary>The "show more" row's own interop: taking DOM focus when the keyboard moves onto it.</summary>
public class JsonViewMoreRowTests
{
    [Fact]
    public void A_lost_circuit_moving_keyboard_focus_onto_the_row_does_not_escape_render()
    {
        using var context = new JsonTestContext();
        context.JSInterop.DisconnectVoid("Blazor._internal.domWrapper.focus");

        Action render = () => context.Render<JsonViewMoreRow>(p => p.Add(c => c.AutoFocus, true));

        render.Should().NotThrow("JSDisconnectedException is not a JSException, and the filter must still catch it");
    }
}
