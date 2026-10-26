using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

using static PostgresStudio.Tests.Replication.ReplicationTestFacts;

namespace PostgresStudio.Tests.Replication;

/// <summary>
/// What a visitor is shown of the facts (<see cref="ReplicationService.Build" />): every number at the database-wide gate,
/// and names - a standby's, a slot's (R01's rule), the archive command - only through the activity gate.
/// </summary>
public class ReplicationViewTests
{
    internal static ReplicationSlot Slot(string name, string type, bool thisDatabase, string? plugin = null, string? database = null) =>
        new(name, type, plugin, database, false, false, 2L << 30, "reserved", null, 12, null, null, null, null, thisDatabase);

    internal static ReplicationFacts Facts(
        PostgresPlatformKind platform = PostgresPlatformKind.SelfHosted,
        int version = 170011,
        IReadOnlyList<StandbyRow>? standbys = null,
        PostgresGrants? grants = null,
        HealthStatement[]? statements = null)
    {
        PostgresServerFacts server = ServerOf(version, platform);
        ReplicationSettings settings = Archiving();

        string? Na(ReplicationSection section) => ReplicationPlatform.NotApplicable(server.Platform, section);

        ReplicationRead<T> Section<T>(ReplicationSection section, T value)
            where T : class =>
            Na(section) is { } why ? ReplicationRead<T>.NotApplicable(why) : ReplicationRead<T>.Of(value);

        return new ReplicationFacts(
            Now,
            server,
            ReplicationRead<PostgresGrants>.Of(grants ?? Grants()),
            ReplicationRead<ReplicationRole>.Of(new ReplicationRole(false, null, null, null, null, null, null, Now)),
            Section(ReplicationSection.Standbys, new StandbySnapshot(standbys?.Count ?? 0, standbys ?? [], Now)),
            ReplicationRead<ReplicationSlotSnapshot>.Of(new ReplicationSlotSnapshot(Now, version, false, -1, 0, 3,
            [
                Slot("standby1", "physical", true),
                Slot("cdc", "logical", true, "pgoutput", "app"),
                Slot("billing_cdc", "logical", false, "wal2json", "billing"),
            ])),
            ReplicationRead<SlotStatisticsList>.Of(new SlotStatisticsList([new SlotStatisticsRow("cdc", true, false, 1, 10, 0, 0, 5, 1000)], true)),
            Section(ReplicationSection.Archiver, Archiver()),
            new ReplicationRead<WalDirectoryFacts>(null, ReplicationReadOutcome.Read, null),
            Section(ReplicationSection.Wal, new WalStatistics(1000, 250, 64e6, 0, 10, 5, null, null, null)),
            Section(ReplicationSection.Checkpoints, new CheckpointStatistics("pg_stat_checkpointer", CheckpointCounting.CheckpointerWithSkipped,
                8, 2, null, 0, 0, 0, null, null, null, 12, 0, null, 100, null)),
            ReplicationRead<IoStatistics>.Of(new IoStatistics([new IoGroup("client backend", "relation", 10, 2, 1, 100, 0, 0, 81920, 16384, 8192, null, null)], true)),
            Section(ReplicationSection.Tablespaces, new TablespaceList(2, [new TablespaceRow("pg_default", "", true), new TablespaceRow("pg_global", "", false)])),
            ReplicationRead<ReplicationSettings>.Of(settings),
            statements ?? []);
    }

    internal static StandbyRow Standby(string name = "reporting") =>
        new(name, "10.0.0.3", "replicator", "streaming", "async", 2048, 1024, TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(6), TimeSpan.FromMilliseconds(7));

    private static ReplicationView Build(ReplicationFacts facts, bool names, HealthHistoryView? history = null) =>
        ReplicationService.Build(facts, names, names ? null : "needs ViewDatabaseActivity", history ?? HealthHistoryView.Empty, PostgresStudioText.Instance);

    [Fact]
    public void Without_the_activity_gate_no_standby_slot_or_command_is_named()
    {
        ReplicationView view = Build(Facts(standbys: [Standby()]), names: false);

        view.NamesShown.Should().BeFalse();
        view.NamesWithheld.Should().Be("needs ViewDatabaseActivity");

        StandbyView standby = view.Standbys.Value!.Rows.Should().ContainSingle().Subject;
        (standby.Name, standby.Client, standby.User).Should().Be((null, null, null));
        (standby.Row.ApplicationName, standby.Row.ClientAddress, standby.Row.User).Should().Be((null, null, null), "the numbers' row carries no name either");
        standby.Row.BehindBytes.Should().Be(2048, "the numbers are the database-wide gate's");

        view.Slots.Value!.Rows.Select(static x => x.Description).Should().Equal(
            "a physical slot", "a logical slot of this database", ReplicationSlot.AnotherDatabase);
        view.Slots.Value.Rows.Should().OnlyContain(static x => x.Slot.Name.Length == 0 && x.Slot.Plugin == null && x.Slot.Database == null);

        view.Archiver.Value!.CommandText.Should().BeNull();
        view.Archiver.Value.CommandWithheld.Should().Be("needs ViewDatabaseActivity");
        view.Archiver.Value.Facts.Settings.ArchiveCommandText.Should().Be("(withheld)", "the facts the view carries have the text blanked");
    }

    [Fact]
    public void Through_the_gate_names_are_shown_but_another_databases_logical_slot_never_is()
    {
        ReplicationView view = Build(Facts(standbys: [Standby()]), names: true);

        StandbyView standby = view.Standbys.Value!.Rows.Single();
        (standby.Name, standby.Client, standby.User).Should().Be(("reporting", "10.0.0.3", "replicator"));

        view.Slots.Value!.Rows.Select(static x => x.Description).Should().Equal(
            "'standby1', physical", "'cdc', logical (pgoutput) for database 'app'", ReplicationSlot.AnotherDatabase);
        view.Slots.Value.Rows.Should().NotContain(static x => x.Description.Contains("billing", StringComparison.Ordinal));
        view.Slots.Value.Rows[1].Statistics!.TotalBytes.Should().Be(1000, "a slot's statistics are joined by name and shown by its rule");

        view.Archiver.Value!.CommandText.Should().Be("/usr/local/bin/archive %p");
    }

    [Fact]
    public void Without_pg_read_all_stats_the_standbys_are_counted_and_not_checked()
    {
        StandbyRow unreadable = new("walreceiver", null, "postgres", null, null, null, null, null, null, null);

        StandbysView standbys = Build(Facts(standbys: [unreadable]), names: true).Standbys.Value!;

        standbys.Total.Should().Be(1);
        standbys.Unreadable.Should().Be(1);
        standbys.NotChecked.Should().StartWith(PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, PostgresPlatform.SelfHosted, "app"));
    }

    [Fact]
    public void Without_pg_monitor_the_directory_is_not_checked_naming_it()
    {
        Build(Facts(), names: false).Archiver.Value!.DirectoryNotChecked
            .Should().Be(PostgresGrants.NotChecked(PostgresGrant.Monitor, PostgresPlatform.SelfHosted, "app"));
    }

    [Fact]
    public void On_Aurora_the_sections_its_storage_layer_does_say_so_and_slots_and_io_stay()
    {
        ReplicationView view = Build(Facts(PostgresPlatformKind.AmazonAurora), names: true);

        foreach (ReplicationReadOutcome outcome in new[] { view.Standbys.Outcome, view.Archiver.Outcome, view.Wal.Outcome, view.Checkpoints.Outcome, view.Tablespaces.Outcome })
        {
            outcome.Should().Be(ReplicationReadOutcome.NotApplicable);
        }

        view.Checkpoints.Note.Should().Be(ReplicationPlatform.Aurora);
        view.Slots.Value.Should().NotBeNull();
        view.Io.Value.Should().NotBeNull();
    }

    [Fact]
    public void The_wal_rate_and_full_page_share_are_window_deltas_of_the_advisors_samples()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-10), [(ReplicationHistory.WalBytes, 4e6), (ReplicationHistory.WalRecords, 400), (ReplicationHistory.WalFullPageImages, 100)]));

        WalView wal = Build(Facts(), names: false, history).Wal.Value!;

        wal.Window!.Span.Should().Be(TimeSpan.FromMinutes(10));
        wal.BytesPerSecond.Should().BeApproximately((64e6 - 4e6) / 600, 1e-6);
        wal.FullPageImageShare.Should().BeApproximately((250d - 100) / (1000 - 400), 1e-9);

        Build(Facts(), names: false).Wal.Value!.BytesPerSecond.Should().BeNull("one reading is no rate");
    }

    [Fact]
    public void The_checkpoints_window_is_R04s_own_judgement()
    {
        HealthHistoryView history = History(
            (Now.AddMinutes(-30), [(ReplicationHistory.CheckpointsTimed, 4), (ReplicationHistory.CheckpointsRequested, 0)]));

        CheckpointsView checkpoints = Build(Facts(), names: false, history).Checkpoints.Value!;

        checkpoints.Judgement!.Requested.Should().Be(2);
        checkpoints.Judgement.Denominator.Should().Be(6);
        checkpoints.CheckpointTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Below_16_there_is_no_pg_stat_io_and_the_section_says_which_release_has_it()
    {
        ReplicationFacts facts = Facts(version: 150019) with { Io = ReplicationRead<IoStatistics>.Needs(PostgresFeature.PgStatIo) };

        Build(facts, names: false).Io.Note.Should().Be("Not on this server: needs PostgreSQL 16.");
    }
}
