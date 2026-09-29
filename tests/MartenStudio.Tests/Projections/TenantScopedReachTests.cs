using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// DB-0-fix-2, B1 (release blocker): an operation that reaches a whole database is authorized for that
/// database as a whole, and not for the tenant the scope selector happens to be on.
/// </summary>
/// <remarks>
/// <para>
/// Marten 9.31's tenant overloads of the two progression corrections find the tenant's database and then
/// run an untenanted <c>HighWaterDetector</c> over it: the advance marks the store-global
/// <c>HighWaterMark</c> row, the correction rewrites <c>mt_event_progression</c> with no <c>where</c>.
/// On a conjoined database both reach every tenant in it. DB-0-fix authorized only
/// <c>(store, database, tenant)</c>, so a visitor holding <c>CorrectProgression</c> for <c>acme</c> could
/// advance the mark <c>globex</c>'s async projections are read against, and those projections then skipped
/// their unprocessed events for good. A rebuild, a high-water restart, a store-global agent and the
/// cancelling of a rebuild are database-wide in the same way (<c>DatabaseReach</c> says where each was
/// read), and got the same fix.
/// </para>
/// <para>
/// No database: the store is a connection string that is never opened, every refusal happens before
/// Marten is touched, and each refusal has a positive control that lets the same call through - to fail
/// later for a reason that is not authorization, which is how "refused" is known to be the check's.
/// The live half is <c>TenantScopedCorrectionLiveTests</c>.
/// </para>
/// </remarks>
public class TenantScopedReachTests
{
    private const string SharedConnectionString =
        "Host=marten-studio-tenant-reach.invalid;Database=shared;Username=none;Password=none;Timeout=2";

    private const string PrimaryConnectionString =
        "Host=marten-studio-tenant-reach.invalid;Database=primary;Username=none;Password=none;Timeout=2";

    private const string SecondaryConnectionString =
        "Host=marten-studio-tenant-reach.invalid;Database=secondary;Username=none;Password=none;Timeout=2";

    private const string Acme = "acme";

    private const string Globex = "globex";

    private const string StorePolicy = "studio-store";

    private const string WritePolicy = "studio-write";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Every operation that reaches the whole database, by the action name the audit records.</summary>
    public static TheoryData<string> DatabaseWideOperations =>
    [
        "AdvanceHighWaterMark",
        "CorrectProgression",
        "RebuildProjection",
        "RestartHighWaterAgent",
        "StartAgent",
        "StopAgent",
        "CancelOperation",
    ];

    /// <summary>The two corrections.</summary>
    public static TheoryData<string> Corrections => ["AdvanceHighWaterMark", "CorrectProgression"];

    // ------------------------------------------------------------------------------------------------
    // A conjoined database: the tenant is not the database
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The reviewer's case: a policy that allows <c>(db, acme)</c> and refuses <c>(db, null)</c>. Every
    /// database-wide operation asked from the <c>acme</c> scope is refused, with the refused scope - the
    /// database as a whole - on the exception and in the audit entry.
    /// </summary>
    [Theory]
    [MemberData(nameof(DatabaseWideOperations))]
    public async Task A_tenant_scope_on_a_shared_database_is_refused_an_operation_that_reaches_every_tenant(string action)
    {
        await using Harness harness = await Harness.ConjoinedAsync();
        harness.Policies.Allow(static resource => resource.TenantId is not null);

        Func<Task> running = () => harness.RunAsync(action, harness.Scope(Acme));

        StudioNotAuthorizedException refused = (await running.Should().ThrowAsync<StudioNotAuthorizedException>(
            "Marten would act on every tenant in this database, and the visitor may address only acme")).Which;

        refused.Scope.DatabaseId.Should().Be(harness.Database);
        refused.Scope.TenantId.Should().BeNull("the question refused is the database as a whole");

        StudioActionLogEntry entry = harness.Ring.GetLatest()
            .Should().ContainSingle(x => x.Action == action && !x.Succeeded).Subject;

        entry.DatabaseId.Should().Be(harness.Database);
        entry.TenantId.Should().BeNull("the audit names what was refused, which is the whole database");
    }

    /// <summary>
    /// The positive control: with <c>(db, null)</c> allowed as well, the same call passes the check and
    /// fails later - at the connection, or at the daemon nobody hosts here - for a reason that is not a
    /// scope refusal. And the whole-database question was asked, with the capability on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(DatabaseWideOperations))]
    public async Task With_the_whole_database_allowed_the_same_call_passes_the_check(string action)
    {
        await using Harness harness = await Harness.ConjoinedAsync();

        Exception? thrown = null;
        try
        {
            await harness.RunAsync(action, harness.Scope(Acme));
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        (thrown is StudioNotAuthorizedException).Should().BeFalse("the check let it through: {0}", thrown?.Message);

        harness.Policies.Calls.Should().Contain(
            x => x.Resource.DatabaseIdentifier == harness.Database && x.Resource.TenantId == null && x.Resource.Capability != null,
            "the operation reaches the whole database, and that is what the write policy is asked about");
    }

    /// <summary>
    /// Without a tenant the scope already <em>is</em> the database as a whole, which the resolver
    /// authorized - so a policy that allows it lets every one of these through the check.
    /// </summary>
    [Theory]
    [MemberData(nameof(DatabaseWideOperations))]
    public async Task A_scope_without_a_tenant_is_the_whole_database_and_needs_nothing_more(string action)
    {
        await using Harness harness = await Harness.ConjoinedAsync();
        harness.Policies.Allow(static resource => resource.TenantId is null);

        Exception? thrown = null;
        try
        {
            await harness.RunAsync(action, harness.Scope(tenantId: null));
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        (thrown is StudioNotAuthorizedException).Should().BeFalse("the check let it through: {0}", thrown?.Message);
    }

    // ------------------------------------------------------------------------------------------------
    // Per-agent controls: an agent that is one tenant's
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Under per-tenant partitioning an agent's shard name carries its tenant, and that agent processes
    /// that tenant's events alone: the visitor's own tenant's agent needs no more than the tenant.
    /// </summary>
    [Theory]
    [InlineData("StartAgent")]
    [InlineData("StopAgent")]
    public async Task The_visitors_own_tenants_agent_needs_only_the_tenant(string action)
    {
        await using Harness harness = await Harness.ConjoinedAsync();
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        Func<Task> running = () => harness.RunAsync(action, harness.Scope(Acme), shardName: "DailySales:All:acme");

        // Past the check, to the daemon this process does not host.
        await running.Should().ThrowAsync<StudioDaemonNotHostedException>();
    }

    /// <summary>And another tenant's agent is asked about as that tenant, and refused.</summary>
    [Theory]
    [InlineData("StartAgent")]
    [InlineData("StopAgent")]
    public async Task Another_tenants_agent_is_asked_about_as_that_tenant(string action)
    {
        await using Harness harness = await Harness.ConjoinedAsync();
        harness.Policies.Allow(static resource => resource.TenantId == Acme);

        Func<Task> running = () => harness.RunAsync(action, harness.Scope(Acme), shardName: "DailySales:All:globex");

        StudioNotAuthorizedException refused = (await running.Should().ThrowAsync<StudioNotAuthorizedException>()).Which;
        refused.Scope.TenantId.Should().Be(Globex);
    }

    [Theory]
    [InlineData("DailySales:All:acme", "acme")]
    [InlineData("DailySales:V2:All:acme", "acme")]
    [InlineData("DailySales:All", null)]
    [InlineData("DailySales:acme", null)]
    [InlineData("DailySales", null)]
    [InlineData("", null)]
    public void Only_a_shard_name_that_parses_with_a_tenant_is_one_tenants(string shardName, string? expected) =>
        DatabaseReach.TenantOfShard(shardName).Should().Be(expected);

    // ------------------------------------------------------------------------------------------------
    // Database per tenant: the tenant is the database
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The exception the fix allows: where the tenant's database holds that tenant and nobody else,
    /// reaching the database <em>is</em> reaching the tenant, and the tenant question is the equivalent
    /// one. The whole-database question is not asked at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corrections))]
    public async Task A_database_that_is_exclusively_the_tenants_needs_only_the_tenant(string action)
    {
        await using Harness harness = await Harness.DatabasePerTenantAsync(primaryIsDefault: false);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier == harness.Database && resource.TenantId == Acme);

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
        (thrown is StudioNotAuthorizedException).Should().BeFalse("the database holds acme and no one else");

        harness.Policies.Calls.Should().NotContain(
            x => x.Resource.TenantId == null,
            "the database as a whole is acme's, so there is nothing wider to ask about");
    }

    /// <summary>
    /// A database the tenancy also writes untenanted data to - <c>AsDefault()</c> - is not exclusively
    /// anybody's, and the whole-database question is asked again.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corrections))]
    public async Task The_default_database_is_never_exclusively_one_tenants(string action)
    {
        await using Harness harness = await Harness.DatabasePerTenantAsync(primaryIsDefault: true);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier == harness.Database && resource.TenantId == Acme);

        Func<Task> running = () => harness.RunAsync(action, harness.Scope(Acme));

        (await running.Should().ThrowAsync<StudioNotAuthorizedException>()).Which.Scope.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task Exclusivity_is_decided_by_the_tenants_the_database_lists_and_nothing_else()
    {
        await using (Harness conjoined = await Harness.ConjoinedAsync())
        {
            (await DatabaseReach.IsExclusivelyTenantsAsync(conjoined.Store, conjoined.ScopeDatabase, Acme, Token))
                .Should().BeFalse("a conjoined database lists no tenant: it holds all of them");
        }

        await using (Harness perTenant = await Harness.DatabasePerTenantAsync(primaryIsDefault: false))
        {
            (await DatabaseReach.IsExclusivelyTenantsAsync(perTenant.Store, perTenant.ScopeDatabase, Acme, Token)).Should().BeTrue();
            (await DatabaseReach.IsExclusivelyTenantsAsync(perTenant.Store, perTenant.ScopeDatabase, Globex, Token))
                .Should().BeFalse("globex lives in the other database");
        }

        await using (Harness shared = await Harness.DatabasePerTenantAsync(primaryIsDefault: false, primaryTenants: [Acme, "initech"]))
        {
            (await DatabaseReach.IsExclusivelyTenantsAsync(shared.Store, shared.ScopeDatabase, Acme, Token))
                .Should().BeFalse("a database with two tenants is not either one's");
        }

        await using (Harness twins = await Harness.TwoObjectsOverOneDatabaseAsync())
        {
            (await DatabaseReach.IsExclusivelyTenantsAsync(twins.Store, twins.ScopeDatabase, Acme, Token))
                .Should().BeFalse("another MartenDatabase over the same physical database holds globex");
        }
    }

    // ------------------------------------------------------------------------------------------------
    // The harness
    // ------------------------------------------------------------------------------------------------

    /// <summary>A studio over a store that is never connected to, with the policy in the test's hand.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, IMartenDatabase database)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Policies = policies;
            ScopeDatabase = database;
        }

        public TestStoreAuthorizationService Policies { get; }

        /// <summary>The database every scope here is on.</summary>
        public IMartenDatabase ScopeDatabase { get; }

        /// <summary>Its <c>Id.Identity</c>, which is how the studio addresses it.</summary>
        public string Database => ScopeDatabase.Id.Identity;

        public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        private IProjectionDataService Service => scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        public StudioScope Scope(string? tenantId) => new(MartenStoreRegistry.DefaultStoreKey, Database, tenantId);

        public Task RunAsync(string action, StudioScope on, string shardName = "DailySales:All") => action switch
        {
            "AdvanceHighWaterMark" => Service.AdvanceHighWaterMarkAsync(on, Token),
            "CorrectProgression" => Service.CorrectProgressionAsync(on, Token),
            "RebuildProjection" => Service.RebuildAsync(on, "DailySales", Token),
            "RestartHighWaterAgent" => Service.RestartHighWaterAgentAsync(on, Token),
            "StartAgent" => Service.StartAgentAsync(on, shardName, Token),
            "StopAgent" => Service.StopAgentAsync(on, shardName, Token),
            "CancelOperation" => Service.CancelOperationAsync(on, "op-1", Token),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "not an operation this test knows"),
        };

        /// <summary>One database, every document and the event store conjoined: acme and globex share it.</summary>
        public static Task<Harness> ConjoinedAsync() =>
            CreateAsync(options =>
            {
                options.Connection(SharedConnectionString);
                options.Policies.AllDocumentsAreMultiTenanted();
                options.Events.TenancyStyle = TenancyStyle.Conjoined;
            });

        /// <summary>
        /// Two databases, one per tenant - acme's (or acme's and more) in the primary, globex's in the
        /// secondary - optionally with the primary as the tenancy's default database too.
        /// </summary>
        public static Task<Harness> DatabasePerTenantAsync(bool primaryIsDefault, string[]? primaryTenants = null) =>
            CreateAsync(options => options.MultiTenantedDatabases(tenancy =>
            {
                var primary = tenancy.AddMultipleTenantDatabase(PrimaryConnectionString, "primary").ForTenants(primaryTenants ?? [Acme]);
                if (primaryIsDefault)
                {
                    primary.AsDefault();
                }

                tenancy.AddMultipleTenantDatabase(SecondaryConnectionString, "secondary").ForTenants(Globex);
            }));

        /// <summary>
        /// Two <c>MartenDatabase</c> objects over one physical database, each listing one tenant - which is
        /// what a master table with two rows naming the same connection string builds.
        /// </summary>
        public static Task<Harness> TwoObjectsOverOneDatabaseAsync() =>
            CreateAsync(options => options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(PrimaryConnectionString, "first").ForTenants(Acme);
                tenancy.AddMultipleTenantDatabase(PrimaryConnectionString, "second").ForTenants(Globex);
            }));

        private static async Task<Harness> CreateAsync(Action<StoreOptions> configure)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(configure);

            services.AddMartenStudio(options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                options.KnownTenantIds.Add(Acme);
                options.KnownTenantIds.Add(Globex);
            });

            // After AddMartenStudio, so these win over anything the framework registrations contributed.
            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            // acme's database where the tenancy says which that is; the one database of a conjoined store
            // otherwise. Never databases[0] of several: a static tenancy enumerates them in hash order.
            IMartenDatabase database = databases.FirstOrDefault(static x => x.TenantIds.Contains(Acme)) ?? databases[0];

            return new Harness(provider, policies, database);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
