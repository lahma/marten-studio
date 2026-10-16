using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// C01, C02, C03 and C09's judgement, over stated facts and no Postgres (the <c>IndexAdvice.Join</c> precedent).
/// </summary>
public class PoolChecksTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static HealthEvaluation At(HealthHistoryView? history = null) => HealthEvaluation.At(Now, history: history);

    private static HealthHistoryView Saturated(params double[] values) => new(
        [
            .. values.Select((value, i) => new HealthSample(
                Now.AddMinutes(i - values.Length + 1),
                new Dictionary<string, double>(StringComparer.Ordinal) { [PoolSaturationCheck.SaturatedSeries] = value })),
        ]);

    private static ServerConnectionLimits Server(long clients, int max = 100, int superuser = 3, int reserved = 0, int version = 170000) =>
        new(clients, clients / 2, max, superuser, reserved, version);

    // ----------------------------------------------------------------------------------------------
    // C01
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void C01_is_quiet_with_room_in_the_pool()
    {
        HealthVerdict verdict = new PoolSaturationCheck().Evaluate(new PoolFacts(true, 100, 40, 10, 100, 1, Now), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("40 of 100");
    }

    [Fact]
    public void C01_warns_above_eighty_percent()
    {
        HealthVerdict verdict = new PoolSaturationCheck().Evaluate(new PoolFacts(true, 10, 9, 1, 10, 1, Now), At());

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Code.Should().Be("C01");
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("In use", "9 of 10 (90%)"));
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
        finding.Caveat.Should().Contain("never listens to").And.Contain("Other processes");
    }

    [Fact]
    public void C01_at_exactly_eighty_percent_is_quiet()
    {
        new PoolSaturationCheck().Evaluate(new PoolFacts(true, 10, 8, 2, 10, 1, Now), At()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void C01_is_critical_only_on_two_consecutive_saturated_samples()
    {
        PoolFacts full = new(true, 10, 10, 0, 10, 1, Now);

        new PoolSaturationCheck().Evaluate(full, At(Saturated(1))).Findings.Single().Severity
            .Should().Be(HealthSeverity.Warning, "one sample is a moment; two in a row are a pool that stays full");

        new PoolSaturationCheck().Evaluate(full, At(Saturated(0, 1))).Findings.Single().Severity
            .Should().Be(HealthSeverity.Warning);

        HealthFinding critical = new PoolSaturationCheck().Evaluate(full, At(Saturated(0, 1, 1))).Findings.Single();
        critical.Severity.Should().Be(HealthSeverity.Critical);
        critical.Evidence.Should().Contain(new HealthEvidence("Fully used", "2 consecutive samples"));
    }

    [Fact]
    public void C01_samples_saturation_so_the_next_sweep_can_count_it()
    {
        new PoolSaturationCheck().Sample(new PoolFacts(true, 10, 10, 0, 10, 1, Now))
            .Should().Contain(new HealthSamplePart(PoolSaturationCheck.SaturatedSeries, 1));

        new PoolSaturationCheck().Sample(new PoolFacts(false, 10, 0, 0, null, 0, Now)).Should().BeEmpty();
    }

    [Fact]
    public void C01_says_why_an_unpooled_data_source_is_not_judged()
    {
        HealthVerdict verdict = new PoolSaturationCheck().Evaluate(new PoolFacts(false, 100, 0, 0, null, 0, Now), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("Pooling=false");
    }

    [Fact]
    public void C01_judges_against_the_ceiling_Npgsql_reports_over_the_configured_one()
    {
        new PoolSaturationCheck().Evaluate(new PoolFacts(true, 100, 9, 1, 10, 1, Now), At()).Findings.Should().ContainSingle();
    }

    // ----------------------------------------------------------------------------------------------
    // C02
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void C02_is_quiet_for_one_database_on_the_server_whatever_its_ceiling()
    {
        HealthVerdict verdict = new ConnectionBudgetCheck().Evaluate(new ConnectionBudgetFacts(1, 1, 500, 0, 1, Server(10)), At());

        verdict.Findings.Should().BeEmpty("one pool against the server is the ordinary case, and C03's to watch");
        verdict.Note.Should().Contain("C03");
    }

    [Fact]
    public void C02_warns_when_the_pools_together_can_open_more_than_the_server_takes()
    {
        HealthVerdict verdict = new ConnectionBudgetCheck().Evaluate(new ConnectionBudgetFacts(3, 3, 300, 0, 4, Server(20)), At());

        HealthFinding finding = verdict.Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(new HealthEvidence("Max Pool Size, summed", "300"));
        finding.Evidence.Should().Contain(x => x.Label == "The server accepts" && x.Value.StartsWith("97 (max_connections 100", StringComparison.Ordinal));
        finding.Evidence.Should().Contain(new HealthEvidence("Databases on this server", "3 of the store's 4"));
        finding.Remedy!.Code.Should().Contain("MaxPoolSize");
    }

    [Fact]
    public void C02_is_quiet_when_the_pools_fit()
    {
        new ConnectionBudgetCheck().Evaluate(new ConnectionBudgetFacts(3, 3, 60, 0, 3, Server(20)), At()).Findings.Should().BeEmpty();
    }

    [Fact]
    public void C02_counts_reserved_connections_on_PostgreSQL_16_and_later()
    {
        ServerConnectionLimits sixteen = Server(20, max: 100, superuser: 3, reserved: 10, version: 160004);

        sixteen.Available.Should().Be(87);
        sixteen.AvailableText.Should().Contain("10 reserved_connections");
        Server(20, version: 150008).AvailableText.Should().NotContain(" reserved_connections", "there is no such setting before PostgreSQL 16");

        new ConnectionBudgetCheck().Evaluate(new ConnectionBudgetFacts(2, 2, 90, 0, 2, sixteen), At()).Findings.Should().ContainSingle();
    }

    // ----------------------------------------------------------------------------------------------
    // C03
    // ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(70, null)]
    [InlineData(77, null)]
    [InlineData(78, "Warning")]
    [InlineData(92, "Warning")]
    [InlineData(93, "Critical")]
    [InlineData(99, "Critical")]
    public void C03_warns_above_eighty_percent_of_what_ordinary_roles_may_open_and_is_critical_at_ninety_five(long clients, string? expected)
    {
        // 97 available: 77 is 79.4%, 78 is 80.4%; 92 is 94.8%, 93 is 95.9%.
        HealthVerdict verdict = new ServerConnectionHeadroomCheck().Evaluate(Server(clients), At());

        if (expected is null)
        {
            verdict.Findings.Should().BeEmpty();
        }
        else
        {
            verdict.Findings.Should().ContainSingle().Which.Severity.Should().Be(Enum.Parse<HealthSeverity>(expected));
        }
    }

    [Fact]
    public void C03_says_it_counts_the_whole_server()
    {
        HealthFinding finding = new ServerConnectionHeadroomCheck().Evaluate(Server(90), At()).Findings.Single();

        finding.Caveat.Should().Contain("every database included");
        finding.Evidence.Should().Contain(new HealthEvidence("To this database", "45"));
    }

    // ----------------------------------------------------------------------------------------------
    // C09
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void C09_is_quiet_when_no_daemon_is_hosted_here_and_says_why()
    {
        HealthVerdict verdict = new DaemonPoolCheck().Evaluate(new DaemonPoolFacts(false, "Not registered with AddAsyncDaemon.", 0, null, true, 3), At());

        verdict.Findings.Should().BeEmpty();
        verdict.Note.Should().Contain("not hosted in this process").And.Contain("AddAsyncDaemon");
    }

    [Theory]
    [InlineData(1, 3, false)]
    [InlineData(2, 3, true)]
    [InlineData(5, 3, true)]
    [InlineData(98, 100, false)]
    [InlineData(99, 100, true)]
    public void C09_warns_when_running_shards_and_the_daemons_own_connection_reach_the_pool(int running, int pool, bool fires)
    {
        HealthVerdict verdict = new DaemonPoolCheck().Evaluate(new DaemonPoolFacts(true, null, running, running + 1, true, pool), At());

        verdict.Findings.Should().HaveCount(fires ? 1 : 0);

        if (fires)
        {
            HealthFinding finding = verdict.Findings[0];
            finding.Severity.Should().Be(HealthSeverity.Warning);
            finding.Evidence.Should().Contain(new HealthEvidence("Running shards", running.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
        }
    }

    [Fact]
    public void C09_is_quiet_for_an_unpooled_data_source()
    {
        new DaemonPoolCheck().Evaluate(new DaemonPoolFacts(true, null, 50, 50, false, 3), At()).Findings.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------------------------------
    // Shared rules
    // ----------------------------------------------------------------------------------------------

    [Fact]
    public void No_pool_finding_names_a_session_and_none_needs_the_activity_gate()
    {
        HealthFinding[] findings =
        [
            new PoolSaturationCheck().Evaluate(new PoolFacts(true, 10, 10, 0, 10, 1, Now), At(Saturated(1, 1))).Findings.Single(),
            new ConnectionBudgetCheck().Evaluate(new ConnectionBudgetFacts(3, 3, 300, 1, 3, Server(20)), At()).Findings.Single(),
            new ServerConnectionHeadroomCheck().Evaluate(Server(96), At()).Findings.Single(),
            new DaemonPoolCheck().Evaluate(new DaemonPoolFacts(true, null, 5, 5, true, 3), At()).Findings.Single(),
        ];

        findings.SelectMany(static x => x.Evidence).Should().OnlyContain(
            static x => x.Gate == HealthGate.None,
            "counts and settings are the database's, shown to anybody its store policy passes");
    }
}
