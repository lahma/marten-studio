using MartenStudio.Services;
using MartenStudio.Services.Live;

namespace MartenStudio.Tests.Components;

/// <summary>
/// What the sidebar's badges are told, said outright by a test.
/// </summary>
/// <remarks>
/// The real <c>NavIndicatorService</c> reads through MartenStudio's contributor, which reads through the events and
/// projections services, which every layout test would then have to provide with a Postgres behind them - for two
/// small numbers that no test about navigation is about. The numbers below are worded by the contributor's own
/// <c>MartenNavBadges</c>, so the badges drawn are the ones the real contributor would draw. It answers nothing by default, which is also what it answers in a
/// process where the counts could not be read: no badge.
/// </remarks>
internal sealed class FakeNavIndicatorService : INavIndicatorService
{
    private readonly Queue<TaskCompletionSource<NavIndicators>> held = new();
    private readonly Lock gate = new();

    /// <summary>How many times the navigation asked.</summary>
    public int Reads { get; private set; }

    /// <summary>
    /// Holds the next read in flight until the test completes the source this returns, with whatever that read is to
    /// answer - the way a slow database keeps the real service's read open while the visitor moves on. Reads held this
    /// way are answered in the order they were asked for; every other read answers at once from the properties below.
    /// </summary>
    public TaskCompletionSource<NavIndicators> HoldNextRead()
    {
        TaskCompletionSource<NavIndicators> read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (gate)
        {
            held.Enqueue(read);
        }

        return read;
    }

    /// <summary>How many dead letters the badge is told about, or <see langword="null" /> for "unknown".</summary>
    public long? DeadLetters { get; set; }

    /// <summary>Whether a shard is lagging, paused or failed.</summary>
    public bool ProjectionsNeedAttention { get; set; }

    /// <summary>What the amber dot's tooltip says.</summary>
    public string? ProjectionsExplanation { get; set; } = "OrderSummary:All is 5,000 events behind";

    /// <summary>What <see cref="ReadAsync" /> throws, when a test is about a sidebar that must not break.</summary>
    public Exception? Failure { get; set; }

    /// <summary>The health glance the badge is told about (H1), or <see langword="null" /> for none.</summary>
    public MartenStudio.Services.Health.HealthGlance? Health { get; set; }

    public Task<NavIndicators> ReadAsync(CancellationToken cancellationToken = default)
    {
        Reads++;

        if (Failure is not null)
        {
            return Task.FromException<NavIndicators>(Failure);
        }

        lock (gate)
        {
            if (held.TryDequeue(out TaskCompletionSource<NavIndicators>? read))
            {
                return read.Task;
            }
        }

        return Task.FromResult(new NavIndicators(
            MartenNavBadges.From(
                DeadLetters,
                ProjectionsNeedAttention,
                ProjectionsNeedAttention ? ProjectionsExplanation : null))
        {
            Health = Health,
        });
    }
}
