using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

using static PostgresStudio.Tests.Connection.ConnectionCheckFacts;
using static PostgresStudio.Tests.Server.ServerTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// 1.1-NP2: C16, C17 and C18 over stated facts and no Postgres - every variant both ways, every stand-down, the remedy in every
/// form, and the findings' words in both studios.
/// </summary>
public sealed class PoolChecksTests
{
    // ----------------------------------------------------------------------------------------------
    // C16 Server or proxy ends idle pooled connections first
    // ----------------------------------------------------------------------------------------------

    private static HealthVerdict C16(ConnectionFacts facts, bool postgres = false) =>
        new PoolIdleCutCheck().Evaluate(facts, Evaluation(postgres)).ShouldBeWellFormedFor<PoolIdleCutCheck>();

    [Theory]
    // The server cuts at 60 s. The default pool (idle lifetime 300 s, no floor) holds a connection past it.
    [InlineData("Host=db.example", 60_000, true)]
    // A floor keeps connections open however short the idle lifetime.
    [InlineData("Host=db.example;Minimum Pool Size=1;Connection Idle Lifetime=20", 60_000, true)]
    // Retired first, with no floor: quiet.
    [InlineData("Host=db.example;Connection Idle Lifetime=45", 60_000, false)]
    // Equal to the cut is not first.
    [InlineData("Host=db.example;Connection Idle Lifetime=60", 60_000, true)]
    [InlineData("Host=db.example;Connection Idle Lifetime=59", 60_000, false)]
    // Keepalive shorter than the cut covers a pool that would outlast it; equal to the cut does not.
    [InlineData("Host=db.example;Keepalive=30", 60_000, false)]
    [InlineData("Host=db.example;Keepalive=59", 60_000, false)]
    [InlineData("Host=db.example;Keepalive=60", 60_000, true)]
    [InlineData("Host=db.example;Keepalive=120", 60_000, true)]
    [InlineData("Host=db.example;Minimum Pool Size=5;Keepalive=30", 60_000, false)]
    // The server has no idle cut, or none for this login.
    [InlineData("Host=db.example", 0, false)]
    [InlineData("Host=db.example", -1, false)]
    public void The_server_variant_fires_when_the_pool_outlasts_idle_session_timeout_and_keepalive_does_not_cover_it(string connectionString, int idleSessionMs, bool fires)
    {
        // -1 is a server older than PostgreSQL 14, which has no idle_session_timeout.
        HealthVerdict verdict = C16(Facts(connectionString, Observed(idleSessionMs < 0 ? null : idleSessionMs)));

        if (fires)
        {
            HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
            finding.Variant.Should().Be("idle-session-timeout");
            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Key.Should().Be("C16|idle-session-timeout");
        }
        else
        {
            verdict.Findings.Should().BeEmpty();
            verdict.Note.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("Host=" + ProxyHost + ";Minimum Pool Size=2", true)]
    [InlineData("Host=" + ProxyHost + ";Connection Idle Lifetime=1799", false)]
    [InlineData("Host=" + ProxyHost + ";Connection Idle Lifetime=1800", true)]
    [InlineData("Host=" + ProxyHost + ";Connection Idle Lifetime=3600;Keepalive=600", false)]
    [InlineData("Host=" + ProxyHost + ";Connection Idle Lifetime=3600;Keepalive=1799", false)]
    [InlineData("Host=" + ProxyHost + ";Connection Idle Lifetime=3600;Keepalive=1800", true)]
    [InlineData("Host=" + ProxyHost + ";Minimum Pool Size=1;Keepalive=900", false)]
    public void The_proxy_variant_fires_for_an_RDS_Proxy_when_the_pool_outlasts_its_1800_second_idle_timeout(string connectionString, bool expected)
    {
        HealthVerdict verdict = C16(Facts(connectionString, Observed(0)));

        if (expected)
        {
            HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
            finding.Variant.Should().Be("proxy-idle-timeout");
            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Evidence.Should().Contain(x => x.Label == "Idle client timeout of the proxy" && x.Value.StartsWith("1,800 s", StringComparison.Ordinal));
            finding.Caveat.Should().Contain("cannot read the proxy's own setting");
        }
        else
        {
            verdict.Findings.Should().BeEmpty();
        }
    }

    [Fact]
    public void The_default_pool_does_not_outlast_an_RDS_Proxys_default_because_its_idle_lifetime_is_300_seconds()
    {
        C16(Facts("Host=" + ProxyHost, Observed(0))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_proxy_and_a_server_cut_are_two_findings_with_keys_of_their_own()
    {
        HealthVerdict verdict = C16(Facts("Host=" + ProxyHost + ";Connection Idle Lifetime=3600", Observed(600_000)));

        verdict.Findings.Select(static x => x.Key).Should().BeEquivalentTo("C16|idle-session-timeout", "C16|proxy-idle-timeout");
    }

    [Theory]
    [InlineData("Host=db.example;Port=6432")]
    [InlineData("Host=ep-cool-123-pooler.us-east-2.aws.neon.tech")]
    [InlineData("Host=db.example;Port=6543")]
    public void Behind_a_PgBouncer_family_pooler_the_server_variant_is_not_judged(string connectionString)
    {
        HealthVerdict verdict = C16(Facts(connectionString, Observed(60_000)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("connection pooler");
    }

    [Fact]
    public void An_RDS_Proxy_is_not_PgBouncer_so_its_server_cut_is_still_judged()
    {
        // An idle lifetime of 100 s is under the proxy's 1800 s and over the server's 60 s: only the server's variant fires.
        C16(Facts("Host=" + ProxyHost + ";Connection Idle Lifetime=100", Observed(60_000))).Findings.Should().ContainSingle()
            .Which.Variant.Should().Be("idle-session-timeout");
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, 1)]
    [InlineData(500, 1)]
    [InlineData(999, 1)]
    [InlineData(1_000, 1)]
    [InlineData(1_999, 1)]
    [InlineData(90_000, 90)]
    public void A_sub_second_idle_session_timeout_is_a_cut_of_one_second_never_zero_which_means_off(long milliseconds, int? seconds)
    {
        Observed(milliseconds).IdleSessionTimeoutSeconds.Should().Be(seconds);

        if (seconds is not null)
        {
            HealthFinding finding = C16(Facts("Host=db.example;Minimum Pool Size=1", Observed(milliseconds))).Findings.Should().ContainSingle().Subject;

            finding.Evidence.Should().Contain(x => x.Label == "idle_session_timeout" && x.Value.StartsWith(seconds + " s", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Behind_an_RDS_Proxy_with_No_Reset_On_Close_the_server_cut_is_not_judged_and_the_note_says_why()
    {
        // The server's cut reaches the client only on a pinned connection, and Npgsql's reset is what pins it.
        HealthVerdict verdict = C16(Facts("Host=" + ProxyHost + ";No Reset On Close=true;Connection Idle Lifetime=100", Observed(60_000)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("No Reset On Close=true").And.Contain("pinned").And.Contain("idle_session_timeout is not judged");

        // The proxy's own idle client timeout applies either way.
        C16(Facts("Host=" + ProxyHost + ";No Reset On Close=true;Minimum Pool Size=1", Observed(60_000))).Findings.Should().ContainSingle()
            .Which.Variant.Should().Be("proxy-idle-timeout");
    }

    [Theory]
    [InlineData("Host=" + ProxyHost + ";No Reset On Close=false;Connection Idle Lifetime=100")]
    [InlineData("Host=" + ProxyHost + ";Connection Idle Lifetime=100")]
    [InlineData("Host=db.example;No Reset On Close=true;Connection Idle Lifetime=100")]
    public void With_the_reset_on_or_without_a_proxy_the_server_cut_is_judged_as_before(string connectionString)
    {
        C16(Facts(connectionString, Observed(60_000))).Findings.Should().ContainSingle()
            .Which.Variant.Should().Be("idle-session-timeout");
    }

    [Fact]
    public void When_the_server_was_not_read_the_proxy_variant_is_still_judged_and_the_server_variant_says_why_not()
    {
        HealthVerdict verdict = C16(Facts("Host=" + ProxyHost + ";Minimum Pool Size=1", observed: null, notRead: "the connection read failed (SQLSTATE 42501)"));

        verdict.Findings.Should().ContainSingle().Which.Variant.Should().Be("proxy-idle-timeout");

        HealthVerdict self = C16(Facts("Host=db.example", observed: null, notRead: "the connection read failed (SQLSTATE 42501)"));
        self.Findings.Should().BeEmpty();
        self.Note.Should().Contain("SQLSTATE 42501").And.Contain("not judged");
    }

    [Fact]
    public void Pooling_off_is_not_applicable_with_a_sentence_and_pooling_on_is_not()
    {
        PoolIdleCutCheck.NotApplicableReason(Facts("Host=db.example;Pooling=false")).Should().Contain("Pooling is off").And.Contain("C17");
        PoolIdleCutCheck.NotApplicableReason(Facts("Host=db.example")).Should().BeNull();
    }

    [Fact]
    public void The_remedy_gives_Keepalive_and_the_pool_alternative_in_the_connection_string_and_on_the_builder()
    {
        HealthFinding finding = C16(Facts("Host=db.example", Observed(60_000))).Findings.Single();

        finding.Remedy!.Code.Should()
            .Contain("Keepalive=30")
            .And.Contain("b.ConnectionStringBuilder.KeepAlive = 30;")
            .And.Contain("Connection Idle Lifetime=30;Minimum Pool Size=0")
            .And.Contain("b.ConnectionStringBuilder.ConnectionIdleLifetime = 30;")
            .And.Contain("b.ConnectionStringBuilder.MinPoolSize = 0;");
        finding.Remedy.Code.Should().Contain("opts.ConfigureNpgsqlDataSourceBuilder(b =>", "Marten Studio's words wrap the builder statements in the store's hook");
        finding.Remedy.Kind.Should().Be(HealthRemedyKind.Configuration);
        finding.Link.Should().NotBeNull();
    }

    [Fact]
    public void A_cut_too_short_to_retire_connections_before_is_offered_Keepalive_only()
    {
        // Npgsql refuses a Connection Idle Lifetime shorter than its 10 s pruning interval.
        HealthFinding finding = C16(Facts("Host=db.example", Observed(15_000))).Findings.Single();

        finding.Remedy!.Code.Should().Contain("Keepalive=7").And.NotContain("Connection Idle Lifetime=");
        finding.Remedy.Explanation.Should().Contain("pruning interval");
    }

    [Fact]
    public void In_Postgres_Studios_words_there_is_no_Marten_and_no_K08_and_the_builder_is_named()
    {
        HealthFinding finding = C16(Facts("Host=db.example", Observed(60_000)), postgres: true).Findings.Single();
        string text = Everything(finding);

        text.Should().Contain("NpgsqlDataSourceBuilder").And.Contain("b.ConnectionStringBuilder.KeepAlive = 30;");
        text.Should().NotContain("opts.").And.NotContain("K08").And.NotContain("Marten");

        Everything(C16(Facts("Host=db.example", Observed(60_000))).Findings.Single()).Should().Contain("K08");
    }

    [Fact]
    public void The_findings_evidence_names_no_host_and_the_title_names_the_cut()
    {
        HealthFinding finding = C16(Facts("Host=" + ProxyHost + ";Connection Idle Lifetime=3600", Observed(600_000))).Findings[0];

        Everything(finding).Should().NotContain("amazonaws").And.NotContain("the-proxy");
        finding.Title.Should().Contain("600 s");
    }

    // ----------------------------------------------------------------------------------------------
    // C17 Connection pooling switched off
    // ----------------------------------------------------------------------------------------------

    private static HealthVerdict C17(ConnectionFacts facts, bool postgres = false) =>
        new PoolingOffCheck().Evaluate(facts, Evaluation(postgres)).ShouldBeWellFormedFor<PoolingOffCheck>();

    private static ConnectionSessionStatistics Sessions(long sessions, double timeMs) => new(sessions, timeMs, At.AddDays(-3));

    [Theory]
    [InlineData(1000, 500.0, "Warning")]
    [InlineData(1000, 999_000.0, "Warning")]
    [InlineData(1000, 1_000_000.0, "Info")]
    [InlineData(1000, 40_000_000.0, "Info")]
    public void Pooling_off_is_a_Warning_when_the_average_session_is_under_a_second_and_Info_otherwise(long sessions, double timeMs, string expected)
    {
        HealthFinding finding = C17(Facts("Host=db.example;Pooling=false", sessions: Sessions(sessions, timeMs))).Findings.Should().ContainSingle().Subject;

        finding.Severity.ToString().Should().Be(expected);
        finding.Evidence.Should().Contain(x => x.Label == "Average session" && x.Value.Contains("1,000 sessions", StringComparison.Ordinal));
    }

    [Fact]
    public void Pooling_off_without_session_statistics_is_Info_and_says_it_does_not_know()
    {
        HealthFinding finding = C17(Facts("Host=db.example;Pooling=false")).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Evidence.Should().Contain(x => x.Label == "Average session" && x.Value.StartsWith("not known", StringComparison.Ordinal));

        // PostgreSQL 14's counters on a database no session has ended in.
        C17(Facts("Host=db.example;Pooling=false", sessions: new ConnectionSessionStatistics(0, 0, null))).Findings.Single().Severity.Should().Be(HealthSeverity.Info);
    }

    [Fact]
    public void Pooling_on_is_quiet()
    {
        C17(Facts("Host=db.example")).Findings.Should().BeEmpty();
        C17(Facts("Host=db.example;Pooling=true")).Note.Should().Contain("Pooling is on");
    }

    [Theory]
    [InlineData("Host=db.example;Port=6432;Pooling=false", "PgBouncer")]
    [InlineData("Host=ep-cool-123-pooler.us-east-2.aws.neon.tech;Pooling=false", "PgBouncer")]
    [InlineData("Host=db.example;Port=6543;Pooling=false", "PgBouncer")]
    [InlineData("Host=" + ProxyHost + ";Pooling=false", "RDS Proxy")]
    public void Behind_a_pooler_pooling_off_is_not_applicable_and_cites_Npgsqls_advice(string connectionString, string mentions)
    {
        string? reason = PoolingOffCheck.NotApplicableReason(Facts(connectionString));

        reason.Should().Contain(mentions);

        if (mentions == "PgBouncer")
        {
            reason.Should().Contain("compatibility page").And.Contain("Pooling=false");
        }
    }

    [Theory]
    [InlineData("Host=db.example;Pooling=false")]
    [InlineData("Host=db.example")]
    [InlineData("Host=db.example;Port=6432")]
    public void Without_a_pooler_sign_or_with_pooling_on_the_check_applies(string connectionString)
    {
        PoolingOffCheck.NotApplicableReason(Facts(connectionString)).Should().BeNull();
    }

    [Fact]
    public void The_remedy_turns_pooling_on_in_both_forms_and_the_finding_links_Sessions()
    {
        HealthFinding finding = C17(Facts("Host=db.example;Pooling=false", sessions: Sessions(10, 100))).Findings.Single();

        finding.Remedy!.Code.Should().Contain("Pooling=true").And.Contain("b.ConnectionStringBuilder.Pooling = true;");
        finding.Link!.Route.Should().Be(SessionEvidence.SessionsLink.Route);
        finding.Caveat.Should().Contain("every session of this database").And.Contain("AcceptedChecks");

        Everything(C17(Facts("Host=db.example;Pooling=false"), postgres: true).Findings.Single()).Should().NotContain("Marten").And.NotContain("opts.");
    }

    // ----------------------------------------------------------------------------------------------
    // C18 Several pools to one database in this process
    // ----------------------------------------------------------------------------------------------

    private static HealthVerdict C18(ConnectionFacts facts, bool postgres = false) =>
        new SiblingPoolsCheck().Evaluate(facts, Evaluation(postgres)).ShouldBeWellFormedFor<SiblingPoolsCheck>();

    [Fact]
    public void Two_or_more_pools_are_an_Info_finding_with_the_count_and_the_sum_of_their_ceilings()
    {
        HealthFinding finding = C18(Facts("Host=db.example", siblings: new ConnectionPoolSiblings(3, 250, 0))).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Evidence.Should().Contain(x => x.Label == "Pools to this database as this login" && x.Value == "3");
        finding.Evidence.Should().Contain(x => x.Label == "Maximum Pool Size, added up" && x.Value == "250 connections at most");
        finding.Evidence.Should().NotContain(static x => x.Label.StartsWith("Named pools", StringComparison.Ordinal), "no named pool was left out");
        finding.Link!.Route.Should().Be(SessionEvidence.HoldersLink.Route);
    }

    [Fact]
    public void Named_pools_the_studio_could_not_identify_are_counted_in_the_evidence_and_a_missing_ceiling_says_so()
    {
        HealthFinding finding = C18(Facts("Host=db.example", siblings: new ConnectionPoolSiblings(2, null, 4))).Findings.Single();

        finding.Evidence.Should().Contain(x => x.Label == "Named pools not identified, left out" && x.Value == "4");
        finding.Evidence.Should().Contain(x => x.Label == "Maximum Pool Size, added up" && x.Value == "not reported by every pool");
        finding.Caveat.Should().Contain("(4 here)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Fewer_than_two_pools_is_quiet(int pools)
    {
        HealthVerdict verdict = C18(Facts("Host=db.example", siblings: new ConnectionPoolSiblings(pools, pools == 0 ? null : 100, 2)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Not judged for 2 named pool(s)", "a quiet C18 with unidentified named pools is not proof");
    }

    [Fact]
    public void A_data_source_whose_own_identity_does_not_parse_is_not_checked_for_that_reason_and_not_for_the_meters()
    {
        // The facts the sweep builds when the connection string names no Host: no identity, so no sibling can be compared.
        ConnectionFacts noIdentity = Facts("Host=db.example") with { SiblingsNotObserved = ConnectionFacts.NoIdentityReason };

        C18(noIdentity).Note.Should().Contain("host, database and login").And.NotContain("meter");
        ConnectionFacts.NoIdentityReason.Should().Contain("names no Host").And.NotContain("meter");
        C18(Facts("Host=db.example")).Note.Should().Contain("were not observed", "without that reason the meter is the cause, as before");
    }

    [Fact]
    public void The_remedy_is_one_shared_data_source_and_in_Marten_Studio_hands_it_to_Marten()
    {
        ConnectionFacts facts = Facts("Host=db.example", siblings: new ConnectionPoolSiblings(2, 200, 0));

        HealthFinding marten = C18(facts).Findings.Single();
        marten.Remedy!.Code.Should().Contain("services.AddNpgsqlDataSource(connectionString);").And.Contain(".UseNpgsqlDataSource()");

        HealthFinding postgres = C18(facts, postgres: true).Findings.Single();
        postgres.Remedy!.Code.Should().Contain("services.AddNpgsqlDataSource(connectionString);");
        Everything(postgres).Should().NotContain("Marten").And.NotContain("UseNpgsqlDataSource");
    }

    [Fact]
    public void Facts_hold_counts_only_so_no_finding_can_name_a_pool()
    {
        // The facts have no field a name could be in: pools, a sum and a count of names left out.
        typeof(ConnectionPoolSiblings).GetProperties().Select(static x => x.PropertyType).Should().OnlyContain(static x => x == typeof(int) || x == typeof(int?));
    }
}
