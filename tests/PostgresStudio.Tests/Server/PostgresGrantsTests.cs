using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Tests.Server;

/// <summary>
/// "Not checked" is a sentence that names the grant (W5 §2.6, issue #8 A7): every grant on every platform, pinned as the
/// page prints it - the privilege, the role the reads run as, and who can grant it there.
/// </summary>
public class PostgresGrantsTests
{
    /// <summary>What each grant's "this needs ..." says.</summary>
    private static readonly Dictionary<string, string> Needs = new(StringComparer.Ordinal)
    {
        ["pg_read_all_stats"] = "pg_read_all_stats (pg_monitor includes it)",
        ["pg_stat_scan_tables"] = "pg_stat_scan_tables (pg_monitor includes it)",
        ["pg_read_all_settings"] = "pg_read_all_settings (pg_monitor includes it)",
        ["pg_monitor"] = "pg_monitor",
        ["pg_signal_backend"] = "pg_signal_backend",
        ["pg_maintain"] = "pg_maintain",
    };

    /// <summary>Who can grant a predefined role, per platform, as the sentence ends.</summary>
    private static readonly Dictionary<PostgresPlatformKind, string> Grantors = new()
    {
        [PostgresPlatformKind.SelfHosted] = "On a self-hosted server a superuser can grant it.",
        [PostgresPlatformKind.AmazonRds] = "On Amazon RDS for PostgreSQL the master user (a member of rds_superuser) can grant it.",
        [PostgresPlatformKind.AmazonAurora] = "On Amazon Aurora PostgreSQL the master user (a member of rds_superuser) can grant it.",
        [PostgresPlatformKind.Azure] = "On Azure Database for PostgreSQL the server admin (a member of azure_pg_admin) can grant it.",
        [PostgresPlatformKind.CloudSql] = "On Google Cloud SQL for PostgreSQL a member of cloudsqlsuperuser, such as the postgres user, can grant it.",
        [PostgresPlatformKind.Neon] = "On Neon a member of neon_superuser can grant it.",
        [PostgresPlatformKind.Supabase] = "On Supabase the postgres role can grant it.",
    };

    public static TheoryData<PostgresPlatformKind, string> Pairs
    {
        get
        {
            TheoryData<PostgresPlatformKind, string> pairs = [];

            foreach (PostgresPlatformKind kind in Enum.GetValues<PostgresPlatformKind>())
            {
                foreach (string grant in Needs.Keys)
                {
                    pairs.Add(kind, grant);
                }
            }

            return pairs;
        }
    }

    private static PostgresPlatform Platform(PostgresPlatformKind kind) => new(kind, kind == PostgresPlatformKind.SelfHosted ? null : "a marker");

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Every_grant_on_every_platform_names_the_privilege_the_role_and_the_grantor(PostgresPlatformKind kind, string privilege)
    {
        PostgresGrant grant = PostgresGrant.Roles.Single(x => x.Privilege == privilege);

        PostgresGrants.NotChecked(grant, Platform(kind), "app_reader").Should().Be(
            "Not checked: this needs " + Needs[privilege] + " for role app_reader. " + Grantors[kind]);
    }

    [Fact]
    public void Every_platform_and_every_grant_has_its_row_here()
    {
        Grantors.Keys.Should().BeEquivalentTo(Enum.GetValues<PostgresPlatformKind>(), "a new platform words its grantor");
        Needs.Keys.Should().BeEquivalentTo(PostgresGrant.Roles.Select(static x => x.Privilege), "a new grant words what it needs");
    }

    [Fact]
    public void The_brief_s_example_reads_as_it_was_written()
    {
        PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, Platform(PostgresPlatformKind.AmazonAurora), "app_reader").Should().Be(
            "Not checked: this needs pg_read_all_stats (pg_monitor includes it) for role app_reader. On Amazon Aurora PostgreSQL the "
            + "master user (a member of rds_superuser) can grant it.");
    }

    [Theory]
    [InlineData(PostgresPlatformKind.SelfHosted)]
    [InlineData(PostgresPlatformKind.AmazonAurora)]
    [InlineData(PostgresPlatformKind.Supabase)]
    public void A_column_privilege_is_the_tables_owners_to_grant_on_every_platform(PostgresPlatformKind kind)
    {
        PostgresGrants.NotChecked(PostgresGrant.ColumnSelect("store.docs", "tenant_id"), Platform(kind), "app_reader").Should().Be(
            "Not checked: this needs SELECT on column tenant_id of store.docs for role app_reader. The owner of store.docs can grant it.");
    }

    /// <summary>
    /// An undeclared refusal claims no one grant: it names the role, points at Postgres' own words, says whose each kind of
    /// object is to grant, and who grants a monitoring role on this platform.
    /// </summary>
    [Theory]
    [InlineData(PostgresPlatformKind.SelfHosted, "On a self-hosted server a superuser can grant it.")]
    [InlineData(PostgresPlatformKind.AmazonRds, "On Amazon RDS for PostgreSQL the master user (a member of rds_superuser) can grant it.")]
    [InlineData(PostgresPlatformKind.AmazonAurora, "On Amazon Aurora PostgreSQL the master user (a member of rds_superuser) can grant it.")]
    public void An_undeclared_refusal_names_the_role_and_whose_each_kind_of_object_is_to_grant(PostgresPlatformKind kind, string grantor)
    {
        PostgresGrants.Refused(Platform(kind), "probe").Should().Be(
            "Not checked: Postgres refused role probe a read this check needs (SQLSTATE 42501), and what it refused is in "
            + "Postgres' words below. A table, schema or sequence is its owner's to grant (SELECT or USAGE); a statistics view "
            + "or function is covered by pg_monitor. " + grantor);
    }

    /// <summary>The declared branch: a check that knows its grant claims it, and only then is pg_monitor named as the grant.</summary>
    [Fact]
    public void A_declared_grant_is_claimed_and_an_undeclared_one_is_not()
    {
        PostgresGrants.NotChecked(PostgresGrant.Monitor, Platform(PostgresPlatformKind.SelfHosted), "probe")
            .Should().StartWith("Not checked: this needs pg_monitor for role probe.");
        PostgresGrants.Refused(Platform(PostgresPlatformKind.SelfHosted), "probe")
            .Should().NotContain("this needs", "an undeclared refusal does not know which grant the statement lacked");
    }

    /// <summary>
    /// The role a refusal names: <c>SqlConsoleRole</c>'s (<see cref="PostgresGrants.ReadsAs" />) for a statement read as it,
    /// the login (<see cref="PostgresGrants.LoginRole" />) for one read as the application's own role while a console role
    /// is set.
    /// </summary>
    [Theory]
    [InlineData(false, "console_role")]
    [InlineData(true, "console_login")]
    public void With_a_console_role_set_a_refusal_names_the_role_the_statement_ran_as(bool asApplicationRole, string role)
    {
        PostgresGrants grants = new("console_role", false, []) { LoginRole = "console_login" };

        grants.RoleOf(asApplicationRole, sqlConsoleRole: "console_role").Should().Be(role);
    }

    /// <summary>
    /// With no console role the two sessions are one, so the application-role read ran as this read's <c>current_user</c> -
    /// even where it differs from the login, as for a login whose sessions start under another role.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_a_console_role_the_reading_role_is_named_whichever_session(bool asApplicationRole)
    {
        PostgresGrants grants = new("app_owner", false, []) { LoginRole = "app_login" };

        grants.RoleOf(asApplicationRole, sqlConsoleRole: null).Should().Be("app_owner");
        grants.RoleOf(asApplicationRole, sqlConsoleRole: " ").Should().Be("app_owner");
    }

    [Fact]
    public void Without_a_login_read_the_reading_role_is_named()
    {
        new PostgresGrants("app", false, []).RoleOf(asApplicationRole: true, sqlConsoleRole: "console_role").Should().Be("app");
    }

    /// <summary>The runner's mark, which a shared read's exception carries to every check that asked for it.</summary>
    [Fact]
    public void A_failure_read_as_the_application_role_is_told_by_its_mark_or_its_inner_exceptions()
    {
        InvalidOperationException marked = new("refused");
        marked.Data[HealthSqlRunner.ApplicationRoleKey] = true;

        HealthSqlRunner.WasReadAsApplicationRole(marked).Should().BeTrue();
        HealthSqlRunner.WasReadAsApplicationRole(new InvalidOperationException("wrapped", marked)).Should().BeTrue();
        HealthSqlRunner.WasReadAsApplicationRole(new InvalidOperationException("refused")).Should().BeFalse();
    }

    [Theory]
    [InlineData(PostgresPlatformKind.SelfHosted,
        "On a self-hosted server a superuser can run CREATE EXTENSION pgstattuple; it is not a trusted extension, so a database owner cannot.")]
    [InlineData(PostgresPlatformKind.AmazonRds,
        "On Amazon RDS for PostgreSQL the master user (a member of rds_superuser) can run CREATE EXTENSION pgstattuple, where the engine version offers it.")]
    [InlineData(PostgresPlatformKind.AmazonAurora,
        "On Amazon Aurora PostgreSQL the master user (a member of rds_superuser) can run CREATE EXTENSION pgstattuple, where the engine version offers it.")]
    [InlineData(PostgresPlatformKind.Azure,
        "On Azure Database for PostgreSQL the server admin (a member of azure_pg_admin) can run CREATE EXTENSION pgstattuple once the azure.extensions server parameter allows it.")]
    [InlineData(PostgresPlatformKind.CloudSql,
        "On Google Cloud SQL for PostgreSQL a member of cloudsqlsuperuser, such as the postgres user, can run CREATE EXTENSION pgstattuple.")]
    [InlineData(PostgresPlatformKind.Neon, "On Neon a member of neon_superuser can run CREATE EXTENSION pgstattuple.")]
    [InlineData(PostgresPlatformKind.Supabase, "On Supabase the postgres role can run CREATE EXTENSION pgstattuple.")]
    public void An_untrusted_extension_names_who_may_create_it_on_each_platform(PostgresPlatformKind kind, string who)
    {
        PostgresGrants.ExtensionNotInstalled("pgstattuple", trusted: false, Platform(kind)).Should().Be(
            "Not checked: this needs the pgstattuple extension, which this database does not have. " + who);
    }

    [Fact]
    public void A_trusted_extension_is_any_creator_on_the_databases()
    {
        PostgresGrants.ExtensionNotInstalled("pg_trgm", trusted: true, Platform(PostgresPlatformKind.AmazonRds)).Should().Be(
            "Not checked: this needs the pg_trgm extension, which this database does not have. It is a trusted extension, so a role "
            + "with CREATE on this database can run CREATE EXTENSION pg_trgm.");
    }

    [Fact]
    public void The_three_extensions_a_check_may_need_are_untrusted()
    {
        PostgresGrants.KnownExtensions.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["pg_stat_statements"] = false,
            ["pgstattuple"] = false,
            ["hypopg"] = false,
        });
    }

    [Fact]
    public void Membership_through_pg_monitor_holds_what_it_includes_and_a_superuser_holds_every_role()
    {
        PostgresGrants monitor = new("mon", false, [new("pg_read_all_stats", true, false), new("pg_monitor", true, true)]);

        monitor.Holds(PostgresGrant.ReadAllStats).Should().BeTrue("pg_monitor includes it");
        monitor.Holds(PostgresGrant.StatScanTables).Should().BeTrue();
        monitor.Holds(PostgresGrant.SignalBackend).Should().BeFalse();
        monitor.Holds(PostgresGrant.ColumnSelect("store.docs", "tenant_id")).Should().BeFalse("a column privilege is never read here");
        monitor.IsProviderAdmin.Should().BeFalse();
        monitor.MemberOf.Should().Be("pg_monitor");

        PostgresGrants superuser = new("postgres", true, []);
        superuser.Holds(PostgresGrant.Maintain).Should().BeTrue();
        superuser.MemberOf.Should().Be("superuser");

        PostgresGrants admin = new("master", false, [new("rds_superuser", true, true)]);
        admin.IsProviderAdmin.Should().BeTrue("a provider admin, not a superuser (B0 §5)");
        admin.Superuser.Should().BeFalse();
    }

    [Fact]
    public void No_kind_of_remedy_may_be_a_grant()
    {
        FluentActions.Invoking(static () => HealthRemedy.ForMigration("GRANT pg_read_all_stats TO app;", "Lets it read."))
            .Should().Throw<ArgumentException>().WithMessage("*never a privilege change*");
        FluentActions.Invoking(static () => HealthRemedy.ForServerSetting("revoke all on schema public from public", null))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(static () => HealthRemedy.ForManagedPlatform("grant pg_monitor to app", null))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(static () => new HealthRemedy("GRANT pg_monitor TO app;"))
            .Should().Throw<ArgumentException>();
    }
}
