using System.Reflection;

using JasperFx.Descriptors;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Tests.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

namespace MartenStudio.Tests.Services;

/// <summary>
/// F8-D3 C4: tier 2 of tenant discovery reads a master-table or sharded database's own tenant list, and
/// describes every other tenancy.
/// </summary>
/// <remarks>
/// <para>
/// Both dynamic tenancies here are Marten's own classes with <c>ITenancy</c> re-listed, so that the
/// interface's <c>BuildDatabases</c> and <c>DescribeDatabasesAsync</c> map to counting methods while every
/// other member stays Marten's - the technique Marten's own <c>TenantPartitionedSingleDatabaseTenancy</c> uses,
/// and <c>TenantScopedReachTests</c>' <c>ListedShardedTenancy</c>. The database handed to discovery is built
/// by hand with the tenant list <c>BuildDatabases()</c> would have reconciled onto it, which is what the
/// caller's own <c>AllDatabases()</c> hands over.
/// </para>
/// <para>
/// Nothing has a database behind it. A describe call that was made would be counted, and a real one would
/// fail against an <c>.invalid</c> host and fall through to a tier that answers something else.
/// </para>
/// </remarks>
public class TenantDiscoveryInstanceTests
{
    private const string UnreachableMaster = "Host=f8d3-master.invalid;Database=master;Username=u;Password=p";

    private const string UnreachableTenant = "Host=f8d3-tenant.invalid;Database=tenant;Username=u;Password=p";

    private const string OtherUnreachableTenant = "Host=f8d3-other-tenant.invalid;Database=other;Username=u;Password=p";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TenantDiscovery Create() =>
        new(Options.Create(new MartenStudioOptions()), NullLogger<TenantDiscovery>.Instance, TimeProvider.System);

    [Fact]
    public async Task A_master_table_database_is_read_for_its_own_tenant_and_never_described()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabasesWithMasterDatabaseTable(UnreachableMaster, "tenants");
        options.Schema.For<TenantedNote>().MultiTenanted();

        var tenancy = new CountingMasterTableTenancy(options);
        options.Tenancy = tenancy;

        await using var store = new DocumentStore(options);
        await using MartenDatabase database = TenantDatabase(options, "acme", ["acme"]);

        TenantList tenants = await Create().DiscoverAsync("default", store, database, [database], Token);

        tenants.Source.Should().Be(TenantListSource.Descriptor);
        tenants.Ids.Should().Equal("acme");
        tenancy.Describes.Should().Be(0, "describing a master-table tenancy is BuildDatabases() again, a read of the master table");
        tenancy.Builds.Should().Be(0, "and discovery never enumerates either");
    }

    [Fact]
    public async Task A_shard_is_read_for_its_reconciled_tenants_and_never_described()
    {
        StoreOptions options = new();
        options.MultiTenantedWithShardedDatabases(x => x.ConnectionString = UnreachableMaster);

        var tenancy = new CountingShardedTenancy(options, new ShardedTenancyOptions { ConnectionString = UnreachableMaster });
        options.Tenancy = tenancy;

        await using var store = new DocumentStore(options);
        await using MartenDatabase shard = TenantDatabase(options, "shard-1", ["globex", "acme"]);

        TenantList tenants = await Create().DiscoverAsync("default", store, shard, [shard], Token);

        tenants.Source.Should().Be(TenantListSource.Descriptor);
        tenants.Ids.Should().Equal(["acme", "globex"], "ordinal order, as every tier answers");
        tenancy.Describes.Should().Be(0, "describing a sharded tenancy is BuildDatabases() again, two reads of the pool tables");
        tenancy.Builds.Should().Be(0);
    }

    /// <summary>
    /// A database that lists no tenant is not described as a fallback: on these two tenancies the describe
    /// call would answer the same empty list, after a read of the lookup table.
    /// </summary>
    [Fact]
    public async Task A_dynamic_database_that_lists_no_tenant_falls_through_without_being_described()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabasesWithMasterDatabaseTable(UnreachableMaster, "tenants");

        var tenancy = new CountingMasterTableTenancy(options);
        options.Tenancy = tenancy;

        await using var store = new DocumentStore(options);
        await using MartenDatabase database = TenantDatabase(options, "acme", []);

        TenantList tenants = await Create().DiscoverAsync("default", store, database, [database], Token);

        tenants.Source.Should().Be(TenantListSource.Unavailable, "nothing in this store has a tenant column to query");
        tenancy.Describes.Should().Be(0);
    }

    /// <summary>
    /// The other half, and the proof the counting technique counts: a static multi-tenancy's databases list
    /// their tenants too, so only the counter can say which path ran - and it is the describe call, which
    /// answers from configuration.
    /// </summary>
    [Fact]
    public async Task Every_other_tenancy_is_described()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabases(x => x.AddMultipleTenantDatabase(UnreachableTenant, "tenants-a").ForTenants("globex", "acme"));

        DescribeCountingTenancy counting = DescribeCountingTenancy.Over(options.Tenancy);
        options.Tenancy = (ITenancy) (object) counting;

        await using var store = new DocumentStore(options);
        IMartenDatabase database = (await store.Storage.AllDatabases()).Single();
        database.TenantIds.Should().BeEquivalentTo(["acme", "globex"], "the premise: the instance would have answered too");

        TenantList tenants = await Create().DiscoverAsync("default", store, database, [database], Token);

        tenants.Source.Should().Be(TenantListSource.Descriptor);
        tenants.Ids.Should().Equal("acme", "globex");
        counting.Describes.Should().Be(1, "a static tenancy describes from its configuration, and that is the call tier 2 makes for it");
    }

    /// <summary>
    /// CL1 item 8: a tenant-partitioned single database lists its tenants on the main descriptor, which its describe
    /// fills from <c>mt_tenant_partitions</c> - and tier 2 used to read only <c>Databases</c>, throw that answer away and
    /// fall through to a scan of the tenanted tables. Now the main descriptor answers, capped at
    /// <see cref="TenantDiscovery.MaxListedTenants" /> like tier 3, and an empty one still falls through (here to a query
    /// against an unreachable host, which is "unavailable" - the proof that nothing else answered).
    /// </summary>
    [Theory]
    [InlineData(3, 3, false, true)]
    [InlineData(250, TenantDiscovery.MaxListedTenants, true, true)]
    [InlineData(0, 0, false, false)]
    public async Task A_tenant_partitioned_single_database_answers_from_its_main_descriptor(
        int registered,
        int listed,
        bool truncated,
        bool fromDescriptor)
    {
        StoreOptions options = new();
        options.Connection(UnreachableTenant);
        options.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
        options.Events.UseTenantPartitionedEvents = true;
        options.Events.AppendMode = JasperFx.Events.EventAppendMode.QuickWithServerTimestamps;
        options.Schema.For<TenantedNote>().MultiTenanted();

        // Built with Marten's own tenancy, which is what initialises its database (DefaultTenancy.Initialize, by type).
        await using var store = new DocumentStore(options);

        ITenancy partitioned = options.Tenancy;
        partitioned.GetType().Name.Should().Be("TenantPartitionedSingleDatabaseTenancy", "the premise");
        partitioned.Cardinality.Should().Be(DatabaseCardinality.Single, "the premise: nothing to enumerate");
        IMartenDatabase database = (await store.Storage.AllDatabases()).Single();

        // Then counted, and answered as the tenancy's own describe answers once mt_tenant_partitions has: DefaultTenancy's
        // usage - one main descriptor, no Databases - with the main descriptor's TenantIds filled (read at V9.39.0 and
        // V9.50.2). Disposing the store disposes the tenancy through the proxy.
        DescribeCountingTenancy counting = DescribeCountingTenancy.Over(partitioned);
        counting.Answer = () =>
        {
            DatabaseDescriptor main = database.Describe();
            main.TenantIds.AddRange(Enumerable.Range(0, registered).Select(static x => "t" + x.ToString("000", System.Globalization.CultureInfo.InvariantCulture)));
            return new DatabaseUsage { Cardinality = DatabaseCardinality.Single, MainDatabase = main };
        };
        options.Tenancy = (ITenancy) (object) counting;
        store.Options.Tenancy.Should().BeSameAs(counting, "the premise: discovery asks the store's tenancy, which is now the proxy");

        TenantList tenants = await Create().DiscoverAsync("default", store, database, [database], Token);

        tenants.Source.Should().Be(fromDescriptor ? TenantListSource.Descriptor : TenantListSource.Unavailable);
        tenants.Ids.Should().HaveCount(listed);
        tenants.IsTruncated.Should().Be(truncated, "a list of more than the selector shows turns it into a free-text box");
        counting.Describes.Should().Be(1, "the describe the tier always made, and nothing more");

        if (listed > 0)
        {
            tenants.Ids[0].Should().Be("t000");
            tenants.Ids[^1].Should().Be("t" + (listed - 1).ToString("000", System.Globalization.CultureInfo.InvariantCulture), "the first in ordinal order are kept");
        }
    }

    /// <summary>
    /// F8-D3-fix: two master-table rows with one connection string are two instances over one database, and
    /// the studio resolves that database to the first of them. Each instance lists only its own row's tenant,
    /// so discovery reads every instance in the caller's enumeration with that identity - and none with
    /// another.
    /// </summary>
    [Fact]
    public async Task Twin_master_table_databases_answer_for_each_others_tenants()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabasesWithMasterDatabaseTable(UnreachableMaster, "tenants");

        var tenancy = new CountingMasterTableTenancy(options);
        options.Tenancy = tenancy;

        await using var store = new DocumentStore(options);
        await using MartenDatabase first = TenantDatabase(options, "t1", ["t1"]);
        await using MartenDatabase second = TenantDatabase(options, "t2", ["t2"]);
        await using MartenDatabase elsewhere = TenantDatabase(options, "t3", ["t3"], OtherUnreachableTenant);

        first.Id.Identity.Should().Be(second.Id.Identity, "the premise: one connection string, one identity");
        elsewhere.Id.Identity.Should().NotBe(first.Id.Identity, "the premise: another database");

        TenantList tenants = await Create().DiscoverAsync("default", store, first, [first, second, elsewhere], Token);

        tenants.Source.Should().Be(TenantListSource.Descriptor);
        tenants.Ids.Should().Equal(["t1", "t2"], "both rows' tenants live in this database, and t3's does not");
        tenancy.Describes.Should().Be(0, "the siblings come from the caller's enumeration, not from another one");
        tenancy.Builds.Should().Be(0);
    }

    /// <summary>
    /// The same twins end to end: the header lists both tenants, and a link naming the second row's tenant -
    /// <c>?db=…&amp;tenant=t2</c> - resolves rather than being refused as "not a tenant of this database", which
    /// is what reading the first instance alone did. And it resolves to the <em>second</em> instance, the one built
    /// from t2's row (SEC1 L1): resolving it to the first opened every session for t2 on t1's row's data source.
    /// </summary>
    [Fact]
    public async Task A_link_to_a_twin_rows_tenant_resolves_and_the_header_lists_it()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabasesWithMasterDatabaseTable(UnreachableMaster, "tenants");
        options.Schema.For<TenantedNote>().MultiTenanted();

        var tenancy = new CountingMasterTableTenancy(options);
        options.Tenancy = tenancy;

        await using var store = new DocumentStore(options);
        await using MartenDatabase first = TenantDatabase(options, "t1", ["t1"]);
        await using MartenDatabase second = TenantDatabase(options, "t2", ["t2"]);
        tenancy.Listed = [first, second];

        var users = new TestAuthenticationStateProvider();
        users.SignIn("tester");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IDocumentStore>(store);
        services.AddMartenStudio(static _ => { });
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        string identity = first.Id.Identity;

        StudioScopeResolver resolver = circuit.ServiceProvider.GetRequiredService<StudioScopeResolver>();

        ResolvedScope resolved = await resolver
            .ResolveAsync(new StudioScope(MartenStoreRegistry.DefaultStoreKey, identity, "t2"), null, Token);

        resolved.Database.Should().BeSameAs(second, "t2 resolves to the instance built from t2's row, whose tenant list holds it");

        (await resolver.ResolveAsync(new StudioScope(MartenStoreRegistry.DefaultStoreKey, identity, "t1"), null, Token))
            .Database.Should().BeSameAs(first, "and t1 to t1's");
        (await resolver.ResolveAsync(new StudioScope(MartenStoreRegistry.DefaultStoreKey, identity, null), null, Token))
            .Database.Should().BeSameAs(first, "with no tenant, the first instance with the identity - the one the picker lists");

        StoreScopeFacts facts = await circuit.ServiceProvider.GetRequiredService<IStudioScopeCatalog>()
            .DescribeAsync(MartenStoreRegistry.DefaultStoreKey, identity, Token);

        facts.Tenants.Ids.Should().Equal(["t1", "t2"], "the header offers both rows' tenants");
        tenancy.Describes.Should().Be(0);
    }

    /// <summary>
    /// SEC1 L1, the rule on its own: of the instances with the matched database's identity, the one whose own list holds
    /// the tenant - never an instance of another database, whatever it lists - and the matched one when none does.
    /// </summary>
    [Fact]
    public async Task Only_a_twin_of_the_matched_database_answers_for_a_tenant()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabasesWithMasterDatabaseTable(UnreachableMaster, "tenants");

        var tenancy = new CountingMasterTableTenancy(options);
        options.Tenancy = tenancy;

        await using var store = new DocumentStore(options);
        await using MartenDatabase first = TenantDatabase(options, "t1", ["t1"]);
        await using MartenDatabase second = TenantDatabase(options, "t2", ["t2"]);
        await using MartenDatabase elsewhere = TenantDatabase(options, "t3", ["t3", "t2"], OtherUnreachableTenant);

        first.Id.Identity.Should().Be(second.Id.Identity, "the premise: twins");
        elsewhere.Id.Identity.Should().NotBe(first.Id.Identity, "the premise: another database");

        StudioScopeResolver.InstanceFor([elsewhere, first, second], first, "t2").Should().BeSameAs(second);
        StudioScopeResolver.InstanceFor([elsewhere, first, second], first, "t1").Should().BeSameAs(first);
        StudioScopeResolver.InstanceFor([elsewhere, first, second], first, "t3")
            .Should().BeSameAs(first, "a database elsewhere is not a twin, whatever it lists");
        StudioScopeResolver.InstanceFor([first, second], first, "nobody").Should().BeSameAs(first, "none lists it: the matched one");
        StudioScopeResolver.InstanceFor([first, second], first, "T2").Should().BeSameAs(first, "ordinal, as IsAcceptable matched it");
        tenancy.Describes.Should().Be(0);
        tenancy.Builds.Should().Be(0, "the lists are the caller's enumeration; nothing is enumerated again");
    }

    /// <summary>
    /// CL1 item 7: the same twins in the database picker. Two master-table rows naming one connection string are one
    /// database to the studio, which the picker listed twice - two options with one value - and the policy was asked
    /// about twice. It lists it once, as the first instance, and a database elsewhere is still its own entry.
    /// </summary>
    [Fact]
    public async Task The_database_picker_lists_a_twin_rows_database_once()
    {
        StoreOptions options = new();
        options.MultiTenantedDatabasesWithMasterDatabaseTable(UnreachableMaster, "tenants");

        var tenancy = new CountingMasterTableTenancy(options);
        options.Tenancy = tenancy;

        await using var store = new DocumentStore(options);
        await using MartenDatabase first = TenantDatabase(options, "t1", ["t1"]);
        await using MartenDatabase second = TenantDatabase(options, "t2", ["t2"]);
        await using MartenDatabase elsewhere = TenantDatabase(options, "t3", ["t3"], OtherUnreachableTenant);
        tenancy.Listed = [first, second, elsewhere];

        first.Id.Identity.Should().Be(second.Id.Identity, "the premise: one connection string, one identity");

        var users = new TestAuthenticationStateProvider();
        users.SignIn("tester");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<IDocumentStore>(store);
        services.AddMartenStudio(static _ => { });
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        await using ServiceProvider provider = services.BuildServiceProvider();
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        IReadOnlyList<DatabaseListing> listings = await circuit.ServiceProvider.GetRequiredService<IStudioScopeCatalog>()
            .ListDatabasesAsync(MartenStoreRegistry.DefaultStoreKey, Token);

        listings.Select(static x => x.Identity).Should().Equal(
            [first.Id.Identity, elsewhere.Id.Identity],
            "one entry per identity, ignoring case - the first twin, which is the one the resolver matches");
    }

    [Fact]
    public void Only_Martens_master_table_and_sharded_tenancies_are_read_from_the_instance()
    {
        StoreOptions single = new();
        single.Connection(UnreachableTenant);

        StoreOptions partitioned = new();
        partitioned.Connection(UnreachableTenant);
        partitioned.Events.UseTenantPartitionedEvents = true;

        StoreOptions statics = new();
        statics.MultiTenantedDatabases(x => x.AddMultipleTenantDatabase(UnreachableTenant, "tenants-a").ForTenants("acme"));

        StoreOptions singleServer = new();
        singleServer.MultiTenantedWithSingleServer(UnreachableMaster);

        StoreOptions masterTable = new();
        masterTable.MultiTenantedDatabasesWithMasterDatabaseTable(UnreachableMaster, "tenants");

        StoreOptions sharded = new();
        sharded.MultiTenantedWithShardedDatabases(x => x.ConnectionString = UnreachableMaster);

        partitioned.Tenancy.GetType().Name.Should().Be("TenantPartitionedSingleDatabaseTenancy", "the premise");

        TenantDiscovery.ReadsTenantsFromTheInstance(single.Tenancy).Should().BeFalse();
        TenantDiscovery.ReadsTenantsFromTheInstance(partitioned.Tenancy).Should().BeFalse();
        TenantDiscovery.ReadsTenantsFromTheInstance(statics.Tenancy).Should().BeFalse();
        TenantDiscovery.ReadsTenantsFromTheInstance(singleServer.Tenancy)
            .Should().BeFalse("a single-server tenancy's databases list no tenants; only its descriptors carry them");
        TenantDiscovery.ReadsTenantsFromTheInstance(masterTable.Tenancy).Should().BeTrue();
        TenantDiscovery.ReadsTenantsFromTheInstance(sharded.Tenancy).Should().BeTrue();
    }

    /// <summary>A database as a dynamic tenancy builds one, with the tenant list it reconciles onto it.</summary>
    private static MartenDatabase TenantDatabase(
        StoreOptions options,
        string identifier,
        string[] tenantIds,
        string connectionString = UnreachableTenant)
    {
        var database = new MartenDatabase(options, NpgsqlDataSource.Create(connectionString), identifier);
        foreach (string tenantId in tenantIds)
        {
            database.TenantIds.Add(tenantId);
        }

        return database;
    }

    /// <summary>A conjoined document type, so the query tier would have had a table to read.</summary>
    public class TenantedNote
    {
        /// <summary>The id.</summary>
        public Guid Id { get; set; }
    }

    /// <summary>
    /// Marten's master-table tenancy, counting the two calls tier 2 must not make, and listing from memory
    /// whatever databases a test hands it - which is what its <c>BuildDatabases()</c> answers after reading the
    /// master table.
    /// </summary>
    private sealed class CountingMasterTableTenancy(StoreOptions options)
        : MasterTableTenancy(options, UnreachableMaster, "tenants"), ITenancy
    {
        private int builds;
        private int describes;

        public int Builds => Volatile.Read(ref builds);

        public int Describes => Volatile.Read(ref describes);

        /// <summary>What <c>BuildDatabases()</c> answers.</summary>
        public IReadOnlyList<IMartenDatabase> Listed { get; set; } = [];

        public new ValueTask<IReadOnlyList<Weasel.Core.Migrations.IDatabase>> BuildDatabases()
        {
            Interlocked.Increment(ref builds);
            return ValueTask.FromResult<IReadOnlyList<Weasel.Core.Migrations.IDatabase>>([.. Listed]);
        }

        public new ValueTask<DatabaseUsage> DescribeDatabasesAsync(CancellationToken token)
        {
            Interlocked.Increment(ref describes);
            return ValueTask.FromResult(new DatabaseUsage { Cardinality = DatabaseCardinality.DynamicMultiple });
        }
    }

    /// <summary>Marten's sharded tenancy, counting the same two calls.</summary>
    private sealed class CountingShardedTenancy(StoreOptions options, ShardedTenancyOptions configuration)
        : ShardedTenancy(options, configuration), ITenancy
    {
        private int builds;
        private int describes;

        public int Builds => Volatile.Read(ref builds);

        public int Describes => Volatile.Read(ref describes);

        public new ValueTask<IReadOnlyList<Weasel.Core.Migrations.IDatabase>> BuildDatabases()
        {
            Interlocked.Increment(ref builds);
            return ValueTask.FromResult<IReadOnlyList<Weasel.Core.Migrations.IDatabase>>([]);
        }

        public new ValueTask<DatabaseUsage> DescribeDatabasesAsync(CancellationToken token)
        {
            Interlocked.Increment(ref describes);
            return ValueTask.FromResult(new DatabaseUsage { Cardinality = DatabaseCardinality.DynamicMultiple });
        }
    }
}

/// <summary>
/// Any tenancy, counting its <c>DescribeDatabasesAsync</c> calls and forwarding everything. A
/// <see cref="DispatchProxy" /> rather than a subclass because Marten's static and single-database tenancies
/// are built through an internal data source factory; public and unsealed because a proxy type must be.
/// </summary>
public class DescribeCountingTenancy : DispatchProxy
{
    private ITenancy? inner;
    private int describes;

    /// <summary>How many times anything asked the tenancy to describe its databases.</summary>
    public int Describes => Volatile.Read(ref describes);

    /// <summary>
    /// What the describe call answers instead of the tenancy's own, when set - for a tenancy whose own describe would
    /// read a table this suite has no database for.
    /// </summary>
    public Func<DatabaseUsage>? Answer { get; set; }

    internal static DescribeCountingTenancy Over(ITenancy tenancy)
    {
        ITenancy proxy = Create<ITenancy, DescribeCountingTenancy>();
        var counting = (DescribeCountingTenancy) (object) proxy;
        counting.inner = tenancy;
        return counting;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.Name == "DescribeDatabasesAsync")
        {
            Interlocked.Increment(ref describes);

            if (Answer is { } answer)
            {
                // Constructed rather than returned from a call, so it is handed back - boxed, as a proxy must - unconsumed.
                return new ValueTask<DatabaseUsage>(answer());
            }
        }

        return UnlistableStore.Forward(targetMethod, inner!, args);
    }
}
