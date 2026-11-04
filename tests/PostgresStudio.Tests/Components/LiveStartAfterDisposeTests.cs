using Bunit;

using Microsoft.AspNetCore.Components;

using PostgresStudio.Components.Layout;
using PostgresStudio.Components.Pages;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// 1.2-LIVE-FU: the Overview and the rail start their polling loop in the first render, after reads they await, and a visitor who
/// leaves during those reads disposed them under the await: starting the loop then either throws <c>ObjectDisposedException</c> (the
/// page's token source is gone, which is not a cancellation and ends the circuit) or starts a loop nothing disposes.
/// </summary>
public class LiveStartAfterDisposeTests
{
    private const string Muted = "martenStudio.health.getMuted";
    private const string Watch = "martenStudio.visibility.watch";

    [Fact]
    public async Task The_Overview_left_during_its_first_reads_starts_no_loop_and_ends_no_circuit()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        await AssertLeftDuringTheFirstReadStartsNothingAsync<PostgresOverview>(context);
    }

    [Fact]
    public async Task The_rail_left_during_its_first_reads_starts_no_loop_and_ends_no_circuit()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        await AssertLeftDuringTheFirstReadStartsNothingAsync<NavMenu>(context);
    }

    /// <summary>
    /// Holds the first interactive read (the browser's muted findings) pending, disposes the component, completes the read: the
    /// renderer has no unhandled exception and no <c>visibility.watch</c> was ever called, which a loop would have done.
    /// </summary>
    private static async Task AssertLeftDuringTheFirstReadStartsNothingAsync<TComponent>(PostgresStudioComponentContext context)
        where TComponent : IComponent
    {
        var muted = context.JSInterop.Setup<string?>(Muted, static _ => true);

        var page = context.Render<TComponent>();
        page.WaitForAssertion(() => context.JSInterop.Invocations.Select(static x => x.Identifier).Should().Contain(Muted));

        await context.DisposeComponentsAsync();
        muted.SetResult(null);

        // The component's continuation, and whatever it throws, are hops on the dispatcher.
        await LetTheContinuationRunAsync(context);

        Task<Exception> unhandled = context.Renderer.UnhandledException;
        string said = unhandled.IsCompleted ? (await unhandled).ToString() : string.Empty;

        unhandled.IsCompleted.Should().BeFalse("an exception out of OnAfterRenderAsync ends a circuit: " + said);
        context.JSInterop.Invocations.Select(static x => x.Identifier).Should().NotContain(Watch, "a loop was started after the component was disposed");
    }

    /// <summary>
    /// Lets what the completed read set going run: its continuations hop between the thread pool and the dispatcher, so there is no
    /// single thing to await. A bounded poll that ends at once when the renderer reports an unhandled exception, and otherwise after a
    /// second of dispatcher round trips - a negative assertion needs some bound, and this one is not a delay on the success path of
    /// the failure it looks for.
    /// </summary>
    private static async Task LetTheContinuationRunAsync(BunitContext context)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        while (elapsed.Elapsed < TimeSpan.FromSeconds(1) && !context.Renderer.UnhandledException.IsCompleted)
        {
            await context.Renderer.Dispatcher.InvokeAsync(static () => Task.CompletedTask);
            await Task.Yield();
        }
    }
}
