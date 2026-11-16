using Bunit;

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Layout;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Components;

/// <summary>
/// Shell A2a: the database's scope gate draws what <see cref="DatabaseScopeHost" /> last wrote to <see cref="DatabaseScopeAccess" /> - the page
/// when the policy admitted the visitor, the refusal when it did not, and a spinner until it has answered - and follows the answer as it changes.
/// </summary>
public class DatabaseScopeGateTests
{
    private static readonly RenderFragment Body = builder => builder.AddMarkupContent(0, "<p id=\"body-marker\">the page</p>");

    private static IRenderedComponent<DatabaseScopeGate> Render(StudioComponentContext context) =>
        context.Render<DatabaseScopeGate>(parameters => parameters.Add(x => x.ChildContent, Body));

    [Fact]
    public void Until_the_policy_has_answered_the_gate_draws_a_spinner_and_never_the_page()
    {
        using StudioComponentContext context = new();

        IRenderedComponent<DatabaseScopeGate> gate = Render(context);

        gate.FindAll(".ms-loading-spinner").Should().ContainSingle();
        gate.FindAll("#body-marker").Should().BeEmpty();
    }

    [Fact]
    public void The_gate_follows_the_answer_as_it_changes()
    {
        using StudioComponentContext context = new();
        DatabaseScopeAccess access = context.Services.GetRequiredService<DatabaseScopeAccess>();
        IRenderedComponent<DatabaseScopeGate> gate = Render(context);

        access.Set(ScopeAccess.Granted);
        gate.WaitForAssertion(() => gate.FindAll("#body-marker").Should().ContainSingle());
        gate.FindAll(".ms-loading-spinner").Should().BeEmpty();

        access.Set(ScopeAccess.Denied);
        gate.WaitForAssertion(() => gate.Find(".ms-empty-title").TextContent.Should().Be("Not authorized"));
        gate.FindAll("#body-marker").Should().BeEmpty();

        access.Set(ScopeAccess.Unknown);
        gate.WaitForAssertion(() => gate.FindAll(".ms-loading-spinner").Should().ContainSingle());
    }

    /// <summary>
    /// A gate that was disposed is no longer subscribed, and a late answer neither renders it nor puts an exception on the renderer. The
    /// subscription is read from the event's invocation list, because a gate that stayed subscribed would otherwise be saved by its own
    /// <c>disposed</c> check; the render count is read after a round trip through the dispatcher, which is where a stale handler's render lands.
    /// </summary>
    [Fact]
    public async Task A_disposed_gate_is_unsubscribed_and_a_late_answer_renders_nothing()
    {
        await using StudioComponentContext context = new();
        DatabaseScopeAccess access = context.Services.GetRequiredService<DatabaseScopeAccess>();
        IRenderedComponent<DatabaseScopeGate> gate = Render(context);
        System.Reflection.FieldInfo changed = typeof(DatabaseScopeAccess).GetField(nameof(DatabaseScopeAccess.Changed), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        changed.GetValue(access).Should().NotBeNull("the premise: a live gate listens");
        int renders = gate.RenderCount;

        gate.Instance.Dispose();

        changed.GetValue(access).Should().BeNull("a disposed gate leaves the event");

        access.Set(ScopeAccess.Granted);
        await context.Renderer.Dispatcher.InvokeAsync(static () => Task.CompletedTask);

        gate.RenderCount.Should().Be(renders, "a late answer is nobody's to draw");
        context.Renderer.UnhandledException.IsCompleted.Should().BeFalse("a disposed gate must not put an exception on the renderer");
    }
}
