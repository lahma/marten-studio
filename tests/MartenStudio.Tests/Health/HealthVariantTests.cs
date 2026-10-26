using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The variant model (API design §1.10), early, so that W5's new checks are born with it: a finding's key takes its
/// variant when it has one and is unchanged when it has none, and the sweep refuses a variant its check does not list.
/// </summary>
public class HealthVariantTests
{
    private static HealthFinding Finding(string? variant, string? subject) => new()
    {
        Code = "C08",
        Severity = HealthSeverity.Info,
        Title = "t",
        WhyItMatters = "w",
        Variant = variant,
        Subject = subject,
    };

    [Theory]
    [InlineData(null, null, "C08")]
    [InlineData(null, "temp-files", "C08|temp-files")]
    [InlineData("temp-files", null, "C08|temp-files")]
    [InlineData("deadlocks", "public.orders", "C08|deadlocks|public.orders")]
    public void The_key_is_code_then_variant_then_subject_and_unchanged_without_a_variant(string? variant, string? subject, string key)
    {
        Finding(variant, subject).Key.Should().Be(key);
    }

    [Fact]
    public void The_split_moves_a_kind_out_of_the_subject_and_a_kind_with_no_object_keeps_its_key()
    {
        // The API packet's split (C08, R01, S02, D02, T03): S02's pending-restart finding is about no one setting, so its kind
        // moved from the subject to the variant and its key - code, then the variant - is the one it always had. A setting's
        // finding gained the variant "setting" and keeps the setting as its subject, so its key moved (W5-API's mute-key note).
        HealthVerdict verdict = new DangerousSettingsCheck().Evaluate(
            new DangerousSettingsFacts(
                DateTimeOffset.UnixEpoch,
                [
                    new ServerSetting("shared_buffers", "16384", "configuration file", true),
                    new ServerSetting("fsync", "off", "configuration file", false),
                ],
                2,
                "Production",
                PostgresPlatform.SelfHosted),
            HealthEvaluation.At(DateTimeOffset.UnixEpoch));

        IReadOnlyList<HealthFinding> findings = verdict.ShouldBeWellFormedFor<DangerousSettingsCheck>().Findings;

        HealthFinding pending = findings.Single(static x => x.Variant == "pending-restart");
        pending.Subject.Should().BeNull();
        pending.Key.Should().Be("S02|pending-restart");

        HealthFinding fsync = findings.Single(static x => x.Variant == "setting");
        fsync.Subject.Should().Be("fsync");
        fsync.Key.Should().Be("S02|setting|fsync");
    }

    [Fact]
    public void The_evaluator_helper_catches_a_variant_the_check_does_not_list()
    {
        HealthVerdict verdict = HealthVerdict.Of(Finding("deadlocks", null) with { Code = "S02" });

        FluentActions.Invoking(() => verdict.ShouldBeWellFormedFor<DangerousSettingsCheck>())
            .Should().Throw<Exception>().WithMessage("*not one of its variants*");
    }

    [Fact]
    public void The_evaluator_helper_catches_a_finding_with_no_variant_from_a_check_that_lists_its_kinds()
    {
        // All or nothing (HealthCheckAttribute.Variants): a host reads a null variant as "the check has one kind".
        HealthVerdict verdict = HealthVerdict.Of(Finding(null, "fsync") with { Code = "S02" });

        FluentActions.Invoking(() => verdict.ShouldBeWellFormedFor<DangerousSettingsCheck>())
            .Should().Throw<Exception>().WithMessage("*no variant*");
    }

    [Fact]
    public async Task The_sweep_refuses_a_verdict_with_a_variant_its_check_does_not_list_and_the_row_names_no_subject()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Judge["K90"] = static (_, _) => HealthVerdict.Of(
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Info, Title = "One", WhyItMatters = "x", Variant = "temp-files", Subject = "acme-orders" });
        harness.Script.Finds("K91", HealthSeverity.Info);

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult result = sweep.Results.Single(static x => x.Check.Code == "K90");
        result.Outcome.Should().Be(HealthCheckOutcome.Failed);
        result.Problem!.Reason.Should().Contain("of a variant the check does not list").And.NotContain("acme").And.NotContain("temp-files");
        sweep.Results.Single(static x => x.Check.Code == "K91").Findings.Should().ContainSingle("one check's broken verdict never hides another's");
    }
}
