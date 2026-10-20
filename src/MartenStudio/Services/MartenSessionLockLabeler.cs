using Marten;

using PostgresStudio.Services.Database;
using MartenStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="ISessionLockLabeler" />: the store's daemon leadership and shard keys and its schema-apply
/// lock, read from its options, and the relations named through the classifier the caller hands in.
/// </summary>
/// <remarks>
/// Reads configuration only - <c>StoreOptions.ApplyChangesLockId</c>, the daemon's <c>DaemonLockId</c> and every async
/// shard's lock id - and never a connection or a running daemon, so it is cheap enough for a health sweep and a
/// Sessions poll. A singleton: nothing here is about a visitor.
/// </remarks>
internal sealed class MartenSessionLockLabeler : ISessionLockLabeler
{
    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="source" /> is not a Marten store's.</exception>
    public SessionLockLabels For(ResolvedSource source, DatabaseObjectClassifier? classifier)
    {
        ArgumentNullException.ThrowIfNull(source);

        return source is ResolvedScope resolved
            ? For(resolved.Store, resolved.Registration.Key, classifier)
            : throw new ArgumentException("MartenStudio labels the locks of a Marten store only.", nameof(source));
    }

    /// <summary>The labels for <paramref name="store" />, registered under <paramref name="storeKey" />.</summary>
    /// <param name="store">The store.</param>
    /// <param name="storeKey">Its registration key.</param>
    /// <param name="classifier">
    /// The classifier over every registered store's declarations (<see cref="IDatabaseDeclarationsSource" />), or
    /// <see langword="null" /> when they could not all be read - or when only advisory keys are wanted - which names
    /// no relation at all.
    /// </param>
    internal static SessionLockLabels For(IDocumentStore store, string storeKey, DatabaseObjectClassifier? classifier)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(storeKey);

        long? apply = store.Options is StoreOptions options ? options.ApplyChangesLockId : null;

        // The daemon's ids from the one definition of the set, H2's: a projection that cannot list its shards lists
        // none, and its locks read as "not Marten's" - a label, not a judgement, and C04 then treats such a session as
        // it would any other; a shard whose id cannot be computed is nobody's, as it would be in pg_locks too.
        string schema = store.Options.Events.DatabaseSchemaName;
        int baseLockId = DaemonCheckConfiguration.DaemonLockIdOf(store.Options);
        var asyncShards = DaemonCheckConfiguration.AsyncShardsOf(store);

        List<KeyValuePair<long, string>> shards = [];
        foreach ((long id, var shard) in DaemonCheckConfiguration.ShardLockIdsFor(schema, asyncShards, baseLockId))
        {
            shards.Add(new KeyValuePair<long, string>(id, shard.Identity));
        }

        return SessionLockLabels.Create(
            storeKey, apply, baseLockId, shards, DaemonCheckConfiguration.LockKeysFor(schema, asyncShards, baseLockId), classifier);
    }
}
