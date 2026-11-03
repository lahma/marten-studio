using Marten.Events.Aggregation;

namespace MartenStudio.Integration.Tests.Events;

// 1.1-CMP: an aggregate that can delete its own stream, so a stream can fold to nothing - the case Marten 9.57 leaves alone
// and every older Marten compacted into an empty snapshot.

/// <summary>The event that starts a basket.</summary>
/// <param name="Owner">Whose it is.</param>
public sealed record BasketOpened(string Owner);

/// <summary>A line put in a basket.</summary>
/// <param name="Sku">What.</param>
public sealed record BasketItemAdded(string Sku);

/// <summary>The event the basket's own aggregate answers <c>ShouldDelete</c> for: after it the stream folds to no aggregate.</summary>
/// <param name="Reason">Why.</param>
public sealed record BasketClosed(string Reason);

/// <summary>The aggregate a basket stream folds into, or to nothing once it is closed.</summary>
public sealed class CompactionBasket
{
    /// <summary>The stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Whose it is.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>How many lines it holds.</summary>
    public int Items { get; set; }
}

/// <summary>Folds a basket, and deletes it when it is closed (<c>ShouldDelete</c>).</summary>
public sealed partial class CompactionBasketProjection : SingleStreamProjection<CompactionBasket, Guid>
{
    /// <summary>Starts the basket.</summary>
    public static CompactionBasket Create(BasketOpened opened) => new() { Owner = opened.Owner };

    /// <summary>Adds a line.</summary>
    public static void Apply(BasketItemAdded added, CompactionBasket basket) => basket.Items++;

    /// <summary>Closing the basket deletes the aggregate.</summary>
    public static bool ShouldDelete(BasketClosed closed) => closed is not null;
}
