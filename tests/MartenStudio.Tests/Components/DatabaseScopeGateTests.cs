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

    [Fact]
    public void A_disposed_gate_no_longer_listens()
    {
        using StudioComponentContext context = new();
        DatabaseScopeAccess access = context.Services.GetRequiredService<DatabaseScopeAccess>();
        IRenderedComponent<DatabaseScopeGate> gate = Render(context);

        gate.Instance.Dispose();

        Action change = () => access.Set(ScopeAccess.Granted);
        change.Should().NotThrow();
    }
}
