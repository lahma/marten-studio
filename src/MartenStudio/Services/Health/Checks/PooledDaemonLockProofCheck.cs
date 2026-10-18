using System.Globalization;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What C10 reads.</summary>
/// <param name="Configuration">The daemon mode, the lock setting and the data source's endpoints.</param>
/// <param name="FirstProcess">The server process of the first of two consecutive transactions, or <see langword="null" /> when not probed.</param>
/// <param name="SecondProcess">The server process of the second.</param>
internal sealed record PooledLockProbeFacts(DaemonLockPooling Configuration, int? FirstProcess, int? SecondProcess)
{
    /// <summary>Whether the probe ran.</summary>
    public bool Probed => FirstProcess is not null && SecondProcess is not null;

    /// <summary>Whether two consecutive transactions on one connection reached two server processes.</summary>
    public bool Split => Probed && FirstProcess != SecondProcess;
}

/// <summary>
/// C10: consecutive transactions on one connection reach different Postgres server processes - proof of a
/// transaction-mode pooler - while the HotCold daemon's leadership lock is session-scoped.
/// </summary>
/// <remarks>
/// <para>
/// <b>The probe.</b> <c>select pg_backend_pid()</c>, twice, in two consecutive read-only transactions on the sweep's one
/// connection - which the sweep opens from the store database's own data source, with its pooling as configured. Postgres
/// gives every connection a server process of its own for its whole life, so on a direct connection the two answers are
/// the same; a transaction-mode pooler is free to run each transaction on a different server connection, and when it does,
/// the answers differ. Each transaction is the sweep's ordinary read-only one, under its <c>statement_timeout</c>; nothing
/// is locked or written.
/// </para>
/// <para>
/// <b>A different process is proof; the same one is not.</b> A pooler with one idle server connection, or one that hands
/// the most recently released back first (PgBouncer's default, <c>server_round_robin = 0</c>), answers the same both times,
/// so "the same" is inconclusive and quiet. Two answers that differ cannot come from a direct connection - unless the
/// data source multiplexes, which sends each command over whichever physical connection is free; then the probe proves
/// nothing and is not run. The sweep runs its checks one after the other, so nothing else uses the connection between the
/// two statements, and it never swaps a connection that is still open.
/// </para>
/// <para>
/// <b>Critical, and only for the configuration it breaks</b>: a HotCold daemon in this process whose lock is
/// session-scoped (<c>Events.UseAdvisoryLockTransaction = false</c>, Marten's default from 9.41 - see K04). Then the
/// leadership lock is held by a server process the pooler lends to other clients. For Solo, <c>ExternallyManaged</c>, a
/// process with no daemon, or a transaction-scoped lock, nothing is probed.
/// </para>
/// <para>
/// The two process ids are the studio's own sessions, but a process id is a session's name, so they are evidence behind
/// <see cref="HealthGate.Activity" />; that the two differ is the database's.
/// </para>
/// </remarks>
[HealthCheck("C10", HealthArea.Connections, HealthCostTier.Catalog, "HotCold daemon behind a transaction pooler, proven")]
internal sealed class PooledDaemonLockProofCheck : HealthCheck<PooledLockProbeFacts>
{
    /// <inheritdoc />
    public override async Task<PooledLockProbeFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DaemonLockPooling configuration = DaemonLockPooling.Read(context);

        if (!configuration.SessionLockUnderHotCold)
        {
            return new PooledLockProbeFacts(configuration, null, null);
        }

        if (configuration.Endpoints?.Multiplexing == true)
        {
            throw new HealthCheckUnavailableException(
                "This database's data source multiplexes (Multiplexing=true), which sends each command over whichever physical "
                + "connection is free, so two transactions reaching two server processes would prove nothing about a pooler.");
        }

        // Two transactions, back to back, on the sweep's one connection: the runner opens each in a read-only transaction
        // of its own and rolls it back. A cached query, so both statements are the same text in Show SQL.
        var probe = HealthServerQueries.BackendProcess();
        int first = await context.Sql.ScalarAsync<int>(probe, cancellationToken).ConfigureAwait(false);
        int second = await context.Sql.ScalarAsync<int>(probe, cancellationToken).ConfigureAwait(false);

        return new PooledLockProbeFacts(configuration, first, second);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PooledLockProbeFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        DaemonLockPooling configuration = facts.Configuration;

        if (configuration.NotApplicable is { } reason)
        {
            return HealthVerdict.Quiet(reason);
        }

        if (!facts.Probed)
        {
            return HealthVerdict.Quiet("The probe did not run.");
        }

        if (!facts.Split)
        {
            return HealthVerdict.Quiet(
                "Two consecutive transactions on one connection reached the same Postgres server process. That is what a direct "
                + "connection does - and what a pooler with one idle server connection does too, so it is not proof that there is none.");
        }

        List<HealthEvidence> evidence =
        [
            new("Async mode", configuration.AsyncMode.ToString()),
            new("UseAdvisoryLockTransaction", configuration.DescribeLockSetting(evaluation.Runtime)),
            new("Server process of the first transaction", "pid " + facts.FirstProcess!.Value.ToString(CultureInfo.InvariantCulture), HealthGate.Activity),
            new("Server process of the second transaction", "pid " + facts.SecondProcess!.Value.ToString(CultureInfo.InvariantCulture), HealthGate.Activity),
        ];

        if (configuration.Endpoints is { } endpoints)
        {
            evidence.Add(new HealthEvidence(
                "Data source connects to",
                string.Join(", ", endpoints.Endpoints.Select(static x => x.Host + ":" + x.Port.ToString(CultureInfo.InvariantCulture)))));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C10",
            Severity = HealthSeverity.Critical,
            Title = "A transaction-mode pooler is in front of this database, and the HotCold daemon's leadership lock is session-scoped",
            WhyItMatters =
                "Two consecutive transactions on one of this data source's connections ran on two different Postgres server processes, "
                + "which only a pooler that pools by transaction does. The HotCold daemon takes its leadership with a session-scoped "
                + "advisory lock, which belongs to the server process that took it - a process this pooler lends to other clients "
                + "between transactions. The lock then belongs to whoever borrows that process next, the leader cannot release it from "
                + "its own, and two nodes can run the same projection at once, or none can take it over.",
            Evidence = evidence,
            Caveat =
                "Measured on the studio's own connection, from this database's data source as this process configures it. A daemon on "
                + "another node runs with that node's connection string, which may not go through the pooler; this says what this "
                + "process's daemon would meet. The two process ids are shown only with MartenStudioOptions.Capabilities.ViewDatabaseActivity.",
            Remedy = DaemonLockPooling.Remedy,
        });
    }
}
