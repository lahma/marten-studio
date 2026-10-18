using System.Globalization;

using JasperFx.Events.Daemon;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One session holding one of the daemon's leadership locks.</summary>
/// <param name="Pid">Its backend.</param>
/// <param name="LockKey">The lock id it holds.</param>
/// <param name="User">Its role, or <see langword="null" /> where this role may not see it.</param>
/// <param name="Application">Its <c>application_name</c>.</param>
/// <param name="ClientAddress">Its client address.</param>
/// <param name="BackendStart">When it connected.</param>
/// <param name="State">What it is doing.</param>
internal sealed record DaemonLockHolder(
    int Pid,
    long LockKey,
    string? User,
    string? Application,
    string? ClientAddress,
    DateTimeOffset? BackendStart,
    string? State);

/// <summary>What D02 reads.</summary>
/// <param name="Configuration">The store's daemon settings.</param>
/// <param name="Reading">The high-water reading, for the lag and for the progression table's last write.</param>
/// <param name="Shards">The registered async shards' progression rows.</param>
/// <param name="InProcess">Whether this process hosts the daemon, and whether the studio paused it.</param>
/// <param name="Holders">
/// The sessions holding a leadership lock, or <see langword="null" /> when they were not asked for - nothing lags, or
/// a healthy daemon runs here - because <c>pg_locks</c> is a lock-table scan and is read only when it could matter.
/// </param>
internal sealed record DaemonNotRunningFacts(
    DaemonCheckConfiguration Configuration,
    HighWaterReading Reading,
    IReadOnlyList<DaemonShardRow> Shards,
    DaemonInProcess InProcess,
    IReadOnlyList<DaemonLockHolder>? Holders)
{
    /// <summary>
    /// How far the furthest-behind registered shard is from the highest sequence handed out - the projections
    /// screen's own lag. A shard with no row has processed nothing.
    /// </summary>
    public long Lag
    {
        get
        {
            if (Configuration.AsyncShards.Count == 0 || Reading.HighestSequence <= 0)
            {
                return 0;
            }

            long lowest = Configuration.AsyncShards
                .Select(shard => Shards.FirstOrDefault(x => string.Equals(x.Name, shard, StringComparison.OrdinalIgnoreCase))?.Sequence ?? 0)
                .Min();

            return Math.Max(0, Reading.HighestSequence - lowest);
        }
    }
}

/// <summary>
/// D02: async projections are behind and nothing is running the daemon.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three kinds of evidence, all of which must agree.</b> There is work (a registered async shard is behind the
/// highest sequence handed out); nothing holds a leadership lock for this store in this database; and nothing has
/// written to <c>mt_event_progression</c> for more than <c>2 x SlowPollingTime + 1 min</c>, in server time. Then
/// Critical. A session that holds the lock while nothing is written is a different finding of the same check: a
/// daemon that has the leadership and is not doing anything with it (Warning), named only behind
/// <c>ViewDatabaseActivity</c>.
/// </para>
/// <para>
/// <b>The lock ids are pinned per distributor</b> (JasperFx V2.74.0 and V2.87.1, byte-identical): HotCold over one
/// database takes one lock per shard, <c>ProjectionLockIds.Compute(eventSchema, shard, DaemonLockId)</c>; HotCold over
/// several takes <c>DaemonLockId</c> per database; <b>Solo takes none</b>. So for Solo the lock evidence is always
/// "none", and the progression table carries the argument: a running daemon writes it - each shard as it advances, the
/// allocation fence on every high-water poll while caught up (#5108), and from Marten 9.43 the <c>HighWaterMark</c>
/// row on every poll (#5541). The one shape that writes nothing below 9.43 is a mark pinned behind a gap, which is
/// D01's finding: below 9.43 D02 stands down while it holds. From 9.43 a running daemon restamps the mark's row even
/// while pinned, so the stand-down lasts only while that row is fresh - a daemon that died pinned is D02's, or D01
/// would stay Critical and nothing would say why. 9.41 moved HotCold's lock from transaction to session scope
/// (#5526); both appear in <c>pg_locks</c> the same way, an advisory lock with <c>objsubid = 1</c>.
/// </para>
/// <para>
/// <b>Never raised</b> for <c>DaemonMode.ExternallyManaged</c> - Wolverine's leadership uses other lock ids, and D03
/// alone applies - nor for a daemon hosted in this process that says it is running, nor while the studio's own
/// coordinator pause is recorded for the store, nor in the first quiet window after the process hosting a daemon
/// started: a HotCold node just restarted holds no lock and has written nothing yet, and is not a dead daemon.
/// </para>
/// </remarks>
[HealthCheck("D02", HealthArea.Daemon, HealthCostTier.Catalog, "Async projections behind, and nothing runs the daemon")]
internal sealed class DaemonNotRunningCheck : HealthCheck<DaemonNotRunningFacts>
{
    /// <inheritdoc />
    public override async Task<DaemonNotRunningFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        DaemonCheckConfiguration configuration = DaemonCheckConfiguration.Read(context);

        if (configuration.TenantPartitionedEvents)
        {
            throw new HealthCheckUnavailableException(DaemonCheckConfiguration.TenantPartitionedReason);
        }

        if (configuration.AsyncMode == DaemonMode.ExternallyManaged)
        {
            // A value, read from configuration before anything is asked: the system that runs these projections
            // is not one whose leadership this studio can see.
            throw new HealthCheckUnavailableException(
                "An external system runs this store's async projections (DaemonMode.ExternallyManaged) - Wolverine's leadership "
                + "uses lock ids of its own - so whether anything runs them is not something the studio can see. D03 still "
                + "reports a shard that stopped moving.");
        }

        EventStoreRelations shape = await EventHealth.ShapeAsync(context, cancellationToken).ConfigureAwait(false);

        if (!shape.HasHighWater)
        {
            throw new HealthCheckUnavailableException(EventStoreRelations.NoEventStoreReason);
        }

        HighWaterReading reading = await HighWaterReading.ReadAsync(context, configuration.EventSchema, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DaemonShardRow> shards = await DaemonShardRow
            .ReadAsync(context, shape, configuration, extendedProgressionTracking: false, cancellationToken)
            .ConfigureAwait(false);

        DaemonInProcess inProcess = await DaemonInProcess.ReadAsync(context, configuration.AsyncShards, cancellationToken).ConfigureAwait(false);

        DaemonNotRunningFacts facts = new(configuration, reading, shards, inProcess, null);

        if (facts.Lag == 0
            || inProcess.IsHealthyHere
            || inProcess.PausedByStudio is not null
            || inProcess.IsStartingHere(context.Now, DaemonThresholds.DaemonQuiet(configuration.SlowPollingTime)))
        {
            return facts;
        }

        var locks = HealthDaemonQueries.DaemonLocks(configuration.LockKeys);
        IReadOnlyList<DaemonLockHolder> holders = await context.Sql.QueryAsync(
                locks,
                static reader => new DaemonLockHolder(
                    reader.GetInt32(0),
                    reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    HighWaterReading.NullableTime(reader, 5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)),
                cancellationToken)
            .ConfigureAwait(false);

        return facts with { Holders = holders };
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DaemonNotRunningFacts facts, HealthEvaluation evaluation)
    {
        DaemonCheckConfiguration configuration = facts.Configuration;
        HighWaterReading reading = facts.Reading;

        if (!configuration.HasAsyncWork || configuration.AsyncShards.Count == 0)
        {
            return HealthVerdict.Quiet("The store registers no async projection or subscription, so there is no daemon to run.");
        }

        if (facts.InProcess.IsHealthyHere)
        {
            return HealthVerdict.Quiet("The async daemon is hosted in this process and says it is running.");
        }

        if (facts.InProcess.PausedByStudio is not null)
        {
            return HealthVerdict.Quiet("Marten Studio paused this store's projection coordinator; the projections screen says who and when.");
        }

        TimeSpan window = DaemonThresholds.DaemonQuiet(configuration.SlowPollingTime);

        if (facts.InProcess.IsStartingHere(evaluation.Now, window))
        {
            return HealthVerdict.Quiet(
                "The daemon is hosted in this process, which started " + DaemonCheckConfiguration.Format(evaluation.Now - facts.InProcess.ProcessStartedAt!.Value)
                + " ago; it is given " + DaemonCheckConfiguration.Format(window) + " to take the leadership and write its first progress.");
        }

        long lag = facts.Lag;
        if (lag == 0)
        {
            return HealthVerdict.Quiet("No registered async shard is behind the highest sequence handed out.");
        }

        if (reading.IsPinnedBehindGap)
        {
            if (!RuntimeFeature.HighWaterMarkLivenessStamp.IsAvailable(evaluation.Runtime))
            {
                return HealthVerdict.Quiet(
                    "The high-water mark is held behind a gap (D01). Below Marten 9.43 a daemon in that state writes nothing to the "
                    + "progression table, so its silence is not evidence that nothing runs it.");
            }

            if (reading.SinceMarkUpdated is { } stamped && stamped <= window)
            {
                return HealthVerdict.Quiet(
                    "The high-water mark is held behind a gap (D01), and the high-water agent restamped it "
                    + DaemonCheckConfiguration.Format(stamped) + " ago - which from Marten 9.43 a running one does on every poll.");
            }

            // From 9.43 a running daemon restamps the mark's row even while it is pinned (#5541), so a pin whose row has
            // gone quiet is a daemon that died pinned: D01 alone would stay Critical and say nothing about that.
        }

        TimeSpan? quiet = reading.SinceLatestWrite;

        if (quiet is { } since && since <= window)
        {
            return HealthVerdict.Quiet(
                "Something wrote to the progression table " + DaemonCheckConfiguration.Format(since) + " ago, which a running daemon does.");
        }

        IReadOnlyList<DaemonLockHolder> holders = facts.Holders ?? [];
        string quietFor = quiet is { } q ? DaemonCheckConfiguration.Format(q) : "ever";
        string hosting = facts.InProcess.Hosting switch
        {
            DaemonHostingState.Hosted => "hosted in this process, and stopped",
            _ => "not hosted in this process (Projections.AsyncMode here is " + configuration.AsyncMode + ")",
        };

        List<HealthEvidence> evidence =
        [
            new("Furthest behind", DaemonCheckConfiguration.Number(lag) + " events behind the highest sequence handed out"),
            new("Last write to mt_event_progression", quiet is { } ago
                ? DaemonCheckConfiguration.Format(ago) + " ago (server time)"
                : "never"),
            new("Quiet window", DaemonCheckConfiguration.Format(window) + " (2 x Projections.SlowPollingTime + 1 min)"),
            new("Daemon", hosting),
        ];

        if (holders.Count == 0)
        {
            evidence.Add(new HealthEvidence(
                "Leadership locks held",
                "none of this store's " + configuration.LockKeys.Count.ToString(CultureInfo.InvariantCulture)
                + " lock ids is held by any session in this database (a Solo daemon holds none, a HotCold one always does)"));

            return HealthVerdict.Of(new HealthFinding
            {
                Code = "D02",
                Severity = HealthSeverity.Critical,
                Subject = "no-daemon",
                Title = "Async projections are " + DaemonCheckConfiguration.Number(lag) + " events behind and nothing appears to be running the daemon",
                WhyItMatters =
                    "Every async projection and subscription in this database waits for a daemon, and the evidence says none is "
                    + "running: no session holds this store's leadership lock, nothing has written to mt_event_progression for "
                    + quietFor + ", and no daemon runs in this process. Events keep arriving and nothing projects them.",
                Evidence = evidence,
                Caveat =
                    "A Solo daemon in another process takes no lock, so for it only the progression table speaks - and it writes "
                    + "while it runs. Another store's daemon sharing this database and DaemonLockId would be counted as this one's "
                    + "holder; Marten asks for a DaemonLockId unique to each application on a database.",
                Remedy = new HealthRemedy(
                    "// On the host that should run the async projections (one, or several with HotCold):\n"
                    + "builder.Services.AddMarten(opts => { /* ... */ })\n"
                    + "    .AddAsyncDaemon(DaemonMode.HotCold);",
                    "HotCold lets several nodes run it and elects one; Solo is for exactly one node. A host that runs it already "
                    + "and is down or stopped is what this sees too: start it again."),
            });
        }

        evidence.Add(new HealthEvidence(
            "Leadership locks held",
            holders.Count.ToString(CultureInfo.InvariantCulture) + " of this store's lock ids, by "
            + holders.Select(static x => x.Pid).Distinct().Count().ToString(CultureInfo.InvariantCulture) + " session(s)"));

        // The label is shown to everybody and only the value is gated, so the label is an ordinal and the pid is in
        // the value.
        int ordinal = 0;
        foreach (DaemonLockHolder holder in holders.GroupBy(static x => x.Pid).Select(static x => x.First()).Take(5))
        {
            ordinal++;
            evidence.Add(new HealthEvidence(
                "Lock holder " + ordinal.ToString(CultureInfo.InvariantCulture),
                DescribeHolder(holder),
                HealthGate.Activity));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "D02",
            Severity = HealthSeverity.Warning,
            Subject = "lock-held-idle",
            Title = "A session holds the daemon's leadership lock, but nothing has been written for " + quietFor,
            WhyItMatters =
                "Async projections here are " + DaemonCheckConfiguration.Number(lag) + " events behind, and the session holding this "
                + "store's leadership lock has written nothing to mt_event_progression for " + quietFor + ". A running daemon writes on "
                + "every high-water poll while caught up and on every shard advance, so the process holding the lock is hung, or the "
                + "lock outlived it - while it is held, no other node takes over.",
            Evidence = evidence,
            Caveat =
                "Which session holds the lock is visible to every role; who it is (role, application, client address) needs "
                + "pg_read_all_stats for another role's session. Another store's daemon on this database with the same "
                + "DaemonLockId would look the same.",
            Remedy = new HealthRemedy(
                "// If the lock is another application's, give this store a lock id of its own:\n"
                + "opts.Projections.DaemonLockId = 5150;",
                "Otherwise restart the process that holds it - its node is named above for anyone allowed to see sessions - so "
                + "the lock is released and a healthy node takes the leadership."),
        });
    }

    private static string DescribeHolder(DaemonLockHolder holder)
    {
        List<string> parts = ["pid " + holder.Pid.ToString(CultureInfo.InvariantCulture)];

        if (holder.Application is { Length: > 0 } application)
        {
            parts.Add("application '" + application + "'");
        }

        if (holder.User is { Length: > 0 } user)
        {
            parts.Add("role " + user);
        }

        if (holder.ClientAddress is { Length: > 0 } client)
        {
            parts.Add("from " + client);
        }

        if (holder.BackendStart is { } started)
        {
            parts.Add("connected " + started.ToString("u", CultureInfo.InvariantCulture));
        }

        if (holder.State is { Length: > 0 } state)
        {
            parts.Add(state);
        }

        if (parts.Count == 1)
        {
            parts.Add("its role, application and address are not visible to the role the studio reads as");
        }

        return string.Join(", ", parts);
    }
}
