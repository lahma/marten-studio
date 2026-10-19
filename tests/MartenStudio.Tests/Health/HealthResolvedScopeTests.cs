using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Health;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Health;

/// <summary>
/// SEC2 L2 and L3: what the advisor asks a policy is about the database it swept, by that database's own identity,
/// and a cached read - the sidebar's badge, a screen's banner - is shown only to a scope the report would be shown
/// to.
/// </summary>
public class HealthResolvedScopeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ----------------------------------------------------------------------------------------------
    // L2: the tenant-less questions name the resolved identity, not the visitor's spelling
    // ----------------------------------------------------------------------------------------------

    private static HealthHarness Spelled()
    {
        HealthHarness harness = HealthHarness.Create(
            studio: static options =>
            {
                options.KnownTenantIds.Add("acme");
                options.Capabilities.ViewDatabaseActivity = true;
            },
            policies: true);

        harness.Script.Finds("T90", HealthSeverity.Warning, "Database-wide finding");
        harness.Script.Finds("C90", HealthSeverity.Critical, "Activity finding");

        return harness;
    }

    /// <summary>
    /// A link with no <c>?db=</c> means the store's first database. The resolver's own question takes that spelling -
    /// it is asked before anything is looked up - and every tenant-less question after it names the database's identity,
    /// which is what the report's audit records and the high-water Explain asks about. A policy keyed on the identity
    /// therefore shows this visitor the database-wide and the activity findings, on the page and in the cached reads.
    /// </summary>
    [Fact]
    public async Task A_policy_keyed_on_the_identity_is_asked_about_the_identity_when_the_scope_spells_it_empty()
    {
        await using HealthHarness harness = Spelled();
        string identity = harness.Scope.DatabaseId;
        harness.Policies.Allow(resource => resource.TenantId is not null || resource.DatabaseIdentifier == identity);

        StudioScope spelledEmpty = new("default", string.Empty, "acme");

        HealthReport report = await harness.Health.GetReportAsync(spelledEmpty, HealthReportRequest.Regular, Token);

        report.Findings.Select(static x => x.Code).Should().BeEquivalentTo(["T90", "C90"]);
        report.WithheldCount.Should().Be(0);
        report.DatabaseId.Should().Be(identity);

        HealthGlance glance = (await harness.Health.GlanceAsync(spelledEmpty, Token))!;
        glance.Critical.Should().Be(1);
        glance.Warning.Should().Be(1);
        glance.Withheld.Should().Be(0);

        (await harness.Health.CachedFindingsAsync(spelledEmpty, ["T90"], Token)).Select(static x => x.Code).Should().Equal("T90");

        harness.Policies.Calls.Where(static x => x.Resource.TenantId is null).Should().NotBeEmpty()
            .And.OnlyContain(x => x.Resource.DatabaseIdentifier == identity,
                "every question about the database as a whole names the database the scope resolved to");
    }

    /// <summary>The refusing half: a policy that grants the database as a whole only under its empty spelling is never asked that.</summary>
    [Fact]
    public async Task A_policy_that_grants_only_the_visitors_spelling_withholds_the_database_wide_findings()
    {
        await using HealthHarness harness = Spelled();
        harness.Policies.Allow(static resource => resource.TenantId is not null || resource.DatabaseIdentifier.Length == 0);

        StudioScope spelledEmpty = new("default", string.Empty, "acme");

        HealthReport report = await harness.Health.GetReportAsync(spelledEmpty, HealthReportRequest.Regular, Token);

        report.Findings.Should().BeEmpty();
        report.WithheldCount.Should().Be(2);
        report.WithheldReason.Should().Contain("as a whole");

        HealthGlance glance = (await harness.Health.GlanceAsync(spelledEmpty, Token))!;
        glance.Worst.Should().BeNull();
        glance.Withheld.Should().Be(2);
    }

    /// <summary>
    /// SEC2-fix item 1: a store key matches in any case (<c>MartenStoreRegistry.Find</c>), so <c>?store=DEFAULT</c>
    /// reaches the store registered as <c>default</c>. Every question after the resolver's own, the audit entry and the
    /// mute keys name the registered key - what the high-water Explain asks about - so a policy that compares keys
    /// ordinally is shown one resource, and a mute made under one spelling holds under another.
    /// </summary>
    [Fact]
    public async Task A_store_key_spelt_in_another_case_is_asked_audited_and_muted_under_the_registered_key()
    {
        await using HealthHarness harness = Spelled();
        harness.Policies.Allow(static resource =>
            resource.TenantId is not null || string.Equals(resource.StoreName, "default", StringComparison.Ordinal));

        StudioScope shouted = harness.Scope with { StoreKey = "DEFAULT", TenantId = "acme" };

        HealthReport report = await harness.Health.GetReportAsync(shouted, HealthReportRequest.Regular, Token);

        report.Findings.Select(static x => x.Code).Should().BeEquivalentTo(["T90", "C90"]);
        report.StoreKey.Should().Be("default");
        report.Findings.Should().OnlyContain(static x => x.MuteKey.StartsWith("default|", StringComparison.Ordinal));

        HealthGlance glance = (await harness.Health.GlanceAsync(shouted, Token))!;
        glance.Critical.Should().Be(1);
        glance.Warning.Should().Be(1);
        glance.Criticals.Should().OnlyContain(static x => x.MuteKey.StartsWith("default|", StringComparison.Ordinal));

        (await harness.Health.CachedFindingsAsync(shouted, ["T90"], Token)).Should().ContainSingle()
            .Which.MuteKey.Should().StartWith("default|");

        harness.Policies.Calls.Where(static x => x.Resource.TenantId is null).Should().NotBeEmpty()
            .And.OnlyContain(static x => x.Resource.StoreName == "default", "every question about the database as a whole names the registered key");

        StudioActionLogEntry shown = harness.Services.GetRequiredService<StudioActionLogService>().GetLatest()
            .Should().ContainSingle(static x => x.Action == HealthService.ActivityAction).Subject;
        shown.StoreKey.Should().Be("default", "the audit names the store the policy was asked about");
    }

    // ----------------------------------------------------------------------------------------------
    // L3: the badge and the banners place the tenant in the swept database, as the report does
    // ----------------------------------------------------------------------------------------------

    /// <summary>
    /// SEC2-fix item 2: placing a tenant resolves the scope, which on a master-table or sharded tenancy reads the
    /// tenancy's lookup table - and the sidebar asks on its prerender, when it becomes interactive, after every mute
    /// and at every interval. So the answer is remembered per store, database and tenant for tenant discovery's
    /// minute; the store policy is still asked on every read, so a refusal is immediate.
    /// </summary>
    [Fact]
    public async Task The_tenant_is_placed_once_a_minute_while_the_store_policy_is_asked_on_every_read()
    {
        await using HealthHarness harness = TwoDatabases();
        string b = await IdentityAsync(harness, "health_scope_b");
        harness.Script.Finds("K90", HealthSeverity.Warning, "Configuration finding");

        await harness.Health.GetReportAsync(new StudioScope("default", b, null), HealthReportRequest.Regular, Token);

        HealthService service = (HealthService) harness.Health;
        StudioScope globexInB = new("default", b, "globex");
        StudioScope acmeInB = new("default", b, "acme");
        int policyCallsBefore = harness.Policies.Calls.Count;

        for (int read = 0; read < 5; read++)
        {
            (await harness.Health.GlanceAsync(globexInB, Token))!.Warning.Should().Be(1);
            (await harness.Health.CachedFindingsAsync(globexInB, ["K90"], Token)).Should().ContainSingle();
        }

        service.TenantPlacements.Should().Be(1, "ten reads inside the window place globex in B once");
        harness.Policies.Calls.Skip(policyCallsBefore)
            .Count(x => x.Resource.TenantId == "globex" && x.Resource.DatabaseIdentifier == b && x.Resource.Capability == null)
            .Should().BeGreaterThanOrEqualTo(10, "the store policy is asked on every read");

        // A tenant that is not one of B's is remembered as not one, as well.
        (await harness.Health.GlanceAsync(acmeInB, Token)).Should().BeNull();
        (await harness.Health.GlanceAsync(acmeInB, Token)).Should().BeNull();
        service.TenantPlacements.Should().Be(2);

        // The policy changing its mind is seen at once, without placing anything again.
        harness.Policies.Allow(static resource => resource.TenantId != "globex");
        (await harness.Health.GlanceAsync(globexInB, Token)).Should().BeNull("the store policy now refuses globex");
        service.TenantPlacements.Should().Be(2);

        // Past the window, the tenant is placed again.
        harness.Policies.Allow(static _ => true);
        harness.Clock.Advance(HealthService.TenantPlacementTimeToLive + TimeSpan.FromSeconds(1));
        (await harness.Health.GlanceAsync(globexInB, Token))!.Warning.Should().Be(1);
        service.TenantPlacements.Should().Be(3);
    }

    /// <summary>
    /// SEC2-fix2: the scope selector's refresh clears tenant discovery's list, and with it what the badge remembers -
    /// so the placement is never more than one discovery window behind the selector, and a refresh is seen at once.
    /// </summary>
    [Fact]
    public async Task The_selectors_refresh_clears_the_remembered_placement()
    {
        await using HealthHarness harness = TwoDatabases();
        string b = await IdentityAsync(harness, "health_scope_b");
        harness.Script.Finds("K90", HealthSeverity.Warning, "Configuration finding");

        await harness.Health.GetReportAsync(new StudioScope("default", b, null), HealthReportRequest.Regular, Token);

        HealthService service = (HealthService) harness.Health;
        StudioScope globexInB = new("default", b, "globex");

        await harness.Health.GlanceAsync(globexInB, Token);
        await harness.Health.GlanceAsync(globexInB, Token);
        service.TenantPlacements.Should().Be(1, "the premise: inside the window the placement is remembered");

        // What the selector's refresh button does: the circuit's catalog invalidates the circuit's tenant discovery.
        harness.Circuit.GetRequiredService<IStudioScopeCatalog>().Invalidate();

        (await harness.Health.GlanceAsync(globexInB, Token))!.Warning.Should().Be(1);
        service.TenantPlacements.Should().Be(2, "the refresh moved discovery's generation, so the tenant is placed again");

        await harness.Health.GlanceAsync(globexInB, Token);
        service.TenantPlacements.Should().Be(2, "and remembered again from then on");
    }

    /// <summary>
    /// <c>?db=B&amp;tenant=acme</c>, where acme is database A's tenant, under a policy that looks only at the tenant: the
    /// report is refused - the resolver says acme is not one of B's tenants - and the badge and the banners, which read
    /// B's last sweep, are not shown either. Before, they asked the store policy alone and showed B's ungated findings.
    /// </summary>
    [Fact]
    public async Task A_tenant_that_is_not_one_of_the_swept_databases_gets_no_badge_and_no_banner_from_it()
    {
        await using HealthHarness harness = TwoDatabases();
        string b = await IdentityAsync(harness, "health_scope_b");

        harness.Script.Finds("K90", HealthSeverity.Warning, "Configuration finding");
        harness.Policies.Allow(static resource => resource.TenantId is null or "acme" or "globex");

        // Somebody sweeps B.
        await harness.Health.GetReportAsync(new StudioScope("default", b, null), HealthReportRequest.Regular, Token);
        harness.Cache.Latest("default", b).Should().NotBeNull("the premise: B has a recent sweep");

        StudioScope acmeInB = new("default", b, "acme");

        (await harness.Health.GetReportAsync(acmeInB, HealthReportRequest.Regular, Token)).Refusal
            .Should().Be(HealthRefusal.Unavailable, "acme is not one of B's tenants");
        (await harness.Health.GlanceAsync(acmeInB, Token)).Should().BeNull("the badge agrees with the report");
        (await harness.Health.CachedFindingsAsync(acmeInB, ["K90"], Token)).Should().BeEmpty("and so does a banner");

        // The allowed half: B's own tenant gets B's finding.
        StudioScope globexInB = new("default", b, "globex");

        HealthGlance glance = (await harness.Health.GlanceAsync(globexInB, Token))!;
        glance.Warning.Should().Be(1);
        (await harness.Health.CachedFindingsAsync(globexInB, ["K90"], Token)).Select(static x => x.Code).Should().Equal("K90");
    }

    /// <summary>
    /// The cached reads' own questions name the swept database by its identity - with no <c>?db=</c>, the store's first
    /// database, as the report resolved it - and the visitor's tenant; never the empty spelling, which names no
    /// database to a policy.
    /// </summary>
    [Fact]
    public async Task The_cached_reads_ask_about_the_swept_database_by_its_identity_with_the_visitors_tenant()
    {
        await using HealthHarness harness = TwoDatabases();
        IReadOnlyList<IMartenDatabase> databases = await harness.Store.Storage.AllDatabases();
        string first = databases[0].Id.Identity;
        string tenant = databases[0].Id.Name == "health_scope_a" ? "acme" : "globex";

        harness.Script.Finds("K90", HealthSeverity.Warning, "Configuration finding");

        StudioScope spelledEmpty = new("default", string.Empty, tenant);
        await harness.Health.GetReportAsync(spelledEmpty, HealthReportRequest.Regular, Token);
        int before = harness.Policies.Calls.Count;

        HealthGlance glance = (await harness.Health.GlanceAsync(spelledEmpty, Token))!;
        glance.Warning.Should().Be(1, "the premise: the empty spelling found the first database's sweep");

        List<MartenStoreResource> asked = [.. harness.Policies.Calls.Skip(before).Select(static x => x.Resource)];
        asked.Should().NotBeEmpty().And.OnlyContain(x => x.DatabaseIdentifier == first);
        asked.Should().Contain(x => x.TenantId == tenant && x.Capability == null, "the tenant is placed in the swept database");
    }

    private static HealthHarness TwoDatabases() => HealthHarness.Create(
        policies: true,
        store: static options => options.MultiTenantedDatabases(static tenancy =>
        {
            tenancy.AddMultipleTenantDatabase(HealthExportHarness.Refused("health_scope_a"), "health_scope_a").ForTenants("acme");
            tenancy.AddMultipleTenantDatabase(HealthExportHarness.Refused("health_scope_b"), "health_scope_b").ForTenants("globex");
        }));

    private static async Task<string> IdentityAsync(HealthHarness harness, string name)
    {
        IReadOnlyList<IMartenDatabase> databases = await harness.Store.Storage.AllDatabases();
        return databases.Single(x => x.Id.Name == name).Id.Identity;
    }
}
