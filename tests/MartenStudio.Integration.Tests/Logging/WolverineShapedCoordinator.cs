using JasperFx.Events.Daemon;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Integration.Tests.Logging;

/// <summary>
/// A projection coordinator with the shape of Wolverine's managed event-subscription distribution, and no
/// Wolverine anywhere (AGENTS.md hard rule 1).
/// </summary>
/// <remarks>
/// The same fake the fast suite uses (<c>MartenStudio.Tests.Projections.WolverineShapedCoordinator</c>),
/// repeated because the two test projects do not reference each other. Written from
/// <c>Wolverine.Marten.Distribution.WolverineProjectionCoordinator</c> and
/// <c>Wolverine.Runtime.Agents.EventStoreAgents</c>: both daemon lookups throw
/// <c>NotSupportedException</c> with <see cref="NotSupportedMessage" />, whatever they are asked. Every
/// member counts its calls, because for a store whose <c>AsyncMode</c> is <c>ExternallyManaged</c> the
/// studio must not ask it anything - and pause and resume are counted on their own, because for any store
/// the studio reads as externally managed it must never reach them: on Wolverine they are
/// <c>StopAllAsync</c> and <c>StartAllAsync</c> on this node.
/// </remarks>
internal sealed class WolverineShapedCoordinator : MartenCoordinator
{
    /// <summary>Wolverine's own message, verbatim.</summary>
    public const string NotSupportedMessage =
        "This method is not supported with the Wolverine managed projection/subscription distribution";

    private int calls;
    private int pauseCalls;
    private int resumeCalls;

    /// <summary>How many times any member was called, from any thread.</summary>
    public int Calls => Volatile.Read(ref calls);

    /// <summary>How many times <see cref="PauseAsync" /> was called.</summary>
    public int PauseCalls => Volatile.Read(ref pauseCalls);

    /// <summary>How many times <see cref="ResumeAsync" /> was called.</summary>
    public int ResumeCalls => Volatile.Read(ref resumeCalls);

    public IProjectionDaemon DaemonForMainDatabase()
    {
        Interlocked.Increment(ref calls);
        throw new NotSupportedException(NotSupportedMessage);
    }

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        Interlocked.Increment(ref calls);
        throw new NotSupportedException(NotSupportedMessage);
    }

    public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
    {
        Interlocked.Increment(ref calls);
        return ValueTask.FromResult<IReadOnlyList<IProjectionDaemon>>([]);
    }

    public Task PauseAsync()
    {
        Interlocked.Increment(ref calls);
        Interlocked.Increment(ref pauseCalls);
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        Interlocked.Increment(ref calls);
        Interlocked.Increment(ref resumeCalls);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref calls);
        return Task.CompletedTask;
    }
}
