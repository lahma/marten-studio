using KStudio.Core.Export;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;

namespace KStudio.Tests.Services;

/// <summary>
/// The two capabilities M5 adds (AGENTS.md D45 and D46, D-M5 decision 1): both default to off, both need <c>SearchRecords</c>, the audit gate judges their
/// entries with it, and no profile and no combination of flags lets anything through that the flags did not grant.
/// </summary>
public class KafkaExportCapabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<List<StudioActionLogEntry>> Visible(KafkaStudioHarness harness) =>
        harness.Context.ActionLog.GetVisibleAsync(
            harness.Context.Services.GetRequiredService<StudioAuthorization>(),
            harness.Context.Services.GetRequiredService<StudioCapabilityGuard>(),
            cancellationToken: Ct);

    private static void Record(KafkaStudioHarness harness, string[] topics, KafkaCapability capability) =>
        harness.ActionLog.Record(KafkaStudioHarness.Primary, topics, capability.ToString(), string.Join(", ", topics), succeeded: true, "done", capability);

    // ---- the flags and the guard

    [Fact]
    public void The_two_new_flags_default_to_false_and_All_turns_them_on()
    {
        new KafkaStudioCapabilities().ExportRecords.Should().BeFalse();
        new KafkaStudioCapabilities().LiveTail.Should().BeFalse();
        KafkaStudioCapabilities.None().ExportRecords.Should().BeFalse();
        new KafkaStudioOptions().Capabilities.LiveTail.Should().BeFalse();
        KafkaStudioCapabilities.All().ExportRecords.Should().BeTrue();
        KafkaStudioCapabilities.All().LiveTail.Should().BeTrue();
    }

    [Fact]
    public void Prerequisites_name_SearchRecords_for_every_other_capability_and_nothing_for_SearchRecords()
    {
        KafkaCapabilityGuard.Prerequisites(KafkaCapability.SearchRecords).Should().BeEmpty();

        foreach (KafkaCapability capability in KafkaCapabilityGuard.Offered.Where(static x => x != KafkaCapability.SearchRecords))
        {
            KafkaCapabilityGuard.Prerequisites(capability).Should().Equal(KafkaCapability.SearchRecords);
        }
    }

    [Theory]
    [InlineData("ReadPayloads")]
    [InlineData("ExportRecords")]
    [InlineData("LiveTail")]
    public void Requiring_a_capability_names_SearchRecords_first_when_it_is_off(string name)
    {
        KafkaCapability capability = Enum.Parse<KafkaCapability>(name);
        using KafkaStudioHarness harness = new(options =>
        {
            options.Capabilities.ReadPayloads = true;
            options.Capabilities.ExportRecords = true;
            options.Capabilities.LiveTail = true;
        });
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();

        Action require = () => guard.Require(capability);

        require.Should().Throw<KafkaCapabilityDeniedException>().Which.Capability.Should().Be(KafkaCapability.SearchRecords);
    }

    [Fact]
    public void Requiring_a_capability_with_SearchRecords_on_names_the_capability_when_it_is_off()
    {
        using KafkaStudioHarness harness = new(static o => o.Capabilities.SearchRecords = true);
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();

        Action export = () => guard.Require(KafkaCapability.ExportRecords);

        export.Should().Throw<KafkaCapabilityDeniedException>().WithMessage(
            "KStudio refused 'ExportRecords' because KafkaStudioOptions.Capabilities.ExportRecords is false.");
    }

    [Fact]
    public void ReadOnly_turns_all_four_off()
    {
        using KafkaStudioHarness harness = new(static o => o.Capabilities = KafkaStudioCapabilities.All());
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();
        guard.Offered().Should().HaveCount(4);
        guard.Offered().Should().OnlyContain(c => guard.IsEnabled(c));

        harness.Context.Options.ReadOnly = true;

        guard.Offered().Should().OnlyContain(c => !guard.IsEnabled(c), "the host studio's master switch turns every capability off");
    }

    [Fact]
    public void A_host_that_sets_ExportRecords_or_LiveTail_without_SearchRecords_still_starts_and_every_use_is_refused()
    {
        using KafkaStudioHarness harness = new(static o => o.Capabilities.ExportRecords = true);
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();

        guard.IsConfigured(KafkaCapability.ExportRecords).Should().BeTrue();
        Action require = () => guard.Require(KafkaCapability.ExportRecords);

        require.Should().Throw<KafkaCapabilityDeniedException>().WithMessage("*SearchRecords*");
    }

    // ---- the audit gate

    [Theory]
    [InlineData("ExportRecords")]
    [InlineData("LiveTail")]
    public async Task An_entry_of_the_new_capabilities_is_judged_with_SearchRecords_per_topic(string name)
    {
        KafkaCapability capability = Enum.Parse<KafkaCapability>(name);
        using KafkaStudioHarness harness = new(static o => o.CapabilityAuthorizationPolicy = "kafka-search");
        harness.Options.Capabilities.ExportRecords = true;
        harness.Options.Capabilities.LiveTail = true;
        Record(harness, ["orders.v1"], capability);

        (await Visible(harness)).Should().BeEmpty("SearchRecords is off, so the entry would reveal a read the visitor may not make");

        harness.Options.Capabilities.SearchRecords = true;
        (await Visible(harness)).Should().ContainSingle("the capability and SearchRecords are on and both policies pass");

        harness.Authorization.AllowKafka(r => r.Capability != "SearchRecords");
        (await Visible(harness)).Should().BeEmpty("the capability policy refuses SearchRecords on the entry's topic");

        harness.Authorization.AllowKafka(r => r.Capability != capability.ToString());
        (await Visible(harness)).Should().BeEmpty("the capability policy refuses the capability itself");

        harness.Authorization.AllowKafka(static _ => true);
        (await Visible(harness)).Should().ContainSingle();

        harness.Context.Options.ReadOnly = true;
        (await Visible(harness)).Should().BeEmpty("the host's master switch turns both off");
    }

    [Fact]
    public async Task An_entry_of_the_new_capabilities_that_names_no_topic_is_judged_at_cluster_level_with_SearchRecords()
    {
        using KafkaStudioHarness harness = new(static o => o.CapabilityAuthorizationPolicy = "kafka-search");
        harness.Options.Capabilities.SearchRecords = true;
        harness.Options.Capabilities.ExportRecords = true;
        Record(harness, [], KafkaCapability.ExportRecords);

        (await Visible(harness)).Should().ContainSingle();

        harness.Authorization.AllowKafka(static r => r.Capability != "SearchRecords");
        (await Visible(harness)).Should().BeEmpty("SearchRecords is refused for the cluster");
    }

    // ---- a profile and the flags never grant

    [Fact]
    public async Task No_profile_and_none_of_the_sixteen_flag_combinations_lets_an_export_through_that_the_flags_did_not_grant()
    {
        foreach (KafkaProfile? profile in new KafkaProfile?[] { null, KafkaProfile.Development, KafkaProfile.Test, KafkaProfile.Production })
        {
            await using KafkaSearchRig rig = new(
                studio: static o => o.Capabilities.SearchRecords = true,
                profile: profile);
            rig.Topic("orders");
            rig.Produce("orders", 0, 10, "k", "{\"n\":1}");
            KafkaSearchService search = rig.Search();
            KafkaSearchPlanned planned = ((KafkaSearchPlanResult.Planned) await search.PlanAsync(rig.Input(["orders"]), Ct)).Value;
            await using KafkaSearchRun run = ((KafkaSearchStartResult.Running) await search.StartAsync(planned, confirmed: true, Ct)).Run;
            (KafkaScanResult result, List<KafkaMatch> matches) = await KafkaSearchRig.DrainAsync(run);
            KafkaCapabilityGuard guard = rig.Studio.Context.Services.GetRequiredService<KafkaCapabilityGuard>();
            KafkaExportService export = rig.Export();

            for (int mask = 0; mask < 16; mask++)
            {
                KafkaStudioCapabilities set = new()
                {
                    SearchRecords = (mask & 1) != 0,
                    ReadPayloads = (mask & 2) != 0,
                    ExportRecords = (mask & 4) != 0,
                    LiveTail = (mask & 8) != 0,
                };

                foreach (bool readOnly in new[] { false, true })
                {
                    rig.Studio.Options.Capabilities = set;
                    rig.Studio.Context.Options.ReadOnly = readOnly;
                    string where = $"profile {profile?.ToString() ?? "none"}, flags {mask}, ReadOnly {readOnly}";

                    KafkaCapability[] usable = [.. KafkaCapabilityGuard.Offered.Where(guard.IsEnabled)];
                    usable.Should().BeSubsetOf(KafkaCapabilityGuard.ConfiguredIn(set), where);

                    if (readOnly)
                    {
                        usable.Should().BeEmpty(where);
                    }

                    KafkaExportOutcome outcome = await export.ExportAsync(
                        new KafkaExportInput(rig.Entry, ["orders"], KafkaExportSource.Search, planned.Input.From, planned.Input.To, planned.Plan.AuditText, result.Records, result.Bytes, matches, KafkaExportFormat.JsonLines),
                        static (_, _, content, _) =>
                        {
                            content.Dispose();
                            return Task.FromResult(true);
                        },
                        Ct);

                    bool mayExport = !readOnly && set.SearchRecords && set.ExportRecords && profile != KafkaProfile.Production;
                    outcome.Delivered.Should().Be(mayExport, where);
                }
            }
        }
    }
}

internal static class KafkaCapabilityGuardTestExtensions
{
    /// <summary>Every capability KStudio offers, for a test that loops over them.</summary>
    public static IReadOnlyList<KafkaCapability> Offered(this KafkaCapabilityGuard guard) => KafkaCapabilityGuard.Offered;
}
