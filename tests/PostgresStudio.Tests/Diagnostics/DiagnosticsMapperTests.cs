using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The internal health model as the public read model says it (API design §1.4, §5.3): every internal enum member has a public
/// value - a theory over every member, so one added without a mapping fails here - and every public field comes from its
/// source.
/// </summary>
public class DiagnosticsMapperTests
{
    // By name: the internal enums cannot be a public theory's parameters.
    public static TheoryData<string> Severities => [.. Enum.GetNames<HealthSeverity>()];

    public static TheoryData<string> Areas => [.. Enum.GetNames<HealthArea>()];

    public static TheoryData<string> RemedyKinds => [.. Enum.GetNames<HealthRemedyKind>()];

    public static TheoryData<string> Outcomes => [.. Enum.GetNames<HealthCheckOutcome>()];

    public static TheoryData<string> Ownerships => [.. Enum.GetNames<StudioTableOwnership>()];

    [Theory]
    [MemberData(nameof(Severities))]
    public void Every_severity_has_a_public_value_with_the_same_name(string name) =>
        DiagnosticsMapper.Severity(Enum.Parse<HealthSeverity>(name)).ToString().Should().Be(name);

    [Theory]
    [MemberData(nameof(Areas))]
    public void Every_area_has_a_public_category_with_the_same_name(string name) =>
        DiagnosticsMapper.Category(Enum.Parse<HealthArea>(name)).ToString().Should().Be(name);

    [Theory]
    [MemberData(nameof(RemedyKinds))]
    public void Every_remedy_kind_has_a_public_action_kind(string name) =>
        Enum.IsDefined(DiagnosticsMapper.ActionKind(Enum.Parse<HealthRemedyKind>(name))).Should().BeTrue();

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Every_outcome_has_a_public_value(string name)
    {
        HealthCheckOutcome outcome = Enum.Parse<HealthCheckOutcome>(name);
        Enum.IsDefined(DiagnosticsMapper.Outcome(outcome)).Should().BeTrue();

        foreach (HealthUnavailableReason reason in Enum.GetValues<HealthUnavailableReason>())
        {
            Enum.IsDefined(DiagnosticsMapper.Outcome(outcome, reason)).Should().BeTrue();
        }
    }

    [Theory]
    [MemberData(nameof(Ownerships))]
    public void Every_ownership_has_a_public_value(string name) =>
        Enum.IsDefined(DiagnosticsMapper.Ownership(Enum.Parse<StudioTableOwnership>(name))).Should().BeTrue();

    [Fact]
    public void The_five_remedy_kinds_are_frozen_with_the_values_the_json_spells()
    {
        // schemaVersion 1's actionKind vocabulary (the orchestrator's note: all five frozen together).
        new[] { HealthRemedyKind.Configuration, HealthRemedyKind.Migration, HealthRemedyKind.ServerSetting, HealthRemedyKind.ManagedPlatform, HealthRemedyKind.Repair }
            .Select(static x => (Value: (int) DiagnosticsMapper.ActionKind(x), Json: DiagnosticsJson.Name(DiagnosticsMapper.ActionKind(x))))
            .Should().Equal((0, "applicationConfiguration"), (1, "migration"), (2, "serverSetting"), (3, "managedPlatform"), (4, "repair"));
    }

    [Fact]
    public void An_unavailable_check_out_of_time_is_timed_out_and_one_the_host_chose_is_unavailable()
    {
        DiagnosticsMapper.Outcome(HealthCheckOutcome.Unavailable, HealthUnavailableReason.TimedOut).Should().Be(PostgresReadOutcome.TimedOut);
        DiagnosticsMapper.Outcome(HealthCheckOutcome.Unavailable, HealthUnavailableReason.SweepBudget).Should().Be(PostgresReadOutcome.TimedOut);
        DiagnosticsMapper.Outcome(HealthCheckOutcome.Unavailable, HealthUnavailableReason.HostChoice).Should().Be(PostgresReadOutcome.Unavailable);
        DiagnosticsMapper.Outcome(HealthCheckOutcome.Checked).Should().Be(PostgresReadOutcome.Read);
        DiagnosticsMapper.Outcome(HealthCheckOutcome.NotApplicable).Should().Be(PostgresReadOutcome.NotApplicable);
        DiagnosticsMapper.Outcome(HealthCheckOutcome.Failed).Should().Be(PostgresReadOutcome.Failed);
    }

    [Fact]
    public void Every_public_field_of_a_finding_comes_from_its_source()
    {
        HealthCheckInfo info = Info("T03", HealthArea.Tables, variants: ["index", "partitions"]);
        HealthFinding finding = Finding(info, "Index orders_idx is invalid", "public.orders_idx", "index") with
        {
            Severity = HealthSeverity.Critical,
            Caveat = "Read from pg_index.",
            Evidence = [new HealthEvidence("Index", "orders_idx"), new HealthEvidence("Size", "8.0 kB")],
            Remedy = HealthRemedy.ForMigration("REINDEX INDEX CONCURRENTLY \"public\".\"orders_idx\";", "Rebuilds it."),
        };

        PostgresFinding mapped = DiagnosticsMapper.Finding(finding, info, Gate(false), NoStudioRuntime.Instance, "store", new PostgresStudio.Services.StudioScope("main", "db1", null));

        mapped.Code.Should().Be("T03");
        mapped.Variant.Should().Be("index");
        mapped.Subject.Should().Be("public.orders_idx");
        mapped.Severity.Should().Be(PostgresSeverity.Critical);
        mapped.Category.Should().Be(PostgresFindingCategory.Tables);
        mapped.Title.Should().Be(finding.Title);
        mapped.Detail.Should().Be(finding.WhyItMatters);
        mapped.Caveat.Should().Be("Read from pg_index.");
        mapped.Action.Should().Be(finding.Remedy.Code);
        mapped.ActionExplanation.Should().Be(finding.Remedy.Explanation);
        mapped.ActionKind.Should().Be(PostgresActionKind.Migration);
        mapped.ActionRequires.Should().BeNull("the remedy needs nothing this host lacks");
        mapped.Evidence.Should().Equal(new KeyValuePair<string, string>("Index", "orders_idx"), new KeyValuePair<string, string>("Size", "8.0 kB"));
        mapped.DocsUrl.Should().Be(info.DocsUrl);
        mapped.MeasuredAt.Should().Be(At);
        mapped.Key.Should().Be(finding.Key, "the public key is the studio's own, so a host's dedupe and a visitor's mute agree");
    }

    [Theory]
    [InlineData(null, null, "C08")]
    [InlineData("deadlocks", null, "C08|deadlocks")]
    [InlineData(null, "public.orders", "C08|public.orders")]
    [InlineData("index", "public.orders_idx", "C08|index|public.orders_idx")]
    public void The_key_is_code_variant_and_subject_and_stable(string? variant, string? subject, string key)
    {
        PostgresFinding finding = new() { Code = "C08", Variant = variant, Subject = subject };

        finding.Key.Should().Be(key);
        (finding with { Title = "another wording" }).Key.Should().Be(key, "the English text never moves the key");
    }

    [Fact]
    public void A_check_short_of_the_host_gate_says_withheld_and_nothing_more()
    {
        HealthCheckInfo info = Info("C04", HealthArea.Connections, HealthGate.Activity);
        HealthCheckResult result = Result(
            info, HealthCheckOutcome.Failed, new HealthCheckProblem("The check failed.", "XX000", "postgres said secret_schema"), "a note");

        PostgresCheck withheld = DiagnosticsMapper.Check(result, Gate(false), 0);

        withheld.Outcome.Should().Be(PostgresReadOutcome.Withheld);
        withheld.Reason.Should().BeNull();
        withheld.SqlState.Should().BeNull();
        withheld.RequiresActivity.Should().BeTrue();

        PostgresCheck shown = DiagnosticsMapper.Check(result, Gate(true), 0);

        shown.Outcome.Should().Be(PostgresReadOutcome.Failed);
        shown.Reason.Should().Be("The check failed.", "the studio's own sentence");
        shown.SqlState.Should().Be("XX000");
        System.Text.Json.JsonSerializer.Serialize(shown).Should().NotContain("secret_schema", "Postgres' own words are never carried");
    }

    [Fact]
    public void A_check_that_did_not_run_gives_its_note_as_the_reason()
    {
        HealthCheckInfo info = Info("K02", HealthArea.Configuration, HealthGate.None);

        DiagnosticsMapper.Check(Result(info, HealthCheckOutcome.NotApplicable, note: "Needs Marten 9.47+; this host runs Marten 9.39.0."), Gate(false), 0)
            .Should().BeEquivalentTo(new { Outcome = (PostgresReadOutcome?) PostgresReadOutcome.NotApplicable, Reason = "Needs Marten 9.47+; this host runs Marten 9.39.0." });

        DiagnosticsMapper.Check(Result(info, note: "Nothing to say."), Gate(false), 0).Reason
            .Should().BeNull("a checked row's note is the page's, beside its findings; a reason is why a check did not check");
    }

    [Fact]
    public void The_catalogue_copies_a_checks_variants_so_no_result_shares_the_registrys_list()
    {
        HealthCheckInfo info = Info("C08", HealthArea.Connections, variants: ["deadlocks", "rollbacks"]);

        PostgresCheck check = DiagnosticsMapper.Check(info);

        check.Variants.Should().Equal("deadlocks", "rollbacks");
        check.Variants.Should().NotBeSameAs(info.Variants);
        check.Outcome.Should().BeNull("the catalogue has no outcome");
    }
}
