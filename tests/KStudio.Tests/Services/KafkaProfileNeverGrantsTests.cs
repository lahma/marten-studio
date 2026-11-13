using KStudio.Core.Clusters;
using KStudio.Core.Export;
using KStudio.Core.Scanning;
using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Services;

/// <summary>
/// A profile never grants (AGENTS.md D47, D-M5 decision 4.7): for every profile and every one of the sixteen combinations of the four capabilities, what is
/// usable on the cluster is a subset of what the host switched on - and empty under the host studio's <c>ReadOnly</c> - so that a later limit, capability or
/// profile cannot add a way through. Held where the operation is: the real export service is asked for every case and delivers only what the flags allow.
/// </summary>
public class KafkaProfileNeverGrantsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What a cluster with this profile lets a capability do at all, whatever the host granted: the profile only ever forbids.</summary>
    private static bool ProfileForbids(KafkaProfile? profile, KafkaCapability capability) => capability switch
    {
        KafkaCapability.ExportRecords => KafkaProfileCeilings.ForbidsExport(profile),
        KafkaCapability.LiveTail => KafkaProfileCeilings.ForbidsTail(profile),
        _ => false,
    };

    [Theory]
    [InlineData(null)]
    [InlineData(KafkaProfile.Development)]
    [InlineData(KafkaProfile.Test)]
    [InlineData(KafkaProfile.Production)]
    public async Task What_is_usable_on_a_cluster_is_a_subset_of_what_the_host_granted_for_every_combination_of_the_four_capabilities(KafkaProfile? profile)
    {
        await using KafkaSearchRig rig = new(studio: static o => o.Capabilities.SearchRecords = true, profile: profile);
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

                KafkaCapability[] usable = [.. KafkaCapabilityGuard.Offered.Where(c => guard.IsEnabled(c) && !ProfileForbids(profile, c))];

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

                bool mayExport = !readOnly && set.SearchRecords && set.ExportRecords && !ProfileForbids(profile, KafkaCapability.ExportRecords);
                outcome.Delivered.Should().Be(mayExport, where);
            }
        }
    }

    [Fact]
    public void Only_Production_forbids_anything_and_only_export_and_the_tail()
    {
        foreach (KafkaProfile? profile in new KafkaProfile?[] { null, KafkaProfile.Development, KafkaProfile.Test })
        {
            KafkaProfileCeilings.ForbidsExport(profile).Should().BeFalse();
            KafkaProfileCeilings.ForbidsTail(profile).Should().BeFalse();
        }

        KafkaProfileCeilings.ForbidsExport(KafkaProfile.Production).Should().BeTrue();
        KafkaProfileCeilings.ForbidsTail(KafkaProfile.Production).Should().BeTrue();
        KafkaCapabilityGuard.Offered.Where(c => ProfileForbids(KafkaProfile.Production, c)).Should().BeEquivalentTo([KafkaCapability.ExportRecords, KafkaCapability.LiveTail]);
    }
}
