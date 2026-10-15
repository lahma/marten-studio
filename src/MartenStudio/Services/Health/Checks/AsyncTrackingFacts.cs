using Marten;

using MartenStudio.Services.Projections;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What K02 and K03 read: whether the daemon has anything to run, and the tracking switch.</summary>
/// <param name="HasAsyncWork">Whether the store registers an async projection or a subscription.</param>
/// <param name="AsyncShards">How many async shards it registers, or <see langword="null" /> when that cannot be listed.</param>
/// <param name="Enabled">The switch the check is about.</param>
internal sealed record AsyncTrackingFacts(bool HasAsyncWork, int? AsyncShards, bool Enabled)
{
    /// <summary>The facts for one switch, read from the store's configuration alone - no connection.</summary>
    public static AsyncTrackingFacts Read(HealthCheckContext context, Func<IReadOnlyStoreOptions, bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(enabled);

        return new AsyncTrackingFacts(
            DaemonAccessor.HasAsyncWork(context.Store),
            CountShards(context.Store),
            enabled(context.StoreOptions));
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
