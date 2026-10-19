using System.Text.RegularExpressions;

using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Export;

using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

using StudioCheckResult = MartenStudio.Services.Health.HealthCheckResult;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The export's judgement, with no container and no Postgres: which status a run's findings report, and
/// exactly what the description and the data say - codes and counts, and nothing a finding wrote.
/// </summary>
public class HealthExportSummaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly Regex Code = new("^[DETCK][0-9]{2}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    [Fact]
    public void A_run_with_no_findings_is_healthy_and_says_how_much_it_checked()
    {
        HealthExportSummary summary = HealthExportSummary.From(Now, [Sweep(Quiet("K01"), Quiet("T03"))]);

        summary.Level.Should().Be(HealthExportLevel.Healthy);
        summary.Describe(TimeSpan.FromSeconds(60)).Should().Be("No findings on 1 database. Refreshed after 60 s, served for at most 120 s.");
    }

    [Fact]
    public void Info_findings_are_reported_and_leave_the_run_healthy()
    {
        HealthExportSummary summary = HealthExportSummary.From(Now, [Sweep(Found("K02", HealthSeverity.Info), Found("K03", HealthSeverity.Info))]);

        summary.Level.Should().Be(HealthExportLevel.Healthy);
        summary.Info.Should().Equal("K02", "K03");
        summary.Describe(TimeSpan.FromSeconds(60)).Should().Be("2 info (K02, K03) on 1 database. Refreshed after 60 s, served for at most 120 s.");
    }

    [Fact]
    public void A_warning_is_degraded_and_a_critical_is_critical()
    {
        HealthExportSummary.From(Now, [Sweep(Found("T06", HealthSeverity.Warning))]).Level.Should().Be(HealthExportLevel.Degraded);
        HealthExportSummary.From(Now, [Sweep(Found("T06", HealthSeverity.Warning), Found("D02", HealthSeverity.Critical))])
            .Level.Should().Be(HealthExportLevel.Critical);
    }

    /// <summary>The packet's own example: codes once each, findings counted, worst first.</summary>
    [Fact]
    public void The_description_counts_findings_and_names_each_code_once()
    {
        HealthExportSummary summary = HealthExportSummary.From(
            Now,
            [
                Sweep(Found("D02", HealthSeverity.Critical), Found("T06", HealthSeverity.Warning)),
                Sweep(Found("C04", HealthSeverity.Warning)),
                Sweep(Found("K02", HealthSeverity.Info)),
            ]);

        summary.Critical.Should().Equal("D02");
        summary.Warning.Should().Equal("C04", "T06");
        summary.Describe(TimeSpan.FromSeconds(60)).Should().Be(
            "1 critical (D02), 2 warnings (C04, T06), 1 info (K02) on 3 databases. Refreshed after 60 s, served for at most 120 s.");
    }

    [Fact]
    public void The_same_code_in_two_databases_is_listed_once_and_counted_twice()
    {
        HealthExportSummary summary = HealthExportSummary.From(
            Now, [Sweep(Found("T06", HealthSeverity.Warning)), Sweep(Found("T06", HealthSeverity.Warning))]);

        summary.Warning.Should().Equal("T06");
        summary.WarningFindings.Should().Be(2);
        summary.Describe(TimeSpan.FromSeconds(30)).Should().StartWith("2 warnings (T06) on 2 databases");
    }

    [Fact]
    public void A_check_that_failed_is_a_could_not_check_entry_and_degraded()
    {
        HealthExportSummary summary = HealthExportSummary.From(Now, [Sweep(Quiet("K01"), Outcome("T03", HealthCheckOutcome.Failed))]);

        summary.Level.Should().Be(HealthExportLevel.Degraded);
        summary.Failed.Should().Equal("T03");
        summary.Describe(TimeSpan.FromSeconds(60)).Should().Be("No findings on 1 database; 1 check could not run (T03). Refreshed after 60 s, served for at most 120 s.");
    }

    /// <summary>Expected states are values: listed in the data, and no reason to page anybody.</summary>
    [Fact]
    public void A_check_unavailable_here_is_listed_and_changes_nothing()
    {
        HealthExportSummary summary = HealthExportSummary.From(Now, [Sweep(Outcome("C03", HealthCheckOutcome.Unavailable))]);

        summary.Level.Should().Be(HealthExportLevel.Healthy);
        summary.Unavailable.Should().Equal("C03");
        summary.Failed.Should().BeEmpty();
    }

    /// <summary>
    /// H8-fix F2: a check that ran out of time - its statement, its lock wait, its budget, or the sweep's - is not
    /// the host's choice, and is Degraded; one the host chose not to make possible is still only listed.
    /// </summary>
    [Fact]
    public void A_check_that_ran_out_of_time_is_Degraded_and_said()
    {
        HealthExportSummary summary = HealthExportSummary.From(
            Now,
            [
                Sweep(
                    NotChecked("T03", HealthUnavailableReason.TimedOut),
                    NotChecked("C04", HealthUnavailableReason.SweepBudget),
                    NotChecked("C03", HealthUnavailableReason.HostChoice)),
            ]);

        summary.Level.Should().Be(HealthExportLevel.Degraded);
        summary.TimedOut.Should().Equal("C04", "T03");
        summary.Unavailable.Should().Equal("C03");
        summary.Describe(TimeSpan.FromSeconds(60)).Should().Be(
            "No findings on 1 database; 2 checks ran out of time (C04, T03). Refreshed after 60 s, served for at most 120 s.");
    }

    /// <summary>The sweeper's own classification of each expected state, which F2 reads instead of a sentence.</summary>
    /// <param name="sqlState">What Postgres answered, or null for an exception of another kind.</param>
    /// <param name="expected">The <c>HealthUnavailableReason</c> name it is classified as.</param>
    [Theory]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege, nameof(HealthUnavailableReason.HostChoice))]
    [InlineData(PostgresErrorCodes.QueryCanceled, nameof(HealthUnavailableReason.TimedOut))]
    [InlineData(PostgresErrorCodes.LockNotAvailable, nameof(HealthUnavailableReason.TimedOut))]
    public void The_sweeper_marks_which_kind_of_expected_state_a_Postgres_refusal_is(string sqlState, string expected)
    {
        (HealthCheckOutcome outcome, HealthCheckProblem problem) = HealthSweeper.Classify(
            new PostgresException("refused", "ERROR", "ERROR", sqlState), budgetExpired: false);

        outcome.Should().Be(HealthCheckOutcome.Unavailable);
        problem.UnavailableReason.Should().Be(Enum.Parse<HealthUnavailableReason>(expected));
    }

    [Fact]
    public void A_gatherers_own_absence_is_the_hosts_choice_and_a_spent_budget_is_running_out_of_time()
    {
        HealthSweeper.Classify(new HealthCheckUnavailableException("No extension."), budgetExpired: false)
            .Problem.UnavailableReason.Should().Be(HealthUnavailableReason.HostChoice);
        HealthSweeper.Classify(new OperationCanceledException(), budgetExpired: true)
            .Problem.UnavailableReason.Should().Be(HealthUnavailableReason.TimedOut);
        HealthSweeper.Classify(new InvalidOperationException("a bug"), budgetExpired: false)
            .Problem.UnavailableReason.Should().BeNull("a failure is not an expected state at all");
    }

    [Fact]
    public void A_check_the_hosts_Marten_cannot_run_is_not_mentioned_at_all()
    {
        HealthExportSummary summary = HealthExportSummary.From(Now, [Sweep(Outcome("D05", HealthCheckOutcome.NotApplicable))]);

        summary.Level.Should().Be(HealthExportLevel.Healthy);
        summary.Unavailable.Should().BeEmpty();
        summary.Failed.Should().BeEmpty();
    }

    [Fact]
    public void A_store_not_reached_and_a_run_out_of_time_are_degraded()
    {
        HealthExportSummary.From(Now, [Sweep(Quiet("K01"))], storesNotReached: 1).Level.Should().Be(HealthExportLevel.Degraded);
        HealthExportSummary.From(Now, [], databasesTimedOut: 2).Level.Should().Be(HealthExportLevel.Degraded);
    }

    /// <summary>What the export chose not to check is said, and is not a fault.</summary>
    [Fact]
    public void Databases_past_the_limit_and_stores_not_yet_loaded_are_said_and_change_nothing()
    {
        HealthExportSummary summary = HealthExportSummary.From(Now, [Sweep(Quiet("K01"))], databasesSkipped: 3, storesNotEnumerated: 1);

        summary.Level.Should().Be(HealthExportLevel.Healthy);
        summary.Describe(TimeSpan.FromSeconds(60)).Should().Be(
            "No findings on 1 database; 3 databases skipped: past the most one run checks; "
            + "1 store not enumerated: it holds no database it can name without building one. Refreshed after 60 s, served for at most 120 s.");
    }

    /// <summary>
    /// H8-fix2: a sharded tenancy's held databases whose ids its <c>TenantIdStyle</c> would correct are not looked
    /// up - a lookup would read the master table - and are counted with the skipped ones, and said apart.
    /// </summary>
    [Fact]
    public void Held_databases_that_cannot_be_named_from_the_cache_are_counted_as_skipped_and_said()
    {
        HealthExportSummary summary = HealthExportSummary.From(Now, [Sweep(Quiet("K01"))], databasesSkipped: 1, databasesUnnamed: 2);

        summary.Level.Should().Be(HealthExportLevel.Healthy);
        summary.ToData()["databasesSkipped"].Should().Be(3);
        summary.Describe(TimeSpan.FromSeconds(60)).Should().Be(
            "No findings on 1 database; 1 database skipped: past the most one run checks; "
            + "2 held databases skipped: naming them would read the master table. Refreshed after 60 s, served for at most 120 s.");
    }

    [Fact]
    public void A_run_that_could_not_check_anything_says_why_in_the_studios_own_words()
    {
        HealthExportSummary.NotRegistered(Now).Level.Should().Be(HealthExportLevel.Degraded);
        HealthExportSummary.NotRegistered(Now).Describe(TimeSpan.FromSeconds(60)).Should().Contain("services.AddMartenStudio()");
        HealthExportSummary.NoStores(Now).Level.Should().Be(HealthExportLevel.Degraded);

        HealthExportSummary broken = HealthExportSummary.Broken(Now, nameof(InvalidOperationException));
        broken.Level.Should().Be(HealthExportLevel.Degraded);
        broken.Describe(TimeSpan.FromSeconds(60)).Should().Be(
            "Marten Studio could not check: its health export failed with InvalidOperationException, logged as event 9270. Refreshed after 60 s, served for at most 120 s.");
    }

    // ---- The status a level reports -----------------------------------------------------------------

    /// <param name="level">A <c>HealthExportLevel</c> name: the enum is internal, so a public theory takes its name.</param>
    /// <param name="failureStatus">What the caller registered for a Critical.</param>
    /// <param name="expected">What the level reports.</param>
    [Theory]
    [InlineData(nameof(HealthExportLevel.Healthy), HealthStatus.Unhealthy, HealthStatus.Healthy)]
    [InlineData(nameof(HealthExportLevel.Degraded), HealthStatus.Unhealthy, HealthStatus.Degraded)]
    [InlineData(nameof(HealthExportLevel.Critical), HealthStatus.Unhealthy, HealthStatus.Unhealthy)]
    [InlineData(nameof(HealthExportLevel.Critical), HealthStatus.Degraded, HealthStatus.Degraded)]
    [InlineData(nameof(HealthExportLevel.Degraded), HealthStatus.Degraded, HealthStatus.Degraded)]
    [InlineData(nameof(HealthExportLevel.Degraded), HealthStatus.Healthy, HealthStatus.Healthy)]
    [InlineData(nameof(HealthExportLevel.Critical), HealthStatus.Healthy, HealthStatus.Healthy)]
    public void Critical_reports_the_failure_status_and_nothing_reports_worse(
        string level, HealthStatus failureStatus, HealthStatus expected)
    {
        MartenStudioHealthCheck.StatusOf(Enum.Parse<HealthExportLevel>(level), failureStatus).Should().Be(expected);
    }

    // ---- The data ------------------------------------------------------------------------------------

    [Fact]
    public void The_data_has_one_shape_whatever_was_found()
    {
        IReadOnlyDictionary<string, object> empty = HealthExportSummary.From(Now, []).ToData();
        IReadOnlyDictionary<string, object> full = HealthExportSummary.From(
                Now,
                [Sweep(Found("D02", HealthSeverity.Critical), Found("T06", HealthSeverity.Warning), Outcome("C04", HealthCheckOutcome.Failed))],
                databasesSkipped: 1,
                storesNotReached: 1)
            .ToData();

        string[] keys =
        [
            "critical", "warning", "info", "failed", "unavailable", "timedOut", "databases", "databasesSkipped", "databasesTimedOut",
            "storesNotReached", "storesNotEnumerated", "measuredAt",
        ];

        empty.Keys.Should().BeEquivalentTo(keys);
        full.Keys.Should().BeEquivalentTo(keys);

        ((string[]) full["critical"]).Should().Equal("D02");
        ((string[]) full["warning"]).Should().Equal("T06");
        ((string[]) full["failed"]).Should().Equal("C04");
        full["databases"].Should().Be(1);
        full["databasesSkipped"].Should().Be(1);
        full["storesNotReached"].Should().Be(1);
        full["measuredAt"].Should().Be("2026-10-15T09:00:00.0000000+00:00");
    }

    /// <summary>
    /// Every value is a list of codes, a count or the time - so there is no slot in the data a name could
    /// ever be put in, whatever a later check writes into its findings.
    /// </summary>
    [Fact]
    public void Every_value_in_the_data_is_a_code_list_a_count_or_the_time()
    {
        IReadOnlyDictionary<string, object> data = HealthExportSummary.From(
                Now,
                [Sweep(Found("D02", HealthSeverity.Critical), Found("K02", HealthSeverity.Info), Outcome("C03", HealthCheckOutcome.Unavailable))])
            .ToData();

        foreach ((string key, object value) in data)
        {
            switch (value)
            {
                case string[] codes:
                    codes.Should().OnlyContain(x => Code.IsMatch(x), "'{0}' lists codes and nothing else", key);
                    break;
                case int count:
                    count.Should().BeGreaterThanOrEqualTo(0);
                    break;
                case string time:
                    key.Should().Be("measuredAt");
                    DateTimeOffset.Parse(time, System.Globalization.CultureInfo.InvariantCulture).Should().Be(Now);
                    break;
                default:
                    throw new InvalidOperationException("'" + key + "' holds a " + value.GetType().Name + ", which the export never writes.");
            }
        }
    }

    /// <summary>
    /// The summary reads the check's code and the finding's severity, and nothing else of a finding: a
    /// finding stuffed with names contributes exactly what a bare one does.
    /// </summary>
    [Fact]
    public void Nothing_a_finding_wrote_reaches_the_description_or_the_data()
    {
        HealthFinding loud = new()
        {
            Code = "C04",
            Severity = HealthSeverity.Warning,
            Title = "Session 4242 of role secret_role from 10.9.8.7 is idle in transaction on acme-secret-tenant",
            WhyItMatters = "It holds a lock on secret_schema.mt_doc_orders.",
            Subject = "4242",
            TenantId = "acme-secret-tenant",
            Evidence = [new HealthEvidence("Query", "select * from secret_schema.mt_doc_orders where name = 'secret-literal'", HealthGate.Activity)],
            Caveat = "secret caveat",
            Statements = [new HealthStatement("select secret from pg_stat_activity", ["@secret"])],
        };

        HealthExportSummary summary = HealthExportSummary.From(
            Now,
            [Sweep(Result("C04", HealthCheckOutcome.Checked, loud) with
            {
                Note = "secret note",
                Problem = new HealthCheckProblem("secret reason", "42P01", "relation \"secret_schema.mt_doc_orders\" does not exist"),
            })]);

        string said = summary.Describe(TimeSpan.FromSeconds(60)) + System.Text.Json.JsonSerializer.Serialize(summary.ToData());

        said.Should().NotContainAny("secret", "4242", "10.9.8.7", "acme", "mt_doc", "select", "42P01");
        said.Should().Contain("C04");
    }

    // ---- Fixtures ------------------------------------------------------------------------------------

    private static HealthSweep Sweep(params StudioCheckResult[] results) =>
        new("default", "127.0.0.1.health", HealthSweepKind.Regular, Now, TimeSpan.Zero, results);

    private static StudioCheckResult Quiet(string code) => Result(code, HealthCheckOutcome.Checked);

    private static StudioCheckResult Outcome(string code, HealthCheckOutcome outcome) => Result(code, outcome);

    private static StudioCheckResult NotChecked(string code, HealthUnavailableReason reason) =>
        Result(code, HealthCheckOutcome.Unavailable) with
        {
            Problem = new HealthCheckProblem("Not checked.", null, null, UnavailableReason: reason),
        };

    private static StudioCheckResult Found(string code, HealthSeverity severity) =>
        Result(code, HealthCheckOutcome.Checked, new HealthFinding
        {
            Code = code,
            Severity = severity,
            Title = "A finding",
            WhyItMatters = "Because.",
        });

    private static StudioCheckResult Result(string code, HealthCheckOutcome outcome, params HealthFinding[] findings) =>
        new(Info(code), outcome, findings, null, null, [], TimeSpan.Zero);

    private static HealthCheckInfo Info(string code) =>
        new(code, HealthCheckRegistry.AreaOf(code[0])!.Value, HealthCostTier.Catalog, HealthGate.Database, "A check", null, typeof(object));
}
