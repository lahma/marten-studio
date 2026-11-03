using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;

using static PostgresStudio.Tests.Connection.ConnectionTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// NP3: the Connection page's model - the strip, the cards, the settings rows and what the server saw - from stated facts. Pure:
/// every sentence the page says that is not a hint's or a check's own is built here, and each is held to its facts.
/// </summary>
public sealed class ConnectionPageBuilderTests
{
    private static string Fact(ConnectionPageView view, string label) => view.Header.Single(x => x.Label == label).Value;

    // -------------------------------------------------------------------------------------------------
    // The strip
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_strip_states_the_server_database_role_tls_pool_npgsql_and_platform_in_that_order()
    {
        ConnectionPageView view = View(Facts(
            "Host=db.example;Port=5433;Database=app;Username=svc;SSL Mode=VerifyFull;Maximum Pool Size=40",
            Observed()));

        view.Header.Select(static x => x.Label).Should().Equal("Server", "Database", "Role", "TLS", "Pool", "Npgsql", "Platform");
        Fact(view, "Server").Should().Be("db.example:5433");
        Fact(view, "Database").Should().Be("app");
        Fact(view, "Role").Should().Be("svc");
        Fact(view, "TLS").Should().Be("verified (VerifyFull)");
        Fact(view, "Pool").Should().Be("max 40");
        Fact(view, "Npgsql").Should().Be(NpgsqlSettingsReader.LoadedVersion);
        Fact(view, "Platform").Should().Be("no managed platform or pooler detected");
        view.PoolMax.Should().Be(40);
    }

    [Fact]
    public void Every_fact_names_the_row_or_section_that_holds_its_detail()
    {
        ConnectionPageView view = View(Facts(Remote, Observed()));

        view.Header.Single(static x => x.Label == "Server").Anchor.Should().Be("conn-row-host");
        view.Header.Single(static x => x.Label == "TLS").Anchor.Should().Be("conn-row-ssl-mode");
        view.Header.Single(static x => x.Label == "Pool").Anchor.Should().Be("conn-row-maximum-pool-size");
        view.Header.Single(static x => x.Label == "Npgsql").Anchor.Should().Be("conn-settings");
    }

    [Theory]
    [InlineData(true, "Prefer", nameof(ConnectionTlsState.EncryptedNotVerified), "encrypted, not verified")]
    [InlineData(true, "VerifyCA", nameof(ConnectionTlsState.Verified), "verified (VerifyCA)")]
    [InlineData(false, "Prefer", nameof(ConnectionTlsState.NotEncrypted), "not encrypted")]
    public void The_tls_fact_follows_what_the_server_reports_and_what_ssl_mode_checks(bool ssl, string mode, string state, string text)
    {
        ConnectionPageView view = View(Facts("Host=db.example;SSL Mode=" + mode, Observed(ssl)));

        view.Tls.State.ToString().Should().Be(state);
        view.Tls.Text.Should().Be(text);
        view.Header.Single(static x => x.Label == "TLS").Tone.Should().Be(state == nameof(ConnectionTlsState.Verified) ? "ok" : "warn");
    }

    [Fact]
    public void A_role_that_cannot_see_its_own_ssl_row_is_not_visible_and_never_not_encrypted()
    {
        ConnectionPageView view = View(Facts(Remote, Observed(ssl: null)));

        view.Tls.State.Should().Be(ConnectionTlsState.NotVisible);
        view.Tls.Text.Should().Be("not visible to this role");
        view.Tls.Text.Should().NotContain("not encrypted");
    }

    [Fact]
    public void A_server_that_was_not_asked_is_not_read()
    {
        ConnectionPageView view = View(Facts(Remote, observed: null, observedNotRead: "the connection read failed (SQLSTATE 28P01)"));

        view.Tls.State.Should().Be(ConnectionTlsState.NotRead);
        view.Tls.Detail.Should().Contain("28P01");
        view.Observed.NotRead.Should().Contain("28P01");
        view.Observed.Lines.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Host=localhost;SSL Mode=Prefer", nameof(ConnectionTlsState.NotEncryptedLocal))]
    [InlineData("Host=/var/run/postgresql", nameof(ConnectionTlsState.NotEncryptedLocal))]
    [InlineData("Host=db.example", nameof(ConnectionTlsState.NotEncrypted))]
    public void Not_encrypted_on_the_same_machine_is_local_and_otherwise_is_not_encrypted(string connectionString, string expected) =>
        View(Facts(connectionString, Observed(ssl: false))).Tls.State.ToString().Should().Be(expected);

    [Fact]
    public void The_cloud_sql_auth_proxy_encrypts_onward_and_gss_encrypts_without_tls()
    {
        View(Facts("Host=127.0.0.1", Observed(ssl: false), PostgresPlatformKind.CloudSql)).Tls.State.Should().Be(ConnectionTlsState.EncryptedByProxy);
        View(Facts(Remote, Observed(ssl: false, gssEncrypted: true))).Tls.State.Should().Be(ConnectionTlsState.EncryptedGss);
    }

    [Fact]
    public void Gssapi_encryption_is_called_encryption_and_never_tls()
    {
        ConnectionPageView view = View(Facts(Remote, Observed(ssl: false, gssEncrypted: true)));

        view.Header.Select(static x => x.Label).Should().Contain("Encryption").And.NotContain("TLS");
        Fact(view, "Encryption").Should().Be("GSSAPI");
        view.Tls.Summary.Should().Be("Encryption: GSSAPI");
        View(Facts(Remote, Observed(ssl: true))).Tls.Summary.Should().StartWith("TLS ");
    }

    [Fact]
    public void Settings_that_could_not_be_read_never_claim_a_certificate_check_or_an_ssl_mode()
    {
        ConnectionFacts facts = Facts("Host=db.example;Maximum Pool Size=not-a-number", Observed(ssl: true));

        facts.Settings.Readable.Should().BeFalse();

        ConnectionPageView encrypted = View(facts);

        encrypted.Tls.State.Should().Be(ConnectionTlsState.EncryptedVerificationUnknown);
        encrypted.Tls.Text.Should().Be("encrypted; whether the certificate is checked could not be read");
        encrypted.Tls.Detail.Should().NotContain("SSL Mode").And.NotContain("is not checked");
        Fact(encrypted, "TLS").Should().Be(encrypted.Tls.Text);

        ConnectionPageView plain = View(Facts("Host=db.example;Maximum Pool Size=not-a-number", Observed(ssl: false)));

        plain.Tls.State.Should().Be(ConnectionTlsState.NotEncrypted);
        plain.Tls.Detail.Should().NotContain("SSL Mode", "no mode was read, so none is named");
    }

    [Fact]
    public void The_platform_is_a_name_and_the_poolers_are_names_for_the_snapshot()
    {
        ConnectionPageView none = View(Facts("Host=db.example", Observed()));

        none.PlatformName.Should().BeNull();
        none.Poolers.Should().BeEmpty();

        ConnectionPageView proxied = View(Facts("Host=app.proxy-abc123.eu-west-1.rds.amazonaws.com", Observed(), PostgresPlatformKind.AmazonRds));

        proxied.PlatformName.Should().Be("Amazon RDS for PostgreSQL");
        proxied.Poolers.Should().Equal("Amazon RDS Proxy");
    }

    [Fact]
    public void A_studio_s_sentence_for_a_hint_is_added_to_the_end_of_that_hints_card_and_a_note_for_a_hint_that_is_not_there_is_not_drawn()
    {
        ConnectionContribution contribution = new()
        {
            HintNotes = new Dictionary<string, string>
            {
                [ConnectionHintIds.MultiHost] = "The studio's own sentence.",
                [ConnectionHintIds.Lifetime] = "A note for a hint that is not on this page.",
            },
        };

        ConnectionPageView view = View(Facts("Host=a.example,b.example;Database=app;Username=svc", Observed()), contributions: contribution);

        ConnectionCard card = view.Cards.Concat(view.Notes).Single(static x => x.Code == ConnectionHintIds.MultiHost);
        card.Text.Should().EndWith(" The studio's own sentence.");
        card.Text.Should().Contain("can land on a standby", "the hint's own sentence comes first");

        view.Cards.Concat(view.Notes).Concat(view.Contributed.SelectMany(static x => x.Cards)).Select(static x => x.Text)
            .Should().NotContain(static x => x.Contains("not on this page", StringComparison.Ordinal));

        // Without the contribution the hint is exactly its own.
        View(Facts("Host=a.example,b.example;Database=app;Username=svc", Observed())).Cards.Concat(view.Notes).Single(static x => x.Code == ConnectionHintIds.MultiHost)
            .Text.Should().NotContain("The studio's own sentence.");
    }

    [Fact]
    public void Contributions_with_a_repeated_id_are_made_distinct_so_the_page_never_repeats_a_key()
    {
        ConnectionCard card(string id) => new(id, ConnectionCardKind.Contributed, "Advice", "text", ["X"], [], []);
        ConnectionContribution first = new()
        {
            Cards = [card("conn-card-x"), card("conn-card-x")],
            Sections = [new("conn-extra", "One", null, [], [card("conn-card-x")]), new("conn-extra", "Two", null, [], []), new("conn-settings", "Three", null, [], [])],
        };
        ConnectionContribution second = new() { Cards = [card("conn-card-x")] };

        ConnectionPageView view = View(Facts("Host=db.example", Observed()), contributions: [first, second]);

        string[] cardIds = [.. view.Cards.Concat(view.Notes).Concat(view.Contributed.SelectMany(static x => x.Cards)).Select(static x => x.Id)];
        cardIds.Should().OnlyHaveUniqueItems();
        cardIds.Should().Contain(["conn-card-x", "conn-card-x-2", "conn-card-x-3", "conn-card-x-4"]);
        view.Contributed.Select(static x => x.Id).Should().OnlyHaveUniqueItems().And.NotContain("conn-settings");
        view.Contributed.Select(static x => x.Id).Should().Equal("conn-extra", "conn-extra-2", "conn-settings-2");
    }

    [Fact]
    public void The_pool_fact_says_what_the_pool_is_doing_now_and_what_it_peaked_at()
    {
        ConnectionFacts facts = Facts("Host=db.example;Maximum Pool Size=100", Observed());

        Fact(View(facts, pool: new SessionPoolSummary(true, 3, 2, 100, true)), "Pool").Should().Be("max 100 · 3 in use, 2 idle");
        Fact(View(facts, pool: new SessionPoolSummary(true, 3, 2, 100, true), peak: new ConnectionPoolPeak(41, 100, "7 days", "orders")), "Pool")
            .Should().Be("max 100 · 3 in use, 2 idle · peak 41 named 'orders', 7 days", "the peak is the sessions with this name on the server, not this pool's");
        Fact(View(facts, pool: new SessionPoolSummary(false, null, null, 100, true, Ambiguous: true)), "Pool")
            .Should().Be("max 100 · live counts unavailable: several pools in this process share this connection string");
        Fact(View(Facts("Host=db.example;Pooling=false", Observed())), "Pool").Should().Be("no pool (Pooling=false)");
        View(Facts("Host=db.example;Pooling=false", Observed())).PoolMax.Should().BeNull();
    }

    [Fact]
    public void The_platform_fact_names_the_provider_and_every_pooler_sign()
    {
        Fact(View(Facts("Host=app.proxy-abc123.eu-west-1.rds.amazonaws.com", Observed(), PostgresPlatformKind.AmazonRds)), "Platform")
            .Should().Be("Amazon RDS for PostgreSQL · Amazon RDS Proxy");
        Fact(View(Facts("Host=db.example;Port=6432", Observed())), "Platform").Should().Contain("PgBouncer");
        Fact(View(Facts("Host=ep-cool-darkness-123456-pooler.us-east-2.aws.neon.tech", Observed(), PostgresPlatformKind.Neon)), "Platform")
            .Should().Contain("Neon pooled endpoint");
    }

    [Fact]
    public void A_connection_string_that_does_not_parse_has_no_settings_and_says_so()
    {
        ConnectionPageView view = View(Facts("Host=db.example;Maximum Pool Size=not-a-number", Observed()));

        view.Readable.Should().BeFalse();
        view.UnreadableReason.Should().NotBeNullOrEmpty().And.NotContain("not-a-number", "the type only, never the value Npgsql rejected");
        view.Settings.Should().BeEmpty();
        Fact(view, "Server").Should().Be("not readable");
        view.Observed.Lines.Should().NotBeEmpty("what the server saw does not depend on the connection string");
    }

    // -------------------------------------------------------------------------------------------------
    // Worth a look
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Findings_come_first_worst_first_and_their_hint_is_left_out_while_the_finding_is_there()
    {
        HealthReport report = Report(0, null,
            Finding("C16", HealthSeverity.Info, "Idle pooled connections may be ended first"),
            Finding("S11", HealthSeverity.Warning, "The server's certificate is not verified"));

        ConnectionPageView view = View(Facts("Host=db.example;SSL Mode=Require", Observed()), report);

        view.Cards.Select(static x => x.Code).Should().StartWith(["S11", "C16"]);
        view.Cards.Take(2).Should().OnlyContain(static x => x.Kind == ConnectionCardKind.Finding);
        view.Cards.Concat(view.Notes).Should().NotContain(static x => x.Code == ConnectionHintIds.Tls, "S11 says it louder");
    }

    [Fact]
    public void A_connection_check_that_is_not_about_connecting_is_not_here()
    {
        HealthReport report = Report(0, null,
            Finding("T01", HealthSeverity.Warning, "A table is read whole"),
            Finding("S11", HealthSeverity.Info, "The server's certificate is not verified"));

        View(Facts("Host=db.example;SSL Mode=Require", Observed()), report).Cards
            .Where(static x => x.Finding is not null).Select(static x => x.Code).Should().Equal("S11");
    }

    [Fact]
    public void Without_the_finding_the_tls_hint_is_a_card_with_the_connection_string_and_the_builder_forms()
    {
        ConnectionPageView view = View(Facts("Host=db.example;SSL Mode=Prefer", Observed()));

        ConnectionCard card = view.Cards.Should().ContainSingle(static x => x.Code == ConnectionHintIds.Tls).Subject;
        card.Kind.Should().Be(ConnectionCardKind.Hint);
        card.Heading.Should().Be("The server's certificate is not verified");
        card.Fixes.Select(static x => x.Label).Should().Equal("Connection string", "NpgsqlDataSourceBuilder");
        card.Fixes[0].Code.Should().StartWith("SSL Mode=VerifyFull");
        card.Fixes[1].Code.Should().Contain("SslMode.VerifyFull");
        card.Keywords.Should().Contain(["SSL Mode", "Root Certificate"]);
    }

    [Fact]
    public void A_connection_that_never_encrypts_is_headed_as_that_and_not_as_an_unverified_certificate()
    {
        View(Facts("Host=db.example;SSL Mode=Disable", Observed(ssl: false))).Cards
            .Single(static x => x.Code == ConnectionHintIds.Tls).Heading.Should().Be("This connection is never encrypted");
    }

    [Fact]
    public void Context_that_is_not_advice_is_a_note_and_is_not_counted()
    {
        // A socket (LOCAL), a server that cancels first (CMDTIMEOUT) and auto-prepare left off (PREPARE) say what is so, not what to change.
        ConnectionPageView view = View(Facts("Host=/var/run/postgresql;SSL Mode=Disable", Observed(ssl: false, statementTimeoutMs: 5000)));

        view.Notes.Select(static x => x.Code).Should().Contain([ConnectionHintIds.Local, ConnectionHintIds.CommandTimeout, ConnectionHintIds.Prepare, ConnectionHintIds.Enlist]);
        view.Notes.Should().OnlyContain(static x => x.Kind == ConnectionCardKind.Note && !x.Counts);
        view.Cards.Should().NotContain(static x => x.Code == ConnectionHintIds.Local || x.Code == ConnectionHintIds.CommandTimeout || x.Code == ConnectionHintIds.Prepare);
        view.WorthALook.Should().Be(view.Cards.Count);
    }

    [Fact]
    public void The_ambient_transaction_note_belongs_to_the_all_settings_view()
    {
        View(Facts(Remote, Observed())).Notes.Single(static x => x.Code == ConnectionHintIds.Enlist).AllSettingsOnly.Should().BeTrue();
    }

    [Fact]
    public void Several_hosts_with_no_target_session_attribute_is_a_counted_hint_with_its_fixes()
    {
        ConnectionPageView view = View(Facts("Host=a.example,b.example;Database=app", Observed()));

        ConnectionCard card = view.Cards.Single(static x => x.Code == ConnectionHintIds.MultiHost);
        card.Heading.Should().Be("Writes can land on a standby");
        card.Fixes[0].Code.Should().Be("Target Session Attributes=primary");
        card.Keywords.Should().Contain("Target Session Attributes");
    }

    [Fact]
    public void An_obsolete_keyword_is_a_hint_about_that_keyword_alone_with_no_copyable_sentence()
    {
        ConnectionPageView view = View(Facts("Host=db.example;Server Compatibility Mode=NoTypeLoading", Observed()));

        ConnectionCard card = view.Cards.Single(static x => x.Code == ConnectionHintIds.Obsolete);
        card.Heading.Should().Be("Server Compatibility Mode is obsolete");
        card.Keywords.Should().Equal("Server Compatibility Mode");
        card.Fixes.Should().BeEmpty("'Remove it from the connection string' is a sentence, not text to copy");
    }

    [Fact]
    public void A_finding_the_host_accepted_is_a_count_and_not_a_card()
    {
        HealthReport report = Report(0, null, Finding("S11", HealthSeverity.Warning, "The server's certificate is not verified", accepted: true));

        ConnectionPageView view = View(Facts("Host=db.example;SSL Mode=Require", Observed()), report);

        view.AcceptedFindings.Should().Be(1);
        view.Cards.Where(static x => x.Finding is not null).Should().BeEmpty();
        view.Cards.Should().NotContain(static x => x.Code == ConnectionHintIds.Tls, "the finding is in the report, so the hint stays quiet whether or not the host accepted it");
    }

    [Fact]
    public void Withheld_findings_are_counted_and_never_described()
    {
        ConnectionPageView view = View(Facts(Remote, Observed()), Report(2, "They need ViewDatabaseActivity."));

        view.FindingsWithheld.Should().Be(2);
        view.FindingsWithheldReason.Should().Be("They need ViewDatabaseActivity.");
    }

    [Fact]
    public void A_report_that_could_not_be_read_says_so_and_does_not_hide_the_hints()
    {
        ConnectionPageView view = ConnectionPageBuilder.Build(new ConnectionPageInputs
        {
            Facts = Facts("Host=db.example;SSL Mode=Require", Observed()),
            Report = null,
            ReportNote = "The health advisor's findings could not be read (InvalidOperationException).",
            Activity = GateClosed,
            MeasuredAt = DateTimeOffset.UnixEpoch,
        });

        view.FindingsNote.Should().Contain("could not be read");
        view.Cards.Should().Contain(static x => x.Code == ConnectionHintIds.Tls);
    }

    [Fact]
    public void A_pool_peak_is_a_note_until_the_servers_headroom_is_tight_and_then_it_counts()
    {
        ConnectionFacts facts = Facts("Host=db.example;Maximum Pool Size=100;Application Name=orders", Observed());
        ConnectionPoolPeak peak = new(41, 100, "7 days", "orders");

        View(facts, peak: peak).Notes.Single(static x => x.Code == ConnectionHintIds.PoolSize).Text
            .Should().Be("Sessions named 'orders' peaked at 41 on this server in the last 7 days (every process using this Application Name, in every database); this process's pool allows 100.")
            .And.NotContain("The pool peaked");

        View(facts, Report(0, null, Finding("C03", HealthSeverity.Warning, "Headroom is tight")), peak: peak).Cards
            .Should().Contain(static x => x.Code == ConnectionHintIds.PoolSize, "C03 reports tight headroom, the one case lowering is weighed");
    }

    [Fact]
    public void A_contributors_cards_sections_and_row_size_join_the_page()
    {
        ConnectionCard contributed = new("conn-card-marten", ConnectionCardKind.Contributed, "Marten's timeout is 60 s", "text", ["MARTEN"], ["Command Timeout"], [new("StoreOptions", "options.CommandTimeout = 30;")]);
        ConnectionContribution contribution = new()
        {
            Cards = [contributed],
            AverageRowBytes = 20_000,
            Sections = [new("conn-marten", "Marten", "intro", [new("UseStickyConnectionLifetimes", "false")], [])],
        };

        ConnectionPageView view = View(Facts("Host=db.example;Read Buffer Size=8192", Observed()), contributions: contribution);

        view.Cards.Should().Contain(contributed);
        view.Contributed.Should().ContainSingle().Which.Title.Should().Be("Marten");
        view.Cards.Single(static x => x.Code == ConnectionHintIds.Buffer).Text.Should().Contain("20000");
        view.Settings.Single(static x => x.Row.Keyword == "Command Timeout").AdviceCard.Should().Be(contributed);
    }

    // -------------------------------------------------------------------------------------------------
    // The settings table
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_what_differs_keeps_what_something_sets_what_identifies_the_connection_and_what_has_advice()
    {
        ConnectionPageView view = View(Facts("Host=db.example;Database=app;Command Timeout=45;SSL Mode=Prefer", Observed()));

        string[] differing = [.. view.Settings.Where(static x => x.Differs).Select(static x => x.Row.Keyword)];

        differing.Should().Contain(["Host", "Database", "Command Timeout", "Username", "Password", "SSL Mode", "Maximum Pool Size"]);
        differing.Should().NotContain(["Connection Pruning Interval", "Cancellation Timeout", "Write Buffer Size"]);
        view.Settings.Single(static x => x.Row.Keyword == "Connection Pruning Interval").From.Should().Be("default");
    }

    [Fact]
    public void A_settings_advice_marker_names_the_card_that_is_about_it()
    {
        ConnectionPageView view = View(Facts("Host=db.example;SSL Mode=Prefer", Observed()));

        ConnectionSettingView mode = view.Settings.Single(static x => x.Row.Keyword == "SSL Mode");
        mode.AdviceCard.Should().NotBeNull();
        mode.AdviceCard!.Code.Should().Be(ConnectionHintIds.Tls);
        mode.Anchor.Should().Be("conn-row-ssl-mode");
        view.Settings.Single(static x => x.Row.Keyword == "Port").AdviceCard.Should().BeNull();
    }

    [Fact]
    public void A_note_does_not_pull_a_default_row_into_the_differs_view_but_a_counted_card_does()
    {
        ConnectionPageView view = View(Facts("Host=a.example,b.example;Database=app", Observed(statementTimeoutMs: 5000)));

        // CMDTIMEOUT is a note, and Command Timeout is at its default: out. MULTIHOST counts, and Target Session Attributes is at its default: in.
        view.Settings.Single(static x => x.Row.Keyword == "Command Timeout").Differs.Should().BeFalse();
        view.Settings.Single(static x => x.Row.Keyword == "Target Session Attributes").Differs.Should().BeTrue();
    }

    [Theory]
    [InlineData("Host=db.example;Application Name=orders", "Application Name", "connection string")]
    [InlineData("Host=db.example", "Application Name", "default")]
    public void The_from_badge_says_where_the_value_came_from(string connectionString, string keyword, string expected) =>
        View(Facts(connectionString, Observed())).Settings.Single(x => x.Row.Keyword == keyword).From.Should().Be(expected);

    [Fact]
    public void An_environment_variable_names_itself_in_the_from_badge()
    {
        ConnectionFacts facts = Facts("Host=db.example", Observed(), environment: new FakeEnvironment(("PGUSER", "from-env")));

        ConnectionSettingView user = View(facts).Settings.Single(static x => x.Row.Keyword == "Username");
        user.From.Should().Be("PGUSER");
        user.Row.Value.Should().Be("from-env");
    }

    [Fact]
    public void A_row_carries_npgsqls_default_and_the_builder_property_that_holds_it()
    {
        ConnectionSettingRow row = View(Facts("Host=db.example;Maximum Pool Size=20", Observed())).Settings.Single(static x => x.Row.Keyword == "Maximum Pool Size").Row;

        row.Value.Should().Be("20");
        row.DefaultValue.Should().Be("100");
        row.BuilderProperty.Should().Be("MaxPoolSize");
    }

    [Fact]
    public void A_secrets_default_is_not_set_and_a_keyword_the_studio_does_not_know_has_none()
    {
        ConnectionPageView view = View(Facts("Host=db.example;Password=hunter2;Persist Security Info=true", Observed()));

        view.Settings.Single(static x => x.Row.Keyword == "Password").Row.DefaultValue.Should().Be("not set");
        view.Settings.Single(static x => x.Row.Keyword == "Password").Row.Value.Should().Be("set").And.NotContain("hunter2");
        view.Settings.Select(static x => x.Row).Where(static x => x.Exposure is null).Should().OnlyContain(static x => x.DefaultValue == null);
    }

    // -------------------------------------------------------------------------------------------------
    // What the server saw
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void What_the_server_saw_is_the_start_up_values_of_the_login_and_the_timeouts_in_words()
    {
        ConnectionPageView view = View(Facts(Remote, Observed(idleSessionTimeoutMs: 1_800_000, statementTimeoutMs: 30_000, idleInTransactionMs: 0, transactionMs: null)));

        string Value(string label) => view.Observed.Lines.Single(x => x.Label == label).Value;

        Value("Logged in as").Should().Be("svc");
        Value("Database").Should().Be("app");
        Value("Server").Should().Be("10.0.0.5:5432");
        Value("Role of the server").Should().Be("primary");
        Value("Encryption").Should().Be("TLSv1.3, TLS_AES_256_GCM_SHA384, 256 bits");
        Value("idle_session_timeout").Should().Be("30 min");
        Value("statement_timeout").Should().Be("30 s");
        Value("idle_in_transaction_session_timeout").Should().Be("off");
        Value("transaction_timeout").Should().Be("not available before PostgreSQL 17");
        Value("Reported at start-up: server_version").Should().Be("17.4");
        view.Observed.Lines.Select(static x => x.Label).Should().NotContain("Reads run as", "the reading role is the login's here");
    }

    [Fact]
    public void A_console_role_is_named_when_it_differs_from_the_login()
    {
        ConnectionPageView view = View(Facts(Remote, Observed(currentUser: "studio_reader")));

        view.Observed.Lines.Single(static x => x.Label == "Reads run as").Value.Should().Be("studio_reader");
    }

    [Theory]
    [InlineData(null, "not available")]
    [InlineData(0L, "off")]
    [InlineData(250L, "250 ms")]
    [InlineData(1500L, "1500 ms")]
    [InlineData(60_000L, "1 min")]
    public void A_server_timeout_reads_as_a_person_would_say_it(long? milliseconds, string expected) =>
        ConnectionPageBuilder.Timeout(milliseconds, null).Should().StartWith(expected);

    // -------------------------------------------------------------------------------------------------
    // What no page can see
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void What_is_set_in_code_and_the_default_password_file_are_said_not_to_be_visible()
    {
        ConnectionPageView view = View(Facts(Remote, Observed()));

        string text = string.Join(" ", view.NotVisible.Select(static x => x.Label + " " + x.Value));
        text.Should().Contain("UsePasswordProvider").And.Contain("UseUserCertificateValidationCallback").And.Contain("UseLoggerFactory")
            .And.Contain("MapEnum").And.Contain("UsePhysicalConnectionInitializer").And.Contain("never looks");
        view.NotVisible.Select(static x => x.Label).Should().Contain("The default password file");
    }

    // -------------------------------------------------------------------------------------------------
    // Gates
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_model_carries_the_activity_gate_it_was_built_with()
    {
        View(Facts(Remote, Observed()), activity: GateOpen).Activity.IsOpen.Should().BeTrue();
        View(Facts(Remote, Observed()), activity: GateClosed).Activity.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void A_refusal_is_a_value_with_no_settings_in_it()
    {
        ConnectionPageView view = ConnectionPageView.Refused(ConnectionRefusal.StorePolicy, "Your account may not see this.");

        view.Refusal.Should().Be(ConnectionRefusal.StorePolicy);
        view.Settings.Should().BeEmpty();
        view.Cards.Should().BeEmpty();
        view.Header.Should().BeEmpty();
    }
}
