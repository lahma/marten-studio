using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Projections;

using Npgsql;

using PostgresStudio.Services.Health;

using static MartenStudio.Tests.Health.HighWaterPinnedCheckTests;

namespace MartenStudio.Tests.Health;

/// <summary>
/// D05's <c>ApplyEvent</c> remedy, per case. From JasperFx.Events 2.93 (Marten 9.52) a database error in projection code is
/// attributed to its event and never dead-lettered, so <c>SkipApplyErrors</c> is not its remedy: dropped where the daemon
/// here says the failure is one, kept as it was where it says it is not, kept with the exception named where nothing can
/// say - and below 2.93 the remedy every version had.
/// </summary>
/// <remarks>
/// Each case is judged against a made-up runtime, so all of them run at the floor and at the latest alike. Only the live
/// classifier itself (<see cref="The_live_classifier_is_JasperFxs_own_and_answers_only_where_it_exists" />) depends on what
/// this run resolved.
/// </remarks>
public class ShardFailedOrPausedDatabaseErrorTests
{
    private const string Shard = "ShipmentTracker:All";

    private const string SkipApplyErrors = "opts.Projections.Errors.SkipApplyErrors = true;";

    /// <summary>Marten 9.51.0 with what it brings: no systemic classification.</summary>
    private static readonly MartenRuntime Below = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
    {
        [RuntimePackage.Marten] = "9.51.0",
        [RuntimePackage.JasperFxEvents] = "2.91.0",
    });

    /// <summary>Marten 9.52.0 with what it brings: JasperFx.Events 2.93.1.</summary>
    private static readonly MartenRuntime From = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
    {
        [RuntimePackage.Marten] = "9.52.0",
        [RuntimePackage.JasperFxEvents] = "2.93.1",
    });

    private static HealthFinding Judge(MartenRuntime runtime, IReadOnlyList<DaemonShardRow>? rows, DaemonInProcess? inProcess = null) =>
        new ShardFailedOrPausedCheck()
            .Evaluate(
                new ShardFailedOrPausedFacts(DaemonConfiguration(extended: rows is not null), rows, inProcess ?? DaemonInProcess.NotHosted),
                HealthEvaluation.At(Now, runtime))
            .Findings.Should().ContainSingle().Subject;

    private static DaemonShardRow PersistedApplyError() =>
        new(Shard, 40, Now, Now - TimeSpan.FromMinutes(1), "Paused", "42703: column \"weight\" does not exist", "ApplyEvent", 41, "item_added");

    private static DaemonInProcess Hosted(bool? systemic, string? root) => DaemonInProcess.NotHosted with
    {
        Hosting = DaemonHostingState.Hosted,
        IsRunning = true,
        Agents =
        [
            new DaemonAgentFact(Shard, "Paused", "ApplyEvent", "boom", Now - TimeSpan.FromSeconds(20), 40, 41, "item_added", systemic, root),
        ],
    };

    [Fact]
    public void Below_JasperFx_2_93_the_apply_error_remedy_is_the_one_every_version_had()
    {
        HealthFinding finding = Judge(Below, [PersistedApplyError()]);

        finding.Remedy!.Code.Should().Be(SkipApplyErrors);
        finding.Remedy.Explanation.Should().Be(ShardFailedOrPausedCheck.SkipApplyErrorsExplanation);
        finding.Evidence.Should().NotContain(static x => x.Label == "Kind of failure");
    }

    /// <summary>Below 2.93 a database error never reached ApplyEvent, so even a fact that says one did changes nothing.</summary>
    [Fact]
    public void Below_JasperFx_2_93_a_classification_that_says_database_error_is_not_believed()
    {
        HealthFinding finding = Judge(Below, null, Hosted(systemic: true, "Npgsql.PostgresException"));

        finding.Remedy!.Code.Should().Be(SkipApplyErrors);
        finding.Remedy.Explanation.Should().Be(ShardFailedOrPausedCheck.SkipApplyErrorsExplanation);
        finding.Evidence.Should().NotContain(static x => x.Label == "Kind of failure");
    }

    /// <summary>A row read back from mt_event_progression keeps only a category and a text: the remedy names the exception.</summary>
    [Fact]
    public void From_9_52_a_persisted_apply_error_keeps_the_switch_and_names_the_database_error_it_does_not_skip()
    {
        HealthFinding finding = Judge(From, [PersistedApplyError()]);

        finding.Remedy!.Code.Should().Be(SkipApplyErrors);
        finding.Remedy.Explanation.Should().Be(
            ShardFailedOrPausedCheck.SkipApplyErrorsExplanation + ShardFailedOrPausedCheck.DatabaseErrorExclusion);
        finding.Remedy.Explanation.Should().Contain("never dead-lettered");
        finding.Evidence.Should().NotContain(static x => x.Label == "Kind of failure");
    }

    [Fact]
    public void From_9_52_a_failure_the_daemon_here_calls_a_database_error_has_no_skip_switch_and_says_what_to_fix()
    {
        HealthFinding finding = Judge(From, null, Hosted(systemic: true, "Npgsql.PostgresException"));

        finding.Remedy.Should().BeNull("SkipApplyErrors does not skip a systemic database failure, so offering it would be wrong");
        HealthEvidence kind = finding.Evidence.Should().ContainSingle(static x => x.Label == "Kind of failure").Subject;
        kind.Value.Should().Contain("Npgsql.PostgresException").And.Contain("Fix the schema or the grant");
        finding.Evidence.Select(static x => x.Label).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void From_9_52_a_failure_the_daemon_here_says_is_not_a_database_error_keeps_the_switch_as_it_was()
    {
        HealthFinding finding = Judge(From, null, Hosted(systemic: false, "System.InvalidOperationException"));

        finding.Remedy!.Code.Should().Be(SkipApplyErrors);
        finding.Remedy.Explanation.Should().Be(ShardFailedOrPausedCheck.SkipApplyErrorsExplanation);
        finding.Evidence.Should().NotContain(static x => x.Label == "Kind of failure");
    }

    /// <summary>The daemon here, when it cannot tell, is no better than the row: the switch with the exception named.</summary>
    [Fact]
    public void From_9_52_a_failure_the_daemon_here_cannot_classify_keeps_the_switch_with_the_exception_named()
    {
        HealthFinding finding = Judge(From, null, Hosted(systemic: null, root: null));

        finding.Remedy!.Code.Should().Be(SkipApplyErrors);
        finding.Remedy.Explanation.Should().EndWith(ShardFailedOrPausedCheck.DatabaseErrorExclusion);
    }

    /// <summary>A category other than ApplyEvent is untouched by any of this.</summary>
    [Fact]
    public void From_9_52_a_serialization_failure_keeps_its_own_switch()
    {
        DaemonShardRow row = new(Shard, 40, Now, Now, "Paused", "cannot read", "EventSerialization", 41, "item_added");

        Judge(From, [row]).Remedy!.Code.Should().Be("opts.Projections.Errors.SkipSerializationErrors = true;");
    }

    /// <summary>
    /// The classified-failure half: a <c>ShardFailure</c>'s root type is resolved among the loaded assemblies, and only a
    /// <c>DbException</c> counts.
    /// </summary>
    [Theory]
    [InlineData("Npgsql.PostgresException", true)]
    [InlineData("Npgsql.NpgsqlException", true)]
    [InlineData("System.InvalidOperationException", false)]
    [InlineData("Not.A.Type", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_root_exception_type_counts_as_a_database_error_only_when_it_names_a_DbException(string? typeName, bool expected)
    {
        _ = typeof(PostgresException);

        DaemonLightUp.IsDatabaseExceptionType(typeName).Should().Be(expected);
    }

    /// <summary>
    /// The live half is JasperFx's own <c>IsSystemicDatabaseFailure</c>, reached by reflection: it answers where this run's
    /// JasperFx.Events has it (2.93+) and is <see langword="null" /> below - so at the floor nothing is classified.
    /// </summary>
    [Fact]
    public void The_live_classifier_is_JasperFxs_own_and_answers_only_where_it_exists()
    {
        var undefinedColumn = new PostgresException("column \"weight\" does not exist", "ERROR", "ERROR", PostgresErrorCodes.UndefinedColumn);
        var wrapped = new InvalidOperationException("the projection failed", undefinedColumn);
        var notDatabase = new InvalidOperationException("SKU 'POISON' cannot ship");

        if (RuntimeFeature.SystemicDatabaseFailure.IsAvailable(MartenRuntime.Current))
        {
            DaemonLightUp.IsSystemicDatabaseFailure(undefinedColumn).Should().BeTrue("42703 is permanent: the next event fails the same way");
            DaemonLightUp.IsSystemicDatabaseFailure(wrapped).Should().BeTrue("JasperFx walks the exception graph");
            DaemonLightUp.IsSystemicDatabaseFailure(notDatabase).Should().BeFalse();
        }
        else
        {
            DaemonLightUp.IsSystemicDatabaseFailure(undefinedColumn).Should().BeNull("JasperFx.Events below 2.93 has no such classifier");
            DaemonLightUp.IsSystemicDatabaseFailure((JasperFx.Events.Daemon.ShardFailure?) null).Should().BeNull();
        }
    }
}
