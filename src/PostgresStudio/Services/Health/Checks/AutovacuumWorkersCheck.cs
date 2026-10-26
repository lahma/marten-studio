using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T16 reads: the server's workers, what maintenance runs in this database, and who runs the server.</summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Workers">The workers read.</param>
/// <param name="Running">What runs in this database now.</param>
/// <param name="Platform">Who runs the server, for the remedy's wording.</param>
internal sealed record AutovacuumWorkersFacts(
    DateTimeOffset ReadAt,
    MaintenanceWorkersRow Workers,
    IReadOnlyList<MaintenanceProgressRow> Running,
    PostgresPlatform Platform)
{
    /// <summary>
    /// How many workers autovacuum can run at once: <c>autovacuum_max_workers</c>, held on PostgreSQL 18 to
    /// <c>autovacuum_worker_slots</c>.
    /// </summary>
    public int Capacity => Workers.WorkerSlots is { } slots && slots < Workers.MaxWorkers ? slots : Workers.MaxWorkers;

    /// <summary>
    /// How many are busy, as far as the role can tell: autovacuum workers by <c>backend_type</c> with <c>pg_read_all_stats</c>,
    /// else every process running a <c>VACUUM</c> or an <c>ANALYZE</c> on the server - at least, manual ones included.
    /// </summary>
    public int Busy => Workers.CanReadAllStats ? Workers.WorkersSeen : Workers.MaintenanceRunning;

    /// <summary>Whether every worker is busy in this read.</summary>
    public bool Saturated => Workers.Autovacuum && Capacity > 0 && Busy >= Capacity;
}

/// <summary>
/// T16: every autovacuum worker on the server has been busy for several samples running - so a table that crosses its
/// trigger now waits for one to come free (R1 #12; B0 §7c).
/// </summary>
/// <remarks>
/// <para>
/// <b>Counted as the role can count them.</b> With <c>pg_read_all_stats</c> the workers are <c>pg_stat_activity</c>'s rows of
/// <c>backend_type = 'autovacuum worker'</c>. Without it a role is shown nothing of autovacuum's processes but their
/// process ids in the progress views - even on its own tables - so the count is every process running a <c>VACUUM</c> or an
/// <c>ANALYZE</c> on the server: at least the busy workers, manual runs included, and the finding says so.
/// </para>
/// <para>
/// <b>Held for <see cref="SamplesNeeded" /> samples running</b>, because a burst of autovacuum after a batch is the
/// normal case and a busy minute is not a finding. Warning. The remedy is <c>autovacuum_max_workers</c>, which on
/// PostgreSQL 18 changes with a reload up to <c>autovacuum_worker_slots</c> and before 18 needs a restart.
/// </para>
/// <para>
/// <b>It also keeps the history the Maintenance page dates progress by</b>: each running command's blocks done, by
/// process id and relation number - numbers only, never a name - so an ETA is the rate between two samples, never a guess.
/// </para>
/// </remarks>
[HealthCheck("T16", HealthArea.Tables, HealthCostTier.Catalog, "Autovacuum workers saturated", Gate = HealthGate.Database)]
internal sealed class AutovacuumWorkersCheck : HealthCheck<AutovacuumWorkersFacts>
{
    /// <summary>How many consecutive samples every worker must have been busy in.</summary>
    public const int SamplesNeeded = 3;

    /// <summary>The series T16 writes in every sweep it runs in.</summary>
    internal const string SweptSeries = "tables.autovacuum-workers.swept";

    /// <summary>The series T16 writes, as 1, while every worker is busy.</summary>
    internal const string SaturatedSeries = "tables.autovacuum-workers.saturated";

    /// <summary>The series family of a running command's blocks done: <c>…:kind:pid:relid</c>.</summary>
    internal const string ProgressSeries = "maintenance.progress-blocks:";

    /// <inheritdoc />
    public override async Task<AutovacuumWorkersFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        MaintenanceWorkersRow workers = await WorkersAsync(context).ConfigureAwait(false);
        IReadOnlyList<MaintenanceProgressRow> running = await ProgressAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        // The Maintenance page words what this role cannot see with the grant it lacks and the role it reads as; the read is
        // the sweep's one grant read, shared, and only for a role that cannot see everything.
        if (!workers.CanReadAllStats)
        {
            await PostgresGrants.SharedAsync(context).ConfigureAwait(false);
        }

        return new AutovacuumWorkersFacts(context.Now, workers, running, server.Platform);
    }

    /// <summary>The sweep's one read of the server's workers.</summary>
    internal static Task<MaintenanceWorkersRow> WorkersAsync(HealthCheckContext context) =>
        context.Shared(MaintenanceQueries.WorkersKey, async token =>
        {
            IReadOnlyList<MaintenanceWorkersRow> rows = await context.Sql.QueryAsync(MaintenanceQueries.Workers(), MaintenanceQueries.ReadWorkers, token).ConfigureAwait(false);
            return rows.Count == 1 ? rows[0] : throw new InvalidOperationException("The workers read returned no row; it always returns one.");
        });

    /// <summary>The sweep's one read of what maintenance runs in this database.</summary>
    internal static Task<IReadOnlyList<MaintenanceProgressRow>> ProgressAsync(HealthCheckContext context) =>
        context.Shared(MaintenanceQueries.ProgressKey, token => context.Sql.QueryAsync(MaintenanceQueries.Progress(), MaintenanceQueries.ReadProgress, token));

    /// <summary>The series a running command's blocks done are sampled under, or <see langword="null" /> without a relation number.</summary>
    internal static string? ProgressSeriesOf(MaintenanceProgressRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.RelationId is { } relid
            ? ProgressSeries + row.Kind.Replace(' ', '-') + ":" + row.ProcessId.ToString(CultureInfo.InvariantCulture) + ":" + relid.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(AutovacuumWorkersFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        yield return new HealthSamplePart(SweptSeries, 1);

        if (facts.Saturated)
        {
            yield return new HealthSamplePart(SaturatedSeries, 1);
        }

        HashSet<string> written = new(StringComparer.Ordinal);

        foreach (MaintenanceProgressRow row in facts.Running)
        {
            if (ProgressSeriesOf(row) is { } series && row.BlocksDone is { } done && written.Add(series))
            {
                yield return new HealthSamplePart(series, done);
            }
        }
    }

    /// <summary>How many of the latest samples T16 ran in, newest first and without a break, found every worker busy.</summary>
    internal static int SaturatedRun(HealthHistoryView history)
    {
        ArgumentNullException.ThrowIfNull(history);

        int run = 0;

        for (int i = history.Samples.Count - 1; i >= 0; i--)
        {
            HealthSample sample = history.Samples[i];

            if (!sample.Values.ContainsKey(SweptSeries))
            {
                continue;
            }

            if (!sample.Values.TryGetValue(SaturatedSeries, out double value) || value <= 0)
            {
                break;
            }

            run++;
        }

        return run;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(AutovacuumWorkersFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        MaintenanceWorkersRow workers = facts.Workers;
        string busy = BusyText(facts);

        if (!workers.Autovacuum)
        {
            return HealthVerdict.Quiet("Autovacuum is switched off for the whole server (S02), so no worker runs.");
        }

        int run = SaturatedRun(evaluation.History);

        if (!facts.Saturated || run < SamplesNeeded)
        {
            return HealthVerdict.Quiet(facts.Saturated
                ? "Every autovacuum worker is busy (" + busy + "); a finding needs that in " + SamplesNeeded.ToString(CultureInfo.InvariantCulture)
                  + " samples running, and this is " + Math.Max(run, 1).ToString(CultureInfo.InvariantCulture) + "."
                : busy[..1].ToUpperInvariant() + busy[1..] + ".");
        }

        int suggested = workers.MaxWorkers * 2;
        bool reload = !string.Equals(workers.MaxWorkersContext, ServerSettingRemedy.PostmasterContext, StringComparison.Ordinal);
        bool pastSlots = workers.WorkerSlots is { } slots && suggested > slots;

        List<HealthEvidence> evidence =
        [
            new("Busy", busy),
            new("autovacuum_max_workers", workers.MaxWorkers.ToString(CultureInfo.InvariantCulture)
                + (reload ? " (changes with a reload)" : " (changes only at a restart)")),
            new("Samples running", run.ToString(CultureInfo.InvariantCulture)),
            new("autovacuum_naptime", workers.NaptimeSeconds.ToString(CultureInfo.InvariantCulture) + " s"),
        ];

        if (workers.WorkerSlots is { } workerSlots)
        {
            evidence.Add(new HealthEvidence("autovacuum_worker_slots", workerSlots.ToString(CultureInfo.InvariantCulture)
                + " - the most autovacuum_max_workers can be raised to with a reload (PostgreSQL 18)"));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "T16",
            Severity = HealthSeverity.Warning,
            Title = workers.CanReadAllStats
                ? "Every one of the " + facts.Capacity.ToString(CultureInfo.InvariantCulture) + " autovacuum workers has been busy for "
                  + run.ToString(CultureInfo.InvariantCulture) + " samples running"
                : "At least " + facts.Busy.ToString(CultureInfo.InvariantCulture) + " maintenance processes - as many as autovacuum_max_workers, "
                  + "manual VACUUMs included - have run at once for " + run.ToString(CultureInfo.InvariantCulture) + " samples running",
            WhyItMatters =
                "Autovacuum runs at most autovacuum_max_workers vacuums at once across the whole server. While every worker is busy, a "
                + "table that crosses its trigger waits for one to come free - however far past it it goes (T07) - and one large table "
                + "can hold a worker for hours. More workers share the same autovacuum_vacuum_cost_limit, so raising the count alone "
                + "spreads the same I/O budget thinner; raise the cost limit with it, or the limit per table, where the hardware allows.",
            Evidence = evidence,
            Caveat = (workers.CanReadAllStats
                    ? "Counted in pg_stat_activity by backend_type, server-wide."
                    : "This role is not a member of pg_read_all_stats, so it cannot tell autovacuum's workers from a manual VACUUM or ANALYZE: "
                      + "the count is every process in pg_stat_progress_vacuum and pg_stat_progress_analyze on the server, which is at least the "
                      + "busy workers and includes manual runs.")
                + " Read at " + facts.ReadAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture) + ", once per sweep.",
            Remedy = ServerSettingRemedy.Build(
                facts.Platform,
                "autovacuum_max_workers",
                suggested.ToString(CultureInfo.InvariantCulture),
                evaluation.Text,
                workers.MaxWorkersContext,
                ":",
                where => "Twice the workers, so a table past its trigger is not left waiting for one."
                    + (pastSlots
                        ? " On PostgreSQL 18 it changes with a reload only up to autovacuum_worker_slots (" + workers.WorkerSlots!.Value.ToString(CultureInfo.InvariantCulture)
                          + "); raise that too, which needs a restart."
                        : string.Empty)
                    + " Set it in " + where + "."),
        });
    }

    /// <summary>"3 of 3 autovacuum workers are busy" or "at least 2 maintenance processes are running, manual VACUUMs included".</summary>
    internal static string BusyText(AutovacuumWorkersFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.Workers.CanReadAllStats
            ? facts.Busy.ToString(CultureInfo.InvariantCulture) + " of " + facts.Capacity.ToString(CultureInfo.InvariantCulture) + " autovacuum workers are busy"
            : "at least " + SessionText.Number(facts.Busy) + " maintenance process" + (facts.Busy == 1 ? string.Empty : "es")
              + " running, of " + facts.Capacity.ToString(CultureInfo.InvariantCulture) + " workers, manual VACUUMs included";
    }
}
