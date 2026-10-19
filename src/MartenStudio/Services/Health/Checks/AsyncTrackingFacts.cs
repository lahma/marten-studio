using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Projections;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What K02 and K03 read: whether the daemon has anything to run, and the tracking switch.</summary>
/// <param name="HasAsyncWork">
/// Whether the store registers an async projection or a subscription - or <see langword="null" /> when its
/// configuration could not be read, which is not taken as a yes: both checks fire only for a store known to
/// have work for the daemon.
/// </param>
/// <param name="AsyncShards">How many async shards it registers, or <see langword="null" /> when that cannot be listed.</param>
/// <param name="Enabled">The switch the check is about.</param>
internal sealed record AsyncTrackingFacts(bool? HasAsyncWork, int? AsyncShards, bool Enabled)
{
    /// <summary>What a status row says when the store's projections could not be read.</summary>
    public const string UnreadableNote =
        "The studio could not read whether this store registers an async projection or a subscription, so it says nothing about this setting.";

    /// <summary>The facts for one switch, read from the store's configuration alone - no connection.</summary>
    public static AsyncTrackingFacts Read(HealthCheckContext context, Func<IReadOnlyStoreOptions, bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(enabled);

        return new AsyncTrackingFacts(
            AsyncWorkOf(context.Store),
            CountShards(context.Store),
            enabled(context.StoreOptions));
    }

    /// <summary>
    /// Whether <paramref name="store" /> registers an async projection or a subscription, or
    /// <see langword="null" /> when that cannot be read.
    /// </summary>
    /// <remarks>
    /// <c>ProjectionGraph.HasAnyAsyncProjections()</c>, which is <c>All.Any(async) || _subscriptions.Any()</c> at
    /// JasperFx.Events 2.74.0 (Marten 9.39.0, the floor) and at 2.87.1 (Marten 9.50.2, the latest tested) alike,
    /// and so counts a subscription registered with <c>Events.Subscribe(...)</c>, which
    /// <c>Events.Projections()</c> never lists. The Projections screen's own reader answers "yes" to a store it
    /// cannot read, so that its card says the informative sentence; a check that would turn that into a
    /// finding would be advising a setting for a daemon that may have nothing to run, so this one answers
    /// <see langword="null" /> instead.
    /// </remarks>
    internal static bool? AsyncWorkOf(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            return store.Options.Events.Daemon is ProjectionOptions projections
                ? projections.HasAnyAsyncProjections()
                : store.Options.Events.Projections().Any(static x => x.Lifecycle == ProjectionLifecycle.Async);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The async shards <c>ProjectionOptions.AllShards()</c> lists - configuration only, the same list the
    /// daemon starts from - or <see langword="null" /> for an options object of another shape.
    /// </summary>
    private static int? CountShards(IDocumentStore store)
    {
        try
        {
            return store.Options is StoreOptions concrete ? concrete.Projections.AllShards().Count : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The evidence line about what the daemon has to run.</summary>
    public HealthEvidence AsyncWorkEvidence() => new(
        "Async projections and subscriptions",
        AsyncShards is { } count
            ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " shard" + (count == 1 ? string.Empty : "s")
            : "at least one");
}
