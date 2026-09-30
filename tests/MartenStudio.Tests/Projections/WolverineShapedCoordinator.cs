using System.Reflection;

using JasperFx.Events.Daemon;

using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// A projection coordinator with the shape of Wolverine's managed event-subscription distribution, and no
/// Wolverine anywhere (AGENTS.md hard rule 1).
/// </summary>
/// <remarks>
/// <para>
/// Written from <c>Wolverine.Marten.Distribution.WolverineProjectionCoordinator</c> and
/// <c>Wolverine.Runtime.Agents.EventStoreAgents</c> (read at <c>D:\Work\wolverine</c>, 2026-09-29):
/// <c>DaemonForMainDatabase()</c> and <c>DaemonForDatabase()</c> throw <c>NotSupportedException</c> with
/// exactly <see cref="NotSupportedMessage" />, whatever they are asked; <c>AllDaemonsAsync()</c> answers -
/// in the real thing with the daemons its agents run, which <see cref="Daemons" /> lets a test say;
/// <c>PauseAsync()</c> and <c>ResumeAsync()</c> are <c>StopAllAsync</c> and <c>StartAllAsync</c> on this
/// node.
/// </para>
/// <para>
/// Every member counts its calls, because the property the studio has to keep is stronger than "does not
/// log a warning": for a store whose <c>AsyncMode</c> is <c>ExternallyManaged</c> it must not ask this
/// coordinator anything at all, and for any store it reads as externally managed it must never reach
/// <see cref="PauseAsync" /> or <see cref="ResumeAsync" /> - which on Wolverine would stop and start the
/// agents on this node behind the distribution's back.
/// </para>
/// </remarks>
internal sealed class WolverineShapedCoordinator : MartenCoordinator
{
    /// <summary>Wolverine's own message, verbatim.</summary>
    public const string NotSupportedMessage =
        "This method is not supported with the Wolverine managed projection/subscription distribution";

    /// <summary>How many times any member was called.</summary>
    public int Calls { get; private set; }

    /// <summary>How many times <see cref="PauseAsync" /> was called.</summary>
    public int PauseCalls { get; private set; }

    /// <summary>How many times <see cref="ResumeAsync" /> was called.</summary>
    public int ResumeCalls { get; private set; }

    /// <summary>What <see cref="AllDaemonsAsync" /> answers. Empty unless a test says otherwise.</summary>
    public IReadOnlyList<IProjectionDaemon> Daemons { get; set; } = [];

    /// <summary>
    /// Whether <see cref="AllDaemonsAsync" /> throws <c>NotSupportedException</c> as well, for "every
    /// daemon call refuses".
    /// </summary>
    public bool SetRefuses { get; set; }

    public IProjectionDaemon DaemonForMainDatabase()
    {
        Calls++;
        throw new NotSupportedException(NotSupportedMessage);
    }

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        Calls++;
        throw new NotSupportedException(NotSupportedMessage);
    }

    public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync()
    {
        Calls++;

        return SetRefuses
            ? throw new NotSupportedException(NotSupportedMessage)
            : ValueTask.FromResult(Daemons);
    }

    public Task PauseAsync()
    {
        Calls++;
        PauseCalls++;
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        Calls++;
        ResumeCalls++;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A coordinator that exists, is asked, and fails for a reason nobody expected - the one shape that is a
/// real anomaly and is still logged at Warning, throttled.
/// </summary>
internal sealed class FailingCoordinator : MartenCoordinator
{
    /// <summary>How many times the daemon was asked for.</summary>
    public int Calls { get; private set; }

    public IProjectionDaemon DaemonForMainDatabase()
    {
        Calls++;
        throw new InvalidOperationException("The coordinator lost its advisory lock connection.");
    }

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier)
    {
        Calls++;
        throw new InvalidOperationException("The coordinator lost its advisory lock connection.");
    }

    public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync() =>
        ValueTask.FromResult<IReadOnlyList<IProjectionDaemon>>([]);

    public Task PauseAsync() => Task.CompletedTask;

    public Task ResumeAsync() => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// A coordinator that hands out a daemon for a database - one that is provably not that database's -
/// and then refuses its whole set with <c>NotSupportedException</c>.
/// </summary>
/// <remarks>
/// The shape that proves the accessor reads only the per-database lookup's <c>NotSupportedException</c> as
/// "externally managed": a coordinator that has already handed out a daemon is not refusing to, and a
/// <c>NotSupportedException</c> from the fallback is a fault like any other.
/// </remarks>
internal sealed class SetRefusingCoordinator(IProjectionDaemon strayDaemon) : MartenCoordinator
{
    public IProjectionDaemon DaemonForMainDatabase() => strayDaemon;

    public ValueTask<IProjectionDaemon> DaemonForDatabase(string databaseIdentifier) => ValueTask.FromResult(strayDaemon);

    public ValueTask<IReadOnlyList<IProjectionDaemon>> AllDaemonsAsync() =>
        throw new NotSupportedException("This coordinator does not enumerate its daemons.");

    public Task PauseAsync() => Task.CompletedTask;

    public Task ResumeAsync() => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// An <see cref="IProjectionDaemon" /> that is nothing but its tracker - which is all the accessor reads
/// to decide which database a daemon was built against.
/// </summary>
/// <remarks>
/// A <see cref="DispatchProxy" /> rather than a hand-written class, because <see cref="IProjectionDaemon" />
/// has two dozen members that move between JasperFx releases and the floor this repository builds
/// against is not the latest; a proxy implements whatever the resolved version declares. Every member but
/// <c>Tracker</c> and <c>Dispose</c> throws, so a test that reaches one learns it at once.
/// </remarks>
public class TrackerOnlyDaemon : DispatchProxy
{
    private ShardStateTracker? tracker;

    /// <summary>A daemon whose <c>Tracker</c> is <paramref name="tracker" />.</summary>
    internal static IProjectionDaemon For(ShardStateTracker? tracker)
    {
        IProjectionDaemon daemon = Create<IProjectionDaemon, TrackerOnlyDaemon>();
        ((TrackerOnlyDaemon) (object) daemon).tracker = tracker;
        return daemon;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_Tracker" => tracker,
        "Dispose" => null,
        _ => throw new InvalidOperationException($"TrackerOnlyDaemon does not implement {targetMethod?.Name}."),
    };
}
