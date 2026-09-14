using Bunit;

namespace MartenStudio.Tests.Json;

/// <summary>
/// The bUnit context the JSON toolkit's component tests render in.
/// </summary>
/// <remarks>
/// Deliberately local to this folder and deliberately small. The shared
/// <c>Components/StudioComponentContext</c> - the one that also pins a time zone, a serializer and the
/// fake data services - belongs to the packet that owns the data seam; the JSON toolkit depends on none
/// of that, and a component that needs nothing but a string of JSON should be tested in a context that
/// gives it nothing but a string of JSON.
/// <para>
/// <see cref="JSRuntimeMode.Loose"/> rather than strict: every JS call in the toolkit is already wrapped
/// in try/catch because it has to survive prerendering, so a strict runtime would only be asserting that
/// the catch blocks work. What the tests do assert is which calls were made, through
/// <c>JSInterop.Invocations</c>.
/// </para>
/// </remarks>
internal sealed class JsonTestContext : BunitContext
{
    public JsonTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
}
