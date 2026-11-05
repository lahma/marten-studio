using Bunit;

using Marten;

using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// 1.2-PROJ (b): the Rebuild dialog names the error policy a rebuild runs under - the store's <c>Projections.RebuildErrors</c> -
/// and says so when it differs from the continuous agents' <c>Projections.Errors</c>, because a host that set
/// <c>SkipApplyErrors</c> for the live agents sees a rebuild stop where the live agent skipped.
/// </summary>
public class RebuildErrorPolicyTests
{
    private const string Policy = ".ms-rebuild-error-policy";

    private static RebuildErrorPolicy Policies(SkippedErrors rebuild, SkippedErrors continuous) => new(rebuild, continuous);

    [Fact]
    public void JasperFxs_defaults_stop_the_rebuild_on_everything_and_say_the_live_agents_skip_it_all()
    {
        RebuildErrorPolicy defaults = Policies(new SkippedErrors(false, false, false), new SkippedErrors(true, true, true));

        defaults.Sentence().Should().Be("Runs under RebuildErrors: apply errors, unknown events and serialization errors stop the rebuild.");
        defaults.DifferenceSentence().Should().Be(
            "Differs from Projections.Errors: the continuous agents skip apply errors, unknown events and serialization errors, "
            + "where this rebuild stops on them. A rebuild can therefore fail on an event the live agent got past.");
    }

    [Fact]
    public void A_mixed_policy_names_what_stops_and_what_is_skipped()
    {
        RebuildErrorPolicy mixed = Policies(new SkippedErrors(false, true, false), new SkippedErrors(false, true, false));

        mixed.Sentence().Should().Be("Runs under RebuildErrors: apply errors and serialization errors stop the rebuild; unknown events are skipped.");
        mixed.DifferenceSentence().Should().BeNull("the two policies agree");
    }

    [Fact]
    public void Skipping_everything_says_so()
    {
        RebuildErrorPolicy skipping = Policies(new SkippedErrors(true, true, true), new SkippedErrors(true, true, true));

        skipping.Sentence().Should().Be("Runs under RebuildErrors: apply errors, unknown events and serialization errors are skipped.");
        skipping.DifferenceSentence().Should().BeNull();
    }

    [Fact]
    public void A_rebuild_that_skips_what_the_live_agents_stop_on_says_that_instead()
    {
        RebuildErrorPolicy lenient = Policies(new SkippedErrors(true, false, false), new SkippedErrors(false, false, false));

        lenient.DifferenceSentence().Should().Be(
            "Differs from Projections.Errors: the continuous agents stop on apply errors, where this rebuild skips them.");
    }

    [Fact]
    public void Both_directions_at_once_are_two_clauses()
    {
        RebuildErrorPolicy crossed = Policies(new SkippedErrors(true, false, false), new SkippedErrors(false, true, false));

        crossed.DifferenceSentence().Should().Be(
            "Differs from Projections.Errors: the continuous agents skip unknown events, where this rebuild stops on them; "
            + "the continuous agents stop on apply errors, where this rebuild skips them."
            + " A rebuild can therefore fail on an event the live agent got past.");
    }

    /// <summary>Read off the store's options - memory only, no database - exactly as the host configured them.</summary>
    [Fact]
    public void The_policies_are_read_off_the_stores_options()
    {
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection("Host=marten-studio-rebuild-policy.invalid;Database=none;Username=none;Password=none");
            options.Projections.Errors.SkipApplyErrors = false;
            options.Projections.RebuildErrors.SkipUnknownEvents = true;
        });

        RebuildErrorPolicy read = RebuildErrorPolicy.Read(store)!;

        read.RebuildSkips.Should().Be(new SkippedErrors(false, true, false));
        read.ContinuousSkips.Should().Be(new SkippedErrors(false, true, true), "Errors defaults skip unknown events and serialization errors");
    }

    [Fact]
    public async Task The_dialog_names_the_policy_and_the_difference()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(".ms-projection-rebuild").Click();

        var row = page.WaitForElement(".ms-confirm-dialog " + Policy);
        row.QuerySelector(".ms-kv-key")!.TextContent.Should().Be("Error policy");
        row.QuerySelector(".ms-kv-value")!.TextContent.Should().Contain("Runs under RebuildErrors: apply errors, unknown events and serialization errors stop the rebuild.");
        row.QuerySelector(".ms-rebuild-error-policy-difference")!.TextContent
            .Should().Contain("the continuous agents skip apply errors, unknown events and serialization errors, where this rebuild stops on them");

        // 1.2-UX-FIX: the two sentences were run together ("...stop the rebuild.Differs from Projections.Errors").
        row.QuerySelector(".ms-kv-value")!.TextContent
            .Should().Contain("stop the rebuild. Differs from Projections.Errors:")
            .And.NotContain("rebuild.Differs");
    }

    [Fact]
    public async Task When_the_policies_agree_the_dialog_says_only_what_the_rebuild_runs_under()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.RebuildErrors = Policies(new SkippedErrors(true, true, true), new SkippedErrors(true, true, true));
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(".ms-projection-rebuild").Click();

        var row = page.WaitForElement(".ms-confirm-dialog " + Policy);
        row.TextContent.Should().Contain("Runs under RebuildErrors: apply errors, unknown events and serialization errors are skipped.");
        row.QuerySelectorAll(".ms-rebuild-error-policy-difference").Should().BeEmpty();
    }
}
