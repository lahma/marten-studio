using MartenStudio.Services;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// PS-1e: a health check that reads a source through a studio's own types - MartenStudio's read the store, its options and
/// its daemon - is never run against another kind of source. It is not a "could not check" row there; it is no row at all.
/// </summary>
public class HealthSourceKindTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly IReadOnlySet<StudioSourceKind> DataSourceOnly = new HashSet<StudioSourceKind> { StudioSourceKind.DataSource };

    [Fact]
    public async Task A_check_restricted_to_Marten_stores_is_not_run_against_a_plain_source_and_has_no_row()
    {
        await using HealthHarness harness = HealthHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck), typeof(AnotherScriptedConfigurationCheck)],
            sourceKinds: static type => type == typeof(ScriptedConfigurationCheck) ? MartenStudioDescriptor.MartenStoreOnly : null);

        ResolvedSource plain = new(
            new StudioScope("plain", harness.Scope.DatabaseId, null),
            new StudioSourceRegistration("plain", "Plain", StudioSourceKind.DataSource),
            MartenStudioDatabase.Of(harness.Store.Storage.Database));

        HealthSweep overPlain = await harness.Sweeper.SweepAsync(plain, HealthSweepKind.Regular, Token);

        overPlain.Results.Select(static x => x.Check.Code).Should().Equal(["K91"], "K90 reads a Marten store, and this source is not one");
        harness.Script.Runs.Should().NotContainKey("K90", "a check that does not run against the source is not gathered either");

        HealthSweep overMarten = await harness.SweepAsync();

        overMarten.Results.Select(static x => x.Check.Code).Should().BeEquivalentTo(["K90", "K91"], "both run against a Marten store");
        overMarten.Results.Should().OnlyContain(static x => x.Outcome == HealthCheckOutcome.Checked);
    }

    [Fact]
    public void A_check_found_by_several_entries_runs_only_against_a_kind_every_one_of_them_admits()
    {
        StudioHealthCheckAssembly[] shared = [new(typeof(HealthSourceKindTests).Assembly)];
        StudioHealthCheckAssembly[] marten = [.. shared, new(typeof(HealthSourceKindTests).Assembly, MartenStudioDescriptor.MartenStoreOnly)];
        StudioHealthCheckAssembly[] disjoint = [.. marten, new(typeof(HealthSourceKindTests).Assembly, DataSourceOnly)];

        Kinds(shared).Should().OnlyContain(static x => x == null, "an unrestricted entry admits every kind");
        Kinds(marten).Should().OnlyContain(static x => x != null && x.SetEquals(MartenStudioDescriptor.MartenStoreOnly));
        Kinds(disjoint).Should().OnlyContain(static x => x != null && x.Count == 0, "no kind is admitted by all three");

        Kinds(shared).Should().NotBeEmpty("the test assembly declares scripted checks, so the rule is not judged over nothing");

        static IEnumerable<IReadOnlySet<StudioSourceKind>?> Kinds(StudioHealthCheckAssembly[] entries) =>
            HealthCheckRegistry.DiscoverAll(entries).Select(static x => x.SourceKinds);
    }

    [Fact]
    public void A_registry_offers_a_costlier_tier_only_where_a_check_of_it_runs_against_the_source()
    {
        HealthCheckRegistry registry = HealthCheckRegistry.ForTypes(
            [(typeof(ScriptedDeeperCheck), MartenStudioDescriptor.MartenStoreOnly), (typeof(ScriptedConfigurationCheck), null)]);

        registry.HasTier(HealthCostTier.BoundedData, StudioSourceKind.MartenStore).Should().BeTrue();
        registry.HasTier(HealthCostTier.BoundedData, StudioSourceKind.DataSource).Should().BeFalse(
            "a Run deeper checks button over a plain source would run nothing");
        registry.HasTier(HealthCostTier.InProcess, StudioSourceKind.DataSource).Should().BeTrue();
    }

    /// <summary>
    /// MartenStudio's own registry: every check runs against a Marten store, and every check in MartenStudio's own
    /// assembly runs against nothing else. While the shared engine and MartenStudio are one assembly (before PS-2) that is
    /// all 49, the shared checks included - every source MartenStudio has is a Marten store; from the move on, the 23 in
    /// the shared assembly run against every kind and MartenStudio's against Marten stores alone: 39 shared (23, W5-V1's
    /// T16 and T24, W5-CAP's C11, W5-R2's R02-R04 and W5-S2's S04-S10 and K05-K07) and 28 MartenStudio's (26, W5's D09 and
    /// W5-S2's K08).
    /// </summary>
    [Fact]
    public void MartenStudios_own_checks_run_against_Marten_stores_only()
    {
        HealthCheckRegistry registry = StudioHealthChecks.Registry;
        HealthCheckDescriptor[] marten = [.. registry.All.Where(static x => x.Info.CheckType.Assembly == typeof(MartenStudioDescriptor).Assembly)];
        HealthCheckDescriptor[] restricted = [.. registry.All.Where(static x => x.SourceKinds is not null)];

        // 50; W5-V1's T16 and T24, W5-CAP's C11, W5-R2's R02-R04 and W5-S2's ten in the shared engine; and W5-S2's K08.
        registry.All.Should().HaveCount(67);
        registry.All.Should().OnlyContain(static x => x.RunsAgainst(StudioSourceKind.MartenStore));
        marten.Should().NotBeEmpty().And.OnlyContain(
            static x => !x.RunsAgainst(StudioSourceKind.DataSource) && !x.RunsAgainst(StudioSourceKind.HostProvider));

        if (typeof(HealthCheckRegistry).Assembly == typeof(MartenStudioDescriptor).Assembly)
        {
            restricted.Should().HaveCount(67, "one assembly: a check found by both entries runs against what both admit");
        }
        else
        {
            marten.Should().HaveCount(28, "MartenStudio keeps 26 of the 49 checks (design Appendix B), and W5 adds D09 and K08; W5-S2's S04-S10 and K05-K07 are shared");
            restricted.Should().BeEquivalentTo(marten,
                "the shared engine's 39 (23, W5-V1's T16 and T24, W5-CAP's C11, W5-R2's R02-R04 and W5-S2's ten) run against every kind of source");
        }
    }
}
