using System.Globalization;

using Marten.Events.Projections;

namespace MartenStudio.SampleDomain.Events;

/// <summary>
/// One row per calendar day, fed by events from every order stream.
/// </summary>
public sealed class DailySales
{
    /// <summary>The day, as <c>yyyy-MM-dd</c>. A string identity, which the studio has to render as one.</summary>
    public string Id { get; set; } = string.Empty;

    public int Orders { get; set; }

    public int Items { get; set; }

    public decimal Revenue { get; set; }

    public int Cancellations { get; set; }
}

/// <summary>
/// <see cref="DailySales" />, rolled up across streams by day, <b>asynchronously</b>.
/// </summary>
/// <remarks>
/// The grouping is <c>Identity&lt;IOrderEvent&gt;</c>: a multi-stream projection slices on something the
/// event body carries, and every demo order event carries the day it happened. This is the projection the
/// daemon actually has work to do for, so it is the one a rebuild on the projections screen is
/// demonstrated with.
/// </remarks>
public sealed partial class DailySalesProjection : MultiStreamProjection<DailySales, string>
{
    public DailySalesProjection()
    {
        Name = "DailySales";
        Identity<IOrderEvent>(static x => Day(x.OccurredAt));
    }

    public static void Apply(OrderPlaced placed, DailySales view) => view.Orders++;

    public static void Apply(ItemAdded added, DailySales view)
    {
        view.Items += added.Quantity;
        view.Revenue += added.Quantity * added.Price;
    }

    public static void Apply(OrderCancelled cancelled, DailySales view) => view.Cancellations++;

    /// <summary>The day an event belongs to, which is also the document identity.</summary>
    public static string Day(DateTimeOffset occurredAt) =>
        occurredAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
