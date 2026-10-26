using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Server;

/// <summary>
/// A server setting as a remedy: <c>setting = value</c>, worded for the platform, a restart said only for a setting whose
/// <c>pg_settings.context</c> is <c>postmaster</c> - and every kind of remedy held to its own rule.
/// </summary>
public class ServerSettingRemedyTests
{
    private static readonly StudioText Text = PostgresStudioText.Instance;

    [Fact]
    public void The_plain_form_is_the_line_under_the_comment_and_ends_where_it_is_set()
    {
        HealthRemedy remedy = ServerSettingRemedy.For(PostgresPlatform.SelfHosted, "track_counts", "on", null, Text, "superuser");

        remedy.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        remedy.Code.Should().Be("# A server setting:\ntrack_counts = on");
        remedy.Explanation.Should().Be("Set it in postgresql.conf, followed by a reload.");
        remedy.Heading.Should().Be(HealthRemedy.ChangeHeading);
    }

    [Fact]
    public void A_postmaster_setting_says_the_server_must_restart()
    {
        HealthRemedy remedy = ServerSettingRemedy.For(
            new PostgresPlatform(PostgresPlatformKind.AmazonAurora, "marker"), "max_connections", "400", "Leaves room for the pools.", Text, "postmaster");

        remedy.Code.Should().Be("# A server setting:\nmax_connections = 400");
        remedy.Explanation.Should().Be(
            "Leaves room for the pools. Set it in the cluster's DB cluster parameter group (Aurora). It takes effect only when the server restarts.");
    }

    [Fact]
    public void A_setting_whose_context_was_not_read_says_nothing_about_a_restart()
    {
        ServerSettingRemedy.For(PostgresPlatform.SelfHosted, "max_connections", "400", null, Text, context: null).Explanation
            .Should().Be("Set it in postgresql.conf, followed by a reload.");
    }

    [Fact]
    public void The_general_form_carries_the_checks_own_comment_and_names_the_place_where_the_check_puts_it()
    {
        HealthRemedy remedy = ServerSettingRemedy.Build(
            new PostgresPlatform(PostgresPlatformKind.CloudSql, "marker"),
            "max_slot_wal_keep_size",
            "50GB",
            Text,
            "sighup",
            " (PostgreSQL 13+): no slot may hold more than this.",
            static where => "Cap it, in " + where + ", above the longest outage.");

        remedy.Code.Should().Be("# A server setting (PostgreSQL 13+): no slot may hold more than this.\nmax_slot_wal_keep_size = 50GB");
        remedy.Explanation.Should().Be("Cap it, in the instance's database flags (Cloud SQL), above the longest outage.");
    }

    [Theory]
    [InlineData(PostgresPlatformKind.SelfHosted)]
    [InlineData(PostgresPlatformKind.AmazonRds)]
    [InlineData(PostgresPlatformKind.AmazonAurora)]
    [InlineData(PostgresPlatformKind.Azure)]
    [InlineData(PostgresPlatformKind.CloudSql)]
    [InlineData(PostgresPlatformKind.Neon)]
    [InlineData(PostgresPlatformKind.Supabase)]
    public void Every_setting_changes_where_the_platform_says_until_AUR_refines_a_pair(PostgresPlatformKind kind)
    {
        PostgresPlatform platform = new(kind, null);

        ServerSettingRemedy.WhereToSet(platform, "max_connections").Should().Be(platform.WhereSettingsChange);
    }

    [Theory]
    [InlineData("Max_Connections")]
    [InlineData("max connections")]
    [InlineData("x;drop")]
    public void A_name_that_is_not_a_settings_is_refused(string setting)
    {
        FluentActions.Invoking(() => ServerSettingRemedy.For(PostgresPlatform.SelfHosted, setting, "1", null, Text, null))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_value_on_two_lines_is_refused()
    {
        FluentActions.Invoking(static () => ServerSettingRemedy.For(PostgresPlatform.SelfHosted, "work_mem", "4MB\nfsync = off", null, Text, null))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Only_a_configuration_line_is_refused_DDL()
    {
        FluentActions.Invoking(static () => new HealthRemedy("ALTER TABLE x SET (fillfactor = 80);"))
            .Should().Throw<ArgumentException>().WithMessage("*never DDL*");

        HealthRemedy.ForMigration("ALTER TABLE \"public\".\"x\" SET (fillfactor = 80);", "Leaves room.").Kind.Should().Be(HealthRemedyKind.Migration);
        HealthRemedy.ForServerSetting("alter_system_is_not_a_command = on", null).Kind.Should().Be(HealthRemedyKind.ServerSetting);
        HealthRemedy.ForManagedPlatform("Create a parameter group with max_connections = 400.", null).Kind.Should().Be(HealthRemedyKind.ManagedPlatform);
    }

    [Fact]
    public void Each_kind_has_its_value_and_heading()
    {
        ((int) HealthRemedyKind.Configuration).Should().Be(0);
        ((int) HealthRemedyKind.Migration).Should().Be(1);
        ((int) HealthRemedyKind.ServerSetting).Should().Be(2);
        ((int) HealthRemedyKind.ManagedPlatform).Should().Be(3);

        new HealthRemedy("opts.Schema.For<Order>().FillFactor(80);").Heading.Should().Be("What to change");
        HealthRemedy.ForMigration("ALTER TABLE \"public\".\"x\" SET (fillfactor = 80);", "Leaves room.").Heading
            .Should().Be("For your own migrations - the studio never runs this");
        HealthRemedy.ForManagedPlatform("Raise the maximum capacity.", null).Heading.Should().Be("What to change");
    }
}
