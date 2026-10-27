using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;

namespace MartenStudio.Tests.Health;

/// <summary>
/// C13 and C14's judgement over stated holders and no Postgres (issue #20): C13 speaks only while the server is at or above C03's
/// Warning line and one application - summed across databases - holds a tenth of the client limit idle, exactly at the boundary;
/// C14 says, as Info, that connections set no application name and names the largest few as
/// <c>role@address/database (count)</c>; both are activity checks, and every name is in gated evidence.
/// </summary>
public class ConnectionHolderChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static HealthEvaluation At() => HealthEvaluation.At(Now);

    /// <summary>A server whose clients may open <paramref name="available" /> connections (three reserved for superusers) and has <paramref name="used" /> open.</summary>
    private static ServerConnectionLimits Limits(long used, int available = 100) => new(used, 0, available + 3, 3, 0, 170000);

    private static SessionHolderGroup Group(
        string database,
        string application,
        long idle,
        long active = 0,
        string role = "app_user",
        string? client = "10.0.3.7",
        long idleInTransaction = 0,
        long unknown = 0,
        bool hidden = false) =>
        new(database, application, role, client, hidden, idle + active + idleInTransaction + unknown, active, idle, idleInTransaction, unknown, null, null);

    private static ConnectionHolderFacts Facts(ServerConnectionLimits limits, params SessionHolderGroup[] groups) =>
        new(
            Now,
            limits,
            new SessionServerHolders(true, limits.MaxConnections, limits.SuperuserReserved, limits.Reserved, limits.ServerVersion, limits.ClientBackends, groups.Length, groups));

    private static HealthVerdict Hoard(ConnectionHolderFacts facts) =>
        new IdleConnectionsHoardedCheck().Evaluate(facts, At()).ShouldBeWellFormedFor<IdleConnectionsHoardedCheck>();

    private static HealthVerdict Unattributed(ConnectionHolderFacts facts) =>
        new UnattributedConnectionsCheck().Evaluate(facts, At()).ShouldBeWellFormedFor<UnattributedConnectionsCheck>();

    // ----------------------------------------------------------------------------------------------
    // What they are
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void Both_are_activity_checks_of_the_connections_area_that_run_in_the_regular_sweep_with_no_variants()
    {
        foreach ((Type check, string code, string heading) in new[]
                 {
                     (typeof(IdleConnectionsHoardedCheck), "C13", "## C13 Idle connections hoarded near the limit"),
                     (typeof(UnattributedConnectionsCheck), "C14", "## C14 Connections with no application name"),
                 })
        {
            HealthCheckInfo info = HealthCheckRegistry.Describe(check);

            info.Code.Should().Be(code);
            info.Area.Should().Be(HealthArea.Connections);
            info.Tier.Should().Be(HealthCostTier.Catalog, "{0} runs in the regular sweep, from the sweep's one holders read", code);
            info.Gate.Should().Be(HealthGate.Activity, "{0} names other people's applications, roles, addresses and databases (D29)", code);
            info.Variants.Should().BeEmpty();
            info.DocsHeading.Should().Be(heading);
        }
    }

    [Fact]
    public void Both_checks_judge_one_shared_read_so_a_sweep_makes_it_once()
    {
        ServerConnectionHolders.Key.Should().Be("connections.holders");
        ConnectionThresholds.IdleHoardUsage.Should().Be(ConnectionThresholds.ServerUsedWarning, "C13 waits for C03's own Warning line");
        ConnectionThresholds.IdleHoardShare.Should().Be(0.1);
        ConnectionThresholds.IdleHoardDivisor.Should().Be(10);
    }

    // ----------------------------------------------------------------------------------------------
    // C13: the usage line
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(79)]
    public void Below_the_warning_line_nothing_fires_however_many_idle_connections_one_application_holds(long used)
    {
        HealthVerdict verdict = Hoard(Facts(Limits(used), Group("orders", "billing-api", idle: 60)));

        verdict.Findings.Should().BeEmpty("below C03's line ten idle connections, or sixty, are a warm pool");
        verdict.Note.Should().Contain("warm pool").And.Contain("80%");
    }

    [Fact]
    public void At_exactly_the_warning_line_it_speaks_which_is_the_line_C03_waits_for_to_pass()
    {
        // C03 is quiet at exactly 80%; C13 is at or above it, as its issue says.
        HealthVerdict verdict = Hoard(Facts(Limits(80), Group("orders", "billing-api", idle: 12)));

        verdict.Findings.Should().ContainSingle();
    }

    // ----------------------------------------------------------------------------------------------
    // C13: the hoarding line, exactly
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(100, 9, false)]
    [InlineData(100, 10, true)]
    [InlineData(100, 11, true)]
    [InlineData(97, 9, false)]
    [InlineData(97, 10, true)]
    [InlineData(91, 9, false)]
    [InlineData(91, 10, true)]
    [InlineData(30, 2, false)]
    [InlineData(30, 3, true)]
    [InlineData(5, 1, true)]
    public void An_application_is_a_hoarder_at_exactly_a_tenth_of_the_client_limit_idle(int available, long idle, bool fires)
    {
        HealthVerdict verdict = Hoard(Facts(Limits(available, available), Group("orders", "billing-api", idle), Group("orders", "reports", 1)));

        (verdict.Findings.Count == 1).Should().Be(fires);

        if (!fires)
        {
            verdict.Note.Should().Contain("no application holds").And.Contain("idle");
        }
    }

    [Fact]
    public void The_threshold_is_a_tenth_of_the_limit_rounded_up()
    {
        IdleConnectionsHoardedCheck.Threshold(Limits(0, 100)).Should().Be(10);
        IdleConnectionsHoardedCheck.Threshold(Limits(0, 97)).Should().Be(10);
        IdleConnectionsHoardedCheck.Threshold(Limits(0, 91)).Should().Be(10);
        IdleConnectionsHoardedCheck.Threshold(Limits(0, 90)).Should().Be(9);
        IdleConnectionsHoardedCheck.Threshold(Limits(0, 30)).Should().Be(3);
    }

    [Fact]
    public void An_application_is_summed_across_databases_roles_and_addresses_and_idle_means_idle()
    {
        // 4 + 3 + 3 idle in three databases, from three roles and addresses; plus sessions that are not idle and do not count.
        ConnectionHolderFacts facts = Facts(
            Limits(90),
            Group("orders", "billing-api", idle: 4, role: "app_a", client: "10.0.0.1"),
            Group("billing", "billing-api", idle: 3, role: "app_b", client: "10.0.0.2"),
            Group("audit", "billing-api", idle: 3, active: 9, role: "app_c", client: "10.0.0.3", idleInTransaction: 7),
            Group("orders", "reports", idle: 6, unknown: 5));

        HealthVerdict verdict = Hoard(facts);

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Evidence.Should().Contain(x => x.Label == "Application 1 (most idle)" && x.Value.StartsWith("'billing-api': 10 idle of 26 connections", StringComparison.Ordinal),
            "idle in a transaction (C04's), active and state-unknown sessions are not idle");
        finding.Evidence.Single(static x => x.Label == "Application 1 (most idle)").Value
            .Should().Contain("10% of the client limit")
            .And.Contain("orders (4)").And.Contain("billing (3)").And.Contain("audit (3)")
            .And.Contain("app_a@10.0.0.1 (4)").And.Contain("app_b@10.0.0.2 (3)").And.Contain("app_c@10.0.0.3 (3)");
        finding.Evidence.Should().NotContain(static x => x.Value.Contains("'reports'", StringComparison.Ordinal), "six idle of a limit of 100 is not a hoarder");
    }

    [Fact]
    public void Sessions_whose_state_is_not_readable_are_counted_as_neither_idle_nor_active_and_the_finding_says_so()
    {
        ConnectionHolderFacts facts = Facts(Limits(90), Group("orders", "billing-api", idle: 4, unknown: 6, role: "other", client: null, hidden: true));
        ConnectionHolderFacts partial = facts with { Holders = facts.Holders with { CanReadAllStats = false } };

        HealthVerdict quiet = Hoard(partial);

        quiet.Findings.Should().BeEmpty("four idle and six whose state the role cannot read: the idle ones it can count are below the line");
        quiet.Note.Should().Contain("pg_read_all_stats").And.Contain("6 sessions");

        ConnectionHolderFacts loud = Facts(Limits(90), Group("orders", "billing-api", idle: 12, unknown: 3, hidden: true, client: null));
        HealthFinding finding = Hoard(loud with { Holders = loud.Holders with { CanReadAllStats = false } }).Findings.Should().ContainSingle().Subject;
        finding.Caveat.Should().Contain("pg_read_all_stats").And.Contain("3 sessions");
    }

    [Fact]
    public void A_session_with_no_application_name_is_an_application_of_its_own_summed_across_databases()
    {
        ConnectionHolderFacts facts = Facts(
            Limits(90),
            Group("orders", string.Empty, idle: 6, role: "app_a", client: "10.0.0.1"),
            Group("billing", string.Empty, idle: 5, role: "app_a", client: "10.0.0.1"),
            Group("orders", "billing-api", idle: 9));

        HealthFinding finding = Hoard(facts).Findings.Should().ContainSingle().Subject;

        finding.Evidence.Single(static x => x.Label == "Application 1 (most idle)").Value
            .Should().StartWith("(no application name): 11 idle of 11 connections")
            .And.Contain("app_a@10.0.0.1 (11)", "two groups of one role and address are one holder");
        finding.Evidence.Should().NotContain(static x => x.Value.StartsWith("'billing-api'", StringComparison.Ordinal), "nine idle of a limit of 100 is under the line");
    }

    // ----------------------------------------------------------------------------------------------
    // C13: what it says
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void One_finding_names_every_hoarder_most_idle_first_five_at_most_in_gated_evidence_and_nothing_in_the_title()
    {
        List<SessionHolderGroup> groups = [];

        for (int i = 0; i < 7; i++)
        {
            groups.Add(Group("orders", "service-" + i, idle: 12 + i));
        }

        HealthFinding finding = Hoard(Facts(Limits(95), [.. groups])).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Code.Should().Be("C13");
        finding.Variant.Should().BeNull();
        finding.Subject.Should().BeNull();
        finding.Title.Should().Contain("7 applications").And.Contain("10%").And.Contain("95%");
        finding.Title.Should().NotContain("service-");
        finding.WhyItMatters.Should().NotContain("service-");
        finding.Caveat.Should().NotContain("service-");

        IEnumerable<HealthEvidence> named = finding.Evidence.Where(static x => x.Label.StartsWith("Application ", StringComparison.Ordinal));
        named.Should().HaveCount(ConnectionThresholds.SessionsNamed);
        named.Select(static x => x.Label).Should().Equal("Application 1 (most idle)", "Application 2", "Application 3", "Application 4", "Application 5");
        named.Should().OnlyContain(static x => x.Gate == HealthGate.Activity, "an application's name is other people's");
        named.First().Value.Should().StartWith("'service-6': 18 idle");
        finding.Evidence.Should().Contain(static x => x.Label == "Client connections in use" && x.Value == "95 of 100 (95%)");
        finding.Evidence.Single(static x => x.Label.StartsWith("Hoarding", StringComparison.Ordinal)).Value.Should().StartWith("7 applications");
        finding.Link.Should().Be(SessionEvidence.HoldersLink);
    }

    [Fact]
    public void One_hoarder_is_named_in_the_singular_and_the_remedy_is_the_pool_or_the_servers_timeout_never_a_statement()
    {
        HealthFinding finding = Hoard(Facts(Limits(96, 97), Group("orders", "billing-api", idle: 14, active: 8))).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().StartWith("One application holds 14 idle connections, 14%");
        finding.Remedy!.Kind.Should().Be(HealthRemedyKind.Configuration);
        finding.Remedy.Code.Should().Be("Connection Idle Lifetime=60");
        finding.Remedy.Explanation.Should().Contain("Maximum Pool Size").And.Contain("idle_session_timeout").And.Contain("Connection Idle Lifetime")
            .And.Contain("300 seconds").And.Contain("never ends a session");
        finding.Link!.Query.Should().Contain(new KeyValuePair<string, string?>(SessionLinks.ViewParameter, SessionLinks.HoldersView));
        finding.Evidence.Single(static x => x.Label == "Client limit").Value.Should().Contain("max_connections 100");
    }

    // ----------------------------------------------------------------------------------------------
    // C14
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void When_every_connection_sets_an_application_name_C14_is_quiet()
    {
        HealthVerdict verdict = Unattributed(Facts(Limits(10), Group("orders", "billing-api", idle: 6), Group("orders", "reports", idle: 4)));

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("sets an application name");
    }

    [Fact]
    public void Unnamed_connections_are_info_and_the_largest_few_are_named_as_role_at_address_slash_database_with_their_count()
    {
        ConnectionHolderFacts facts = Facts(
            Limits(30),
            Group("orders", string.Empty, idle: 9, role: "billing", client: "10.0.3.7"),
            Group("orders", string.Empty, idle: 5, active: 2, role: "billing", client: "10.0.3.8"),
            Group("audit", string.Empty, idle: 4, role: "auditor", client: null),
            Group("audit", string.Empty, idle: 3, role: "other", client: null, hidden: true, unknown: 0),
            Group("orders", string.Empty, idle: 2, role: "etl", client: "10.0.9.9"),
            Group("orders", string.Empty, idle: 1, role: "etl", client: "10.0.9.10"),
            Group("orders", "named-app", idle: 20, role: "app_user"));

        HealthFinding finding = Unattributed(facts).Findings.Should().ContainSingle().Subject;

        finding.Severity.Should().Be(HealthSeverity.Info);
        finding.Code.Should().Be("C14");
        finding.Title.Should().Be("26 client connections set no application name, so they cannot be attributed");

        finding.Evidence.Where(static x => x.Label.StartsWith("Holder ", StringComparison.Ordinal)).Select(static x => (x.Label, x.Value, x.Gate)).Should().Equal(
            ("Holder 1 (largest)", "billing@10.0.3.7/orders (9)", HealthGate.Activity),
            ("Holder 2", "billing@10.0.3.8/orders (7)", HealthGate.Activity),
            ("Holder 3", "auditor@local socket/audit (4)", HealthGate.Activity),
            ("Holder 4", "other@not readable/audit (3)", HealthGate.Activity),
            ("Holder 5", "etl@10.0.9.9/orders (2)", HealthGate.Activity));

        finding.Evidence.Single(static x => x.Label == "Without an application name").Value.Should().StartWith("26 client connections of the 100");
        finding.Evidence.Should().NotContain(static x => x.Value.Contains("named-app", StringComparison.Ordinal), "a named application is not the finding's");
        finding.Title.Should().NotContain("billing").And.NotContain("orders");
        finding.Link.Should().Be(SessionEvidence.HoldersLink);
    }

    [Fact]
    public void One_unnamed_connection_is_worded_in_the_singular_and_C14_does_not_wait_for_the_server_to_fill()
    {
        // 1 of 100: C13 would be quiet, and C14 speaks - it is not about how full the server is.
        HealthFinding finding = Unattributed(Facts(Limits(1), Group("orders", string.Empty, idle: 1))).Findings.Should().ContainSingle().Subject;

        finding.Title.Should().Be("A client connection sets no application name, so it cannot be attributed");
        finding.Remedy!.Code.Should().Be("Application Name=billing-api/orders");
        finding.Remedy.Explanation.Should().Contain("{host}/{pool}").And.Contain("nothing secret");
    }

    [Fact]
    public void A_capped_read_says_it_in_the_caveat_of_both()
    {
        ConnectionHolderFacts facts = Facts(Limits(90), Group("orders", string.Empty, idle: 12));
        facts = facts with { Holders = facts.Holders with { GroupCount = 2_400 } };

        Hoard(facts).Findings.Single().Caveat.Should().Contain("Only the 1 largest of 2,400 groups");
        Unattributed(facts).Findings.Single().Caveat.Should().Contain("Only the 1 largest of 2,400 groups");
    }

    [Fact]
    public void The_budget_is_C03s_formula_not_a_second_one()
    {
        ConnectionHolderFacts facts = Facts(new ServerConnectionLimits(50, 0, 100, 3, 2, 170000));

        facts.Limits.Available.Should().Be(95);
        facts.Holders.Limits.Available.Should().Be(new ServerConnectionLimits(50, 0, 100, 3, 2, 170000).Available,
            "the holders read's own budget goes through ServerConnectionLimits.Available");
        facts.Holders.Limits.AvailableText.Should().StartWith("95 (max_connections 100").And.Contain("2 reserved_connections");
    }

    /// <summary>The same facts in Postgres Studio's words: nothing of either check says a store or Marten.</summary>
    [Fact]
    public void Neither_check_says_a_store_in_Postgres_Studios_words()
    {
        ConnectionHolderFacts facts = Facts(Limits(90), Group("orders", "billing-api", idle: 12), Group("orders", string.Empty, idle: 4));
        HealthEvaluation evaluation = HealthEvaluation.At(Now, text: PostgresStudioText.Instance);

        List<HealthFinding> findings =
        [
            .. new IdleConnectionsHoardedCheck().Evaluate(facts, evaluation).Findings,
            .. new UnattributedConnectionsCheck().Evaluate(facts, evaluation).Findings,
        ];

        findings.Select(static x => x.Code).Should().Equal("C13", "C14");

        foreach (HealthFinding finding in findings)
        {
            string all = finding.Title + " " + finding.WhyItMatters + " " + finding.Caveat + " " + finding.Remedy!.Explanation;
            all.Should().NotMatchRegex(@"(?i)\bstores?\b|\bmarten\b", finding.Code);
        }
    }
}
