using System.Collections.Concurrent;
using System.Reflection;

using Marten;

namespace MartenStudio.Services.Events;

/// <summary>
/// Calls the typed session-level <c>CompactStreamAsync&lt;T&gt;</c> for an aggregate type known only at run
/// time, and saves the session.
/// </summary>
/// <remarks>
/// <para>
/// <b>The typed overload, on the studio's own session.</b> <c>IEventStoreOperations.CompactStreamAsync&lt;T&gt;
/// (Guid | string, Action&lt;StreamCompactingRequest&lt;T&gt;&gt;?)</c> is a JasperFx.Events 2.74.0 default
/// interface member that Marten's <c>EventStore</c> implements; it queues the rewrite, the delete and the
/// watermark on the session it is called on, and <c>SaveChangesAsync</c> runs them in one transaction. That
/// is exactly what Marten's own untyped <c>IEventStore.CompactStreamAsync</c> does after resolving the type,
/// except on a session the studio opened - pinned to the scope's database and tenant with
/// <c>SessionOptions.ForDatabase</c> (hard rule 15) - rather than on <c>LightweightSession()</c>.
/// </para>
/// <para>
/// <b>No archiver.</b> The request is configured with the version cutoff and the cancellation token and nothing else, so
/// <c>StreamCompactingRequest&lt;T&gt;.Archiver</c> is null and Marten deletes the folded events without
/// copying them anywhere - which is what the confirmation dialog says.
/// </para>
/// <para>
/// <b>The call.</b> <see cref="MethodInfo.MakeGenericMethod" /> over one compiled generic body, cached as a
/// strongly typed delegate per aggregate type, the pattern <see cref="AggregateStreamInvoker" /> uses for
/// Marten's aggregator: the signature Marten owns is resolved by the compiler here, and only
/// the closing over a runtime type is reflection.
/// </para>
/// </remarks>
internal static class StreamCompactionInvoker
{
    private static readonly ConcurrentDictionary<Type, Func<IDocumentSession, object, long, CancellationToken, Task>> Invokers = new();

    private static readonly MethodInfo Definition =
        typeof(StreamCompactionInvoker).GetMethod(nameof(CompactAndSaveAsync), BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{nameof(StreamCompactionInvoker)}.{nameof(CompactAndSaveAsync)} is missing.");

    /// <summary>
    /// Compacts <paramref name="streamId" /> into <paramref name="aggregateType" /> on
    /// <paramref name="session" /> and saves it.
    /// </summary>
    /// <param name="session">A write session on the scope's own database and tenant.</param>
    /// <param name="aggregateType">The registered single-stream aggregate the stream's type resolved to.</param>
    /// <param name="streamId">The id, already parsed to a <see cref="Guid" /> or a string.</param>
    /// <param name="version">
    /// The version to fold through - <c>StreamCompactingRequest&lt;T&gt;.Version</c>, which Marten's fetch
    /// applies as <c>version &lt;= ?</c>. The dialog's own version, so an event appended after it opened is left
    /// after the snapshot rather than folded into something nobody described. Never 0, which is Marten's
    /// "latest".
    /// </param>
    /// <param name="cancellationToken">Cancels the read and the save.</param>
    public static Task CompactAsync(
        IDocumentSession session,
        Type aggregateType,
        object streamId,
        long version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentNullException.ThrowIfNull(streamId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);

        // The last line of defence for the one id Marten reads as "no stream filter" (StreamCompactionRules,
        // EmptyStreamId): the plan and the service refuse it first, and nothing reaches Marten with it.
        if (StreamCompactionRules.IsWholeLogId(streamId))
        {
            throw new ArgumentException(
                "An empty stream id is no stream filter to Marten's fetch, and compacting it would rewrite the whole event log.",
                nameof(streamId));
        }

        if (aggregateType.IsValueType || aggregateType.ContainsGenericParameters || aggregateType.IsAbstract)
        {
            throw new ArgumentException(
                $"'{aggregateType.Name}' cannot be compacted into: CompactStreamAsync<T> needs a concrete reference type.",
                nameof(aggregateType));
        }

        Func<IDocumentSession, object, long, CancellationToken, Task> invoker = Invokers.GetOrAdd(
            aggregateType,
            static type => Definition
                .MakeGenericMethod(type)
                .CreateDelegate<Func<IDocumentSession, object, long, CancellationToken, Task>>());

        return invoker(session, streamId, version, cancellationToken);
    }

    /// <summary>The one generic body every closed delegate points at.</summary>
    /// <remarks>
    /// Both identities are named explicitly: the id was parsed against the store's stream identity, and the
    /// wrong overload is refused by Marten's <c>EnsureAsGuidStorage</c> / <c>EnsureAsStringStorage</c> with a
    /// message that names the mistake (9.39.0).
    /// </remarks>
    private static async Task CompactAndSaveAsync<T>(
        IDocumentSession session,
        object streamId,
        long version,
        CancellationToken cancellationToken) where T : class
    {
        void Configure(JasperFx.Events.Protected.StreamCompactingRequest<T> request)
        {
            request.Version = version;
            request.CancellationToken = cancellationToken;
        }

        if (streamId is Guid guid)
        {
            await session.Events.CompactStreamAsync<T>(guid, Configure).ConfigureAwait(false);
        }
        else
        {
            await session.Events.CompactStreamAsync<T>((string) streamId, Configure).ConfigureAwait(false);
        }

        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
