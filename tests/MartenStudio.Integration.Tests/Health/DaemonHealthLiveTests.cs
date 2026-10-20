using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using Npgsql;

using static MartenStudio.Integration.Tests.Health.DaemonSweeps;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// D01-D06 on a real Postgres with a real daemon: each produces its condition and is found, and the healthy sample
/// store - a Solo daemon running, caught up, with the one dead letter it is meant to have - is quiet on all six.
/// </summary>
/// <remarks>
/// Run at the floor and at the latest Marten (<c>-p:MartenStudioMartenVersion=9.50.2</c>); the version-dependent
/// halves are asserted per version rather than skipped.
/// </remarks>
public class DaemonHealthLiveTests(PostgresFixture postgres)
{
    private static readonly string[] DaemonCodes = ["D01", "D02", "D03", "D04", "D05", "D06"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Quiet: the healthy sample store --------------------------------------------------------------------------

    /// <summary>
    /// The anti-cry-wolf half: a healthy store, watched for three (studio) minutes, says nothing - and a sweep of these
    /// checks creates nothing and locks only what it reads.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("D01")]
    [HealthQuiet("D02")]
    [HealthQuiet("D03")]
    [HealthQuiet("D04")]
    [HealthQuiet("D05")]
    [HealthQuiet("D06")]
    public async Task The_healthy_sample_store_with_a_running_daemon_is_quiet_and_the_sweep_locks_only_what_it_reads()
    {
        const string schema = "h2_daemon_quiet";
        HealthSweepProbe probe = new(postgres);

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, schema, observer: probe);
        await host.WaitForDaemonAsync();
        await WaitCaughtUpAsync(host, "DailySales:All", "ShipmentTracker:All");

        IReadOnlySet<string> before = await HealthSweepProbe.SnapshotAsync(postgres, schema, host.EventSchema);

        HealthSweep first = await host.SweepAsync();
        host.Advance(TimeSpan.FromMinutes(3));
        HealthSweep second = await host.SweepAsync();

        foreach (HealthSweep sweep in (HealthSweep[]) [first, second])
        {
            foreach (string code in DaemonCodes)
            {
                HealthCheckResult result = Result(sweep, code);
                result.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} must really run on the healthy store: {1}", code, result.Problem?.Reason);
                result.Findings.Should().BeEmpty("{0} must say nothing about a healthy store, and said {1}", code, result.Findings.Count > 0 ? result.Findings[0].Title : null);
            }
        }

        probe.StatementsObserved.Should().BePositive();

        // The daemon checks' own statements only: the same sweep runs every area's checks, and the table checks read rows
        // of their own (T11 reads mt_hilo) - which are theirs to assert, as their own live tests do.
        HashSet<string> daemonStatements =
        [
            .. ((HealthSweep[]) [first, second])
                .SelectMany(static sweep => DaemonCodes.SelectMany(code => Result(sweep, code).Statements))
                .Select(static x => x.Sql),
        ];

        probe.Locks.Should().Contain(x => daemonStatements.Contains(x.Statement), "the scope must hold the daemon checks' statements, or it proves nothing");
        probe.BeyondCatalogReads.Where(x => daemonStatements.Contains(x.Statement) && !IsAllowedRead(x, host.EventSchema)).Should()
            .BeEmpty("a daemon check reads the progression table, the sequence, one probe of mt_events and the dead letters, and locks nothing else");

        (await HealthSweepProbe.SnapshotAsync(postgres, schema, host.EventSchema)).Should().BeEquivalentTo(before, "a sweep runs no DDL");
        host.Logs.WarningsOrWorse.Should().BeEmpty("a healthy store and its expected states log at Debug at most");
    }

    /// <summary>
    /// The H2 review's follow-up: the quiet test above moves only the studio's clock, and D01-D03 measure in server time.
    /// So here real server time is made to have passed - every shard's row and the HighWaterMark row put ten minutes into
    /// the past, as an idle store looks after ten quiet minutes - and the studio watches from a host with no daemon of its
    /// own, beside one running Solo on the same schema: what it sees is only what the database says. Every shard is caught
    /// up here, so D02 is quiet on "nothing is behind" before any write is looked at, and D03 has no shard behind the mark;
    /// what this proves is that ten minutes of server-time silence on rows that should be silent raises nothing. The case
    /// where only the daemon's own restamp keeps D02 quiet is the next-but-one test, with a shard held behind.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("D02")]
    [HealthQuiet("D03")]
    public async Task A_studio_beside_a_solo_daemon_elsewhere_stays_quiet_after_ten_idle_minutes_of_server_time()
    {
        const string schema = "h2_daemon_beside_solo";

        await using DaemonHealthHost daemon = await DaemonHealthHost.StartAsync(postgres, schema, DaemonMode.Solo);
        await daemon.WaitForDaemonAsync();
        await WaitCaughtUpAsync(daemon, "DailySales:All", "ShipmentTracker:All");

        await using DaemonHealthHost studio = await DaemonHealthHost.StartAsync(postgres, schema, mode: null, fresh: false);

        await QuietAfterIdleServerTimeAsync(daemon, studio);
    }

    /// <summary>The same, watched from a HotCold node that is not the leader: hosted here, and running nothing.</summary>
    [PostgresFact]
    [HealthQuiet("D02")]
    [HealthQuiet("D03")]
    public async Task A_hotcold_follower_stays_quiet_beside_its_leader_after_ten_idle_minutes_of_server_time()
    {
        const string schema = "h2_daemon_follower";

        // A lock id of this test's own, so the wait below is for this leader's locks and nobody else's 4444.
        const int lockId = 7_740_040;

        await using DaemonHealthHost leader = await DaemonHealthHost.StartAsync(
            postgres, schema, DaemonMode.HotCold, store: static options => options.Projections.DaemonLockId = lockId);
        await leader.WaitForDaemonAsync();
        await WaitCaughtUpAsync(leader, "DailySales:All", "ShipmentTracker:All");

        IReadOnlyList<long> keys = LiveConfiguration(leader, lockId).LockKeys;
        await Projections.ProjectionsFixture.WaitForAsync(
            async () => (await HeldAsync(keys)).Count > 0,
            "the leader to take its locks",
            TimeSpan.FromSeconds(60),
            Token);

        await using DaemonHealthHost follower = await DaemonHealthHost.StartAsync(
            postgres, schema, DaemonMode.HotCold, fresh: false, store: static options => options.Projections.DaemonLockId = lockId);

        await QuietAfterIdleServerTimeAsync(leader, follower);
    }

    /// <summary>
    /// The H2 re-review: with every shard caught up D02 is quiet before it ever looks at a write, so the two tests above
    /// never reach the branch that matters. Here one shard is hung on a closed gate - the work is real, the lag is above
    /// zero - while the high-water agent is caught up with every committed event; every row is put ten minutes into the
    /// past, and the one write left is the running daemon's own restamp of its allocation fence (and from Marten 9.43 of
    /// its mark's row). That write alone is what keeps D02 quiet, and D02 says so. D03, meanwhile, sees the hung shard.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("D02")]
    public async Task With_work_waiting_only_the_daemons_own_restamp_keeps_D02_quiet()
    {
        const string schema = "h2_daemon_beside_hung";
        const string gate = "h2-beside-hung";
        const string hung = GatedProjection.ProjectionName + ":All";

        await using DaemonHealthHost daemon = await DaemonHealthHost.StartAsync(postgres, schema, DaemonMode.Solo, gated: true);
        await daemon.WaitForDaemonAsync();

        try
        {
            await daemon.AppendGatedAsync(5);
            await WaitCaughtUpAsync(daemon, hung);

            GatedProjection.Close(gate);
            await daemon.AppendGatedAsync(1, gate);
            await daemon.AppendGatedAsync(5);

            long stuck = await daemon.ShardPositionAsync(hung, postgres);
            long highest = await daemon.HighestSequenceAsync(postgres);
            await Projections.ProjectionsFixture.WaitForAsync(
                async () => await MarkAsync(daemon) >= highest,
                "the mark to catch up with every committed event while the gated shard is held",
                TimeSpan.FromSeconds(60),
                Token);
            await WaitCaughtUpAsync(daemon, "DailySales:All", "ShipmentTracker:All");

            await using DaemonHealthHost studio = await DaemonHealthHost.StartAsync(postgres, schema, mode: null, gated: true, fresh: false);

            DateTimeOffset backdatedAt = await daemon.BackdateAllButTheFenceAsync(TimeSpan.FromMinutes(10), postgres);
            await Projections.ProjectionsFixture.WaitForAsync(
                async () => await daemon.FenceStampedAsync(postgres) is { } stamped && stamped > backdatedAt,
                "the running daemon to restamp its allocation fence",
                TimeSpan.FromSeconds(60),
                Token);

            await studio.SweepAsync();
            studio.Advance(TimeSpan.FromMinutes(6));
            HealthSweep second = await studio.SweepAsync();

            HealthCheckResult d02 = Result(second, "D02");
            d02.Outcome.Should().Be(HealthCheckOutcome.Checked, d02.Problem?.Reason);
            d02.Findings.Should().BeEmpty("the daemon runs elsewhere and its own restamp says so");
            d02.Note.Should().StartWith("Something wrote to the progression table", "the lag is above zero, so the write is what decided it");

            (await daemon.ShardPositionAsync(hung, postgres)).Should().Be(stuck, "the gated shard is still held");
            Findings(second, "D03").Should().Contain(x => x.Subject == hung, "and the shard that is not moving is D03's, seen from here too");
        }
        finally
        {
            GatedProjection.Open(gate);
        }
    }

    /// <summary>
    /// D02's 9.43 pin rule, live (H2 re-review): a studio beside a daemon running elsewhere, the mark pinned behind a gap
    /// with work waiting, every row put ten minutes into the past. From Marten 9.43 the running daemon restamps its mark's
    /// row even while pinned, and that restamp is what keeps D02 quiet; below 9.43 a pinned daemon writes nothing, and D02
    /// stands down on the pin itself. Asserted per version.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("D02")]
    public async Task During_a_pin_a_daemon_elsewhere_is_kept_alive_by_its_restamp_from_9_43()
    {
        const string schema = "h2_daemon_beside_pin";

        await using DaemonHealthHost daemon = await DaemonHealthHost.StartAsync(postgres, schema, DaemonMode.Solo);
        await daemon.WaitForDaemonAsync();
        await WaitCaughtUpAsync(daemon, "DailySales:All", "ShipmentTracker:All");

        await using DaemonHealthHost studio = await DaemonHealthHost.StartAsync(postgres, schema, mode: null, fresh: false);

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction transaction = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand draw = new("select nextval(@sequence::regclass)", holder, transaction))
        {
            draw.Parameters.AddWithValue("sequence", SqlIdentifier.Qualify(daemon.EventSchema, "mt_events_sequence"));
            await draw.ExecuteScalarAsync(Token);
        }

        await using (IDocumentSession session = daemon.Store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new OrderPlaced(Guid.NewGuid(), "pinned", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        await WaitForPinAsync(daemon);

        bool stampIsLiveness = MartenStudio.Services.Configuration.RuntimeFeature.HighWaterMarkLivenessStamp
            .IsAvailable(MartenStudio.Services.Configuration.MartenRuntime.Current);

        DateTimeOffset backdatedAt = await daemon.BackdateAllButTheFenceAsync(TimeSpan.FromMinutes(10), postgres);

        if (stampIsLiveness)
        {
            await Projections.ProjectionsFixture.WaitForAsync(
                async () => await daemon.StampedAsync(HealthDaemonQueries.HighWaterMarkRow, postgres) is { } stamped && stamped > backdatedAt,
                "the pinned daemon to restamp its mark's row",
                TimeSpan.FromSeconds(60),
                Token);
        }

        HealthCheckResult d02 = Result(await studio.SweepAsync(), "D02");

        d02.Outcome.Should().Be(HealthCheckOutcome.Checked, d02.Problem?.Reason);
        d02.Findings.Should().BeEmpty("a daemon pinned behind a gap is alive, and is D01's");
        d02.Note.Should().Contain(stampIsLiveness ? "the high-water agent restamped it" : "Below Marten 9.43");

        await transaction.RollbackAsync(Token);
    }

    /// <summary>Backdates every row but the fence, waits for one real restamp of it, and sweeps twice from <paramref name="studio" />.</summary>
    private async Task QuietAfterIdleServerTimeAsync(DaemonHealthHost daemon, DaemonHealthHost studio)
    {
        DateTimeOffset backdatedAt = await daemon.BackdateAllButTheFenceAsync(TimeSpan.FromMinutes(10), postgres);

        await Projections.ProjectionsFixture.WaitForAsync(
            async () => await daemon.FenceStampedAsync(postgres) is { } stamped && stamped > backdatedAt,
            "the running daemon to restamp its allocation fence",
            TimeSpan.FromSeconds(60),
            Token);

        HealthSweep first = await studio.SweepAsync();
        studio.Advance(TimeSpan.FromMinutes(6));
        HealthSweep second = await studio.SweepAsync();

        foreach (HealthSweep sweep in (HealthSweep[]) [first, second])
        {
            foreach (string code in DaemonCodes)
            {
                HealthCheckResult result = Result(sweep, code);
                result.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} must really run: {1}", code, result.Problem?.Reason);
                result.Findings.Should().BeEmpty("{0} must say nothing about a healthy store run elsewhere, and said {1}",
                    code, result.Findings.Count > 0 ? result.Findings[0].Title : null);
            }
        }
    }

    // ---- D01 -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A transaction that drew a sequence number and has not committed holds the mark - Marten's own gap-liveness probe
    /// sees it as a live reserver - with a committed event waiting behind it; and Explain names it, only through the
    /// activity gate: the capability on and the write policy passing, asserted for each refusal.
    /// </summary>
    [PostgresTheory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("policy")]
    [HealthFires("D01")]
    public async Task An_open_transaction_holding_a_sequence_number_pins_the_mark_and_Explain_names_it_only_through_the_activity_gate(string gate)
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres,
            "h2_daemon_d01_" + gate,
            studio: options => options.Capabilities.ViewDatabaseActivity = gate != "off",
            denyWrites: gate == "policy");
        await host.WaitForDaemonAsync();
        await WaitCaughtUpAsync(host, "DailySales:All");

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction transaction = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand draw = new("select nextval(@sequence::regclass)", holder, transaction))
        {
            draw.Parameters.AddWithValue("sequence", SqlIdentifier.Qualify(host.EventSchema, "mt_events_sequence"));
            await draw.ExecuteScalarAsync(Token);
        }

        // Committed above the reserved number: the mark cannot pass it, and this event waits behind it.
        await using (IDocumentSession session = host.Store.LightweightSession())
        {
            session.Events.StartStream(Guid.NewGuid(), new OrderPlaced(Guid.NewGuid(), "pinned", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(Token);
        }

        await WaitForPinAsync(host);

        HighWaterExplanation explained = await host.ProjectionsAsync(x => x.ExplainHighWaterAsync(host.Scope, Token));

        explained.IsPinned.Should().BeTrue(explained.Summary);
        explained.OpenTransactions.Should().BePositive("counts are for anybody the store policy allows: {0}", explained.Summary);

        switch (gate)
        {
            case "on":
                explained.DetailsShown.Should().BeTrue("ViewDatabaseActivity is on and the write policy passes");
                explained.Sessions.Should().Contain(x => x.Pid == holder.ProcessID && x.RuledOutBecause == null, "the open transaction is the reserver");
                break;

            case "off":
                explained.DetailsShown.Should().BeFalse();
                explained.DetailsWithheldReason.Should().Contain("ViewDatabaseActivity");
                break;

            default:
                explained.DetailsShown.Should().BeFalse("the capability is on, and the write policy refuses");
                explained.DetailsWithheldReason.Should().Contain("write policy");
                break;
        }

        if (gate != "on")
        {
            explained.Sessions.Should().BeEmpty("a pid, a role, an address and a statement name a session");
            explained.Prepared.Should().BeEmpty();
        }

        // Plan §8: the read is the database's, shared across circuits for a minute - a second circuit's press is answered
        // from the first one's read, with its own gate applied.
        HighWaterExplanation again = await host.ProjectionsAsync(x => x.ExplainHighWaterAsync(host.Scope, Token));
        again.AskedAt.Should().Be(explained.AskedAt, "one read a minute, whoever presses");
        again.DetailsShown.Should().Be(explained.DetailsShown);

        // The wait is measured from the advisor's first sample that saw the committed event behind the mark - not from
        // Marten's stuck row - so it takes two sweeps, eleven (studio) minutes apart.
        Findings(await host.SweepAsync(), "D01").Should().BeEmpty("the first sweep that sees the wait has nothing to measure it from");
        host.Advance(TimeSpan.FromMinutes(11));

        HealthFinding finding = Findings(await host.SweepAsync(), "D01").Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Evidence.Should().Contain(x => x.Label == "Waiting for" && x.Value.StartsWith("at least 11 min", StringComparison.Ordinal));

        await transaction.RollbackAsync(Token);
    }

    /// <summary>
    /// The H2 review's blocker, live: an open transaction that drew a number with nothing committed above it - a long
    /// append on an idle store - pins the mark, and Marten records the pin, and nothing is late. D01 stays quiet however
    /// long it lasts, and says why.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("D01")]
    public async Task A_number_drawn_with_nothing_committed_above_it_holds_nothing_back_and_is_quiet()
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "h2_daemon_d01_tail");
        await host.WaitForDaemonAsync();
        await WaitCaughtUpAsync(host, "DailySales:All");

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction transaction = await holder.BeginTransactionAsync(Token);
        await using (NpgsqlCommand draw = new("select nextval(@sequence::regclass)", holder, transaction))
        {
            draw.Parameters.AddWithValue("sequence", SqlIdentifier.Qualify(host.EventSchema, "mt_events_sequence"));
            await draw.ExecuteScalarAsync(Token);
        }

        await WaitForPinAsync(host);
        await BackdateStuckGapAsync(host, TimeSpan.FromHours(1));

        await host.SweepAsync();
        host.Advance(TimeSpan.FromMinutes(11));
        HealthCheckResult result = Result(await host.SweepAsync(), "D01");

        result.Outcome.Should().Be(HealthCheckOutcome.Checked);
        result.Findings.Should().BeEmpty("a pin with nothing committed behind it delays nothing");
        result.Note.Should().Contain("no committed event waits above it, so nothing is held up");

        await transaction.RollbackAsync(Token);
    }

    // ---- D02, and the lock ids per distributor -----------------------------------------------------------------

    [PostgresFact]
    [HealthFires("D02")]
    public async Task Events_that_nothing_projects_with_no_daemon_anywhere_are_critical()
    {
        // A lock id of this test's own, so another class's HotCold daemon holding the default 4444 in this database is not
        // taken for this store's leader.
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres, "h2_daemon_d02", mode: null, store: static options => options.Projections.DaemonLockId = 7_740_010);

        HealthFinding finding = Findings(await host.SweepAsync(), "D02").Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Subject.Should().Be("no-daemon");
    }

    /// <summary>
    /// A session holding the leadership lock while nothing is written: the Warning, whose holder is named only to a
    /// visitor through <c>ViewDatabaseActivity</c> - the capability on and the write policy passing, asserted for each
    /// refusal.
    /// </summary>
    [PostgresTheory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("policy")]
    [HealthFires("D02")]
    public async Task A_lock_held_while_nothing_is_written_names_its_holder_only_through_the_activity_gate(string gate)
    {
        bool activity = gate == "on";

        // A lock id of this test's own: advisory locks are the whole database's, and another class's HotCold daemon over
        // several databases holds the default 4444 in this one - which both blocked this lock and would be named here.
        int lockId = 7_740_000 + Array.IndexOf((string[]) ["on", "off", "policy"], gate);

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres,
            "h2_daemon_d02_lock_" + gate,
            mode: null,
            store: options => options.Projections.DaemonLockId = lockId,
            studio: options => options.Capabilities.ViewDatabaseActivity = gate != "off",
            denyWrites: gate == "policy");

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using (NpgsqlCommand take = new("select pg_advisory_lock(@id)", holder))
        {
            take.Parameters.AddWithValue("id", (long) lockId);
            await take.ExecuteNonQueryAsync(Token);
        }

        HealthReport report = await host.ReportAsync();

        HealthReportFinding finding = report.Findings.Single(x => x.Code == "D02");
        finding.Finding.Subject.Should().Be("lock-held-idle");

        HealthEvidence named = finding.Evidence.Single(x => x.Label == "Lock holder 1");
        if (activity)
        {
            named.Value.Should().Contain("pid " + holder.ProcessID);
        }
        else
        {
            named.Value.Should().StartWith("withheld").And.NotContain(holder.ProcessID.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>"Never for ExternallyManaged": Wolverine's leadership uses other lock ids, so D02 does not look.</summary>
    [PostgresFact]
    [HealthQuiet("D02")]
    public async Task An_externally_managed_store_is_never_judged_by_D02()
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres,
            "h2_daemon_d02_external",
            mode: null,
            store: static options => options.Projections.AsyncMode = DaemonMode.ExternallyManaged);

        HealthCheckResult result = Result(await host.SweepAsync(), "D02");

        result.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        result.Findings.Should().BeEmpty();
        result.Problem!.Reason.Should().Contain("ExternallyManaged");
    }

    /// <summary>
    /// The lock-id scheme, pinned live per distributor: Solo takes none; HotCold over one database takes
    /// <c>ProjectionLockIds.Compute(eventSchema, shard, DaemonLockId)</c> per shard - transaction-scoped below Marten 9.41,
    /// session-scoped from it (#5526), the same row in <c>pg_locks</c> either way.
    /// </summary>
    [PostgresTheory]
    [InlineData(DaemonMode.Solo)]
    [InlineData(DaemonMode.HotCold)]
    public async Task The_daemons_lock_ids_are_the_ones_the_studio_recognises(DaemonMode mode)
    {
        // A base lock id of this test's own (H2 re-review): advisory locks are the whole database's, and another class's
        // multi-database daemon holds the default 4444 in this one while the suites run four classes at a time.
        int lockId = mode == DaemonMode.Solo ? 7_740_020 : 7_740_021;

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres, "h2_daemon_locks_" + mode.ToString().ToLowerInvariant(), mode, store: options => options.Projections.DaemonLockId = lockId);
        await host.WaitForDaemonAsync();

        DaemonCheckConfiguration configuration = LiveConfiguration(host, lockId);
        IReadOnlyList<long> expected = mode == DaemonMode.Solo
            ? []
            : [.. DaemonCheckConfiguration.AsyncShardsOf(host.Store).Select(shard => (long) JasperFx.Events.Daemon.ProjectionLockIds.Compute(host.EventSchema, shard, lockId))];

        if (mode == DaemonMode.HotCold)
        {
            await Projections.ProjectionsFixture.WaitForAsync(
                async () => (await HeldAsync(configuration.LockKeys)).Count >= expected.Count,
                "the HotCold leader to take its locks",
                TimeSpan.FromSeconds(60),
                Token);
        }

        IReadOnlyList<(long Key, string? State)> held = await HeldAsync(configuration.LockKeys);

        held.Select(static x => x.Key).Should().BeEquivalentTo(expected);

        if (mode == DaemonMode.HotCold)
        {
            bool sessionScoped = MartenStudio.Services.Configuration.MartenRuntime.Current
                .VersionOf(MartenStudio.Services.Configuration.RuntimePackage.Marten) >= new Version(9, 41, 0);

            held.Should().OnlyContain(x => sessionScoped ? x.State != "idle in transaction" : x.State == "idle in transaction",
                "UseAdvisoryLockTransaction defaults to true at 9.39 and to false from 9.41 (#5526)");
        }
    }

    /// <summary>
    /// The lock key is put back together from <c>classid</c> and <c>objid</c> as a signed 64-bit number, so a lock id the
    /// hash made negative - or a host's own negative <c>DaemonLockId</c> - is recognised too: Marten's own gap probe
    /// matches only <c>classid = 0</c>.
    /// </summary>
    [PostgresFact]
    public async Task A_negative_lock_key_is_reconstructed_and_recognised()
    {
        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using (NpgsqlCommand take = new("select pg_advisory_lock(-5)", holder))
        {
            await take.ExecuteNonQueryAsync(Token);
        }

        IReadOnlyList<(long Key, string? State)> held = await HeldAsync([-5, 5]);

        held.Should().ContainSingle().Which.Key.Should().Be(-5, "the high 32 bits are classid, the low are objid, and the sign is the key's");
    }

    // ---- D03 and D04: a hung shard, and a slow one -----------------------------------------------------------------

    /// <summary>
    /// A projection blocked inside its own handler: the daemon is alive, the mark moves past the shard, the shard does not
    /// move - D03's, and only D03's: a lag that grows while the shard stands still is not "falling behind" (H2 review).
    /// </summary>
    [PostgresFact]
    [HealthFires("D03")]
    [HealthQuiet("D04")]
    public async Task A_hung_shard_is_alive_but_not_moving_and_is_D03s_alone()
    {
        const string gate = "h2-d03";
        const string shard = GatedProjection.ProjectionName + ":All";

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "h2_daemon_d03", gated: true);
        await host.WaitForDaemonAsync();

        try
        {
            await host.AppendGatedAsync(5);
            await WaitCaughtUpAsync(host, shard);

            GatedProjection.Close(gate);
            await host.AppendGatedAsync(1, gate);
            await host.AppendGatedAsync(5);

            long stuck = await host.ShardPositionAsync(shard, postgres);
            await Projections.ProjectionsFixture.WaitForAsync(
                async () => await MarkAsync(host) > stuck,
                "the mark to pass the hung shard",
                TimeSpan.FromSeconds(60),
                Token);

            await host.BackdateAsync(shard, TimeSpan.FromMinutes(10), postgres);

            await host.SweepAsync();
            await host.AppendGatedAsync(1_500);
            await Projections.ProjectionsFixture.WaitForAsync(
                async () => await MarkAsync(host) > stuck + 1_500,
                "the mark to move over the new events",
                TimeSpan.FromSeconds(60),
                Token);

            // Past D03's five-minute floor: the advisor has seen the mark ahead of the still shard for the whole window.
            host.Advance(TimeSpan.FromMinutes(6));
            HealthSweep second = await host.SweepAsync();

            Findings(second, "D03").Should().Contain(x => x.Subject == shard && x.Severity == HealthSeverity.Warning);
            Findings(second, "D04").Should().NotContain(x => x.Subject == shard, "a shard that did not move is stalled, not slow");
            Result(second, "D04").Note.Should().Contain(shard + ": not judged: the shard has not moved while the advisor watched");
        }
        finally
        {
            GatedProjection.Open(gate);
        }
    }

    /// <summary>
    /// A projection that stays slower than its arrivals, sample after sample: every event takes it ten milliseconds, and
    /// three thousand more arrive between each of the advisor's samples while it finishes a page or so. It moves, the
    /// mark moves, and its lag grows at each step - that is D04. One burst alone would not be (the H2 re-review): the
    /// second sweep, after the first burst, says nothing.
    /// </summary>
    [PostgresFact]
    [HealthFires("D04")]
    public async Task A_shard_that_stays_slower_than_events_arrive_is_falling_behind()
    {
        const string shard = GatedProjection.ProjectionName + ":All";

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "h2_daemon_d04", gated: true);
        await host.WaitForDaemonAsync();

        await host.AppendGatedAsync(5);
        await WaitCaughtUpAsync(host, shard);

        await host.SweepAsync();

        HealthSweep? sweep = null;

        for (int burst = 0; burst < 2; burst++)
        {
            long before = await host.ShardPositionAsync(shard, postgres);
            await host.AppendGatedAsync(3_000, delayMilliseconds: 10);

            await Projections.ProjectionsFixture.WaitForAsync(
                async () => await host.ShardPositionAsync(shard, postgres) > before,
                "the slow shard to finish a page",
                TimeSpan.FromSeconds(90),
                Token);

            host.Advance(TimeSpan.FromMinutes(2));
            sweep = await host.SweepAsync();

            if (burst == 0)
            {
                Findings(sweep, "D04").Should().NotContain(x => x.Subject == shard, "one burst, however large, lands in one step");
            }
        }

        HealthFinding finding = Findings(sweep!, "D04").Should().Contain(x => x.Subject == shard).Which;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(x => x.Label == "Processing" && !x.Value.StartsWith("0 ", StringComparison.Ordinal));
        Findings(sweep!, "D03").Should().NotContain(x => x.Subject == shard, "a shard that moved is not stalled");
    }

    // ---- D05 -----------------------------------------------------------------------------------------------------

    /// <summary>The sample's poison event with SkipApplyErrors off: the shard is paused or stopped by its failure.</summary>
    /// <remarks>
    /// <para>
    /// <b>Waited for on the agent the sweep reads.</b> The wait used to pass when the shard's agent was not running
    /// <em>or was not there at all</em> - and it is not there until the coordinator's first poll has started it, so the
    /// wait was true before the shard had run, and the sweep found a daemon with nothing paused (seen at 9.50.2). It
    /// now waits for the agent to exist and to be <c>Paused</c> by its failure, which is what
    /// <c>SubscriptionAgent.ReportCriticalFailureAsync</c> leaves for an apply error (JasperFx 2.74 and 2.87.1 alike).
    /// </para>
    /// <para>
    /// <b>And the pause is held still for the sweep.</b> The coordinator restarts a paused agent once
    /// <c>HealthCheckPollingTime</c> (five seconds by default) has passed since it paused; the restarted agent reads
    /// <c>Running</c> until it reaches the poison event again, and D05 drops a shard the daemon here says is running.
    /// A sweep that landed in that moment found nothing. Ten minutes is longer than the test, so the agent the wait saw
    /// is the agent the sweep reads. The high-water agent's own health timer runs at the same interval, and nothing
    /// here depends on it.
    /// </para>
    /// </remarks>
    [PostgresFact]
    [HealthFires("D05")]
    public async Task A_projection_that_throws_without_skipping_is_failed_or_paused()
    {
        const string shard = "ShipmentTracker:All";

        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres,
            "h2_daemon_d05",
            store: static options =>
            {
                options.Projections.Errors.SkipApplyErrors = false;
                options.Events.EnableExtendedProgressionTracking = true;
                options.Projections.HealthCheckPollingTime = TimeSpan.FromMinutes(10);
            });

        await host.WaitForDaemonAsync();
        await Projections.ProjectionsFixture.WaitForAsync(
            () => Task.FromResult(host.Daemon.CurrentAgents().Any(x => x.Name.Identity == shard && x.Status == JasperFx.AgentStatus.Paused)),
            "the poison event to pause the shard's agent",
            TimeSpan.FromSeconds(60),
            Token);

        Findings(await host.SweepAsync(), "D05").Should().Contain(x => x.Subject == shard);
    }

    // ---- D06 -----------------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("D06")]
    public async Task Dead_letters_that_keep_arriving_are_reported_for_their_projection()
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(postgres, "h2_daemon_d06");
        await host.WaitForDaemonAsync();
        await WaitCaughtUpAsync(host, "ShipmentTracker:All");

        long before = await DeadLettersAsync(host);
        await host.SweepAsync();

        await using (IDocumentSession session = host.Store.LightweightSession())
        {
            for (int i = 0; i < 3; i++)
            {
                session.Events.StartStream(Guid.NewGuid(), new ItemAdded(ItemAdded.PoisonSku, 1, 1m, DateTimeOffset.UtcNow));
            }

            await session.SaveChangesAsync(Token);
        }

        await Projections.ProjectionsFixture.WaitForAsync(
            async () => await DeadLettersAsync(host) >= before + 3,
            "three more dead letters",
            TimeSpan.FromSeconds(60),
            Token);

        host.Advance(TimeSpan.FromMinutes(2));

        Findings(await host.SweepAsync(), "D06").Should().ContainSingle(x => x.Subject == "ShipmentTracker");
    }

    // ---- Hidden document types ----------------------------------------------------------------------------------

    /// <summary>
    /// The rebuild dialog lists the tables a rebuild rewrites; a type <c>IsDocumentTypeVisible</c> hides is never named
    /// there (H2 review) - and, the anti-vacuity half, the same projection's table is listed while its type is visible.
    /// </summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_rebuild_dialog_never_names_the_table_of_a_hidden_document_type(bool hidden)
    {
        await using DaemonHealthHost host = await DaemonHealthHost.StartAsync(
            postgres,
            "h2_daemon_hidden_" + (hidden ? "on" : "off"),
            mode: null,
            seed: false,
            studio: options => options.IsDocumentTypeVisible = hidden ? static type => type != typeof(DailySales) : null);

        RebuildScope rebuild = await host.ProjectionsAsync(x => x.DescribeRebuildAsync(host.Scope, "DailySales", Token));

        if (hidden)
        {
            rebuild.TablesAffected.Should().BeEmpty("the projection's only document type is hidden");
        }
        else
        {
            rebuild.TablesAffected.Should().ContainSingle().Which.Should().ContainEquivalentOf("dailysales");
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private static DaemonCheckConfiguration LiveConfiguration(DaemonHealthHost host, int lockId = DaemonCheckConfiguration.DefaultDaemonLockId) =>
        new(host.EventSchema, false, false, DaemonMode.Solo, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), lockId, null, true, true,
            [.. DaemonCheckConfiguration.AsyncShardsOf(host.Store).Select(static x => x.Identity)],
            DaemonCheckConfiguration.LockKeysFor(host.EventSchema, DaemonCheckConfiguration.AsyncShardsOf(host.Store), lockId));

    private async Task<IReadOnlyList<(long Key, string? State)>> HeldAsync(IReadOnlyList<long> keys)
    {
        HealthQuery query = HealthDaemonQueries.DaemonLocks(keys);

        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(query.Sql, connection);
        foreach (NpgsqlParameter parameter in query.Parameters)
        {
            command.Parameters.Add(parameter);
        }

        List<(long, string?)> held = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            held.Add((reader.GetInt64(1), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return held;
    }

    private async Task WaitCaughtUpAsync(DaemonHealthHost host, params string[] shards)
    {
        long highest = await host.HighestSequenceAsync(postgres);

        foreach (string shard in shards)
        {
            await host.WaitForShardAsync(shard, highest, postgres);
        }
    }

    private async Task<long> MarkAsync(DaemonHealthHost host)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select last_seq_id from " + SqlIdentifier.Qualify(host.EventSchema, "mt_event_progression") + " where name = 'HighWaterMark'", connection);

        return await command.ExecuteScalarAsync(Token) is long mark ? mark : -1;
    }

    private async Task WaitForPinAsync(DaemonHealthHost host) =>
        await Projections.ProjectionsFixture.WaitForAsync(
            async () =>
            {
                await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
                await using NpgsqlCommand command = new(
                    "select g.last_seq_id = h.last_seq_id from " + SqlIdentifier.Qualify(host.EventSchema, "mt_event_progression") + " g, "
                    + SqlIdentifier.Qualify(host.EventSchema, "mt_event_progression") + " h where g.name = 'HighWaterStuckGap' and h.name = 'HighWaterMark'",
                    connection);

                return await command.ExecuteScalarAsync(Token) is true;
            },
            "the daemon to record the mark as pinned",
            TimeSpan.FromSeconds(60),
            Token);

    private Task BackdateStuckGapAsync(DaemonHealthHost host, TimeSpan ago) => host.BackdateAsync("HighWaterStuckGap", ago, postgres);

    private async Task<long> DeadLettersAsync(DaemonHealthHost host)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using NpgsqlCommand command = new("select count(*) from " + SqlIdentifier.Qualify(host.EventSchema, "mt_doc_deadletterevent"), connection);

        return (long) (await command.ExecuteScalarAsync(Token))!;
    }
}
