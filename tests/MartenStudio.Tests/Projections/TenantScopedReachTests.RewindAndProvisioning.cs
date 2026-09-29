using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Events;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// DB-0-fix-3: the dead-letter rewind reaches every tenant (R1), the exclusivity rule on a sharded tenancy
/// (F-b), a tenant correction that provisioned before it was authorized (F-c), and a shard name the parse
/// misreads (F-f).
/// </summary>
/// <remarks>
/// The same harness as the rest of this class: a studio over a store that is never connected to, where
/// every refusal happens before Marten is touched and every refusal has a positive control that lets the
/// same call through to fail later, for a reason that is not authorization. The live half of R1 is
/// <c>RewindReachLiveTests</c>.
/// </remarks>
public partial class TenantScopedReachTests
{
    // ------------------------------------------------------------------------------------------------
    // R1: the dead-letter rewind is authorized for the database it reaches
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The reviewer's case, named: a policy that allows <c>(db, acme)</c> and refuses <c>(db, null)</c>.
    /// Marten's rewind rewrites every shard's progression row of the projection and deletes its dead
    /// letters for every tenant, so from the <c>acme</c> scope it is refused - asked about the database as a
    /// whole, and refused as that, before the projection name is even looked at.
    /// </summary>
    [Fact]
    public async Task A_rewind_from_one_tenants_scope_on_a_shared_database_is_refused_as_the_whole_database()
    {
        await using Harness harness = await Harness.ConjoinedAsync();
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        Func<Task> rewinding = () => harness.RunAsync(EventDataService.RewindSubscriptionAction, harness.Scope(Acme));

        StudioNotAuthorizedException refused = (await rewinding.Should().ThrowAsync<StudioNotAuthorizedException>(
            "Marten deletes globex's dead letters and re-runs globex's events too")).Which;

        refused.Scope.TenantId.Should().BeNull("the question refused is the database as a whole");
        refused.Scope.DatabaseId.Should().Be(harness.Database);

        harness.Policies.Calls.Should().Contain(
            x => x.Resource.DatabaseIdentifier == harness.Database && x.Resource.TenantId == null,
            "the whole-database question was asked, which it never was before DB-0-fix-3");

        StudioActionLogEntry entry = harness.Ring.GetLatest()
            .Should().ContainSingle(static x => x.Action == EventDataService.RewindSubscriptionAction).Subject;

        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().BeNull("the audit records the tenant-less scope that was refused, not the one asked from");
        entry.DatabaseId.Should().Be(harness.Database);
    }

    /// <summary>
    /// Skip event and Discard stay tenant-narrow: each proves its target is in the scope's tenant and acts
    /// on that one event or record, so the visitor's own tenant is all they are asked about - past the
    /// check and on to the connection nothing is listening at.
    /// </summary>
    [Theory]
    [InlineData(EventDataService.SkipEventAction)]
    [InlineData(EventDataService.DiscardDeadLetterAction)]
    public async Task Skip_and_discard_are_still_asked_about_the_tenant_alone(string action)
    {
        await using Harness harness = await Harness.ConjoinedAsync();
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        Exception? thrown = null;
        try
        {
            await harness.RunAsync(action, harness.Scope(Acme));
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        thrown.Should().NotBeNull("nothing is listening at this connection string");
        (thrown is StudioNotAuthorizedException).Should().BeFalse("acme's own event or record is acme's to manage: {0}", thrown?.Message);

        harness.Policies.Calls.Should().NotContain(
            static x => x.Resource.TenantId == null,
            "neither reaches beyond the one tenant-scoped row it proved was acme's");
    }

    // ------------------------------------------------------------------------------------------------
    // F-b: a sharded tenancy is never exclusively one tenant's, and the list is read after the refresh
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>ShardedTenancy</c> pools tenants by design and lists a shard's <em>active</em> tenants only, while
    /// a disabled tenant's partitions stay in the shard. A shard listing only <c>acme</c> is not provably
    /// <c>acme</c>'s alone, so exclusivity is never granted on one - asked of a database that is
    /// exclusively <c>acme</c>'s under its own static store, the sharded store still says no.
    /// </summary>
    [Fact]
    public async Task A_sharded_tenancy_is_never_exclusively_one_tenants()
    {
        await using Harness perTenant = await Harness.DatabasePerTenantAsync(primaryIsDefault: false);
        IMartenDatabase shard = perTenant.ScopeDatabase;

        // The anti-vacuity half: this database lists acme alone and is exclusively acme's under its own,
        // static, store - and a sharded store whose pool is exactly that one shard passes every other
        // condition too (no default, no twin), which is how "a shard listing only acme" used to be read
        // as acme's alone.
        (await DatabaseReach.IsExclusivelyTenantsAsync(perTenant.Store, shard, Acme, Token)).Should().BeTrue();

        var sharded = new StoreOptions();
        sharded.MultiTenantedWithShardedDatabases(x => x.ConnectionString = SharedConnectionString);
        sharded.Tenancy = new ListedShardedTenancy(sharded, new ShardedTenancyOptions { ConnectionString = SharedConnectionString }, [shard]);
        await using var shardedStore = new DocumentStore(sharded);

        (await shardedStore.Storage.AllDatabases()).Should().ContainSingle()
            .Which.TenantIds.Should().Equal([Acme], "the pool lists the shard, and the shard lists acme");

        DatabaseReach.IsPooledByDesign(shardedStore.Options.Tenancy).Should().BeTrue();

        (await DatabaseReach.IsExclusivelyTenantsAsync(shardedStore, shard, Acme, Token))
            .Should().BeFalse("a shard's tenant list is its active tenants, not everyone whose data it holds");

        var masterTable = new StoreOptions();
        masterTable.MultiTenantedDatabasesWithMasterDatabaseTable(SharedConnectionString, "tenants");
        DatabaseReach.IsPooledByDesign(masterTable.Tenancy).Should().BeFalse("a master table is one database per tenant row");
        DatabaseReach.IsPooledByDesign(perTenant.Store.Options.Tenancy).Should().BeFalse();
    }

    /// <summary>
    /// Marten's own <c>ShardedTenancy</c>, with its pool listed in memory rather than read from a pool table,
    /// so <c>AllDatabases()</c> answers without a database behind it.
    /// </summary>
    /// <remarks>
    /// <c>ITenancy</c> is re-listed so that the interface's <c>BuildDatabases</c> - which is what
    /// <c>IMartenStorage.AllDatabases()</c> calls - maps to the method here; every other member stays
    /// Marten's (the same technique Marten's own <c>TenantPartitionedSingleDatabaseTenancy</c> uses).
    /// </remarks>
    private sealed class ListedShardedTenancy(
        StoreOptions options,
        ShardedTenancyOptions configuration,
        IReadOnlyList<IMartenDatabase> shards)
        : ShardedTenancy(options, configuration), ITenancy
    {
        public new ValueTask<IReadOnlyList<Weasel.Core.Migrations.IDatabase>> BuildDatabases() =>
            ValueTask.FromResult<IReadOnlyList<Weasel.Core.Migrations.IDatabase>>([.. shards]);
    }

    /// <summary>
    /// A dynamic tenancy reconciles each database's <c>TenantIds</c> on <c>BuildDatabases()</c>, which is
    /// what <c>AllDatabases()</c> runs. Read before that call, the list is whatever the previous caller
    /// left; the rule reads it after. Here the refresh finds <c>globex</c> in <c>acme</c>'s database.
    /// </summary>
    [Fact]
    public async Task Exclusivity_reads_the_tenant_list_after_the_databases_are_refreshed()
    {
        await using Harness harness = await Harness.DatabasePerTenantAsync(primaryIsDefault: false, observe: true);

        (await DatabaseReach.IsExclusivelyTenantsAsync(harness.Store, harness.ScopeDatabase, Acme, Token))
            .Should().BeTrue("without a reconcile the database lists acme alone");

        IMartenDatabase primary = harness.ScopeDatabase;
        harness.Tenancy!.OnBuildDatabases = () =>
        {
            if (!primary.TenantIds.Contains(Globex))
            {
                primary.TenantIds.Add(Globex);
            }
        };

        (await DatabaseReach.IsExclusivelyTenantsAsync(harness.Store, harness.ScopeDatabase, Acme, Token))
            .Should().BeFalse("the refresh the rule runs found a second tenant in the database");
    }

    // ------------------------------------------------------------------------------------------------
    // F-c: a tenant correction never provisions a tenant
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A tenant the host lists in <c>KnownTenantIds</c> and no database holds yet. On a sharded or
    /// single-server tenancy, asking for it provisions it - an assignment and partitions, or a database -
    /// and the correction used to ask before it asked the policy anything. It is refused now with nothing
    /// asked of the tenancy by name, audited as a failed action and not as a scope denial.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorrectionsOnly))]
    public async Task A_correction_for_a_tenant_no_database_holds_asks_the_tenancy_nothing(string action)
    {
        await using Harness harness = await Harness.DatabasePerTenantAsync(
            primaryIsDefault: false, dynamicShape: true, moreTenants: [NotYetProvisioned]);

        Func<Task> correcting = () => harness.RunAsync(action, harness.Scope(NotYetProvisioned));

        (await correcting.Should().ThrowAsync<KeyNotFoundException>())
            .Which.Message.Should().Contain($"holds tenant '{NotYetProvisioned}' yet");

        harness.Tenancy!.GetTenantCalls.Should().Be(0, "a lookup of a tenant nobody has is a provisioning on this tenancy");
        harness.Tenancy.FindOrCreateDatabaseCalls.Should().Be(0);

        harness.Ring.GetLatest().Should().ContainSingle(x => x.Action == action)
            .Which.Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// The anti-vacuity half: a tenant a database already holds is looked up - which for a present tenant
    /// is a lookup and nothing more - and the correction goes on to the connection nothing listens at.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorrectionsOnly))]
    public async Task A_correction_for_a_tenant_a_database_holds_is_looked_up_and_goes_ahead(string action)
    {
        await using Harness harness = await Harness.DatabasePerTenantAsync(
            primaryIsDefault: false, dynamicShape: true, moreTenants: [NotYetProvisioned]);

        Exception? thrown = null;
        try
        {
            await harness.RunAsync(action, harness.Scope(Acme));
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        thrown.Should().NotBeNull("nothing is listening at this connection string");
        thrown.Should().NotBeOfType<KeyNotFoundException>("acme is in the primary's tenant list");
        (thrown is StudioNotAuthorizedException).Should().BeFalse();

        harness.Tenancy!.GetTenantCalls.Should().BeGreaterThan(0, "a present tenant is looked up the way Marten looks it up");
    }

    /// <summary>
    /// A static tenancy never provisions from a lookup - an unknown tenant is an
    /// <c>UnknownTenantIdException</c> - so it is asked directly, as before.
    /// </summary>
    [Fact]
    public async Task A_static_tenancy_is_asked_for_the_tenant_directly()
    {
        await using Harness harness = await Harness.DatabasePerTenantAsync(primaryIsDefault: false, observe: true);

        (await DatabaseReach.FindTenantDatabaseAsync(harness.Store, Acme, Token))!.Id.Identity.Should().Be(harness.Database);

        harness.Tenancy!.GetTenantCalls.Should().Be(1);
    }

    /// <summary>The two corrections, which are what look a tenant's database up.</summary>
    public static TheoryData<string> CorrectionsOnly => ["AdvanceHighWaterMark", "CorrectProgression"];

    /// <summary>A tenant the host lists and no database holds.</summary>
    private const string NotYetProvisioned = "newco";

    // ------------------------------------------------------------------------------------------------
    // F-f: a registered shard is store-global, whatever the parse says
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>ShardName.TryParse("Billing:Invoices:All")</c> reads tenant <c>All</c> of projection
    /// <c>Billing</c>; the daemon finds the store-global shard of a projection <em>named</em>
    /// <c>Billing:Invoices</c> first. A registered identity is store-global, and a set that could not be
    /// read makes every name store-global.
    /// </summary>
    [Fact]
    public void A_name_that_is_a_registered_shard_is_store_global_whatever_it_parses_as()
    {
        const string ColonNamed = "Billing:Invoices:All";
        var registered = new HashSet<string>(StringComparer.Ordinal) { ColonNamed };

        ShardName.TryParse(ColonNamed, out ShardName? parsed).Should().BeTrue();
        parsed!.TenantId.Should().Be("All", "this is the misreading the rule exists for");

        DatabaseReach.TenantOfShard(ColonNamed, registered).Should().BeNull();
        DatabaseReach.TenantOfShard(ColonNamed, NoRegisteredShards).Should().Be("All", "the anti-vacuity half");
        DatabaseReach.TenantOfShard("DailySales:All:acme", registered).Should().Be("acme", "only the registered name is store-global");
        DatabaseReach.TenantOfShard("DailySales:All:acme", null).Should().BeNull("a store whose shards cannot be listed is asked the stricter question");
    }

    /// <summary>
    /// End to end: a store with an async projection named <c>Billing:Invoices</c>, and a visitor who may
    /// address tenant <c>All</c> and nothing else. Stopping <c>Billing:Invoices:All</c> - that projection's
    /// store-global agent - is asked about the database as a whole and refused; stopping a name the store
    /// does not register goes through as the tenant it parses as.
    /// </summary>
    [Fact]
    public async Task A_store_global_agent_whose_name_parses_with_a_tenant_is_asked_about_the_whole_database()
    {
        const string Tenant = "All";

        await using Harness harness = await Harness.ConjoinedAsync(
            static options => options.Projections.Add(
                new TimeTravelSummaryProjection { Name = "Billing:Invoices" }, ProjectionLifecycle.Async),
            moreTenants: [Tenant]);

        harness.Policies.Allow(static resource => resource.TenantId == Tenant);

        DatabaseReach.TenantOfShard(harness.Store, "Billing:Invoices:All").Should().BeNull("the store registers that identity");

        Func<Task> stopping = () => harness.RunAsync("StopAgent", harness.Scope(Tenant), shardName: "Billing:Invoices:All");

        (await stopping.Should().ThrowAsync<StudioNotAuthorizedException>(
            "the agent processes every tenant's events")).Which.Scope.TenantId.Should().BeNull();

        // The positive control: a name the store does not register is the tenant it parses as.
        Func<Task> ownAgent = () => harness.RunAsync("StopAgent", harness.Scope(Tenant), shardName: "Orders:All:All");

        await ownAgent.Should().ThrowAsync<StudioDaemonNotHostedException>("past the check, to the daemon this process does not host");
    }
}
