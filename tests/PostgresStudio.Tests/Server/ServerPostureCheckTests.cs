using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;

using static PostgresStudio.Tests.Server.ServerTestFacts;

namespace PostgresStudio.Tests.Server;

/// <summary>S04-S10's evaluators at their edges, in Production and in Development (W5-S2).</summary>
public class ServerPostureCheckTests
{
    // ---- S04 ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production", "Warning")]
    [InlineData("production", "Warning")]
    [InlineData("Development", "Info")]
    [InlineData(null, "Info")]
    public void S04_a_superuser_login_is_a_Warning_in_Production_and_Info_elsewhere(string? environment, string expected)
    {
        HealthSeverity severity = Enum.Parse<HealthSeverity>(expected);
        HealthVerdict verdict = new SuperuserConnectionCheck()
            .Evaluate(new SuperuserConnectionFacts(Posture("postgres", superuser: true, memberOf: ["rds_superuser", "pg_read_server_files"]), environment, SelfHosted), Evaluation())
            .ShouldBeWellFormedFor<SuperuserConnectionCheck>();

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(severity);
        finding.Variant.Should().Be("superuser");
        finding.Subject.Should().BeNull("one finding per database: the role is evidence, never the subject");
        finding.Remedy.Should().BeNull("the change is a DBA's role, said in words");
        finding.Evidence.Should().Contain(x => x.Label == "Logs in as" && x.Value == "postgres");
        finding.WhyItMatters.Should().Contain("owns its schema and nothing more");
    }

    [Theory]
    [InlineData("rds_superuser")]
    [InlineData("azure_pg_admin")]
    [InlineData("cloudsqlsuperuser")]
    [InlineData("neon_superuser")]
    public void S04_a_provider_admin_role_is_named_and_never_called_a_superuser(string role)
    {
        HealthFinding finding = new SuperuserConnectionCheck()
            .Evaluate(new SuperuserConnectionFacts(Posture("master", memberOf: [role]), "Production", Platform(PostgresPlatformKind.AmazonRds)), Evaluation())
            .ShouldBeWellFormedFor<SuperuserConnectionCheck>()
            .Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("provider-admin");
        finding.Title.Should().Be("The application connects as a provider admin role (" + role + ")");
        finding.Title.Should().NotContain("as a superuser", "a provider admin role is never called a superuser; the parenthesis only names it");
        finding.WhyItMatters.Should().StartWith("It is not a superuser");
        finding.Evidence.Should().Contain(x => x.Label == "Superuser" && x.Value == "no");
    }

    [Theory]
    [InlineData("pg_read_server_files")]
    [InlineData("pg_write_server_files")]
    [InlineData("pg_execute_server_program")]
    public void S04_a_member_of_a_file_access_role_is_the_file_access_variant(string role)
    {
        HealthFinding finding = new SuperuserConnectionCheck()
            .Evaluate(new SuperuserConnectionFacts(Posture(memberOf: [role]), "Development", SelfHosted), Evaluation())
            .ShouldBeWellFormedFor<SuperuserConnectionCheck>()
            .Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("file-access");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.WhyItMatters.Should().Contain(role);
    }

    [Fact]
    public void S04_is_quiet_for_a_role_that_owns_its_schema_and_nothing_more_and_names_it_at_its_gate()
    {
        HealthVerdict verdict = new SuperuserConnectionCheck()
            .Evaluate(new SuperuserConnectionFacts(Posture("app"), "Production", SelfHosted), Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("role app").And.StartWith("The store's data source");
        HealthCheckRegistry.Describe(typeof(SuperuserConnectionCheck)).Gate.Should().Be(HealthGate.Database, "a role's name is the database-wide gate's (D29)");
    }

    [Fact]
    public void S04_speaks_of_the_data_source_in_Postgres_Studio()
    {
        new SuperuserConnectionCheck()
            .Evaluate(new SuperuserConnectionFacts(Posture("app"), "Production", SelfHosted), Evaluation(postgres: true))
            .Note.Should().StartWith("The data source logs in as role app")
            .And.NotContain("store");
    }

    // ---- S05 ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Production", "Warning")]
    [InlineData("Staging", "Info")]
    public void S05_an_unencrypted_network_connection_fires_by_environment(string environment, string expected)
    {
        HealthSeverity severity = Enum.Parse<HealthSeverity>(expected);
        HealthFinding finding = new UnencryptedConnectionCheck()
            .Evaluate(new UnencryptedConnectionFacts(Posture(ssl: false, client: "172.17.0.1"), "on", environment), Evaluation())
            .ShouldBeWellFormedFor<UnencryptedConnectionCheck>()
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(severity);
        finding.Remedy!.Code.Should().Be("SSL Mode=VerifyFull");
        finding.Remedy.Kind.Should().Be(HealthRemedyKind.Configuration, "a connection-string setting, never a server one");
        finding.Remedy.Explanation.Should().NotContain("ssl = off");
        finding.Evidence.Should().NotContain(x => x.Value.Contains("172.17", StringComparison.Ordinal), "a client address is never shown");
    }

    [Fact]
    public void S05_says_the_server_needs_TLS_first_when_its_ssl_setting_is_off()
    {
        new UnencryptedConnectionCheck()
            .Evaluate(new UnencryptedConnectionFacts(Posture(ssl: false), "off", "Production"), Evaluation())
            .Findings.Single().Remedy!.Explanation.Should().Contain("ssl = off");
    }

    [Theory]
    [InlineData(null, "Unix-domain socket")]
    [InlineData("127.0.0.1", "loopback")]
    [InlineData("127.10.0.1", "loopback")]
    [InlineData("::1", "loopback")]
    [InlineData("::ffff:127.0.0.1", "loopback")]
    public void S05_a_socket_or_a_loopback_connection_is_quiet_without_TLS(string? client, string why)
    {
        HealthVerdict verdict = new UnencryptedConnectionCheck()
            .Evaluate(new UnencryptedConnectionFacts(Posture(ssl: false, client: client), "off", "Production"), Evaluation());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain(why);
    }

    [Fact]
    public void S05_is_quiet_for_an_encrypted_connection_and_for_one_it_could_not_read()
    {
        new UnencryptedConnectionCheck().Evaluate(new UnencryptedConnectionFacts(Posture(ssl: true), "on", "Production"), Evaluation())
            .Note.Should().Be("The connection is encrypted (TLSv1.3).");
        new UnencryptedConnectionCheck().Evaluate(new UnencryptedConnectionFacts(Posture(ssl: null), "on", "Production"), Evaluation())
            .Findings.Should().BeEmpty();
    }

    // ---- S06 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S06_a_PUBLIC_create_grant_fires_once_per_schema_naming_it_and_the_remedy_is_words()
    {
        PublicSchemaCreateFacts facts = new(["public", "store", "missing"], [new("public", true), new("store", false)], 140024);

        HealthFinding finding = new PublicSchemaCreateCheck().Evaluate(facts, Evaluation())
            .ShouldBeWellFormedFor<PublicSchemaCreateCheck>()
            .Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Subject.Should().Be("public");
        finding.Title.Should().Be("Any role may create objects in schema public");
        finding.Remedy.Should().BeNull("REVOKE is a privilege change, which no remedy may be");
        finding.WhyItMatters.Should().Contain("revokes CREATE on it from PUBLIC");
        finding.Caveat.Should().StartWith("Below PostgreSQL 15");
    }

    [Fact]
    public void S06_on_15_and_later_says_somebody_granted_it_or_the_cluster_came_from_14()
    {
        PublicSchemaCreateFacts facts = new(["public"], [new("public", true)], 170011);

        new PublicSchemaCreateCheck().Evaluate(facts, Evaluation()).Findings.Single().Caveat
            .Should().Contain("pg_database_owner").And.Contain("upgraded");
    }

    [Fact]
    public void S06_is_quiet_without_the_grant_and_with_no_schema_of_its_own()
    {
        new PublicSchemaCreateCheck().Evaluate(new PublicSchemaCreateFacts(["public", "store"], [new("public", false), new("store", false)], 170011), Evaluation())
            .Findings.Should().BeEmpty();
        new PublicSchemaCreateCheck().Evaluate(new PublicSchemaCreateFacts([], [], 170011), Evaluation())
            .Note.Should().Contain("no schema");
        new PublicSchemaCreateCheck().Evaluate(new PublicSchemaCreateFacts(["store"], [], 170011), Evaluation())
            .Note.Should().Contain("exists yet");
    }

    // ---- S07 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S07_an_extension_at_another_version_than_the_servers_is_an_Info_naming_it()
    {
        ServerExtensionsRead facts = new(
        [
            new("pg_stat_statements", true, "1.9", "1.10", false),
            new("pgstattuple", true, null, "1.5", false),
            new("plpgsql", false, "1.0", "1.0", true),
            new("gone", false, "2.0", null, null),
        ], 4);

        HealthVerdict verdict = new OutdatedExtensionCheck().Evaluate(facts, Evaluation()).ShouldBeWellFormedFor<OutdatedExtensionCheck>();

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Subject.Should().Be("pg_stat_statements");
        finding.Title.Should().Be("Extension pg_stat_statements is at 1.9; the server offers 1.10");
        finding.Remedy.Should().BeNull("ALTER EXTENSION is DDL, said in words");
        finding.WhyItMatters.Should().Contain("ALTER EXTENSION pg_stat_statements UPDATE");
        verdict.Note.Should().Contain("no files for gone");
    }

    [Fact]
    public void S07_is_quiet_when_every_installed_extension_is_current()
    {
        new OutdatedExtensionCheck()
            .Evaluate(new ServerExtensionsRead([new("plpgsql", false, "1.0", "1.0", true), new("pg_repack", true, null, "1.5.0", false)], 2), Evaluation())
            .Note.Should().Be("Every extension installed in this database is at the version the server offers.");
    }

    // ---- S08 ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(31, null)]
    [InlineData(30, "Warning")]
    [InlineData(8, "Warning")]
    [InlineData(7, "Critical")]
    [InlineData(1, "Critical")]
    [InlineData(-2, "Critical")]
    public void S08_a_password_expiring_within_30_days_is_a_Warning_and_within_7_Critical(int days, string? expected)
    {
        HealthSeverity? severity = expected is null ? null : Enum.Parse<HealthSeverity>(expected);
        HealthVerdict verdict = new PasswordExpiryCheck()
            .Evaluate(new PasswordExpiryFacts(Now, Posture(validUntil: Now.AddDays(days))), Evaluation())
            .ShouldBeWellFormedFor<PasswordExpiryCheck>();

        if (severity is null)
        {
            verdict.Findings.Should().BeEmpty();
            return;
        }

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(severity);
        finding.Variant.Should().BeNull("S08 has one kind of finding");
        finding.Caveat.Should().Contain("IAM, Entra ID or certificate login ignores it");
        finding.Remedy.Should().BeNull("ALTER ROLE is a DBA's change, said in words");
        finding.Title.Should().Contain(days < 0 ? "has expired" : "expires in");
    }

    [Fact]
    public void S08_is_quiet_for_a_password_with_no_expiry()
    {
        new PasswordExpiryCheck().Evaluate(new PasswordExpiryFacts(Now, Posture(validUntil: null)), Evaluation())
            .Note.Should().Contain("no expiry");
    }

    // ---- S09 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S09_a_switch_off_whose_default_is_on_fires_and_one_shipped_off_never_does()
    {
        PlannerSwitchesFacts facts = new(
            Settings(
                Setting("enable_seqscan", "off", source: "configuration file", boot: "on", context: "user"),
                Setting("enable_hashjoin", "on", boot: "on"),
                Setting("enable_group_by_reordering", "off", source: "default", boot: "off")),
            SelfHosted);

        HealthFinding finding = new PlannerSwitchesCheck().Evaluate(facts, Evaluation())
            .ShouldBeWellFormedFor<PlannerSwitchesCheck>()
            .Findings.Should().ContainSingle().Subject;

        finding.Subject.Should().Be("enable_seqscan");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(x => x.Label == "Set by" && x.Value == "the server's configuration file");
        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.ServerSetting);
        finding.Remedy.Code.Should().EndWith("enable_seqscan = on");
        finding.Remedy.Explanation.Should().Contain("postgresql.conf");
    }

    [Theory]
    [InlineData("user", "ALTER ROLE … RESET enable_nestloop")]
    [InlineData("database", "ALTER DATABASE … RESET enable_nestloop")]
    [InlineData("client", "Options of the connection string")]
    public void S09_a_role_database_or_connection_setting_is_reset_in_words_not_a_server_line(string source, string words)
    {
        HealthFinding finding = new PlannerSwitchesCheck()
            .Evaluate(new PlannerSwitchesFacts(Settings(Setting("enable_nestloop", "off", source: source, boot: "on")), SelfHosted), Evaluation())
            .Findings.Single();

        finding.Remedy.Should().BeNull();
        finding.WhyItMatters.Should().Contain(words);
    }

    // ---- S10 ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S10_sessions_that_start_read_only_are_Critical_and_a_standby_a_Warning_to_mute_if_it_only_reads()
    {
        HealthVerdict verdict = new ReadOnlyStoreCheck()
            .Evaluate(new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "on", source: "user", boot: "off"), true, SelfHosted), Evaluation())
            .ShouldBeWellFormedFor<ReadOnlyStoreCheck>();

        verdict.Findings.Should().HaveCount(2);

        HealthFinding readOnly = verdict.Findings.Single(x => x.Variant == "read-only");
        readOnly.Severity.Should().Be(HealthSeverity.Critical);
        readOnly.Evidence.Should().Contain(x => x.Label == "Set by" && x.Value.Contains("ALTER ROLE", StringComparison.Ordinal));
        readOnly.Remedy.Should().BeNull("a role's setting is reset in words");

        HealthFinding standby = verdict.Findings.Single(x => x.Variant == "standby");
        standby.Severity.Should().Be(HealthSeverity.Warning);
        standby.WhyItMatters.Should().Contain("mute this finding");
        standby.Remedy!.Code.Should().Be("Target Session Attributes=read-write");
    }

    [Fact]
    public void S10_on_Azure_the_remedy_is_storage_on_the_platform_and_cites_Microsoft_Learn()
    {
        HealthFinding finding = new ReadOnlyStoreCheck()
            .Evaluate(new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "on", source: "configuration file", boot: "off"), false, Platform(PostgresPlatformKind.Azure)), Evaluation())
            .Findings.Single();

        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.ManagedPlatform);
        finding.Remedy.Code.Should().Contain("autogrow");
        finding.Remedy.Explanation.Should().Contain("95%").And.Contain("5 GiB").And.Contain("Storage options");
    }

    [Fact]
    public void S10_elsewhere_a_server_setting_is_the_server_line()
    {
        new ReadOnlyStoreCheck()
            .Evaluate(new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "on", source: "configuration file", boot: "off"), false, SelfHosted), Evaluation())
            .Findings.Single().Remedy!.Code.Should().EndWith("default_transaction_read_only = off");
    }

    [Fact]
    public void S10_is_quiet_for_a_read_write_primary_and_says_when_the_setting_was_hidden()
    {
        new ReadOnlyStoreCheck().Evaluate(new ReadOnlyStoreFacts(Setting("default_transaction_read_only", "off"), false, SelfHosted), Evaluation())
            .Findings.Should().BeEmpty();
        new ReadOnlyStoreCheck().Evaluate(new ReadOnlyStoreFacts(null, false, SelfHosted), Evaluation())
            .Note.Should().Contain("not shown");
    }
}
