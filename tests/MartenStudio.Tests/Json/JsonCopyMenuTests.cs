using Bunit;
using MartenStudio.Components.Json;
using MartenStudio.Tests.Support;
using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Json;

/// <summary>
/// The shared copy menu's own interop: opening it, moving focus inside it with the keyboard, and copying
/// one of its offers, none of which may take a lost circuit out through the component.
/// </summary>
public class JsonCopyMenuTests
{
    [Fact]
    public void A_lost_circuit_during_a_copy_does_not_escape_and_still_announces_failure()
    {
        using var context = new JsonTestContext();
        string? announced = null;
        context.JSInterop.Disconnect<bool>("martenStudio.clipboard.copyText");

        var menu = context.Render<JsonCopyMenu>(p => p
            .Add(c => c.ValueJson, "42")
            .Add(c => c.OnCopied, EventCallback.Factory.Create<string>(this, text => announced = text)));

        Action copy = () => menu.Find(".ms-menu-item").Click();

        copy.Should().NotThrow("JSDisconnectedException is not a JSException, and the filter must still catch it");
        announced.Should().Be("Could not copy the value.");
    }
}
