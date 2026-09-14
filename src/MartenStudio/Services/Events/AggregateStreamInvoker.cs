using System.Collections.Concurrent;
using System.Reflection;

using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Daemon;
using JasperFx.Events.Subscriptions;

using Marten;
using Marten.Schema;

namespace MartenStudio.Services.Events;

/// <summary>
/// Finds the aggregate types a stream can be replayed into, and calls the generic
/// <c>AggregateStreamAsync&lt;T&gt;</c> for a <see cref="Type" /> only known at run time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovery.</b> Marten's <c>SingleStreamProjection&lt;TDoc,TId&gt;</c> derives from
/// <c>JasperFx.Events.Aggregation.JasperFxSingleStreamProjectionBase&lt;…&gt;</c>, which implements both
/// <see cref="ISubscriptionSource" /> and <see cref="IAggregateProjection" /> - so every row of
/// <c>store.Options.Events.Projections()</c> that is an aggregation answers <c>AggregateType</c>,
/// <c>IdentityType</c> and <c>Scope</c> directly, and there is no need to take apart the generic
/// arguments of <c>ImplementationType</c>. <c>Snapshot&lt;T&gt;()</c> goes the same way: it closes
/// <c>SingleStreamProjection&lt;T, TId&gt;</c> for you (verified against Marten 9.35's
/// <c>ProjectionOptions.SingleStreamProjection&lt;T&gt;</c>).
/// </para>
/// <para>
/// The interface cast is nevertheless not enough on its own. A container-scoped or composite
/// registration wraps the projection in a source that is an <see cref="ISubscriptionSource" /> but not an
/// <see cref="IAggregateProjection" /> - <c>ScopedProjectionWrapper</c>, <c>CompositeProjectionSource</c>
/// and friends - and for those the only thing left is <see cref="ISubscriptionSource.ImplementationType" />,
/// whose base chain still carries <c>TDoc</c> as the first generic argument of
/// <c>JasperFxAggregationProjectionBase&lt;TDoc,TId,…&gt;</c>. So both routes are tried, in that order.
/// </para>
/// <para>
/// <b>"Whose stream matches".</b> A single-stream projection keyed by <see cref="Guid" /> cannot be
/// replayed from a string-identified store and the reverse is equally meaningless, so a candidate is kept
/// only when its identity type agrees with the store's <see cref="StreamIdentity" />. Value types are
/// dropped as well: <c>AggregateStreamAsync&lt;T&gt;</c> is constrained <c>where T : class</c>.
/// </para>
/// <para>
/// Worth knowing about that check: Marten 9.35 refuses the mismatch itself, in
/// <c>ProjectionGraph.AssertValidity</c>, so <c>DocumentStore.For</c> throws
/// <c>InvalidProjectionException</c> ("Id type mismatch") before a store carrying one can exist. The
/// filter therefore never fires on a store that was built at all. It stays because the wrapper path below
/// reads generic arguments off <c>ImplementationType</c> rather than off a validated registration, and
/// because the interface route may one day see a source Marten did not validate.
/// </para>
/// <para>
/// <b>The call.</b> <c>AggregateStreamAsync&lt;T&gt;</c> is generic in the aggregate, and the studio only
/// ever holds a <see cref="Type" />, so <see cref="MethodInfo.MakeGenericMethod" /> is the only route -
/// which AGENTS.md allows here, and this is the one place it happens. It is paid once per aggregate type:
/// the closed method becomes a strongly typed delegate that is cached process-wide, so the reflection cost
/// is not on the version stepper's per-click path.
/// </para>
/// </remarks>
internal static class AggregateStreamInvoker
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>One cached delegate per aggregate type, closed over the generic method.</summary>
    private static readonly ConcurrentDictionary<Type, Func<IQuerySession, object, long, CancellationToken, Task<object?>>>
        Invokers = new();

    private static readonly MethodInfo Definition =
        typeof(AggregateStreamInvoker).GetMethod(nameof(AggregateAsync), PrivateStatic)
        ?? throw new InvalidOperationException($"{nameof(AggregateStreamInvoker)}.{nameof(AggregateAsync)} is missing.");

    /// <summary>
    /// The aggregate types this store's streams can be replayed into: every registered single-stream
    /// projection first, then every document type the store knows, as the manual fallback.
    /// </summary>
    /// <param name="options">The store's own configuration - nothing here is guessed.</param>
    /// <returns>The candidates, projections first, each type appearing once.</returns>
    public static IReadOnlyList<AggregateTypeCandidate> Candidates(IReadOnlyStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        StreamIdentity identity = options.Events.StreamIdentity;

        List<AggregateTypeCandidate> candidates = [];
        HashSet<Type> seen = [];

        foreach (ISubscriptionSource source in options.Events.Projections())
        {
            Type? aggregate = SingleStreamAggregateType(source, identity);
            if (aggregate is not null && IsUsable(aggregate) && seen.Add(aggregate))
            {
                candidates.Add(Describe(aggregate, AggregateCandidateSource.SingleStreamProjection));
            }
        }

        foreach (IDocumentType documentType in options.AllKnownDocumentTypes())
        {
            Type candidate = documentType.DocumentType;
            if (IsUsable(candidate) && seen.Add(candidate))
            {
                candidates.Add(Describe(candidate, AggregateCandidateSource.DocumentType));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Replays <paramref name="streamId" /> into <paramref name="aggregateType" /> as it stood at
    /// <paramref name="version" />.
    /// </summary>
    /// <param name="session">A session on the scope's own store and tenant.</param>
    /// <param name="aggregateType">The aggregate type, which must be a reference type.</param>
    /// <param name="streamId">The stream id, already parsed to a <see cref="Guid" /> or a string.</param>
    /// <param name="version">The version to replay to; zero means the whole stream.</param>
    /// <param name="cancellationToken">Cancels the replay.</param>
    /// <returns>The aggregate, or <see langword="null" /> when the replay produced nothing.</returns>
    public static Task<object?> AggregateAtVersionAsync(
        IQuerySession session,
        Type aggregateType,
        object streamId,
        long version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(aggregateType);
        ArgumentNullException.ThrowIfNull(streamId);

        if (!IsUsable(aggregateType))
        {
            throw new ArgumentException(
                $"'{aggregateType.Name}' cannot be an aggregate: AggregateStreamAsync<T> is constrained to reference types.",
                nameof(aggregateType));
        }

        Func<IQuerySession, object, long, CancellationToken, Task<object?>> invoker = Invokers.GetOrAdd(
            aggregateType,
            static type => Definition
                .MakeGenericMethod(type)
                .CreateDelegate<Func<IQuerySession, object, long, CancellationToken, Task<object?>>>());

        return invoker(session, streamId, version, cancellationToken);
    }

    /// <summary>The one generic body every closed delegate points at.</summary>
    /// <remarks>
    /// Both overloads are named explicitly rather than left to overload resolution on <c>object</c>: the
    /// id has already been parsed against the store's stream identity, so exactly one of them is right and
    /// picking the other would be a silent empty result rather than an error.
    /// </remarks>
    private static async Task<object?> AggregateAsync<T>(
        IQuerySession session,
        object streamId,
        long version,
        CancellationToken cancellationToken) where T : class
    {
        if (streamId is Guid guid)
        {
            return await session.Events
                .AggregateStreamAsync<T>(guid, version, token: cancellationToken)
                .ConfigureAwait(false);
        }

        return await session.Events
            .AggregateStreamAsync<T>((string) streamId, version, token: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The aggregate type behind one projection registration, when it is a single-stream one whose
    /// identity matches this store's streams.
    /// </summary>
    private static Type? SingleStreamAggregateType(ISubscriptionSource source, StreamIdentity identity)
    {
        if (source is IAggregateProjection aggregation)
        {
            return aggregation.Scope == AggregationScope.SingleStream && Matches(aggregation.IdentityType, identity)
                ? aggregation.AggregateType
                : null;
        }

        // A wrapper - container-scoped or composite - is an ISubscriptionSource but not an
        // IAggregateProjection. Its ImplementationType's base chain still carries the aggregate.
        return FromImplementationType(source.ImplementationType, identity);
    }

    /// <summary>
    /// <c>TDoc</c> and <c>TId</c> from the aggregation base in a projection type's base chain.
    /// </summary>
    /// <remarks>
    /// Matched by generic-argument shape rather than by name: <c>SingleStreamProjection&lt;TDoc,TId&gt;</c>
    /// is Marten's own subclass of <c>JasperFxSingleStreamProjectionBase&lt;TDoc,TId,TOperations,TSession&gt;</c>,
    /// and a host may well derive its own type from either. Walking to the first base type with at least
    /// two generic arguments that also implements the aggregation interface covers both, and covers
    /// whatever the next JasperFx release calls the base.
    /// </remarks>
    private static Type? FromImplementationType(Type? implementationType, StreamIdentity identity)
    {
        for (Type? type = implementationType; type is not null; type = type.BaseType)
        {
            if (!type.IsGenericType)
            {
                continue;
            }

            Type[] arguments = type.GetGenericArguments();
            if (arguments.Length < 2)
            {
                continue;
            }

            string name = type.GetGenericTypeDefinition().Name;
            if (!name.StartsWith("SingleStreamProjection", StringComparison.Ordinal)
                && !name.StartsWith("JasperFxSingleStreamProjectionBase", StringComparison.Ordinal))
            {
                continue;
            }

            return Matches(arguments[1], identity) ? arguments[0] : null;
        }

        return null;
    }

    /// <summary>Whether a projection's identity type is the one this store's streams are keyed by.</summary>
    private static bool Matches(Type? identityType, StreamIdentity identity) =>
        identity == StreamIdentity.AsString
            ? identityType == typeof(string)
            : identityType == typeof(Guid);

    /// <summary>
    /// Whether a type can be an aggregate at all: <c>AggregateStreamAsync&lt;T&gt;</c> is
    /// <c>where T : class</c>, and an open generic or an abstract type cannot be closed over.
    /// </summary>
    private static bool IsUsable(Type type) =>
        !type.IsValueType && !type.ContainsGenericParameters && !type.IsAbstract && !type.IsInterface;

    private static AggregateTypeCandidate Describe(Type type, AggregateCandidateSource source) =>
        new(type, type.Name, type.FullName ?? type.Name, source);
}
