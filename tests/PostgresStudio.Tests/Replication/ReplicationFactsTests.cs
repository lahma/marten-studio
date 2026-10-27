using System.Text.RegularExpressions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

using static PostgresStudio.Tests.Replication.ReplicationTestFacts;

namespace PostgresStudio.Tests.Replication;

/// <summary>
/// The facts' version shapes, A2's not-applicable rules, the settings' units and the advisor's counter windows - the parts
/// of the Replication page that are decided before anybody is shown anything.
/// </summary>
public class ReplicationFactsTests
{
    // ---- A2: not applicable on Aurora and where the provider archives --------------------------

    [Theory]
    [InlineData(nameof(ReplicationSection.Slots), false)]
    [InlineData(nameof(ReplicationSection.Standbys), true)]
    [InlineData(nameof(ReplicationSection.Archiver), true)]
    [InlineData(nameof(ReplicationSection.Wal), true)]
    [InlineData(nameof(ReplicationSection.Checkpoints), true)]
    [InlineData(nameof(ReplicationSection.Io), false)]
    [InlineData(nameof(ReplicationSection.Tablespaces), true)]
    public void On_Aurora_every_section_its_storage_layer_does_is_not_applicable(string name, bool notApplicable)
    {
        ReplicationSection section = Enum.Parse<ReplicationSection>(name);
        string? why = ReplicationPlatform.NotApplicable(new PostgresPlatform(PostgresPlatformKind.AmazonAurora, "aurora_version()"), section);

        if (notApplicable)
        {
            why.Should().Be("Not applicable on Aurora: Aurora's storage layer does this, and its readers do not stream.");
        }
        else
        {
            why.Should().BeNull("Aurora has logical slots, and its backends still read and hit");
        }
    }

    [Theory]
    [InlineData(PostgresPlatformKind.AmazonRds, "RDS uploads transaction logs")]
    [InlineData(PostgresPlatformKind.Azure, "The server also backs up transaction logs")]
    [InlineData(PostgresPlatformKind.CloudSql, "Cloud SQL keeps the instance's logs in Cloud Storage")]
    public void Where_the_provider_archives_only_the_archiver_is_not_applicable(PostgresPlatformKind kind, string cites)
    {
        PostgresPlatform platform = new(kind, "a marker");

        ReplicationPlatform.ArchiverNotApplicable(platform).Should().StartWith("Not applicable on " + platform.Name + ":").And.Contain(cites);

        foreach (ReplicationSection section in Enum.GetValues<ReplicationSection>().Where(static x => x != ReplicationSection.Archiver))
        {
            ReplicationPlatform.NotApplicable(platform, section).Should().BeNull(section + " is the host's on " + platform.Name);
        }

        ReplicationPlatform.StandbyLagNotApplicable(platform).Should().BeNull("an RDS-style read replica streams");
        ReplicationPlatform.CheckpointsNotApplicable(platform).Should().BeNull();
    }

    [Theory]
    [InlineData(PostgresPlatformKind.SelfHosted)]
    [InlineData(PostgresPlatformKind.Neon)]
    [InlineData(PostgresPlatformKind.Supabase)]
    public void Everywhere_else_everything_applies(PostgresPlatformKind kind)
    {
        PostgresPlatform platform = kind == PostgresPlatformKind.SelfHosted ? PostgresPlatform.SelfHosted : new PostgresPlatform(kind, "a marker");

        Enum.GetValues<ReplicationSection>().Select(section => ReplicationPlatform.NotApplicable(platform, section)).Should().OnlyContain(static x => x == null);
    }

    [Fact]
    public void R02_R03_and_R04_are_not_applicable_on_Aurora_and_R02_on_the_archiving_providers()
    {
        PostgresPlatform aurora = new(PostgresPlatformKind.AmazonAurora, "aurora_version()");

        ReplicationPlatform.ArchiverNotApplicable(aurora).Should().Be(ReplicationPlatform.Aurora);
        ReplicationPlatform.StandbyLagNotApplicable(aurora).Should().Be(ReplicationPlatform.Aurora);
        ReplicationPlatform.CheckpointsNotApplicable(aurora).Should().Be(ReplicationPlatform.Aurora);
    }

    // ---- B3: no platform marker a role short of a superuser can plant -------------------------

    [Fact]
    public void Every_server_marker_is_fenced_to_where_only_a_superuser_or_the_provider_can_put_it()
    {
        string sql = HealthServerQueries.Platform().Sql;

        sql.Should().Contain("p.proname = 'aurora_version' and p.pronamespace = 'pg_catalog'::pg_catalog.regnamespace",
            "a public.aurora_version() a database owner made is not Aurora");
        Regex.Matches(sql, @"s\.source in \('default', 'environment variable', 'configuration file', 'command line', 'override'\)")
            .Should().HaveCount(3, "the RDS, Azure and Cloud SQL settings count only from the server's own configuration, never a SET");
        sql.Should().Contain("r.rolname = 'neon_superuser' and r.rolreplication and r.rolbypassrls and not r.rolcanlogin",
            "Neon's role carries two attributes only a superuser grants before 16");
    }

    [Fact]
    public void The_standby_read_names_only_physical_senders_and_this_databases_logical_ones()
    {
        string sql = ReplicationQueries.Standbys().Sql;

        sql.Should().Contain("left join pg_catalog.pg_stat_activity a on a.pid = r.pid");
        sql.Should().Contain("(a.pid is not null and a.datid is null) as physical", "a sender with no activity row is nobody's to name (fail closed)");
        sql.Should().Contain("case when s.physical or s.this_database then s.application_name::text end as application_name");
        sql.Should().Contain("case when s.physical or s.this_database then pg_catalog.host(s.client_addr) end as client_addr");
        sql.Should().Contain("case when s.physical or s.this_database then s.usename::text end as usename");
        sql.Should().Contain("select * from r where r.physical or r.this_database", "another database's logical sender is only counted");
        sql.Should().Contain("greatest(pg_catalog.pg_last_wal_receive_lsn(), pg_catalog.pg_last_wal_replay_lsn())",
            "a downstream standby is measured against everything a cascading one holds");
    }

    // ---- The checkpoint and pg_stat_io shapes, per release -------------------------------------

    [Fact]
    public void Up_to_16_the_checkpoints_are_pg_stat_bgwriters_and_from_17_pg_stat_checkpointers_by_name()
    {
        string bgwriter = ReplicationQueries.Checkpoints(checkpointer: false).Sql;
        bgwriter.Should().Contain("pg_catalog.pg_stat_bgwriter").And.Contain("'checkpoints_timed'").And.Contain("'checkpoints_req'")
            .And.NotContain("pg_stat_checkpointer").And.Contain("null::int8 as done");

        string checkpointer = ReplicationQueries.Checkpoints(checkpointer: true).Sql;
        checkpointer.Should().Contain("pg_catalog.pg_stat_checkpointer").And.Contain("'num_timed'").And.Contain("'num_requested'")
            .And.Contain("'num_done'", "18's num_done is read by name, null on 17")
            .And.Contain("pg_catalog.pg_stat_bgwriter", "the background writer's own counters stay there");
    }

    [Theory]
    [InlineData(160015, true)]
    [InlineData(170011, true)]
    [InlineData(180006, false)]
    public void On_16_and_17_pg_stat_io_bytes_are_operations_times_op_bytes_and_on_18_its_own(int version, bool fromOpBytes)
    {
        IoStatistics.CountsBytesFromOpBytes(version).Should().Be(fromOpBytes);

        // One statement for every release that has the view: 18's byte column where there is one, the product otherwise.
        ReplicationQueries.Io().Sql.Should().Contain(
            "coalesce((x.j ->> 'read_bytes')::numeric, (x.j ->> 'reads')::numeric * (x.j ->> 'op_bytes')::numeric)");
        ReplicationQueries.Io().Sql.Should().Contain("group by x.backend_type, x.object", "18's WAL rows are a group of their own, by object");
    }

    // ---- The WAL writes and syncs: one meaning, two homes --------------------------------------

    [Fact]
    public void Up_to_17_the_wal_writes_and_syncs_are_pg_stat_wals_and_read_nothing_of_pg_stat_io()
    {
        string sql = ReplicationQueries.Wal(ioWal: false).Sql;

        sql.Should().Contain("from pg_catalog.pg_stat_wal w")
            .And.Contain("(x.j ->> 'wal_write')::int8 as wal_write").And.Contain("(x.j ->> 'wal_sync')::int8 as wal_sync")
            .And.Contain("(x.j ->> 'wal_write_time')::float8").And.Contain("(x.j ->> 'wal_sync_time')::float8")
            .And.NotContain("pg_stat_io", "14-17 have the counters in pg_stat_wal, and 16 and 17's pg_stat_io has no WAL rows");
        sql.Should().Contain("case when t.timed then (x.j ->> 'wal_write_time')::float8 end", "a zero while timing is off is no time")
            .And.Contain("case when t.timed then (x.j ->> 'wal_sync_time')::float8 end");
        sql.Should().Contain("pg_catalog.current_setting('track_wal_io_timing', true) = 'on'", "the one setting that times WAL I/O");
    }

    [Fact]
    public void From_18_the_wal_writes_and_syncs_are_the_normal_context_of_pg_stat_ios_wal_rows_summed()
    {
        string sql = ReplicationQueries.Wal(ioWal: true).Sql;

        sql.Should().Contain("from pg_catalog.pg_stat_io i").And.Contain("where i.object = 'wal' and i.context = 'normal'",
            "18 also counts a WAL segment's creation, in context init, which pg_stat_wal never did");
        sql.Should().Contain("pg_catalog.sum((s.j ->> 'writes')::numeric)::int8").And.Contain("pg_catalog.sum((s.j ->> 'fsyncs')::numeric)::int8")
            .And.Contain("pg_catalog.sum((s.j ->> 'write_time')::float8)").And.Contain("pg_catalog.sum((s.j ->> 'fsync_time')::float8)");
        sql.Should().Contain("io.writes as wal_write").And.Contain("io.syncs as wal_sync",
            "the same columns, in the same places, as the statement for 14-17: one mapping reads both");
        sql.Should().NotContain("'wal_write'", "18's pg_stat_wal has no write column");
        sql.Should().Contain("case when t.timed then io.write_time end").And.Contain("case when t.timed then io.sync_time end");
        sql.Should().Contain("pg_catalog.current_setting('track_wal_io_timing', true) = 'on'")
            .And.NotContain("track_io_timing", "track_io_timing times relations' I/O and no WAL row (measured on 18.6 and 19rc1)");
        sql.Should().Contain("greatest((x.j ->> 'stats_reset')::timestamptz, io.stats_reset) as stats_reset",
            "pg_stat_io is reset on its own, so the counters began no later than the later reset");
    }

    [Fact]
    public void Both_wal_statements_return_the_same_nine_columns_in_the_same_order()
    {
        static string[] Columns(string sql) =>
            [.. Regex.Matches(sql, @"\bas (\w+),?\n", RegexOptions.CultureInvariant).Select(static x => x.Groups[1].Value)];

        string[] expected = ["wal_records", "wal_fpi", "wal_bytes", "wal_buffers_full", "wal_write", "wal_sync", "wal_write_time", "wal_sync_time", "stats_reset"];

        Columns(ReplicationQueries.Wal(false).Sql).Take(9).Should().Equal(expected);
        Columns(ReplicationQueries.Wal(true).Sql).Take(9).Should().Equal(expected);
    }

    [Theory]
    [InlineData(140013, false)]
    [InlineData(170011, false)]
    [InlineData(180006, true)]
    [InlineData(190000, true)]
    public void The_wal_counters_come_from_pg_stat_io_from_the_release_that_moved_them(int version, bool ioWal)
    {
        PostgresFeatures.Since(PostgresFeature.PgStatIoWal).Should().Be(180000);
        PostgresFeatures.Has(PostgresFeature.PgStatIoWal, version).Should().Be(ioWal);
    }

    [Fact]
    public void The_per_release_reads_are_gated_on_the_feature_table()
    {
        PostgresFeatures.Since(PostgresFeature.PgStatCheckpointer).Should().Be(170000);
        PostgresFeatures.Since(PostgresFeature.CheckpointerNumDone).Should().Be(180000);
        PostgresFeatures.Since(PostgresFeature.PgStatIo).Should().Be(160000);
        PostgresFeatures.Since(PostgresFeature.PgStatIoBytes).Should().Be(180000);

        ReplicationQueries.Recovery(pauseState: false).Sql.Should().NotContain("pg_get_wal_replay_pause_state");
        ReplicationQueries.Recovery(pauseState: true).Sql.Should().Contain(
            "case when pg_catalog.pg_is_in_recovery() then pg_catalog.pg_get_wal_replay_pause_state() end",
            "it raises on a primary, so it runs only in recovery");
        ReplicationQueries.SlotStatistics(statistics: false).Sql.Should().NotContain("pg_stat_replication_slots");
        ReplicationQueries.SlotStatistics(statistics: true).Sql.Should().Contain("left join pg_catalog.pg_stat_replication_slots");
        ReplicationQueries.Archiver().Sql.Should().Contain(
            "case when not pg_catalog.pg_is_in_recovery() then pg_catalog.pg_walfile_name(pg_catalog.pg_current_wal_lsn()) end");
    }

    [Fact]
    public void No_statement_lists_a_size_or_reads_an_activity_view()
    {
        string[] lists =
        [
            ReplicationQueries.Recovery(true).Sql, ReplicationQueries.Standbys().Sql, ReplicationQueries.SlotStatistics(true).Sql,
            ReplicationQueries.Settings().Sql, ReplicationQueries.Archiver().Sql, ReplicationQueries.WalDirectory().Sql,
            ReplicationQueries.Wal(false).Sql, ReplicationQueries.Wal(true).Sql, ReplicationQueries.Checkpoints(true).Sql, ReplicationQueries.Io().Sql,
            ReplicationQueries.Tablespaces().Sql,
        ];

        lists.Should().OnlyContain(static x => !x.Contains("_size(", StringComparison.Ordinal), "a list locks nothing it lists - no size function (D27)");
        lists.Where(static x => x != ReplicationQueries.Standbys().Sql)
            .Should().OnlyContain(static x => !Regex.IsMatch(x, @"\b(from|join)\s+(pg_catalog\.)?pg_(stat_activity|locks|prepared_xacts)\b"),
                "only the standby read joins pg_stat_activity, for the senders' databases (B1), and never pg_locks or pg_prepared_xacts");
        ReplicationQueries.Standbys().Sql.Should().NotMatchRegex(@"\b(from|join)\s+(pg_catalog\.)?pg_(locks|prepared_xacts)\b");
        ReplicationQueries.TablespaceSize("pg_default").Sql.Should().Contain("pg_catalog.pg_tablespace_size(t.oid)").And.Contain("@name");
    }

    // ---- Settings, as pg_settings prints them --------------------------------------------------

    [Theory]
    [InlineData("1024", "MB", 1024L * 1024 * 1024)]
    [InlineData("16777216", "B", 16L * 1024 * 1024)]
    [InlineData("65536", "8kB", 512L * 1024 * 1024)]
    [InlineData("-1", "MB", -1L)]
    public void A_size_setting_is_its_value_in_its_unit(string value, string unit, long bytes)
    {
        Settings(("max_wal_size", value, unit)).Bytes("max_wal_size").Should().Be(bytes);
    }

    [Theory]
    [InlineData("300", "s", 300)]
    [InlineData("5", "min", 300)]
    [InlineData("0", "s", 0)]
    public void A_time_setting_is_its_value_in_its_unit(string value, string unit, int seconds)
    {
        Settings(("checkpoint_timeout", value, unit)).CheckpointTimeout.Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void Archive_command_is_set_empty_disabled_or_hidden_and_its_text_is_only_for_the_gate()
    {
        Settings(("archive_command", "cp %p /archive/%f", null)).ArchiveCommand.Should().Be(ArchiveCommandState.Set);
        Settings(("archive_command", "cp %p /archive/%f", null)).ArchiveCommandText.Should().Be("cp %p /archive/%f");
        Settings(("archive_command", "", null)).ArchiveCommand.Should().Be(ArchiveCommandState.Empty);
        Settings(("archive_command", "(disabled)", null)).ArchiveCommand.Should().Be(ArchiveCommandState.Disabled);
        Settings().ArchiveCommand.Should().Be(ArchiveCommandState.Hidden, "every supported release has it, so a missing row is a hidden one");
        Settings().ArchiveCommandText.Should().BeNull();
    }

    [Theory]
    [InlineData("on", false, true)]
    [InlineData("on", true, false)]
    [InlineData("always", true, true)]
    [InlineData("off", false, false)]
    public void Archive_mode_archives_on_a_primary_and_always_on_a_standby(string mode, bool inRecovery, bool archives)
    {
        Settings(("archive_mode", mode, null)).Archives(inRecovery).Should().Be(archives);
    }

    // ---- The advisor's counter windows ----------------------------------------------------------

    [Fact]
    public void A_window_is_the_gain_from_the_oldest_sample_after_the_last_reset()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-40), [("a", 10), ("b", 100)]),
            (Now.AddMinutes(-30), [("a", 2), ("b", 120)]),
            (Now.AddMinutes(-20), [("a", 5)]),
            (Now.AddMinutes(-10), [("a", 7), ("b", 130)]));

        CounterWindow window = ReplicationHistory.Window(history, [("a", 9), ("b", 140)], Now)!;

        window.Since.Should().Be(Now.AddMinutes(-30), "a went down at -30, and the sample at -20 lacks b");
        window.Gains.Should().Equal(7, 20);
        window.Span.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void No_earlier_sample_or_a_reset_since_the_last_one_is_no_window()
    {
        ReplicationHistory.Window(HealthHistoryView.Empty, [("a", 1)], Now).Should().BeNull();
        ReplicationHistory.Window(History((Now, [("a", 1)])), [("a", 1)], Now).Should().BeNull("one sample, now, spans nothing");
        ReplicationHistory.Window(History((Now.AddMinutes(-5), [("a", 10)])), [("a", 3)], Now).Should().BeNull("reset since the last sample");
    }

    [Fact]
    public void Grown_over_a_span_needs_a_sample_that_old_and_no_drain_since()
    {
        HealthHistoryView history = History((Now.AddMinutes(-12), [("s", 10)]), (Now.AddMinutes(-6), [("s", 12)]));

        ReplicationHistory.GrewOver(history, "s", 20, Now, TimeSpan.FromMinutes(10)).Should().BeTrue();
        ReplicationHistory.GrewOver(history, "s", 10, Now, TimeSpan.FromMinutes(10)).Should().BeFalse("not above where it was");
        ReplicationHistory.GrewOver(history, "s", 20, Now, TimeSpan.FromMinutes(15)).Should().BeFalse("nothing that old");
        ReplicationHistory.GrewOver(History((Now.AddMinutes(-12), [("s", 10)]), (Now.AddMinutes(-6), [("s", 4)])), "s", 20, Now, TimeSpan.FromMinutes(10))
            .Should().BeFalse("it drained in between");
    }
}
