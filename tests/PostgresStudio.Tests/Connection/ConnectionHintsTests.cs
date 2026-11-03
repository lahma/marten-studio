using PostgresStudio.Services.Connection;

namespace PostgresStudio.Tests.Connection;

/// <summary>NP1: every hint's condition, each way (NP-DESIGN §6).</summary>
public sealed class ConnectionHintsTests
{
    private static ConnectionObserved Observed(
        long? idleSessionMs = null,
        long? statementMs = null,
        long? idleInTransactionMs = null,
        long? transactionMs = null,
        string? passwordEncryption = "scram-sha-256") =>
        new("app", "app", "orders", null, null, false, null, null, null, null, idleSessionMs, statementMs, idleInTransactionMs, transactionMs,
            passwordEncryption, true, "TLSv1.3", null, null, null, null, new HashSet<string>());

    private static ConnectionSettings Settings(
        string connectionString,
        PostgresPlatformKind platform = PostgresPlatformKind.SelfHosted,
        ConnectionObserved? observed = null,
        ConnectionPoolPeak? peak = null,
        bool tight = false,
        long? averageRow = null) =>
        NpgsqlSettingsReader.Read(connectionString, new ConnectionReadContext
        {
            Environment = new FakeEnvironment(),
            NpgsqlMajor = 10,
            Platform = platform,
            Observed = observed,
            PoolPeak = peak,
            TightHeadroom = tight,
            AverageRowBytes = averageRow,
        });

    private static ConnectionHint? Hint(ConnectionSettings settings, string id, string? keyword = null) =>
        settings.Hints.FirstOrDefault(x => x.Id == id && (keyword is null || x.Keyword == keyword));

    // ---- TLS -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Host=db.example", true)]
    [InlineData("Host=db.example;SSL Mode=Prefer", true)]
    [InlineData("Host=db.example;SSL Mode=Require", true)]
    [InlineData("Host=db.example;SSL Mode=Allow", true)]
    [InlineData("Host=db.example;SSL Mode=Disable", true)]
    [InlineData("Host=db.example;SSL Mode=VerifyCA", false)]
    [InlineData("Host=db.example;SSL Mode=VerifyFull", false)]
    [InlineData("Host=localhost", false)]
    [InlineData("Host=127.0.0.1", false)]
    [InlineData("Host=::1", false)]
    [InlineData("Host=/var/run/postgresql", false)]
    [InlineData("Host=localhost,db.example", true)]
    public void TLS_applies_below_VerifyCA_to_a_host_that_is_neither_loopback_nor_a_socket(string connectionString, bool expected)
    {
        (Hint(Settings(connectionString), ConnectionHintIds.Tls) is not null).Should().Be(expected);
    }

    [Fact]
    public void TLS_says_what_Prefer_does_and_names_the_providers_bundle()
    {
        ConnectionHint prefer = Hint(Settings("Host=db.example;SSL Mode=Prefer", PostgresPlatformKind.AmazonRds), ConnectionHintIds.Tls)!;

        prefer.Text.Should().Contain("not checked").And.Contain("falls back to plaintext silently").And.Contain("global-bundle.pem");
        prefer.ConnectionStringFix.Should().Contain("SSL Mode=VerifyFull").And.Contain("Root Certificate=");
        prefer.BuilderFix.Should().Contain("SslMode.VerifyFull");

        Hint(Settings("Host=db.example;SSL Mode=Require", PostgresPlatformKind.Azure), ConnectionHintIds.Tls)!.Text
            .Should().Contain("DigiCert Global Root G2").And.Contain("Microsoft RSA Root CA 2017").And.NotContain("falls back");
        Hint(Settings("Host=db.example;SSL Mode=Disable"), ConnectionHintIds.Tls)!.Text.Should().Contain("never encrypted");
        Hint(Settings("Host=db.example", PostgresPlatformKind.AmazonAurora), ConnectionHintIds.Tls)!.Text.Should().NotContain("rds.force_ssl");
    }

    // ---- LIFETIME --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Host=a,b;Connection Lifetime=0", PostgresPlatformKind.SelfHosted, true)]
    [InlineData("Host=a;Connection Lifetime=0;Load Balance Hosts=true", PostgresPlatformKind.SelfHosted, true)]
    [InlineData("Host=a;Connection Lifetime=0", PostgresPlatformKind.AmazonAurora, true)]
    [InlineData("Host=a;Connection Lifetime=0", PostgresPlatformKind.SelfHosted, false)]
    [InlineData("Host=a,b", PostgresPlatformKind.SelfHosted, false)]
    [InlineData("Host=a,b;Connection Lifetime=600", PostgresPlatformKind.SelfHosted, false)]
    public void LIFETIME_is_zero_lifetime_with_several_hosts_load_balancing_or_Aurora(string connectionString, PostgresPlatformKind platform, bool expected)
    {
        (Hint(Settings(connectionString, platform), ConnectionHintIds.Lifetime) is not null).Should().Be(expected);
    }

    // ---- MULTIHOST -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Host=a,b", true)]
    [InlineData("Host=a,b;Target Session Attributes=any", true)]
    [InlineData("Host=a,b;Target Session Attributes=primary", false)]
    [InlineData("Host=a", false)]
    public void MULTIHOST_is_several_hosts_with_no_target_session_attribute(string connectionString, bool expected)
    {
        ConnectionHint? hint = Hint(Settings(connectionString), ConnectionHintIds.MultiHost);

        (hint is not null).Should().Be(expected);
        hint?.Text.Should().Contain("write can land on a standby");
    }

    // ---- KEEPALIVE -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Host=db.example", PostgresPlatformKind.AmazonRds, true)]
    [InlineData("Host=db.example", PostgresPlatformKind.SelfHosted, false)]
    [InlineData("Host=db.example;Port=6432", PostgresPlatformKind.SelfHosted, true)]
    [InlineData("Host=db.example;Keepalive=30", PostgresPlatformKind.AmazonRds, false)]
    public void KEEPALIVE_is_no_keepalive_on_a_cloud_platform_or_behind_a_pooler(string connectionString, PostgresPlatformKind platform, bool expected)
    {
        (Hint(Settings(connectionString, platform), ConnectionHintIds.Keepalive) is not null).Should().Be(expected);
    }

    [Fact]
    public void KEEPALIVE_suggests_half_the_shortest_idle_cut_it_knows()
    {
        Hint(Settings("Host=db.example", PostgresPlatformKind.AmazonRds, Observed(idleSessionMs: 600_000)), ConnectionHintIds.Keepalive)!
            .ConnectionStringFix.Should().Be("Keepalive=300");
        Hint(Settings("Host=the-proxy.proxy-demo.us-east-1.rds.amazonaws.com"), ConnectionHintIds.Keepalive)!
            .ConnectionStringFix.Should().Be("Keepalive=900");
        Hint(Settings("Host=db.example", PostgresPlatformKind.Neon), ConnectionHintIds.Keepalive)!
            .ConnectionStringFix.Should().Be("Keepalive=30");
    }

    // ---- POOLSIZE --------------------------------------------------------------------------------------------------

    [Fact]
    public void POOLSIZE_needs_a_known_peak_and_advises_lowering_only_when_headroom_is_tight()
    {
        Hint(Settings("Host=db.example"), ConnectionHintIds.PoolSize).Should().BeNull();

        ConnectionPoolPeak peak = new(42, 100, "7 days");
        ConnectionHint calm = Hint(Settings("Host=db.example", peak: peak), ConnectionHintIds.PoolSize)!;
        calm.Text.Should().Be("The pool peaked at 42 of 100 connections in the last 7 days.");
        calm.ConnectionStringFix.Should().BeNull();

        Hint(Settings("Host=db.example", peak: peak, tight: true), ConnectionHintIds.PoolSize)!.Text
            .Should().Contain("peaked at 42 of 100").And.Contain("headroom is tight (C03)").And.Contain("lower Maximum Pool Size");
    }

    // ---- BUFFER ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(16384L, "Host=db.example", "Read Buffer Size=18000")]
    [InlineData(20000L, "Host=db.example;Read Buffer Size=16384", "Read Buffer Size=22000")]
    public void BUFFER_is_an_average_row_larger_than_the_read_buffer(long average, string connectionString, string fix)
    {
        Hint(Settings(connectionString, averageRow: average), ConnectionHintIds.Buffer)!.ConnectionStringFix.Should().Be(fix);
    }

    [Theory]
    [InlineData(8192L)]
    [InlineData(100L)]
    [InlineData(null)]
    public void BUFFER_is_quiet_for_a_row_that_fits_or_one_nobody_measured(long? average)
    {
        Hint(Settings("Host=db.example", averageRow: average), ConnectionHintIds.Buffer).Should().BeNull();
    }

    // ---- PREPARE ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PREPARE_warns_of_auto_preparation_behind_Supabases_transaction_pooler()
    {
        ConnectionHint hint = Hint(Settings("Host=aws-0-eu-central-1.pooler.supabase.com;Port=6543;Max Auto Prepare=20"), ConnectionHintIds.Prepare)!;

        hint.Text.Should().Contain("Supavisor").And.Contain("prepared statements");
        hint.ConnectionStringFix.Should().Be("Max Auto Prepare=0");
    }

    [Fact]
    public void PREPARE_suggests_auto_preparation_only_where_there_is_no_pooler_and_it_is_off()
    {
        Hint(Settings("Host=db.example"), ConnectionHintIds.Prepare)!.Text.Should().Contain("repeated planning");
        Hint(Settings("Host=db.example;Max Auto Prepare=20"), ConnectionHintIds.Prepare).Should().BeNull();
        Hint(Settings("Host=db.example;Port=6432"), ConnectionHintIds.Prepare).Should().BeNull("a pooler is in front");
        Hint(Settings("Host=ep-cool-123-pooler.us-east-2.aws.neon.tech;Max Auto Prepare=20"), ConnectionHintIds.Prepare).Should().BeNull("Neon's pooler supports protocol-level prepared statements");
    }

    [Fact]
    public void PREPARE_does_not_name_an_RDS_Proxy()
    {
        // The Amazon RDS pinning page lists SQL PREPARE/EXECUTE/DEALLOCATE for PostgreSQL, not protocol-level preparation.
        Hint(Settings("Host=the-proxy.proxy-demo.us-east-1.rds.amazonaws.com;Max Auto Prepare=20"), ConnectionHintIds.Prepare).Should().BeNull();
    }

    // ---- RESET -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Host=db.example;Port=6432", true)]
    [InlineData("Host=db.example;Port=6432;No Reset On Close=true", false)]
    [InlineData("Host=db.example;Port=6432;Pooling=false", false)]
    [InlineData("Host=db.example", false)]
    [InlineData("Host=the-proxy.proxy-demo.us-east-1.rds.amazonaws.com", true)]
    [InlineData("Host=the-proxy.proxy-demo.us-east-1.rds.amazonaws.com;No Reset On Close=true", false)]
    [InlineData("Host=the-proxy.proxy-demo.us-east-1.rds.amazonaws.com;Pooling=false", false)]
    public void RESET_is_a_PgBouncer_family_pooler_or_an_RDS_Proxy_with_Npgsqls_reset_on(string connectionString, bool expected)
    {
        (Hint(Settings(connectionString), ConnectionHintIds.Reset) is not null).Should().Be(expected);
    }

    [Fact]
    public void RESET_for_an_RDS_Proxy_says_it_pins_on_release_and_what_the_fix_costs()
    {
        ConnectionHint hint = Hint(Settings("Host=the-proxy.proxy-demo.us-east-1.rds.amazonaws.com"), ConnectionHintIds.Reset)!;

        hint.Text.Should().Contain("pins a client connection on release").And.Contain("Avoiding pinning an RDS Proxy")
            .And.Contain("DISCARD ALL").And.Contain("carries over to the next borrower");
        hint.ConnectionStringFix.Should().Be("No Reset On Close=true");
        hint.BuilderFix.Should().Contain("NoResetOnClose = true");

        Hint(Settings("Host=db.example;Port=6432"), ConnectionHintIds.Reset)!.Text.Should().Contain("PgBouncer").And.NotContain("RDS Proxy");
    }

    [Fact]
    public void RDS_Proxy_adds_its_24_hour_life_and_idle_timeout_to_the_LIFETIME_and_KEEPALIVE_texts()
    {
        ConnectionSettings settings = Settings("Host=the-proxy.proxy-demo.us-east-1.rds.amazonaws.com;Connection Lifetime=0;Load Balance Hosts=true");

        Hint(settings, ConnectionHintIds.Lifetime)!.Text.Should().Contain("24 hours");
        Hint(settings, ConnectionHintIds.Keepalive)!.Text.Should().Contain("IdleClientTimeout").And.Contain("24 hours");
        Hint(Settings("Host=a,b;Connection Lifetime=0"), ConnectionHintIds.Lifetime)!.Text.Should().NotContain("RDS Proxy");
        Hint(Settings("Host=db.example", PostgresPlatformKind.AmazonRds), ConnectionHintIds.Keepalive)!.Text.Should().NotContain("IdleClientTimeout");
    }

    // ---- ENLIST ----------------------------------------------------------------------------------------------------

    [Fact]
    public void ENLIST_is_the_default_and_shown_only_in_the_all_settings_view()
    {
        ConnectionHint hint = Hint(Settings("Host=db.example"), ConnectionHintIds.Enlist)!;

        hint.AllSettingsOnly.Should().BeTrue();
        hint.ConnectionStringFix.Should().Be("Enlist=false");
        Hint(Settings("Host=db.example;Enlist=false"), ConnectionHintIds.Enlist).Should().BeNull();
    }

    // ---- CMDTIMEOUT ------------------------------------------------------------------------------------------------

    [Fact]
    public void CMDTIMEOUT_is_shown_when_the_server_has_a_timeout_and_says_which_cancels_first()
    {
        Hint(Settings("Host=db.example"), ConnectionHintIds.CommandTimeout).Should().BeNull();
        Hint(Settings("Host=db.example", observed: Observed()), ConnectionHintIds.CommandTimeout).Should().BeNull("the server sets none");

        Hint(Settings("Host=db.example", observed: Observed(statementMs: 5_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("Command Timeout=30 s").And.Contain("statement_timeout 5 s").And.Contain("the server's statement_timeout cancels a long command first");

        Hint(Settings("Host=db.example;Command Timeout=10", observed: Observed(statementMs: 60_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("Npgsql's Command Timeout cancels a long command first");

        Hint(Settings("Host=db.example", observed: Observed(transactionMs: 120_000, idleInTransactionMs: 30_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("transaction_timeout 120 s").And.Contain("idle_in_transaction_session_timeout 30 s");

        Hint(Settings("Host=db.example;Command Timeout=0", observed: Observed(statementMs: 1_500)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("Command Timeout=0").And.Contain("statement_timeout 1500 ms");
    }

    [Fact]
    public void CMDTIMEOUT_compares_the_command_timeout_with_the_smaller_of_statement_and_transaction_timeout()
    {
        // transaction_timeout (PostgreSQL 17+) alone: 5 s against Command Timeout=30 - the server cuts first, and names the right one.
        Hint(Settings("Host=db.example", observed: Observed(transactionMs: 5_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("the server's transaction_timeout cancels a long command first").And.NotContain("statement_timeout cancels");

        // The smaller of the two wins, either way round.
        Hint(Settings("Host=db.example", observed: Observed(statementMs: 60_000, transactionMs: 5_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("the server's transaction_timeout cancels a long command first");
        Hint(Settings("Host=db.example", observed: Observed(statementMs: 5_000, transactionMs: 60_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("the server's statement_timeout cancels a long command first");

        // A transaction_timeout longer than Command Timeout loses to it.
        Hint(Settings("Host=db.example;Command Timeout=10", observed: Observed(transactionMs: 120_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("Npgsql's Command Timeout cancels a long command first");
    }

    [Fact]
    public void CMDTIMEOUT_leaves_idle_in_transaction_out_of_the_verdict()
    {
        // idle_in_transaction_session_timeout never cuts a running command: with Command Timeout=0 nothing does.
        Hint(Settings("Host=db.example;Command Timeout=0", observed: Observed(idleInTransactionMs: 30_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("idle_in_transaction_session_timeout 30 s").And.Contain("nothing cuts a long running command")
            .And.NotContain("cancels a long command first");

        // ... and it does not beat a Command Timeout that is set.
        Hint(Settings("Host=db.example", observed: Observed(idleInTransactionMs: 1_000)), ConnectionHintIds.CommandTimeout)!.Text
            .Should().Contain("Npgsql's Command Timeout cancels a long command first");
    }

    // ---- COLDSTART -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Host=x.neon.tech;Timeout=3", PostgresPlatformKind.Neon, true)]
    [InlineData("Host=x.neon.tech;Timeout=5", PostgresPlatformKind.Neon, false)]
    [InlineData("Host=x.neon.tech", PostgresPlatformKind.Neon, false)]
    [InlineData("Host=db.example;Timeout=3", PostgresPlatformKind.SelfHosted, false)]
    public void COLDSTART_is_a_short_timeout_on_Neon(string connectionString, PostgresPlatformKind platform, bool expected)
    {
        (Hint(Settings(connectionString, platform), ConnectionHintIds.ColdStart) is not null).Should().Be(expected);
    }

    // ---- AUTH ------------------------------------------------------------------------------------------------------

    [Fact]
    public void AUTH_is_md5_password_encryption_or_channel_binding_off()
    {
        Hint(Settings("Host=db.example", observed: Observed(passwordEncryption: "scram-sha-256")), ConnectionHintIds.Auth).Should().BeNull();
        Hint(Settings("Host=db.example", observed: Observed(passwordEncryption: "md5")), ConnectionHintIds.Auth)!.Text.Should().Contain("password_encryption is md5").And.Contain("pg_hba.conf");
        Hint(Settings("Host=db.example;Channel Binding=Disable"), ConnectionHintIds.Auth)!.Text.Should().Contain("Channel Binding=Disable");
        Hint(Settings("Host=db.example;Channel Binding=Require"), ConnectionHintIds.Auth).Should().BeNull();
    }

    // ---- MUX, OBSOLETE, LOCAL --------------------------------------------------------------------------------------

    [Fact]
    public void MUX_is_multiplexing_on()
    {
        Hint(Settings("Host=db.example;Multiplexing=true"), ConnectionHintIds.Multiplexing)!.Text.Should().Contain("experimental").And.Contain("removed it");
        Hint(Settings("Host=db.example"), ConnectionHintIds.Multiplexing).Should().BeNull();
    }

    [Fact]
    public void OBSOLETE_is_an_obsolete_keyword_set_explicitly_and_carries_its_message()
    {
        ConnectionHint hint = Hint(Settings("Host=db.example;Trust Server Certificate=true"), ConnectionHintIds.Obsolete)!;

        hint.Keyword.Should().Be("Trust Server Certificate");
        hint.Text.Should().Contain("no longer needed and does nothing");
        Hint(Settings("Host=db.example"), ConnectionHintIds.Obsolete).Should().BeNull();
    }

    [Fact]
    public void LOCAL_is_a_socket_directory()
    {
        Hint(Settings("Host=/var/run/postgresql"), ConnectionHintIds.Local)!.Text.Should().StartWith("Unix socket");
        Hint(Settings("Host=localhost"), ConnectionHintIds.Local).Should().BeNull();
    }
}
