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
/// exactly <see cref="NotSupportedMessage" />, whatever they are asked; <c>AllDaemonsAsync()</c> answers;
/// <c>PauseAsync()</c> and <c>ResumeAsync()</c> stop and start the agents.
/// </para>
/// <para>
/// Every member counts its calls, because the property the studio has to keep is stronger than "does not
/// log a warning": for a store whose <c>AsyncMode</c> is <c>ExternallyManaged</c> it must not ask this
/// coordinator anything at all.
/// </para>
/// </remarks>
internal sealed class WolverineShapedCoordinator : MartenCoordinator
{
    /// <summary>Wolverine's own message, verbatim.</summary>
    public const string NotSupportedMessage =
        "This method is not supported with the Wolverine managed projection/subscription distribution";

    /// <summary>How many times any member was called.</summary>
    public int Calls { get; private set; }

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
        return ValueTask.FromResult<IReadOnlyList<IProjectionDaemon>>([]);
    }

    public Task PauseAsync()
    {
        Calls++;
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        Calls++;
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
