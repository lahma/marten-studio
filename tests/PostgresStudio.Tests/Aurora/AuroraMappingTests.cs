using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Tests.Aurora;

/// <summary>
/// W5-AUR: every Aurora read is mapped by column name from <c>to_jsonb(row)</c> - the column-set variants AWS documents
/// included - and A1's instance, its wording and its place in the statistics window.
/// </summary>
public class AuroraMappingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly PostgresPlatform Aurora = new(PostgresPlatformKind.AmazonAurora, "the server has Aurora's aurora_version() function");

    // ---- aurora_replica_status() ------------------------------------------------------------------

    /// <summary>The function's full documented column set: the writer's lag is null, a reader's is its milliseconds.</summary>
    private const string Writer =
        """{"server_id": "db-1-instance-1", "session_id": "MASTER_SESSION_ID", "durable_lsn": 88492069, "highest_lsn_rcvd": null, "current_read_lsn": null, "cur_replay_latency_in_usec": null, "active_txns": null, "is_current": true, "last_transport_error": 0, "last_error_timestamp": null, "last_update_timestamp": null, "feedback_xmin": null, "feedback_epoch": null, "replica_lag_in_msec": null, "log_stream_speed_in_kib_per_second": 0, "log_buffer_sequence_number": 0, "oldest_read_view_trx_id": null, "oldest_read_view_lsn": null, "pending_read_ios": 0, "read_ios": 0, "iops": null, "cpu": null}""";

    private const string Reader =
        """{"server_id": "db-1-instance-2", "session_id": "2b6e1b8c-6f0a-4f2e-9a1d-3f7c4c1d2e11", "durable_lsn": 88492069, "highest_lsn_rcvd": 88492069, "current_read_lsn": 88492060, "cur_replay_latency_in_usec": 120, "active_txns": 1, "is_current": false, "last_transport_error": 0, "last_error_timestamp": null, "last_update_timestamp": "2026-10-26T11:59:58.5+00:00", "feedback_xmin": 1234, "feedback_epoch": null, "replica_lag_in_msec": 13, "log_stream_speed_in_kib_per_second": 2.5, "log_buffer_sequence_number": 0, "oldest_read_view_trx_id": null, "oldest_read_view_lsn": null, "pending_read_ios": 0, "read_ios": 10, "iops": null, "cpu": 1.5}""";

    /// <summary>The <c>aurora_db_instance_identifier</c> page's own example: no <c>read_ios</c>, <c>iops</c> or <c>cpu</c>.</summary>
    private const string ReaderWithoutTheLaterColumns =
        """{"server_id": "db-1-instance-3", "session_id": "c0ffee00-0000-4000-8000-000000000003", "durable_lsn": 88492069, "highest_lsn_rcvd": 88492069, "current_read_lsn": 88492001, "cur_replay_latency_in_usec": 250, "active_txns": 0, "is_current": false, "last_transport_error": 0, "last_error_timestamp": null, "last_update_timestamp": "2022-06-03 11:18:25+00", "feedback_xmin": null, "feedback_epoch": null, "replica_lag_in_msec": 400000, "log_stream_speed_in_kib_per_second": 0, "log_buffer_sequence_number": 0, "oldest_read_view_trx_id": null, "oldest_read_view_lsn": null, "pending_read_ios": 819}""";

    [Fact]
    public void The_replica_status_is_read_by_name_the_writer_first_and_its_lag_null()
    {
        AuroraReplicaStatus status = AuroraReplicaStatus.FromJson(Now, [Reader, Writer]);

        status.Total.Should().Be(2);
        status.Instances.Select(static x => x.Instance).Should().Equal("db-1-instance-1", "db-1-instance-2");

        AuroraReplica writer = status.Instances[0];
        writer.Writer.Should().BeTrue("its session_id is MASTER_SESSION_ID");
        writer.Lag.Should().BeNull("the writer is what the readers lag behind");
        writer.LastUpdate.Should().BeNull("from Aurora 13.9 the connected instance's last_update_timestamp is null");

        AuroraReplica reader = status.Readers.Single();
        reader.Writer.Should().BeFalse();
        reader.Lag.Should().Be(TimeSpan.FromMilliseconds(13));
        reader.ReplayLatency.Should().Be(TimeSpan.FromMicroseconds(120));
        reader.FeedbackXmin.Should().Be("1234");
        reader.LastUpdate.Should().Be(new DateTimeOffset(2026, 10, 26, 11, 59, 58, 500, TimeSpan.Zero));
        status.WriterInstance.Should().BeSameAs(writer);
    }

    [Fact]
    public void A_release_without_read_ios_iops_and_cpu_reads_the_same_and_its_timestamp_in_Postgres_text_form()
    {
        AuroraReplicaStatus status = AuroraReplicaStatus.FromJson(Now, [ReaderWithoutTheLaterColumns]);

        AuroraReplica reader = status.Instances.Single();
        reader.Instance.Should().Be("db-1-instance-3");
        reader.Writer.Should().BeFalse();
        reader.Lag.Should().Be(TimeSpan.FromSeconds(400));
        reader.ReplayLatency.Should().Be(TimeSpan.FromMicroseconds(250));
        reader.LastUpdate.Should().Be(new DateTimeOffset(2022, 6, 3, 11, 18, 25, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("""{"session_id": "MASTER_SESSION_ID"}""")]
    [InlineData("""{"server_id": ""}""")]
    [InlineData("[]")]
    [InlineData("null")]
    public void A_row_that_names_no_instance_is_counted_and_never_listed(string row)
    {
        AuroraReplicaStatus status = AuroraReplicaStatus.FromJson(Now, [row, Writer]);

        status.Total.Should().Be(2);
        status.Instances.Should().ContainSingle().Which.Instance.Should().Be("db-1-instance-1");
    }

    [Fact]
    public void A_negative_or_missing_lag_is_no_lag()
    {
        AuroraReplicaStatus status = AuroraReplicaStatus.FromJson(Now,
        [
            """{"server_id": "r1", "session_id": "u1", "replica_lag_in_msec": -1}""",
            """{"server_id": "r2", "session_id": "u2"}""",
            """{"server_id": "r3", "session_id": "u3", "replica_lag_in_msec": "15.5"}""",
        ]);

        status.Instances.Select(static x => x.Lag).Should().Equal(null, null, TimeSpan.FromMilliseconds(15.5));
    }

    // ---- aurora_stat_system_waits() and its two name functions -----------------------------------

    private const string Types = """[{"type_id": 1, "type_name": "LWLock"}, {"type_id": 3, "type_name": "Lock"}, {"type_id": 5, "type_name": "Activity"}, {"type_id": 10, "type_name": "IO"}]""";

    /// <summary>The name function as its example shows it - no <c>type_name</c> - and one event as the text documents it, with one.</summary>
    private const string Events = """[{"type_id": 10, "event_id": 167772231, "event_name": "XactSync"}, {"type_id": 10, "event_id": 167772230, "event_name": "WALWrite"}, {"type_id": 5, "event_id": 83886080, "event_name": "BgWriterHibernate"}, {"type_id": 12, "event_id": 7, "event_name": "Something", "type_name": "Later"}]""";

    [Fact]
    public void The_waits_are_joined_to_their_names_the_idle_type_left_out_and_most_time_first()
    {
        AuroraWaitProfile profile = AuroraWaitProfile.FromJson(
            Now,
            """
            [{"type_id": 10, "event_id": 167772230, "waits": 52544463, "wait_time": 388850428},
             {"type_id": 10, "event_id": 167772231, "waits": 187073, "wait_time": 597041642},
             {"type_id": 5, "event_id": 83886080, "waits": 7486, "wait_time": 55266949471},
             {"type_id": 12, "event_id": 7, "waits": 1, "wait_time": 5},
             {"type_id": 3, "event_id": 99, "waits": 2, "wait_time": 10}]
            """,
            Events,
            Types);

        profile.Events.Select(static x => x.Name).Should().Equal("IO:XactSync", "IO:WALWrite", "Lock:event 99", "Later:Something");
        profile.IdleLeftOut.Should().Be(1, "Activity is the background loops' idle wait");

        AuroraWaitEvent sync = profile.Events[0];
        sync.Waits.Should().Be(187073);
        sync.TimeMicroseconds.Should().Be(597041642);
        sync.TimeSeries.Should().Be("aurora.wait:IO:XactSync");
        sync.CountSeries.Should().Be("aurora.wait-count:IO:XactSync");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void No_waits_read_as_an_empty_profile(string? waits)
    {
        AuroraWaitProfile.FromJson(Now, waits, Events, Types).Events.Should().BeEmpty();
    }

    [Fact]
    public void A_type_the_type_function_does_not_name_is_named_by_its_id()
    {
        AuroraWaitProfile profile = AuroraWaitProfile.FromJson(Now, """[{"type_id": 42, "event_id": 1, "waits": 1, "wait_time": 1}]""", null, null);

        profile.Events.Single().Name.Should().Be("type 42:event 1");
    }

    // ---- aurora_stat_get_db_commit_latency(oid) and aurora_stat_dml_activity(oid) ----------------

    [Theory]
    [InlineData("22011")]
    [InlineData("""{"aurora_stat_get_db_commit_latency": 22011}""")]
    [InlineData("""{"commit_latency": "22011"}""")]
    public void The_commit_latency_reads_alike_as_a_scalar_and_as_a_one_column_record(string latency)
    {
        AuroraDatabaseActivity activity = AuroraDatabaseActivity.FromJson(Now, 11, latency, null);

        activity.CommitLatencyMicroseconds.Should().Be(22011);
        activity.MeanCommitMicroseconds.Should().Be(2001, "22,011 µs over 11 commits");
    }

    [Fact]
    public void The_dml_activity_is_read_by_name_with_its_mean_latencies()
    {
        AuroraDatabaseActivity activity = AuroraDatabaseActivity.FromJson(
            Now,
            0,
            null,
            """{"select_count": 451312, "select_latency_microsecs": 80205857, "insert_count": 451001, "insert_latency_microsecs": 123667646, "update_count": 1353067, "update_latency_microsecs": 200900695615, "delete_count": 12, "delete_latency_microsecs": 448}""");

        activity.CommitLatencyMicroseconds.Should().BeNull();
        activity.MeanCommitMicroseconds.Should().BeNull("there is no commit to divide by");
        activity.Dml.Select(static x => x.Kind).Should().Equal("select", "insert", "update", "delete");

        // The function's own page's example: 177, 274, 148,478 and 37 µs per statement, truncated there.
        activity.Dml.Select(static x => Math.Floor(x.MeanMicroseconds!.Value)).Should().Equal(177, 274, 148478, 37);
    }

    [Fact]
    public void A_template_databases_empty_dml_row_reads_as_no_counts()
    {
        AuroraDatabaseActivity activity = AuroraDatabaseActivity.FromJson(Now, 0, "0",
            """{"select_count": null, "select_latency_microsecs": null, "insert_count": null, "insert_latency_microsecs": null, "update_count": null, "update_latency_microsecs": null, "delete_count": null, "delete_latency_microsecs": null}""");

        activity.Dml.Should().OnlyContain(static x => x.Count == null && x.MeanMicroseconds == null);
    }

    // ---- aurora_stat_backend_waits(pid) -----------------------------------------------------------

    [Fact]
    public void A_sessions_waits_are_counts_and_shares_of_its_own_time_never_a_time()
    {
        WaitNames names = WaitNames.FromJson(Events, Types);

        AuroraSession session = AuroraSessionWaits.Session(
            3027,
            "app",
            "svcA",
            "active",
            Now,
            """
            [{"type_id": 10, "event_id": 167772231, "waits": 4867, "wait_time": 7331963},
             {"type_id": 10, "event_id": 167772230, "waits": 36666, "wait_time": 98623},
             {"type_id": 1, "event_id": 2, "waits": 3, "wait_time": 27}]
            """,
            names);

        session.Waits.Should().Be(4867 + 36666 + 3);
        session.Top.Select(static x => x.Name).Should().Equal("IO:XactSync", "IO:WALWrite", "LWLock:event 2");
        session.Top[0].Share.Should().BeApproximately(7331963d / (7331963 + 98623 + 27), 1e-9);
        session.Top.Sum(static x => x.Share!.Value).Should().BeApproximately(1, 1e-9);
        typeof(AuroraSessionWait).GetProperties().Select(static x => x.Name).Should().NotContain(static x => x.Contains("Time", StringComparison.Ordinal),
            "the unit of aurora_stat_backend_waits' wait_time is not known, so no time leaves the read");
    }

    [Fact]
    public void A_session_with_no_wait_has_no_share()
    {
        AuroraSessionWaits.Session(1, null, null, null, null, null, WaitNames.FromJson(null, null)).Top.Should().BeEmpty();
    }

    // ---- A1: which instance answered ----------------------------------------------------------------

    [Fact]
    public void The_answer_names_the_instance_its_role_and_the_Aurora_version()
    {
        PostgresServerFacts writer = new(Now, 160004, "16.4", Aurora) { Instance = "studio-test-instance-1", AuroraVersion = "16.4.1" };
        PostgresServerFacts reader = writer with { Instance = "studio-test-instance-2", InRecovery = true };

        writer.AnsweredBy.Should().Be("instance studio-test-instance-1 (the writer), Aurora 16.4.1");
        reader.AnsweredBy.Should().Be("instance studio-test-instance-2 (a reader), Aurora 16.4.1");
    }

    [Fact]
    public void A_release_before_13_7_has_no_identifier_and_its_instance_is_unknown()
    {
        PostgresServerFacts old = new(Now, 120007, "12.7", Aurora) { AuroraVersion = "4.2.2" };

        old.Instance.Should().BeNull();
        old.AnsweredBy.Should().Be("an instance this Aurora release does not name (the writer), Aurora 4.2.2");
        StatisticsWindow.InstanceOf(old).Should().BeNull("an unknown instance is never compared");
    }

    [Fact]
    public void Off_Aurora_nothing_names_an_instance()
    {
        PostgresServerFacts self = new(Now, 170004, "17.4", PostgresPlatform.SelfHosted) { Instance = "spoofed" };

        self.AnsweredBy.Should().BeNull();
        StatisticsWindow.InstanceOf(self).Should().BeNull("only Aurora's instance joins the window");
    }

    [Fact]
    public void The_server_info_a_capacity_source_is_handed_carries_the_instance_and_its_role()
    {
        DateTimeOffset started = Now.AddHours(-3);
        PostgresServerInfo info = new PostgresServerFacts(Now, 160004, "16.4", Aurora)
        {
            Instance = "studio-test-instance-2",
            InRecovery = true,
            StartedAt = started,
        }.ToServerInfo();

        info.Should().Be(new PostgresServerInfo
        {
            VersionNumber = 160004,
            Version = "16.4",
            Platform = PostgresPlatformKind.AmazonAurora,
            Instance = "studio-test-instance-2",
            InRecovery = true,
            StartedAt = started,
        });
    }

    [Fact]
    public void Another_instance_answering_reads_as_a_reset_that_says_so_and_when_its_counters_started()
    {
        DateTimeOffset reset = Now.AddDays(-2);
        StatisticsReading first = new()
        {
            ReadAt = Now.AddMinutes(-1),
            SharedReset = reset,
            ArchiverReset = reset,
            ServerStartedAt = reset,
            Instance = StatisticsWindow.InstanceOf(new PostgresServerFacts(Now, 160004, "16.4", Aurora) { Instance = "studio-test-instance-1" }),
            Commits = 500,
        };

        StatisticsWindow before = StatisticsWindow.Compute(first, HealthHistoryView.Empty);
        HealthHistoryView history = new([new HealthSample(first.ReadAt, before.Sample(first).ToDictionary(static x => x.Series, static x => x.Value))]);

        DateTimeOffset otherReset = Now.AddDays(-5);
        StatisticsReading second = first with
        {
            ReadAt = Now,
            SharedReset = otherReset,
            ArchiverReset = otherReset,
            ServerStartedAt = otherReset,
            Instance = "studio-test-instance-2",
            Commits = 900,
        };

        StatisticsWindow after = StatisticsWindow.Compute(second, history);

        after.LooksReset.Should().BeTrue("the counters are another instance's");
        after.Reason.Should().Be(StatisticsResetReason.OtherInstance);
        after.Signal.Should().Be("another instance answered; its counters started at " + StatisticsWindow.When(otherReset));
        after.Since.Should().Be(otherReset, "that instance's own reset times");

        StatisticsWindow.Compute(first with { ReadAt = Now }, history).LooksReset.Should().BeFalse("the same instance, its counters going on");
    }
}
