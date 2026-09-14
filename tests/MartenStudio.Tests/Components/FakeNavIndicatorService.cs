using MartenStudio.Services.Live;

namespace MartenStudio.Tests.Components;

/// <summary>
/// What the sidebar's badges are told, said outright by a test.
/// </summary>
/// <remarks>
/// The real <c>NavIndicatorService</c> reads through the events and projections services, which every
/// layout test would then have to provide with a Postgres behind them - for two small numbers that no
/// test about navigation is about. It answers nothing by default, which is also what it answers in a
/// process where the counts could not be read: no badge.
/// </remarks>
internal sealed class FakeNavIndicatorService : INavIndicatorService
{
    /// <summary>How many times the navigation asked.</summary>
    public int Reads { get; private set; }

    /// <summary>How many dead letters the badge is told about, or <see langword="null" /> for "unknown".</summary>
    public long? DeadLetters { get; set; }

    /// <summary>Whether a shard is lagging, paused or failed.</summary>
    public bool ProjectionsNeedAttention { get; set; }

    /// <summary>What the amber dot's tooltip says.</summary>
    public string? ProjectionsExplanation { get; set; } = "OrderSummary:All is 5,000 events behind";

    /// <summary>What <see cref="ReadAsync" /> throws, when a test is about a sidebar that must not break.</summary>
    public Exception? Failure { get; set; }

    public Task<NavIndicators> ReadAsync(CancellationToken cancellationToken = default)
    {
        Reads++;

        if (Failure is not null)
        {
            return Task.FromException<NavIndicators>(Failure);
        }

        return Task.FromResult(new NavIndicators(
            DeadLetters,
            ProjectionsNeedAttention,
            ProjectionsNeedAttention ? ProjectionsExplanation : null));
    }
}
