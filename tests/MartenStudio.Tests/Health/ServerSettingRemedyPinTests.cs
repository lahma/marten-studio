using PostgresStudio;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Health;

/// <summary>
/// W5-VERS moved S02's and R01's server-setting remedies onto <c>ServerSettingRemedy</c>, and gave every remedy a kind:
/// what each one renders is pinned here byte for byte as it rendered before the move - in both studios' words - and C03's,
/// a configuration line all along, is pinned unchanged beside them.
/// </summary>
public class ServerSettingRemedyPinTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly int[] Minutes = [0, 5, 10];

    private static readonly PostgresPlatform Rds =new(PostgresPlatformKind.AmazonRds, "the server has Amazon RDS's rds.extensions settings");

    private static readonly ServerSetting[] Defaults =
    [
        new("track_counts", "on", "default", false), new("fsync", "on", "default", false), new("full_page_writes", "on", "default", false),
        new("zero_damaged_pages", "off", "default", false), new("ignore_checksum_failure", "off", "default", false),
        new("synchronous_commit", "on", "default", false),
    ];

    public static TheoryData<string, string> Texts => new()
    {
        { "MartenStudio", "A server setting, not a StoreOptions one" },
        { "PostgresStudio", "A server setting" },
    };

    private static StudioText Text(string studio) => studio == "MartenStudio" ? StudioText.Marten : PostgresStudioText.Instance;

    private static HealthRemedy S02(string studio, ServerSetting changed, PostgresPlatform? platform = null, string environment = "Production") =>
        new DangerousSettingsCheck().Evaluate(
                new DangerousSettingsFacts(Today, [.. Defaults.Where(x => x.Name != changed.Name), changed], 6, environment, platform ?? PostgresPlatform.SelfHosted),
                HealthEvaluation.At(Today, text: Text(studio)))
            .Findings.Single().Remedy!;

    [Theory]
    [MemberData(nameof(Texts))]
    public void S02s_remedy_for_a_server_file_setting_renders_as_it_always_did(string studio, string comment)
    {
        HealthRemedy remedy = S02(studio, new ServerSetting("track_counts", "off", "configuration file", false) { Context = "sighup" });

        remedy.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        remedy.Heading.Should().Be("What to change", "a server setting is drawn under the heading it always was");
        remedy.Code.Should().Be("# " + comment + ":\ntrack_counts = on");
        remedy.Explanation.Should().Be("Set it in postgresql.conf, followed by a reload.");
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void S02s_remedy_on_a_managed_platform_names_the_platforms_place(string studio, string comment)
    {
        HealthRemedy remedy = S02(studio, new ServerSetting("fsync", "off", "configuration file", false), Rds);

        remedy.Code.Should().Be("# " + comment + ":\nfsync = on");
        remedy.Explanation.Should().Be("Set it in the instance's DB parameter group (Amazon RDS).");
    }

    [Fact]
    public void S02s_remedy_for_a_setting_the_connection_sends_points_at_the_connection_string()
    {
        HealthRemedy remedy = S02("MartenStudio", new ServerSetting("synchronous_commit", "off", "client", false), environment: "Development");

        remedy.Code.Should().Be("# A server setting, not a StoreOptions one:\nsynchronous_commit = on");
        remedy.Explanation.Should().Be(
            "It arrives with the connection: remove it from the Options of the connection string the store's data source "
            + "is built from, and every new connection runs with the server's value.");
    }

    [Fact]
    public void S02s_remedy_for_a_role_setting_points_at_the_role()
    {
        HealthRemedy remedy = S02("MartenStudio", new ServerSetting("synchronous_commit", "off", "user", false), environment: "Development");

        remedy.Explanation.Should().Be(
            "It is set for this role or this database, not for the server: whoever owns the "
            + "role or the database resets it there, and new sessions pick the server's value up.");
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void R01s_near_limit_remedy_renders_as_it_always_did(string studio, string comment)
    {
        HealthRemedy remedy = R01(studio, new ReplicationSlot("standby", "physical", null, null, false, true, 120L << 20, "unreserved", null, 1, null, null, null, null, true), 100L << 20);

        remedy.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        remedy.Code.Should().Be("# " + comment + " (PostgreSQL 13+). Raise it only if the WAL volume can hold the difference:\nmax_slot_wal_keep_size = 100GB");
        remedy.Explanation.Should().Be(
            "Restart the consumer that reads the slot now, before the next checkpoint, so that it catches up and Postgres can "
            + "recycle the WAL it confirms. If nothing will read the slot again, drop it (pg_drop_replication_slot) and the WAL "
            + "goes at the next checkpoint. Raising max_slot_wal_keep_size, in postgresql.conf, followed by a reload, "
            + "buys time only where the disk has room for it.");
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void R01s_inactive_remedy_renders_as_it_always_did(string studio, string comment)
    {
        ReplicationSlot slot = new("debezium_orders", "physical", null, null, false, false, 3L << 30, "reserved", null, 1, null, null, null, null, true);
        DateTimeOffset start = Today.AddMinutes(-10);

        HealthHistoryView history = new([
            .. Minutes.Select(minute => new HealthSample(
                start.AddMinutes(minute),
                new ReplicationSlotCheck().Sample(Facts(start.AddMinutes(minute), -1, slot))
                    .ToDictionary(static p => p.Series, static p => p.Value, StringComparer.Ordinal))),
        ]);

        HealthRemedy remedy = new ReplicationSlotCheck()
            .Evaluate(Facts(Today, -1, slot), HealthEvaluation.At(Today, history: history, text: Text(studio)))
            .Findings.Single().Remedy!;

        remedy.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        remedy.Code.Should().Be("# " + comment + " (PostgreSQL 13+): no slot may hold more than this.\nmax_slot_wal_keep_size = 50GB");
        remedy.Explanation.Should().Be(
            "First the consumer: restart whatever reads the slot - a change data capture connector, a logical subscription, a "
            + "standby - so that it catches up and Postgres can recycle the WAL; or, if nothing will read it again, drop the slot "
            + "(pg_drop_replication_slot). Then cap what any slot may hold, in postgresql.conf, followed by a reload: a "
            + "slot that passes max_slot_wal_keep_size is invalidated instead of filling the disk, and its consumer has to be "
            + "re-initialised - so size it above the longest outage a consumer should survive.");
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void C03s_remedy_is_the_configuration_line_it_always_was(string studio, string comment)
    {
        _ = comment;

        HealthRemedy remedy = new ServerConnectionHeadroomCheck()
            .Evaluate(new ServerConnectionLimits(96, 10, 100, 3, 0, 170004), HealthEvaluation.At(Today, text: Text(studio)))
            .Findings.Single().Remedy!;

        remedy.Kind.Should().Be(HealthRemedyKind.Configuration, "C03's remedy is the store's pool ceiling, not a server setting");
        remedy.Code.Should().Be(
            "// Fewer connections per process - every data source Marten builds from a connection string:\n"
            + "opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 50);");
        remedy.Explanation.Should().Be(
            "Lower pool ceilings across the applications that share the server, or a pooler such as PgBouncer in front of it. "
            + "max_connections itself is the server's setting, which the studio never changes; raising it costs memory per "
            + "connection on the server.");
    }

    private static ReplicationSlotFacts Facts(DateTimeOffset at, long? cap, params ReplicationSlot[] slots) =>
        new(new ReplicationSlotSnapshot(at, 160004, false, cap, 0, slots.Length, slots), new PostgresServerFacts(at, 160004, "16.4", PostgresPlatform.SelfHosted));

    private static HealthRemedy R01(string studio, ReplicationSlot slot, long cap) =>
        new ReplicationSlotCheck()
            .Evaluate(Facts(Today, cap, slot), HealthEvaluation.At(Today, text: Text(studio)))
            .Findings.Single().Remedy!;
}
