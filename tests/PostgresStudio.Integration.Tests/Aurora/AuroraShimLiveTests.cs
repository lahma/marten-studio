using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Services;
using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Replication;

namespace PostgresStudio.Integration.Tests.Aurora;

/// <summary>
/// W5-AUR on VERS's Aurora shim (<see cref="AuroraShim" />), each test in a database of its own on PostgreSQL 17 - never one a
/// self-hosted test reads, because the shim turns its database into an Aurora server for the platform detector: which instance
/// answered (A1), R03's <c>aurora-reader</c> (A3), the wait profile and commit latency (A4), the not-applicable checks (A2), a
/// failover between two sweeps, C03's remedy against a capacity source (A6, A8) - and that none of Aurora's markers can be
/// planted by a role short of a superuser.
/// </summary>
public class AuroraShimLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// B0's <c>aurora_replica_status()</c> with its reader 400,000 ms - six minutes and forty seconds - behind the writer.
    /// </summary>
    private const string LaggingReader =
        """
        create or replace function pg_catalog.aurora_replica_status(out server_id text, out session_id text, out durable_lsn bigint, out highest_lsn_rcvd bigint,
          out current_read_lsn bigint, out cur_replay_latency_in_usec bigint, out active_txns int, out is_current boolean,
          out last_transport_error int, out last_error_timestamp timestamptz, out last_update_timestamp timestamptz,
          out feedback_xmin bigint, out feedback_epoch int, out replica_lag_in_msec real, out log_stream_speed_in_kib_per_second real,
          out log_buffer_sequence_number bigint, out oldest_read_view_trx_id bigint, out oldest_read_view_lsn bigint,
          out pending_read_ios int, out read_ios bigint, out iops int, out cpu real) returns setof record language sql as $$
          values ('studio-test-instance-1', 'MASTER_SESSION_ID', 88492069::bigint, null::bigint, null::bigint, null::bigint, null::int, true, 0, null::timestamptz, null::timestamptz, null::bigint, null::int, null::real, 0::real, 0::bigint, null::bigint, null::bigint, 0, 0::bigint, null::int, null::real),
                 ('studio-test-instance-2', gen_random_uuid()::text, 88492069, 88492069, 88492060, 120, 1, false, 0, null, now(), null, null, 400000, 2.5, 0, null, null, 0, 10, null, 1.5) $$;
        """;

    private async Task<MatrixServer> ServerAsync()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();
        return server;
    }

    private static async Task<HealthCheckResult> ResultAsync(MatrixHost host, string code)
    {
        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);
        return sweep.Results.Single(x => x.Check.Code == code);
    }

    [PostgresFact]
    public async Task On_the_shim_the_platform_is_Aurora_and_the_instance_and_version_are_the_shims()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_instance", Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);

        sweep.TryShared(Internal.Sql.HealthServerQueries.PlatformKey, out PostgresServerFacts? facts).Should().BeTrue();
        facts!.Platform.Kind.Should().Be(PostgresPlatformKind.AmazonAurora);
        facts.Instance.Should().Be(AuroraShim.InstanceIdentifier);
        facts.AuroraVersion.Should().Be(AuroraShim.Version);
        facts.InRecovery.Should().BeFalse();
        facts.StartedAt.Should().NotBeNull();
        facts.InstanceNotRead.Should().BeNull();

        HealthServerLine.Of(sweep).Should().Be(new HealthServerLine(
            "instance studio-test-instance-1 (the writer), Aurora 16.4.1", facts.StartedAt, null, null));

        await using MatrixHost selfHosted = await MatrixHost.CreateAsync(server, MatrixRole.App);
        (await selfHosted.SweepAsync(HealthSweepKind.Regular)).TryShared(Internal.Sql.HealthServerQueries.PlatformKey, out PostgresServerFacts? plain).Should().BeTrue();
        plain!.Platform.Kind.Should().Be(PostgresPlatformKind.SelfHosted, "the shim lives in its own database, and pg_proc is per database");
        plain.Instance.Should().BeNull();
        plain.AnsweredBy.Should().BeNull();
    }

    [PostgresFact]
    [HealthFires("R03")]
    public async Task R03_aurora_reader_fires_on_a_reader_four_hundred_seconds_behind_the_writer()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_r03_fires", Token);
        await server.ExecuteAsync(LaggingReader, database, Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthCheckResult r03 = await ResultAsync(host, "R03");

        r03.Outcome.Should().Be(HealthCheckOutcome.Checked, "on Aurora R03 reads aurora_replica_status(), never pg_stat_replication");
        HealthFinding finding = r03.Findings.Should().ContainSingle().Subject;
        finding.Variant.Should().Be("aurora-reader");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Title.Should().Be("An Aurora reader is 6 min behind");
        HealthEvidence named = finding.Evidence.Single(static x => x.Label == "Replica 1 (furthest behind)");
        named.Value.Should().StartWith("studio-test-instance-2, 6 min behind, replay expected to take 0.1 ms");
        named.Gate.Should().Be(HealthGate.Database, "an instance id is the cluster's own name for an instance, not a session's (D29)");
        r03.Statements.Should().Contain(static x => x.Sql.Contains("pg_catalog.aurora_replica_status()", StringComparison.Ordinal))
            .And.NotContain(static x => x.Sql.Contains("pg_stat_replication", StringComparison.Ordinal));
    }

    [PostgresFact]
    [HealthQuiet("R03")]
    public async Task R03_aurora_reader_is_quiet_on_B0s_shim_with_its_reader_thirteen_milliseconds_behind()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_r03_quiet", Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthCheckResult r03 = await ResultAsync(host, "R03");

        r03.Outcome.Should().Be(HealthCheckOutcome.Checked);
        r03.Findings.Should().BeEmpty();
        r03.Note.Should().Be("1 Aurora reader, none behind: the furthest, studio-test-instance-2, is 13 ms behind the writer (aurora_replica_status()).");
    }

    [PostgresFact]
    public async Task R02_R04_and_S03_are_listed_as_not_applicable_and_the_other_checks_run()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_na", Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);

        Dictionary<string, HealthCheckResult> results = sweep.Results.ToDictionary(static x => x.Check.Code, StringComparer.Ordinal);

        results["R02"].Outcome.Should().Be(HealthCheckOutcome.NotApplicable);
        results["R02"].Note.Should().Be(ReplicationPlatform.Aurora);
        results["R04"].Outcome.Should().Be(HealthCheckOutcome.NotApplicable);
        results["R04"].Note.Should().Be(ReplicationPlatform.Aurora);
        results["S03"].Outcome.Should().Be(HealthCheckOutcome.NotApplicable);
        results["S03"].Note.Should().Be(ChecksumFailuresCheck.AuroraReason);
        results["S03"].Statements.Should().NotContain(static x => x.Sql.Contains("checksum_failures", StringComparison.Ordinal), "a not-applicable check reads nothing of its own");

        foreach (string code in new[] { "R01", "R03", "C03", "C08", "C11", "S01", "S02" })
        {
            results[code].Outcome.Should().NotBe(HealthCheckOutcome.NotApplicable, code + " applies on Aurora");
        }

        results["R01"].Outcome.Should().Be(HealthCheckOutcome.Checked, "the slot read asks for a WAL position on Aurora only under logical WAL (W5-AUR-fix F2)");
        results["T14"].Outcome.Should().Be(HealthCheckOutcome.Checked, "T14 shares that read");

        results.Values.Where(static x => x.Outcome == HealthCheckOutcome.NotApplicable).Select(static x => x.Check.Code)
            .Should().BeEquivalentTo(["R02", "R04", "S03"], "A2's review: these three and no other shared check stand down on Aurora");
    }

    /// <summary>
    /// Aurora's writer below <c>wal_level = logical</c> raises <c>55000</c> from <c>pg_current_wal_lsn()</c> (W5-AUR-fix F2), and
    /// the slot read R01, T14 and the Replication page share never calls it there. The shim's superuser makes this database's
    /// <c>pg_current_wal_lsn()</c> one that raises <c>55000</c> when it runs - in this database alone, as <c>pg_proc</c> is per
    /// database. Not by replacing the built-in's row, which changes nothing (<c>fmgr_info</c> runs a built-in OID from its own
    /// table without reading <c>pg_proc</c>), nor by revoking it (a privilege fails the statement at plan time, guarded call or
    /// not): the built-in is renamed, and a PL/pgSQL function of a new OID takes its name.
    /// </summary>
    [PostgresFact]
    public async Task A_WAL_position_Aurora_refuses_is_never_asked_for_and_R01_T14_and_the_slots_still_answer()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_no_wal", Token);
        await server.ExecuteAsync(
            """
            alter function pg_catalog.pg_current_wal_lsn() rename to studio_test_builtin_current_wal_lsn;
            create function pg_catalog.pg_current_wal_lsn() returns pg_lsn language plpgsql volatile as $$
            begin
              raise exception 'recovery is in progress' using errcode = '55000', hint = 'WAL control functions cannot be executed during recovery.';
            end $$;
            """,
            database,
            Token);

        await using (NpgsqlConnection probe = await server.OpenAsync(MatrixRole.App, database, Token))
        {
            await using NpgsqlCommand walLevel = new("select pg_catalog.current_setting('wal_level')", probe);
            ((string) (await walLevel.ExecuteScalarAsync(Token))!).Should().NotBe("logical", "the shim stands for an Aurora cluster with rds.logical_replication off");

            await using NpgsqlCommand refused = new("select pg_catalog.pg_current_wal_lsn()", probe);
            Func<Task> call = async () => await refused.ExecuteScalarAsync(Token);
            (await call.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55000", "the stand-in raises what Aurora raises");

            // And the statement off Aurora - the self-hosted slot read, which asks for the position - meets it.
            await using NpgsqlCommand unguarded = new(Internal.Sql.HealthServerQueries.ReplicationSlots().Sql.Replace("@limit", "200", StringComparison.Ordinal), probe);
            Func<Task> read = async () => await unguarded.ExecuteScalarAsync(Token);
            (await read.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55000");
        }

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);

        HealthCheckResult r01 = sweep.Results.Single(static x => x.Check.Code == "R01");
        r01.Outcome.Should().Be(HealthCheckOutcome.Checked);
        r01.Note.Should().Contain("Aurora gives no WAL position below wal_level = logical");
        sweep.Results.Single(static x => x.Check.Code == "T14").Outcome.Should().Be(HealthCheckOutcome.Checked);
        sweep.Results.Where(static x => x.Problem?.SqlState == "55000")
            .Select(static x => x.Check.Code)
            .Should().BeEmpty("no other shared check reads a WAL function on Aurora");

        ReplicationFacts facts = await host.Services.GetRequiredService<IReplicationFactsReader>().ReadAsync(host.Resolved, Token);
        facts.Failures.Should().BeEmpty();
        facts.Slots.Outcome.Should().Be(ReplicationReadOutcome.Read);
        facts.Slots.Value!.PositionRead.Should().BeFalse();
        facts.Statements.Should().Contain(static x => x.Sql.Contains("current_setting('wal_level') = 'logical'", StringComparison.Ordinal));
        facts.Statements.Should().NotContain(static x => x.Sql.Contains("pg_last_wal_", StringComparison.Ordinal), "on Aurora the role is the platform read's");
    }

    [PostgresFact]
    public async Task The_wait_section_shows_the_shims_XactSync_and_the_sweep_samples_it()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_waits", Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular);

        sweep.TryShared(AuroraWaitProfile.Key, out AuroraWaitProfile? profile).Should().BeTrue("a regular sweep of an Aurora database reads the waits itself");
        profile!.Events.Should().ContainSingle().Which.Should().Be(new AuroraWaitEvent(10, 167772231, "IO", "XactSync", 187073, 597041642));

        HealthHistoryView history = host.Services.GetRequiredService<HealthHistory>().Read(host.Resolved.Source.Key, host.Resolved.Target.Identity);
        history.Samples.Should().ContainSingle().Which.Values.Should().Contain("aurora.wait:IO:XactSync", 597041642).And.Contain(AuroraSamples.CommitLatencySeries, 22011);

        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
        AuroraView view = await circuit.ServiceProvider.GetRequiredService<IAuroraService>().GetAsync(new StudioScope("default", "default", null), Token);

        view.Refused.Should().BeNull();
        view.IsAurora.Should().BeTrue();
        view.AnsweredBy.Should().Be("instance studio-test-instance-1 (the writer), Aurora 16.4.1");
        view.Waits!.Value!.Rows.Select(static x => x.Event.Name).Should().Equal("IO:XactSync");
        view.Database!.Value!.Activity.CommitLatencyMicroseconds.Should().Be(22011, "the shim's scalar, read through to_jsonb whatever its shape");
        view.Database.Value.Dml.Single(static x => x.Kind.Kind == "update").Kind.MeanMicroseconds.Should().BeApproximately(1454579206167d / 519538, 0.01);
        view.Replicas!.Value!.Instances.Select(static x => x.Instance).Should().Equal("studio-test-instance-1", "studio-test-instance-2");
        view.SessionsShown.Should().BeTrue("the matrix host has every capability on, and its write policy allows everything");
        view.Sessions!.Value.Should().NotBeNull("aurora_stat_backend_waits is read for this database's sessions");
        view.Statements.Should().Contain(static x => x.Sql.Contains("pg_catalog.aurora_stat_system_waits()", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Another_instance_answering_between_two_sweeps_reads_as_a_reset_that_says_so()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_failover", Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);

        HealthSweep first = await host.SweepAsync(HealthSweepKind.Regular);
        first.Statistics.Should().NotBeNull();
        first.Statistics!.LooksReset.Should().BeFalse("the first sweep has nothing to compare with");

        await server.ExecuteAsync(
            "create or replace function pg_catalog.aurora_db_instance_identifier() returns varchar language sql as $$ select 'studio-test-instance-2'::varchar $$",
            database,
            Token);

        HealthSweep second = await host.SweepAsync(HealthSweepKind.Regular);

        second.Statistics!.LooksReset.Should().BeTrue();
        second.Statistics.Reason.Should().Be(StatisticsResetReason.OtherInstance);
        second.Statistics.Signal.Should().StartWith("another instance answered; its counters started at ");

        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();
        HealthReport report = await circuit.ServiceProvider.GetRequiredService<IHealthService>()
            .GetReportAsync(new StudioScope("default", "default", null), HealthReportRequest.Regular, Token);

        report.StatisticsReset.Should().StartWith("The statistics look reset since the last check: another instance answered;");
        report.Server!.AnsweredBy.Should().Be("instance studio-test-instance-2 (the writer), Aurora 16.4.1");
    }

    [PostgresFact]
    public async Task C03s_remedy_follows_the_capacity_source_on_each_branch_and_is_the_pool_line_it_always_was_off_the_shim()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_c03", Token);

        FixedCapacity source = new(PostgresComputeCapacity.AuroraServerlessV2(4.5, 0.5, 16, DateTimeOffset.UtcNow));
        await using MatrixHost aurora = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<IPostgresCapacitySource>(source));

        ServerConnectionHeadroomFacts gathered = await GatherAsync(aurora, new ServerConnectionHeadroomCheck());
        gathered.Platform.Kind.Should().Be(PostgresPlatformKind.AmazonAurora);
        gathered.Capacity!.IsAcu.Should().BeTrue();
        gathered.Limits.MaxConnections.Should().BeLessThan(ServerConnectionHeadroomCheck.AuroraLowMinimumCap, "the matrix server runs Postgres' default");
        source.Asked.Should().ContainSingle().Which.Should().Be((new PostgresDatabaseRef("default", "default"), AuroraShim.InstanceIdentifier));

        // The live gather, judged as though its server were nearly full: the thresholds are unit-tested, the wiring is proven
        // here - one case per branch of W5-AUR-fix F1, the server's max_connections stated where only Aurora could set it.
        HealthRemedy remedy = Full(gathered).Remedy!;
        remedy.Kind.Should().Be(HealthRemedyKind.ManagedPlatform);
        remedy.Code.Should().Be("Raise the cluster's maximum capacity (now 16 ACU)", "max_connections is below both of Aurora's caps");

        Full(gathered, maxConnections: 2_000).Remedy!.Code
            .Should().Be("Raise the cluster's minimum capacity to 1 ACU or more (now 0.5 ACU)", "a minimum of 0.5 ACU caps max_connections at 2,000");

        await using MatrixHost large = await MatrixHost.CreateAsync(
            server,
            MatrixRole.App,
            database,
            services: s => s.AddSingleton<IPostgresCapacitySource>(new FixedCapacity(PostgresComputeCapacity.AuroraServerlessV2(40, 1, 64, DateTimeOffset.UtcNow))));
        Full(await GatherAsync(large, new ServerConnectionHeadroomCheck())).Remedy!.Code
            .Should().Be("Lower the pool ceilings of the applications that share the cluster, or put RDS Proxy or PgBouncer in front of it",
                "from 32 ACU Aurora's default is its ceiling, 5,000");

        await using MatrixHost aurora2 = await MatrixHost.CreateAsync(server, MatrixRole.App, database);
        ServerConnectionHeadroomFacts unread = await GatherAsync(aurora2, new ServerConnectionHeadroomCheck());
        Full(unread).Remedy!.Code
            .Should().StartWith("On Aurora Serverless v2: raise the cluster's maximum capacity (ACU), or its minimum to 1 ACU or more", "without a reading it says both cases");
        Full(unread, maxConnections: 5_000).Remedy!.Code.Should().StartWith("Lower the pool ceilings", "5,000 is the ceiling whatever the capacity");

        await using MatrixHost selfHosted = await MatrixHost.CreateAsync(server, MatrixRole.App);
        HealthRemedy plain = Full(await GatherAsync(selfHosted, new ServerConnectionHeadroomCheck())).Remedy!;
        plain.Kind.Should().Be(HealthRemedyKind.Configuration);
        plain.Explanation.Should().Contain("max_connections itself is the server's setting, which the studio never changes",
            "off Aurora C03's remedy is its own pool line, word for word");

        static HealthFinding Full(ServerConnectionHeadroomFacts facts, int? maxConnections = null)
        {
            ServerConnectionLimits limits = facts.Limits with { MaxConnections = maxConnections ?? facts.Limits.MaxConnections };

            return new ServerConnectionHeadroomCheck()
                .Evaluate(facts with { Limits = limits with { ClientBackends = limits.Available } }, HealthEvaluation.At(DateTimeOffset.UtcNow))
                .Findings.Single(static x => x.Variant == ServerConnectionHeadroomCheck.ServerVariant);
        }
    }

    /// <summary>
    /// W5-AUR-fix F4: the platform and the capacity only word C03, so a platform read that fails - here because
    /// <c>pg_postmaster_start_time()</c> is revoked from <c>PUBLIC</c> in this database alone - judges the budget as on a server
    /// of somebody's own, with no capacity, rather than failing the check.
    /// </summary>
    [PostgresFact]
    public async Task C03_judges_the_budget_when_the_platform_read_fails()
    {
        MatrixServer server = await ServerAsync();
        string database = await server.CreateDatabaseAsync("aur_c03_no_platform", Token);
        await server.ExecuteAsync("revoke execute on function pg_catalog.pg_postmaster_start_time() from public", database, Token);

        await using (NpgsqlConnection probe = await server.OpenAsync(MatrixRole.App, database, Token))
        {
            await using NpgsqlCommand started = new("select pg_catalog.pg_postmaster_start_time()", probe);
            Func<Task> call = async () => await started.ExecuteScalarAsync(Token);
            (await call.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501", "the platform read reads it, so it fails here");
        }

        FixedCapacity source = new(PostgresComputeCapacity.AuroraServerlessV2(4.5, 0.5, 16, DateTimeOffset.UtcNow));
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<IPostgresCapacitySource>(source));

        ServerConnectionHeadroomFacts facts = await GatherAsync(host, new ServerConnectionHeadroomCheck());

        facts.Platform.Should().Be(PostgresPlatform.SelfHosted);
        facts.Capacity.Should().BeNull();
        facts.Limits.MaxConnections.Should().BePositive("the budget itself was read");
        source.Asked.Should().BeEmpty("the capacity is asked for with the server facts, which could not be read");
    }

    [PostgresFact]
    public async Task C03s_database_variant_fires_on_a_databases_own_connection_limit()
    {
        MatrixServer server = await ServerAsync();
        string database = await server.CreateDatabaseAsync("aur_c03_limit", Token);

        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database);

        // The host's pooled connection is open already; the superuser's two are exempt from the limit, and counted by it.
        await server.ExecuteAsync("alter database aur_c03_limit connection limit 2", MatrixServer.Database, Token);
        await using NpgsqlConnection one = await server.OpenAsync(MatrixRole.Superuser, database, Token);
        await using NpgsqlConnection two = await server.OpenAsync(MatrixRole.Superuser, database, Token);

        HealthCheckResult c03 = await ResultAsync(host, "C03");

        c03.Outcome.Should().Be(HealthCheckOutcome.Checked);
        HealthFinding finding = c03.Findings.Should().ContainSingle().Subject;
        finding.Variant.Should().Be("database");
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Evidence.Single(static x => x.Label == "Database").Value.Should().Be("CONNECTION LIMIT 2, every role's sessions counted");
        finding.Remedy!.Code.Should().Be("Maximum Pool Size=1");
    }

    [PostgresFact]
    public async Task No_role_short_of_a_superuser_can_plant_an_Aurora_marker_or_change_the_instance()
    {
        MatrixServer server = await ServerAsync();
        string plainDatabase = await server.CreateDatabaseAsync("aur_spoof_plain", Token);
        string shimDatabase = await AuroraShim.ApplyAsync(server, "aur_spoof_shim", Token);

        // The database owner plants both functions where it may - in public - in a database of somebody's own.
        await using (NpgsqlConnection owner = await server.OpenAsync(MatrixRole.App, plainDatabase, Token))
        {
            await Execute(owner, "create function public.aurora_version() returns varchar language sql as $$ select '16.4.1'::varchar $$");
            await Execute(owner, "create function public.aurora_db_instance_identifier() returns varchar language sql as $$ select 'spoofed'::varchar $$");

            PostgresException refused = (await FluentActions.Awaiting(() => Execute(owner,
                "create function pg_catalog.aurora_db_instance_identifier() returns varchar language sql as $$ select 'spoofed'::varchar $$"))
                .Should().ThrowAsync<PostgresException>()).Which;
            refused.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "only a superuser creates a function in pg_catalog");
        }

        await using (MatrixHost plain = await MatrixHost.CreateAsync(server, MatrixRole.App, plainDatabase))
        {
            (HealthCheckContext context, HealthConnection connection) = plain.Context();

            await using (connection)
            {
                PostgresServerFacts facts = await PostgresServerFacts.ReadAsync(context, Token);
                facts.Platform.Kind.Should().Be(PostgresPlatformKind.SelfHosted, "a public.aurora_version() is nobody's Aurora");
                facts.Instance.Should().BeNull();
                StatisticsWindow.InstanceOf(facts).Should().BeNull();
            }
        }

        // On the shim, the owner's public.aurora_db_instance_identifier() does not change which instance answered.
        await using (NpgsqlConnection owner = await server.OpenAsync(MatrixRole.App, shimDatabase, Token))
        {
            await Execute(owner, "create function public.aurora_db_instance_identifier() returns varchar language sql as $$ select 'spoofed'::varchar $$");
        }

        await using MatrixHost shim = await MatrixHost.CreateAsync(server, MatrixRole.App, shimDatabase);
        (HealthCheckContext shimContext, HealthConnection shimConnection) = shim.Context();

        await using (shimConnection)
        {
            PostgresServerFacts facts = await PostgresServerFacts.ReadAsync(shimContext, Token);
            facts.Platform.Kind.Should().Be(PostgresPlatformKind.AmazonAurora);
            facts.Instance.Should().Be(AuroraShim.InstanceIdentifier, "the identifier is read from pg_catalog by name, never from the search path");
        }

        static async Task Execute(NpgsqlConnection connection, string sql)
        {
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(Token);
        }
    }

    /// <summary>
    /// W5-index §2.4: Aurora's reads - the sweep's waits and commit latency, the page's facts and its per-session read - lock
    /// nothing and create nothing. A second session holds <c>ACCESS EXCLUSIVE</c> on a table of the shimmed database; the sweep
    /// and the section's reads return under a 300 ms <c>lock_timeout</c>, hold no lock beyond a catalog read, and the database's
    /// objects are the same after.
    /// </summary>
    [PostgresFact]
    public async Task Auroras_reads_return_behind_an_access_exclusive_lock_and_create_nothing()
    {
        MatrixServer server = await ServerAsync();
        string database = await AuroraShim.ApplyAsync(server, "aur_nolock", Token);
        await server.ExecuteAsync("create schema store authorization app; create table store.locked (id int primary key)", database, Token);

        IReadOnlySet<string> before = await server.SnapshotAsync(database, Token);

        HealthSweepProbe probe = new(cancellation => server.OpenAsync(database: database, cancellationToken: cancellation));
        await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.App, database, services: s => s.AddSingleton<IHealthStatementObserver>(probe));

        await using NpgsqlConnection holder = await server.OpenAsync(database: database, cancellationToken: Token);
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);

        await using (NpgsqlCommand lockIt = new("lock table store.locked in access exclusive mode", holder, held))
        {
            await lockIt.ExecuteNonQueryAsync(Token);
        }

        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Regular, TimeSpan.FromMilliseconds(300));

        sweep.TryShared(AuroraWaitProfile.Key, out AuroraWaitProfile? _).Should().BeTrue("the waits were read behind the lock");
        sweep.TryShared(AuroraDatabaseActivity.Key, out AuroraDatabaseActivity? _).Should().BeTrue("and the commit latency");
        sweep.Results.Single(static x => x.Check.Code == "R03").Outcome.Should().Be(HealthCheckOutcome.Checked);
        probe.StatementsObserved.Should().BePositive("so 'no lock' is not 'nothing ran'");
        probe.BeyondCatalogReads.Should().BeEmpty("Aurora's functions read the instance's own counters, never a relation");

        AuroraFacts facts = await host.Services.GetRequiredService<IAuroraFactsReader>().ReadAsync(host.Resolved, Token);
        facts.Failures.Should().BeEmpty();

        // Another session of the studio's own role, which that role may see the backend type of, is listed with its waits.
        await using NpgsqlConnection other = await server.OpenAsync(MatrixRole.App, database, Token);
        AuroraSessionWaits sessions = await host.Services.GetRequiredService<IAuroraFactsReader>().ReadSessionsAsync(host.Resolved, Token);
        sessions.Sessions.Should().Contain(x => x.ProcessId == other.ProcessID && x.Top.Any(static w => w.Name == "IO:XactSync"),
            "a session is listed with its waits named, as shares");

        await held.RollbackAsync(Token);

        // The database's own objects: roles are the server's, and other classes on this matrix server create theirs meanwhile.
        (await server.SnapshotAsync(database, Token)).Where(static x => !x.StartsWith("role:", StringComparison.Ordinal))
            .Should().BeEquivalentTo(before.Where(static x => !x.StartsWith("role:", StringComparison.Ordinal)), "a read path creates nothing");
    }

    private static async Task<T> GatherAsync<T>(MatrixHost host, HealthCheck<T> check)
        where T : class
    {
        (HealthCheckContext context, HealthConnection connection) = host.Context();

        await using (connection)
        {
            return await check.GatherAsync(context, Token);
        }
    }

    /// <summary>A host's capacity source that always answers the same reading, and records what it was asked.</summary>
    private sealed class FixedCapacity(PostgresComputeCapacity capacity) : IPostgresCapacitySource
    {
        public List<(PostgresDatabaseRef Database, string? Instance)> Asked { get; } = [];

        public ValueTask<PostgresComputeCapacity?> GetCapacityAsync(PostgresDatabaseRef database, PostgresServerInfo server, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.Add((database, server.Instance));
            }

            return ValueTask.FromResult<PostgresComputeCapacity?>(capacity);
        }
    }
}
