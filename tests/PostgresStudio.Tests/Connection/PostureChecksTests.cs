using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using static PostgresStudio.Tests.Connection.ConnectionCheckFacts;
using static PostgresStudio.Tests.Server.ServerTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// 1.1-NP2: S11, S12 and S13 over stated facts and no Postgres - every variant both ways, every stand-down, severities, remedies in
/// every form, and the findings' words in both studios.
/// </summary>
public sealed class PostureChecksTests
{
    // ----------------------------------------------------------------------------------------------
    // S11 Server certificate not verified
    // ----------------------------------------------------------------------------------------------

    private static HealthVerdict S11(ConnectionFacts facts, bool postgres = false) =>
        new ServerCertificateCheck().Evaluate(facts, Evaluation(postgres)).ShouldBeWellFormedFor<ServerCertificateCheck>();

    [Theory]
    [InlineData("Host=db.example", "prefer")]
    [InlineData("Host=db.example;SSL Mode=Prefer", "prefer")]
    [InlineData("Host=db.example;SSL Mode=Allow", "prefer")]
    [InlineData("Host=db.example;SSL Mode=Require", "require")]
    public void TLS_in_use_below_VerifyCA_is_a_finding_with_the_variant_of_its_mode(string connectionString, string variant)
    {
        HealthFinding finding = S11(Facts(connectionString, Observed(ssl: true, sslVersion: "TLSv1.3"))).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be(variant);
        finding.Key.Should().Be("S11|" + variant);
        finding.Evidence.Should().Contain(x => x.Label == "Connection" && x.Value == "encrypted (TLSv1.3)");
    }

    [Theory]
    [InlineData("Host=db.example;SSL Mode=VerifyCA")]
    [InlineData("Host=db.example;SSL Mode=VerifyFull")]
    public void A_mode_that_checks_the_certificate_is_quiet(string connectionString)
    {
        HealthVerdict verdict = S11(Facts(connectionString, Observed(ssl: true)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("checks the server's certificate");
    }

    [Theory]
    [InlineData("Host=localhost")]
    [InlineData("Host=127.0.0.1")]
    [InlineData("Host=/var/run/postgresql")]
    public void A_host_that_never_leaves_the_machine_is_quiet(string connectionString)
    {
        S11(Facts(connectionString, Observed(ssl: true))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_connection_without_TLS_is_S05s_and_quiet_here()
    {
        HealthVerdict verdict = S11(Facts("Host=db.example", Observed(ssl: false)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("S05");
    }

    [Fact]
    public void One_remote_host_among_local_ones_is_judged()
    {
        S11(Facts("Host=localhost,db.example", Observed(ssl: true))).Findings.Should().ContainSingle();
    }

    [Fact]
    public void Trust_Server_Certificate_is_no_variant_and_changes_nothing()
    {
        // Obsolete, and does nothing at Npgsql 9.0.4 and 10.0.3: the mode alone decides.
        S11(Facts("Host=db.example;SSL Mode=Require;Trust Server Certificate=true", Observed(ssl: true))).Findings.Should().ContainSingle()
            .Which.Variant.Should().Be("require");
        HealthCheckRegistry.Describe(typeof(ServerCertificateCheck)).Variants.Should().Equal("prefer", "require");
    }

    [Theory]
    [InlineData(PostgresPlatformKind.AmazonRds, "Warning")]
    [InlineData(PostgresPlatformKind.AmazonAurora, "Warning")]
    [InlineData(PostgresPlatformKind.Azure, "Warning")]
    [InlineData(PostgresPlatformKind.CloudSql, "Warning")]
    [InlineData(PostgresPlatformKind.Neon, "Warning")]
    [InlineData(PostgresPlatformKind.Supabase, "Warning")]
    [InlineData(PostgresPlatformKind.SelfHosted, "Info")]
    public void The_severity_is_Warning_on_a_detected_cloud_platform_and_Info_elsewhere(PostgresPlatformKind platform, string expected)
    {
        S11(Facts("Host=db.example", Observed(ssl: true), platform)).Findings.Single().Severity.ToString().Should().Be(expected);
    }

    [Fact]
    public void The_remedy_is_VerifyFull_with_the_providers_bundle_where_the_studio_has_a_source()
    {
        HealthFinding rds = S11(Facts("Host=db.example", Observed(ssl: true), PostgresPlatformKind.AmazonRds)).Findings.Single();

        rds.Remedy!.Code.Should()
            .Contain("SSL Mode=VerifyFull;Root Certificate=")
            .And.Contain("b.ConnectionStringBuilder.SslMode = SslMode.VerifyFull;")
            .And.Contain("b.ConnectionStringBuilder.RootCertificate = ");
        rds.Remedy.Explanation.Should().Contain("global-bundle.pem");
        Everything(rds).Should().NotContain("rds.force_ssl");

        S11(Facts("Host=db.example", Observed(ssl: true), PostgresPlatformKind.Azure)).Findings.Single().Remedy!.Explanation
            .Should().Contain("DigiCert Global Root G2").And.Contain("Microsoft RSA Root CA 2017");
        Everything(S11(Facts("Host=db.example", Observed(ssl: true), PostgresPlatformKind.AmazonAurora)).Findings.Single()).Should().NotContain("rds.force_ssl", "nothing is claimed about Aurora's default");
    }

    [Fact]
    public void A_Root_Certificate_set_under_Require_is_reported_as_ignored_without_its_path()
    {
        HealthFinding finding = S11(Facts("Host=db.example;SSL Mode=Require;Root Certificate=/etc/ssl/S3NT1NEL-root.pem", Observed(ssl: true))).Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Root Certificate" && x.Value.Contains("not used", StringComparison.Ordinal));
        Everything(finding).Should().NotContain("S3NT1NEL").And.NotContain("/etc/ssl");
    }

    [Fact]
    public void The_Cloud_SQL_Auth_Proxy_case_is_not_applicable_and_says_why()
    {
        // A Cloud SQL server reached on loopback or a socket: the proxy wraps its leg to the instance in TLS itself.
        ServerCertificateCheck.NotApplicableReason(Facts("Host=127.0.0.1", Observed(ssl: false), PostgresPlatformKind.CloudSql))
            .Should().Contain("Auth Proxy").And.Contain("TLS itself");
        ServerCertificateCheck.NotApplicableReason(Facts("Host=/cloudsql/project:region:instance", Observed(ssl: false), PostgresPlatformKind.CloudSql))
            .Should().Contain("Auth Proxy");

        // A Cloud SQL server reached directly is judged; so is a loopback host that is not Cloud SQL.
        ServerCertificateCheck.NotApplicableReason(Facts("Host=34.1.2.3", Observed(ssl: true), PostgresPlatformKind.CloudSql)).Should().BeNull();
        ServerCertificateCheck.NotApplicableReason(Facts("Host=127.0.0.1", Observed(ssl: true))).Should().BeNull();
    }

    // One host (or port) per pooler sign: a Neon pooled endpoint, Supabase's 6543, Azure's built-in PgBouncer, PgBouncer's port and an RDS Proxy.
    public static TheoryData<string> PoolerHosts => new()
    {
        "Host=ep-cool-123-pooler.us-east-2.aws.neon.tech",
        "Host=db.example;Port=6543",
        "Host=myserver.postgres.database.azure.com;Port=6432",
        "Host=db.example;Port=6432",
        "Host=" + ProxyHost,
    };

    [Theory]
    [MemberData(nameof(PoolerHosts))]
    public void Behind_a_pooler_S11_is_not_applicable_because_the_TLS_row_is_the_poolers_own(string connectionString)
    {
        string? reason = ServerCertificateCheck.NotApplicableReason(Facts(connectionString, Observed(ssl: true)));

        reason.Should().Contain("connection pooler").And.Contain("pg_stat_ssl row is the pooler's own connection, not the application's")
            .And.Contain("Connection page's TLS hint judges SSL Mode");
    }

    [Theory]
    [MemberData(nameof(PoolerHosts))]
    public void Behind_a_pooler_S05_is_not_applicable_for_the_same_reason(string connectionString)
    {
        string? reason = UnencryptedConnectionCheck.NotApplicableReason(Facts(connectionString, Observed(ssl: false)).Settings.PoolerSigns);

        reason.Should().Contain("connection pooler").And.Contain("pg_stat_ssl row is the pooler's own connection, not the application's")
            .And.Contain("Connection page's TLS hint judges SSL Mode");
    }

    [Theory]
    [MemberData(nameof(PoolerHosts))]
    public void Behind_a_pooler_S11_stands_down_as_not_applicable_even_when_the_connection_could_not_be_read(string connectionString)
    {
        // GatherAsync asks the pooler first (PoolerReason), then the unread connection (UnavailableReason): an unread server behind a pooler
        // is not applicable, not unavailable.
        ConnectionFacts facts = Facts(connectionString, observed: null, notRead: "the connection read failed (SQLSTATE 57014)");

        ServerCertificateCheck.PoolerReason(facts).Should().Contain("connection pooler");
        ServerCertificateCheck.UnavailableReason(facts).Should().Contain("SQLSTATE 57014");
        ServerCertificateCheck.PoolerReason(Facts("Host=db.example", observed: null, notRead: "x")).Should().BeNull();
    }

    [Fact]
    public void Without_a_pooler_sign_S05_and_S11_apply()
    {
        ConnectionFacts facts = Facts("Host=db.example;Port=5432", Observed(ssl: true));

        UnencryptedConnectionCheck.NotApplicableReason(facts.Settings.PoolerSigns).Should().BeNull();
        UnencryptedConnectionCheck.NotApplicableReason(null).Should().BeNull();
        ServerCertificateCheck.NotApplicableReason(facts).Should().BeNull();
    }

    [Theory]
    [InlineData(PostgresPlatformKind.AmazonRds)]
    [InlineData(PostgresPlatformKind.AmazonAurora)]
    public void The_remedy_behind_an_RDS_Proxy_is_VerifyFull_with_no_Root_Certificate_and_never_the_RDS_bundle(PostgresPlatformKind platform)
    {
        HealthFinding finding = S11(Facts("Host=" + ProxyHost, Observed(ssl: true), platform)).Findings.Single();

        finding.Remedy!.Code.Should().Contain("SSL Mode=VerifyFull").And.Contain("b.ConnectionStringBuilder.SslMode = SslMode.VerifyFull;")
            .And.NotContain("Root Certificate").And.NotContain("RootCertificate");
        finding.Remedy.Explanation.Should().Contain("Using TLS/SSL with RDS Proxy").And.Contain("AWS Certificate Manager").And.NotContain("global-bundle.pem");
    }

    [Fact]
    public void A_role_that_cannot_see_its_own_TLS_row_stands_down_with_a_sentence()
    {
        ServerCertificateCheck.NotApplicableReason(Facts("Host=db.example", Observed(ssl: null))).Should().Contain("pg_stat_ssl").And.Contain("not known");
        ServerCertificateCheck.NotApplicableReason(Facts("Host=db.example", Observed(ssl: true))).Should().BeNull();
    }

    [Fact]
    public void An_unread_server_is_not_checked_not_quiet()
    {
        ServerCertificateCheck.UnavailableReason(Facts("Host=db.example", observed: null, notRead: "the connection read failed (SQLSTATE 57014)"))
            .Should().Contain("SQLSTATE 57014");
        ServerCertificateCheck.UnavailableReason(Facts("Host=db.example", Observed())).Should().BeNull();
    }

    [Fact]
    public void In_Postgres_Studios_words_there_is_no_Marten_and_the_builder_is_named()
    {
        HealthFinding finding = S11(Facts("Host=db.example", Observed(ssl: true)), postgres: true).Findings.Single();

        Everything(finding).Should().Contain("NpgsqlDataSourceBuilder").And.NotContain("Marten").And.NotContain("opts.");
        Everything(S11(Facts("Host=db.example", Observed(ssl: true))).Findings.Single()).Should().Contain("opts.ConfigureNpgsqlDataSourceBuilder");
    }

    // ----------------------------------------------------------------------------------------------
    // S12 A credential is readable from configuration
    // ----------------------------------------------------------------------------------------------

    private static HealthVerdict S12(ConnectionFacts facts, bool postgres = false) =>
        new ReadableCredentialCheck().Evaluate(facts, Evaluation(postgres)).ShouldBeWellFormedFor<ReadableCredentialCheck>();

    [Fact]
    public void Persist_Security_Info_with_a_password_in_the_string_is_a_Warning()
    {
        HealthFinding finding = S12(Facts("Host=db.example;Password=S3NT1NEL-pw;Persist Security Info=true")).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("persist-security-info");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Remedy!.Code.Should().Contain("Persist Security Info=false").And.Contain("b.ConnectionStringBuilder.PersistSecurityInfo = false;");
        Everything(finding).Should().NotContain("S3NT1NEL");
    }

    [Theory]
    [InlineData("Host=db.example;Persist Security Info=true")]
    [InlineData("Host=db.example;Password=S3NT1NEL-pw")]
    [InlineData("Host=db.example")]
    public void Persist_Security_Info_without_a_password_in_the_string_or_a_password_without_it_is_quiet(string connectionString)
    {
        S12(Facts(connectionString)).Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Host=db.example;SSL Password=S3NT1NEL-key", true)]
    [InlineData("Host=db.example;SSL Password=S3NT1NEL-key;Persist Security Info=false", true)]
    [InlineData("Host=db.example;SSL Key=/k.pem", false)]
    public void An_SSL_Password_is_a_Warning_whatever_Persist_Security_Info_says(string connectionString, bool expected)
    {
        HealthVerdict verdict = S12(Facts(connectionString));

        if (expected)
        {
            HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
            finding.Variant.Should().Be("ssl-password");
            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.WhyItMatters.Should().Contain("NpgsqlDataSource.ConnectionString").And.Contain("pool name").And.Contain("OpenTelemetry");
            finding.Remedy!.Code.Should().Contain("UseSslClientAuthenticationOptionsCallback").And.Contain("ClientCertificates");
            finding.Remedy.Explanation.Should().Contain("UseClientCertificate is obsolete");
            Everything(finding).Should().NotContain("S3NT1NEL");
        }
        else
        {
            verdict.Findings.Should().BeEmpty();
        }
    }

    [Fact]
    public void The_SSL_Password_remedy_leaves_all_three_certificate_keywords_out_because_Npgsql_loads_them_before_the_callback()
    {
        HealthFinding finding = S12(Facts("Host=db.example;SSL Password=S3NT1NEL-key")).Findings.Single();

        finding.Remedy!.Code.Should().Contain("SSL Certificate, SSL Key and SSL Password").And.Contain("PGSSLCERT and PGSSLKEY").And.Contain("UseSslClientAuthenticationOptionsCallback");
        finding.Remedy.Explanation.Should().Contain("before the callback runs").And.Contain("throws").And.Contain("client certificate context");
        Everything(finding).Should().NotContain("S3NT1NEL");
    }

    [Theory]
    [InlineData("Host=db.example", "ignores PGPASSWORD")]
    [InlineData("Host=db.example;Persist Security Info=true", "Npgsql uses PGPASSWORD")]
    public void The_PGPASSWORD_caveat_names_the_password_providers_the_studio_cannot_see(string connectionString, string sentence)
    {
        FakeEnvironment environment = new(("PGPASSWORD", "S3NT1NEL-env"));

        HealthFinding finding = S12(Facts(connectionString, environment: environment)).Findings.Single();

        finding.Caveat.Should().Contain(sentence).And.Contain("UsePasswordProvider").And.Contain("UsePeriodicPasswordProvider").And.Contain("cannot see");
    }

    [Fact]
    public void PGPASSWORD_in_the_environment_is_Info_and_never_shows_its_value()
    {
        FakeEnvironment environment = new(("PGPASSWORD", "S3NT1NEL-env"));

        HealthFinding finding = S12(Facts("Host=db.example", environment: environment)).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("pgpassword-env");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Caveat.Should().Contain("cannot see whether the connection string has a password of its own");
        finding.Remedy!.Code.Should().Contain("Passfile=").And.Contain("b.ConnectionStringBuilder.Passfile = ");
        Everything(finding).Should().NotContain("S3NT1NEL");
    }

    [Fact]
    public void PGPASSWORD_is_ignored_when_the_visible_connection_string_has_a_password_of_its_own()
    {
        FakeEnvironment environment = new(("PGPASSWORD", "S3NT1NEL-env"));

        HealthVerdict verdict = S12(Facts("Host=db.example;Password=S3NT1NEL-pw;Persist Security Info=true", environment: environment));

        verdict.Findings.Select(static x => x.Variant).Should().Equal("persist-security-info");

        // Persist Security Info on and no password in the string: Npgsql does use PGPASSWORD, and the finding says so.
        S12(Facts("Host=db.example;Persist Security Info=true", environment: environment)).Findings.Should().ContainSingle()
            .Which.Caveat.Should().Contain("Npgsql uses PGPASSWORD");
    }

    [Fact]
    public void All_three_together_are_three_findings_with_keys_of_their_own_and_quiet_says_what_was_looked_at()
    {
        FakeEnvironment environment = new(("PGPASSWORD", "S3NT1NEL-env"));

        S12(Facts("Host=db.example;Password=S3NT1NEL-pw;Persist Security Info=true;SSL Password=S3NT1NEL-key", environment: environment))
            .Findings.Select(static x => x.Key).Should().Equal("S12|persist-security-info", "S12|ssl-password");

        S12(Facts("Host=db.example;SSL Password=S3NT1NEL-key", environment: environment))
            .Findings.Select(static x => x.Key).Should().Equal("S12|ssl-password", "S12|pgpassword-env");

        S12(Facts("Host=db.example")).Note.Should().Contain("PGPASSWORD is not in the process's environment");
    }

    [Fact]
    public void In_Postgres_Studios_words_S12_names_no_Marten()
    {
        FakeEnvironment environment = new(("PGPASSWORD", "S3NT1NEL-env"));

        foreach (HealthFinding finding in S12(Facts("Host=db.example;Password=x;Persist Security Info=true;SSL Password=y", environment: environment), postgres: true).Findings)
        {
            Everything(finding).Should().NotContain("Marten").And.NotContain("opts.");
        }
    }

    // ----------------------------------------------------------------------------------------------
    // S13 Driver settings that copy values into logs or exceptions
    // ----------------------------------------------------------------------------------------------

    private static HealthVerdict S13(ConnectionFacts facts, string? environment, bool postgres = false) =>
        new DriverLoggingSettingsCheck().Evaluate(new DriverLoggingFacts(facts, environment), Evaluation(postgres))
            .ShouldBeWellFormedFor<DriverLoggingSettingsCheck>();

    private static HealthVerdict S13(string connectionString, string? environment, bool postgres = false) =>
        S13(Facts(connectionString), environment, postgres);

    private static ConnectionFacts AllThree() =>
        ValuesFacts(("Log Parameters", true), ("Include Error Detail", true), ("Include Failed Batched Command", true));

    [Theory]
    [InlineData("Host=db.example;Log Parameters=true", "log-parameters")]
    [InlineData("Host=db.example;Include Error Detail=true", "include-error-detail")]
    public void Each_setting_on_in_Production_is_an_Info_finding_with_its_variant(string connectionString, string variant)
    {
        HealthFinding finding = S13(connectionString, "Production").Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be(variant);
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Remedy!.Code.Should().Contain("=false").And.Contain("b.ConnectionStringBuilder.");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void Outside_Production_none_of_them_is_judged(string? environment)
    {
        HealthVerdict verdict = S13(AllThree(), environment);

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Production only");
    }

    [Fact]
    public void The_settings_off_in_Production_are_quiet_and_all_three_together_are_three_findings()
    {
        S13("Host=db.example", "production").Findings.Should().BeEmpty("the environment name is compared in any case");
        S13(AllThree(), "Production")
            .Findings.Select(static x => x.Key).Should().Equal("S13|log-parameters", "S13|include-error-detail", "S13|include-failed-batched-command");
    }

    [Fact]
    public void Include_Failed_Batched_Command_on_in_Production_is_an_Info_finding_that_names_the_exception_property()
    {
        HealthFinding finding = S13(ValuesFacts(("Include Failed Batched Command", true)), "Production").Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be("include-failed-batched-command");
        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.WhyItMatters.Should().Contain("NpgsqlException.BatchCommand");
        finding.Remedy!.Code.Should().Contain("Include Failed Batched Command=false").And.Contain("b.ConnectionStringBuilder.IncludeFailedBatchedCommand = false;");
    }

    [Fact]
    public void Log_Parameters_says_it_logs_only_through_a_logger_factory_and_that_EnableParameterLogging_overrides_the_keyword()
    {
        foreach (bool postgres in new[] { true, false })
        {
            HealthFinding finding = S13("Host=db.example;Log Parameters=true", "Production", postgres).Findings.Single();

            finding.WhyItMatters.Should().Contain("Where the data source has a logger factory");
            finding.Caveat.Should().Contain("only through a logger factory");
            finding.Remedy!.Explanation.Should().Contain("EnableParameterLogging()").And.Contain("whatever this keyword says");
        }

        // Marten Studio adds what Marten 9 does by default; Postgres Studio's words know nothing of it.
        Everything(S13("Host=db.example;Log Parameters=true", "Production").Findings.Single())
            .Should().Contain("StoreOptions.DisableNpgsqlLogging").And.Contain("logs nothing unless");
        Everything(S13("Host=db.example;Log Parameters=true", "Production", postgres: true).Findings.Single())
            .Should().NotContain("DisableNpgsqlLogging");

        // The other two variants carry none of it.
        Everything(S13("Host=db.example;Include Error Detail=true", "Production").Findings.Single())
            .Should().NotContain("EnableParameterLogging").And.NotContain("logger factory");
    }

    [Fact]
    public void In_Postgres_Studios_words_S13_names_no_Marten()
    {
        foreach (HealthFinding finding in S13("Host=db.example;Log Parameters=true", "Production", postgres: true).Findings)
        {
            Everything(finding).Should().NotContain("Marten").And.NotContain("opts.").And.Contain("NpgsqlDataSourceBuilder");
        }

        Everything(S13("Host=db.example;Log Parameters=true", "Production").Findings.Single()).Should().Contain("opts.ConfigureNpgsqlDataSourceBuilder");
    }
}
