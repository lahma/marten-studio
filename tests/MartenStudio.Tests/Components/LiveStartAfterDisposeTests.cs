using Bunit;

using MartenStudio.Components.Pages;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Projections;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Live;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Components;

/// <summary>
/// 1.2-LIVE-FU: the Overview starts its polling loop in the first render, after reads it awaits, and a visitor who leaves during
/// those reads disposed it under the await: starting the loop then either throws <c>ObjectDisposedException</c> (the page's token
/// source is gone, which is not a cancellation and ends the circuit) or starts a loop nothing disposes.
/// </summary>
public class LiveStartAfterDisposeTests
{
    private const string Muted = "martenStudio.health.getMuted";
    private const string Watch = "martenStudio.visibility.watch";

    [Fact]
    public async Task The_Overview_left_during_its_first_reads_starts_no_loop_and_ends_no_circuit()
    {
        await using StudioComponentContext context = new();
        context.StoreInfo.WithStore();
        await context.ReadyAsync();

        var muted = context.JSInterop.Setup<string?>(Muted, static _ => true);

        var page = context.Render<Overview>();
        page.WaitForAssertion(() => context.JSInterop.Invocations.Select(static x => x.Identifier).Should().Contain(Muted));

        await context.DisposeComponentsAsync();
        muted.SetResult(null);

        // The page's continuation, and whatever it throws, are hops on the dispatcher.
        await LetTheContinuationRunAsync(context);

        Task<Exception> unhandled = context.Renderer.UnhandledException;
        string said = unhandled.IsCompleted ? (await unhandled).ToString() : string.Empty;

        unhandled.IsCompleted.Should().BeFalse("an exception out of OnAfterRenderAsync ends a circuit: " + said);
        context.JSInterop.Invocations.Select(static x => x.Identifier).Should().NotContain(Watch, "a loop was started after the page was disposed");
    }

    /// <summary>
    /// Projections takes a live-state lease in its first render; a page left during that subscription releases the lease when it
    /// arrives - the disposal had already gone by and nothing else would - and starts no loop.
    /// </summary>
    [Fact]
    public async Task Projections_left_during_its_live_state_subscription_releases_the_lease_and_starts_no_loop()
    {
        var data = new FakeProjectionDataService().WithProjection("DailySales");
        data.SubscribeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using StudioComponentContext context = new(services =>
        {
            services.AddSingleton<IProjectionDataService>(data);
            services.AddScoped<StudioLiveUpdatesFactory>();
        });
        context.WithStores("default");
        await context.State.EnsureInitializedAsync(Xunit.TestContext.Current.CancellationToken);

        var page = context.Render<Page>();
        page.WaitForAssertion(() => data.Subscriptions.Should().Be(1));

        await context.DisposeComponentsAsync();
        data.SubscribeGate.SetResult();

        await LetTheContinuationRunAsync(context);

        Task<Exception> unhandled = context.Renderer.UnhandledException;
        string said = unhandled.IsCompleted ? (await unhandled).ToString() : string.Empty;

        unhandled.IsCompleted.Should().BeFalse("an exception out of OnAfterRenderAsync ends a circuit: " + said);
        data.Releases.Should().Be(1, "the lease taken after the page was disposed is released at once");
        context.JSInterop.Invocations.Select(static x => x.Identifier).Should().NotContain(Watch);
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
