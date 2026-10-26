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
    public void No_shipped_finding_sets_a_variant_so_no_mute_key_moves()
    {
        // The five checks whose subject still carries a kind (C08, R01, S02, D02, T03) keep their keys until the API packet
        // splits them; this is S02's pending-restart finding, keyed as it always was.
        HealthVerdict verdict = new DangerousSettingsCheck().Evaluate(
            new DangerousSettingsFacts(
                DateTimeOffset.UnixEpoch,
                [new ServerSetting("shared_buffers", "16384", "configuration file", true)],
                1,
                "Production",
                PostgresPlatform.SelfHosted),
            HealthEvaluation.At(DateTimeOffset.UnixEpoch));

        HealthFinding pending = verdict.ShouldBeWellFormedFor<DangerousSettingsCheck>().Findings.Single(static x => x.Subject == "pending-restart");
        pending.Variant.Should().BeNull();
        pending.Key.Should().Be("S02|pending-restart");
    }

    [Fact]
    public void The_evaluator_helper_catches_a_variant_the_check_does_not_list()
    {
        HealthVerdict verdict = HealthVerdict.Of(Finding("deadlocks", null) with { Code = "S02" });

        FluentActions.Invoking(() => verdict.ShouldBeWellFormedFor<DangerousSettingsCheck>())
            .Should().Throw<Exception>().WithMessage("*lists no variants*");
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
