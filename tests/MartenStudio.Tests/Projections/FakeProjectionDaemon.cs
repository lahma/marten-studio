using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Microsoft.Extensions.Logging.Abstractions;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// A daemon that records which rebuild overload it was asked for, and does nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than mocked (the package budget has no mocking library, deliberately). Everything
/// but the rebuild overloads throws: a test that reached one of them would be a test about something this
/// fake is not pretending to be.
/// </para>
/// <para>
/// The point of it is the pair of overloads. <c>RebuildProjectionAsync(name, token)</c> looks like the
/// plain one and is not - JasperFx 2.69.3 forwards it to a <em>five minute</em> per-shard timeout, applied
/// after the projection's tables have already been torn down. This fake is how a test can see which of
/// the two the studio actually calls, and with what budget.
/// </para>
/// </remarks>
internal sealed class FakeProjectionDaemon : IProjectionDaemon
{
    /// <summary>Every rebuild call, as "name/timeout" or "name/(default)".</summary>
    public List<string> Rebuilds { get; } = [];

    /// <summary>The timeout the last rebuild was given, or <see langword="null" /> when none was named.</summary>
    public TimeSpan? LastShardTimeout { get; private set; }

    /// <summary>Whether the overload that hides Marten's five-minute default was ever used.</summary>
    public bool UsedTimeoutlessOverload { get; private set; }

    public ShardStateTracker Tracker { get; } = new(NullLogger.Instance);

    public bool IsRunning => true;

    public DateTimeOffset? HighWaterLastPolledAt => null;

    public bool IsHighWaterStale => false;

    public Task RebuildProjectionAsync(string projectionName, TimeSpan shardTimeout, CancellationToken token)
    {
        Rebuilds.Add($"{projectionName}/{shardTimeout}");
        LastShardTimeout = shardTimeout;
        return Task.CompletedTask;
    }

    public Task RebuildProjectionAsync(string projectionName, CancellationToken token)
    {
        Rebuilds.Add($"{projectionName}/(default)");
        UsedTimeoutlessOverload = true;
        return Task.CompletedTask;
    }

    public Task RebuildProjectionAsync<TView>(CancellationToken token) => throw NotUsed();

    public Task RebuildProjectionAsync<TView>(TimeSpan shardTimeout, CancellationToken token) => throw NotUsed();

    public Task RebuildProjectionAsync(Type projectionType, CancellationToken token) => throw NotUsed();

    public Task RebuildProjectionAsync(Type projectionType, TimeSpan shardTimeout, CancellationToken token) => throw NotUsed();

    public Task PrepareForRebuildsAsync() => throw NotUsed();

    public Task StartAgentAsync(string shardName, CancellationToken token) => throw NotUsed();

    public Task<ISubscriptionAgent> StartAgentAsync(ShardName name, CancellationToken token) => throw NotUsed();

    public Task StopAgentAsync(string shardName, Exception? ex = null) => throw NotUsed();

    public Task StopAgentAsync(ShardName shardName, Exception? ex = null) => throw NotUsed();

    public Task StartAllAsync() => throw NotUsed();

    public Task StopAllAsync() => throw NotUsed();

    public Task CatchUpAsync(CancellationToken cancellation) => throw NotUsed();

    public Task CatchUpAsync(TimeSpan timeout, CancellationToken cancellation) => throw NotUsed();

    public Task WaitForNonStaleData(TimeSpan timeout) => throw NotUsed();

    public long HighWaterMark() => throw NotUsed();

    public Task RestartHighWaterAgentAsync(CancellationToken token) => throw NotUsed();

    public AgentStatus StatusFor(string shardName) => throw NotUsed();

    public IReadOnlyList<ISubscriptionAgent> CurrentAgents() => throw NotUsed();

    public bool HasAnyPaused() => throw NotUsed();

    public void EjectPausedShard(string shardName) => throw NotUsed();

    public Task WaitForShardToBeRunning(string shardName, TimeSpan timeout) => throw NotUsed();

    /// <summary>Every rewind, as "subscription/floor".</summary>
    public List<string> Rewinds { get; } = [];

    /// <summary>The floor the last rewind was given.</summary>
    public long? LastSequenceFloor { get; private set; }

    /// <summary>The timestamp the last rewind was given. The studio never sets one.</summary>
    public DateTimeOffset? LastTimestamp { get; private set; }

    /// <summary>
    /// Records the rewind, so a test can see the floor and prove the argument order.
    /// </summary>
    /// <remarks>
    /// The order is the thing worth pinning: the token is the <em>second</em> parameter and both of the
    /// interesting arguments are optional, so a call written in the usual C# order would compile and
    /// silently mean <c>sequenceFloor: 0</c> - a full replay of the projection rather than a rewind to
    /// one event.
    /// </remarks>
    public Task RewindSubscriptionAsync(
        string subscriptionName,
        CancellationToken token,
        long? sequenceFloor = 0,
        DateTimeOffset? timestamp = null)
    {
        Rewinds.Add(subscriptionName + "/" + (sequenceFloor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"));
        LastSequenceFloor = sequenceFloor;
        LastTimestamp = timestamp;
        return Task.CompletedTask;
    }

    public void Dispose() => ((IDisposable) Tracker).Dispose();

    private static NotSupportedException NotUsed([System.Runtime.CompilerServices.CallerMemberName] string? member = null) =>
        new($"FakeProjectionDaemon does not implement {member}; no test here is about it.");
}
