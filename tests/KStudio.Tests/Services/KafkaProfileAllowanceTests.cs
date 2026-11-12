using KStudio.Core.Clusters;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Services;

/// <summary>
/// What a profile changes in the search form's allowance line and in the sentence of a refused start (AGENTS.md D47, D-M5 decision 4.5): the line names
/// the allowance that is lower, and a refusal names the allowance that ran out - never the capped value of a plan whose own cluster still has room.
/// </summary>
public class KafkaProfileAllowanceTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Raised(KafkaEngineOptions options) => options.Limits.OwnerBytesPerHour = 50 * GiB;

    /// <summary>The owner of the rig's scans: a named visitor, because an anonymous one is a key made once per service instance.</summary>
    private static async Task<string> OwnerKeyAsync(KafkaSearchRig rig)
    {
        rig.Studio.Visitor.SignIn("ann");
        return (await new KafkaOwnerKey(rig.Studio.Visitor).OwnerAsync()).Key;
    }

    private static void Seed(KafkaSearchRig rig)
    {
        rig.Topic("orders");
        rig.Produce("orders", 0, 1, "k", "v");
    }

    [Fact]
    public async Task A_cluster_with_no_profile_has_the_process_allowance()
    {
        await using KafkaSearchRig rig = new(Raised);

        KafkaAllowance allowance = await rig.Search().AllowanceAsync(rig.Entry);

        allowance.PerHour.Should().Be(50 * GiB);
        allowance.Left.Should().Be(50 * GiB);
        allowance.ClusterProfile.Should().BeNull();
        (await rig.Search().OwnerBytesLeftThisHourAsync(rig.Entry)).Should().Be(50 * GiB);
        (await rig.Search().OwnerBytesLeftThisHourAsync()).Should().Be(50 * GiB, "no cluster named is the process's allowance alone");
    }

    [Fact]
    public async Task A_production_cluster_has_its_own_lower_allowance_and_the_line_names_the_profile()
    {
        await using KafkaSearchRig rig = new(Raised, profile: KafkaProfile.Production);

        KafkaAllowance allowance = await rig.Search().AllowanceAsync(rig.Entry);

        allowance.PerHour.Should().Be(512 * MiB);
        allowance.Left.Should().Be(512 * MiB);
        allowance.ClusterProfile.Should().Be(KafkaProfile.Production);
        KafkaStudioText.ClusterAllowance(allowance.Left, allowance.PerHour, KafkaProfile.Production)
            .Should().Be("512 MiB of this cluster's 512 MiB an hour left (Production profile)");
    }

    [Fact]
    public async Task What_was_read_on_the_cluster_lowers_the_clusters_remainder_and_the_processs_alike()
    {
        await using KafkaSearchRig rig = new(Raised, profile: KafkaProfile.Production);
        KafkaClusterRegistry registry = rig.Engine.Provider.GetRequiredService<KafkaClusterRegistry>();
        KafkaClusterCostControls costs = rig.Engine.Provider.GetRequiredService<KafkaClusterCostControls>();
        string owner = await OwnerKeyAsync(rig);

        costs.For(registry.Get(rig.Entry.Key)).OwnerBudget!.Add(owner, 100 * MiB);

        (await rig.Search().AllowanceAsync(rig.Entry)).Left.Should().Be(412 * MiB);
    }

    [Fact]
    public async Task A_host_whose_own_allowance_is_below_the_ceiling_keeps_it_and_the_process_is_the_lower()
    {
        await using KafkaSearchRig rig = new(static o => o.Limits.OwnerBytesPerHour = 100 * MiB, profile: KafkaProfile.Production);

        KafkaAllowance allowance = await rig.Search().AllowanceAsync(rig.Entry);

        allowance.PerHour.Should().Be(100 * MiB);
        allowance.ClusterProfile.Should().BeNull("the profile only lowers: the host's value is the lower, so the line is the process's");
    }

    [Fact]
    public async Task A_start_refused_because_the_clusters_own_allowance_is_spent_names_that_allowance()
    {
        await using KafkaSearchRig rig = new(Raised, profile: KafkaProfile.Production);
        Seed(rig);
        string owner = await OwnerKeyAsync(rig);
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], "key = 'k'"), Ct)).Value;
        KafkaClusterRegistry registry = rig.Engine.Provider.GetRequiredService<KafkaClusterRegistry>();
        rig.Engine.Provider.GetRequiredService<KafkaClusterCostControls>().For(registry.Get(rig.Entry.Key)).OwnerBudget!.Add(owner, 512 * MiB);

        KafkaSearchStartResult start = await search.StartAsync(planned, confirmed: true, Ct);

        start.Should().BeOfType<KafkaSearchStartResult.Refused>().Which.Text
            .Should().Be("You have read your hourly allowance on this cluster of 512 MiB. It frees up as the hour rolls on.");
    }

    [Fact]
    public async Task A_start_refused_because_the_process_allowance_is_spent_names_the_process_allowance_not_the_capped_one()
    {
        await using KafkaSearchRig rig = new(Raised, profile: KafkaProfile.Production);
        Seed(rig);
        string owner = await OwnerKeyAsync(rig);
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"], "key = 'k'"), Ct)).Value;
        rig.Engine.Provider.GetRequiredService<OwnerByteBudget>().Add(owner, 50 * GiB);

        KafkaSearchStartResult start = await search.StartAsync(planned, confirmed: true, Ct);

        start.Should().BeOfType<KafkaSearchStartResult.Refused>().Which.Text
            .Should().Be("You have read your hourly allowance of 50 GiB. It frees up as the hour rolls on.")
            .And.NotContain("on this cluster");
    }

    // ---- the engine says which allowance ran out

    [Fact]
    public async Task The_engines_refusal_carries_the_allowance_that_is_spent_the_clusters_or_the_processs()
    {
        await using KafkaSearchRig rig = new(Raised, profile: KafkaProfile.Production);
        Seed(rig);
        string owner = await OwnerKeyAsync(rig);
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await rig.Search().PlanAsync(rig.Input(["orders"], "key = 'k'"), Ct)).Value;
        IKafkaScanner scanner = rig.Engine.Provider.GetRequiredService<IKafkaScanner>();
        KafkaScanOwner scanOwner = new(owner);
        KafkaClusterRegistry registry = rig.Engine.Provider.GetRequiredService<KafkaClusterRegistry>();
        OwnerByteBudget own = rig.Engine.Provider.GetRequiredService<KafkaClusterCostControls>().For(registry.Get(rig.Entry.Key)).OwnerBudget!;
        OwnerByteBudget process = rig.Engine.Provider.GetRequiredService<OwnerByteBudget>();

        own.Add(owner, 512 * MiB);
        KafkaScanStart cluster = scanner.Start(planned.Plan, scanOwner, confirmed: true);

        cluster.Refusal.Should().Be(KafkaScanRefusal.OwnerBudgetSpent);
        cluster.SpentBudget.Should().Be(new KafkaSpentBudget(KafkaBudgetScope.Cluster, 512 * MiB));

        process.Add(owner, 50 * GiB);
        KafkaScanStart both = scanner.Start(planned.Plan, scanOwner, confirmed: true);

        both.SpentBudget.Should().Be(new KafkaSpentBudget(KafkaBudgetScope.Process, 50 * GiB), "the process's allowance stops every cluster, so it is the one named");
    }

    [Fact]
    public async Task A_record_read_refused_for_the_allowance_names_the_one_that_is_spent()
    {
        await using KafkaSearchRig rig = new(Raised, profile: KafkaProfile.Production);
        Seed(rig);
        string owner = await OwnerKeyAsync(rig);
        KafkaClusterRegistry registry = rig.Engine.Provider.GetRequiredService<KafkaClusterRegistry>();
        OwnerByteBudget own = rig.Engine.Provider.GetRequiredService<KafkaClusterCostControls>().For(registry.Get(rig.Entry.Key)).OwnerBudget!;

        own.Add(owner, 512 * MiB);
        KafkaRecordPageResult cluster = await rig.Records().ReadAsync(rig.Entry, "orders", 0, 0, Ct);

        cluster.Should().BeOfType<KafkaRecordPageResult.Read>().Which.Value.Text
            .Should().Be("You have read your hourly allowance on this cluster of 512 MiB. It frees up as the hour rolls on.");

        rig.Engine.Provider.GetRequiredService<OwnerByteBudget>().Add(owner, 50 * GiB);
        KafkaRecordPageResult process = await rig.Records().ReadAsync(rig.Entry, "orders", 0, 0, Ct);

        process.Should().BeOfType<KafkaRecordPageResult.Read>().Which.Value.Text
            .Should().Be("You have read your hourly allowance of 50 GiB. It frees up as the hour rolls on.");
    }

    [Fact]
    public async Task A_scan_stopped_by_the_process_allowance_says_so_without_naming_the_cluster()
    {
        await using KafkaSearchRig rig = new(static o => o.Limits.OwnerBytesPerHour = 2 * MiB);
        rig.Topic("big");
        string value = new('x', 60_000);

        for (int i = 0; i < 60; i++)
        {
            rig.Produce("big", 0, i, "k" + i, value);
        }

        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["big"]), Ct)).Value;
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(planned, confirmed: true, Ct)).Run;

        (KafkaScanResult result, _) = await KafkaSearchRig.DrainAsync(run);

        result.StopReason.Should().Be(KafkaScanStopReason.OwnerBudget);
        result.SpentBudget.Should().Be(new KafkaSpentBudget(KafkaBudgetScope.Process, 2 * MiB));
        KafkaSearchText.Stop(result, planned.Plan.Limits).Should().Contain("you have read your hourly allowance of 2 MiB").And.NotContain("on this cluster");
    }

    [Fact]
    public async Task A_scan_stopped_by_the_clusters_own_allowance_says_so()
    {
        await using KafkaSearchRig rig = new(Raised, profile: KafkaProfile.Production);
        rig.Topic("big");
        string value = new('x', 60_000);

        for (int i = 0; i < 60; i++)
        {
            rig.Produce("big", 0, i, "k" + i, value);
        }

        string owner = await OwnerKeyAsync(rig);
        KafkaClusterRegistry registry = rig.Engine.Provider.GetRequiredService<KafkaClusterRegistry>();

        // 1 MiB left of the cluster's 512 MiB; the process's 50 GiB has plenty.
        rig.Engine.Provider.GetRequiredService<KafkaClusterCostControls>().For(registry.Get(rig.Entry.Key)).OwnerBudget!.Add(owner, 511 * MiB);
        KafkaSearchService search = rig.Search();
        KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["big"]), Ct)).Value;
        await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(planned, confirmed: true, Ct)).Run;

        (KafkaScanResult result, _) = await KafkaSearchRig.DrainAsync(run);

        result.StopReason.Should().Be(KafkaScanStopReason.OwnerBudget);
        result.SpentBudget.Should().Be(new KafkaSpentBudget(KafkaBudgetScope.Cluster, 512 * MiB));
        KafkaSearchText.Stop(result, planned.Plan.Limits).Should().Contain("you have read your hourly allowance on this cluster of 512 MiB");
    }

    [Fact]
    public void A_refusal_that_does_not_say_which_allowance_names_no_number()
    {
        KafkaStudioText.Refusal(KafkaScanRefusal.OwnerBudgetSpent, new KafkaScanLimits())
            .Should().Be("You have read your hourly allowance. It frees up as the hour rolls on.");
    }

    [Fact]
    public void The_text_for_a_profile_refusal_names_the_setting()
    {
        string text = KafkaStudioText.Refusal(KafkaScanRefusal.ProfileForbids, new KafkaScanLimits());

        text.Should().Contain("KafkaClusterBuilder.Profile").And.Contain("Production").And.Contain("even when they are granted");
        text.Should().NotBe(KafkaStudioText.SearchFailed, "the default arm is not what a profile refusal says");
    }

    [Fact]
    public void Every_refusal_the_engine_can_make_has_a_sentence_of_its_own()
    {
        foreach (KafkaScanRefusal refusal in Enum.GetValues<KafkaScanRefusal>())
        {
            KafkaStudioText.Refusal(refusal, new KafkaScanLimits()).Should().NotBe(KafkaStudioText.SearchFailed, refusal.ToString());
        }
    }
}
