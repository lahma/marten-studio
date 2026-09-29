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
/// DB-0-fix, item 9: a progression correction is authorized for every database it reaches, not only
/// for the one in the URL.
/// </summary>
/// <remarks>
/// <para>
/// Marten 9.31's <c>AdvanceHighWaterMarkToLatestAsync(token)</c> and
/// <c>TryCorrectProgressInDatabaseAsync(token)</c> - the overloads without a tenant - loop over
/// <c>Tenancy.BuildDatabases()</c>, and the per-database type underneath is internal. The studio resolved
/// and authorized one database and then called them, so on a two-database store a visitor allowed only
/// the primary could move the secondary's high-water mark - which makes every async projection there
/// treat the events it has not processed as already gone.
/// </para>
/// <para>
/// No database: the store is two connection strings that are never opened, and every refusal here
/// happens before Marten is called. <see cref="With_every_database_allowed_the_correction_goes_through_to_Marten" />
/// is the anti-vacuity half - it shows the check lets an allowed call through, which is how "refused"
/// is known to mean "refused by the check" rather than "refused by everything". The live half, against
/// a real two-database store and its running daemon, is in <c>MultiDatabaseDaemonLiveTests</c>.
/// </para>
/// </remarks>
public class ProgressionCorrectionReachTests
{
    private const string PrimaryConnectionString =
        "Host=marten-studio-correction-reach.invalid;Database=primary;Username=none;Password=none;Timeout=2";

    private const string SecondaryConnectionString =
        "Host=marten-studio-correction-reach.invalid;Database=secondary;Username=none;Password=none;Timeout=2";

    private const string PrimaryTenant = "tenant-primary";

    private const string SecondaryTenant = "tenant-secondary";

    private const string StorePolicy = "studio-store";

    private const string WritePolicy = "studio-write";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The two corrections, by the action name the audit records them under.</summary>
    public static TheoryData<string> Corrections => ["AdvanceHighWaterMark", "CorrectProgression"];

    /// <summary>The corrections and the coordinator pause, which reach every database in the same way.</summary>
    public static TheoryData<string> StoreWideOperations => ["AdvanceHighWaterMark", "CorrectProgression", "PauseDaemon"];

    [Theory]
    [MemberData(nameof(Corrections))]
    public async Task A_correction_without_a_tenant_is_refused_unless_the_visitor_may_reach_every_database(string action)
    {
        await using Harness harness = await Harness.CreateAsync(storePolicy: StorePolicy, writePolicy: WritePolicy);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Secondary);

        Func<Task> correcting = () => harness.RunAsync(action, harness.PrimaryScope(tenantId: null));

        await correcting.Should().ThrowAsync<StudioNotAuthorizedException>(
            "Marten runs this over every database of the store, and the visitor may not reach the secondary");

        StudioActionLogEntry refusal = harness.Ring.GetLatest()
            .Should().ContainSingle(x => x.Action == action && !x.Succeeded).Subject;

        refusal.DatabaseId.Should().Be(harness.Secondary, "the audit names the database that was refused, not the one asked for");
    }

    /// <summary>
    /// The old guard short-circuited on the store policy alone, so a host that configured only a write
    /// policy scoping by database had the per-database question skipped entirely - for a pause as well as
    /// for a correction.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreWideOperations))]
    public async Task A_write_policy_on_its_own_is_asked_about_every_database_too(string action)
    {
        await using Harness harness = await Harness.CreateAsync(storePolicy: null, writePolicy: WritePolicy);
        harness.Policies.Allow(resource => resource.Capability is null || resource.DatabaseIdentifier != harness.Secondary);

        Func<Task> running = () => harness.RunAsync(action, harness.PrimaryScope(tenantId: null));

        (await running.Should().ThrowAsync<StudioNotAuthorizedException>())
            .Which.Scope.DatabaseId.Should().Be(harness.Secondary);
    }

    /// <summary>
    /// With a tenant, Marten corrects the tenant's database - found through the tenancy, and not
    /// necessarily the one in the URL: a host's <c>KnownTenantIds</c> are one list for every database,
    /// and the resolver accepts any of them for any database. The database Marten would reach is the one
    /// asked about, with that tenant.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corrections))]
    public async Task A_tenant_correction_is_asked_about_the_tenants_own_database(string action)
    {
        await using Harness harness = await Harness.CreateAsync(storePolicy: StorePolicy, writePolicy: WritePolicy);
        harness.Policies.Allow(resource => resource.DatabaseIdentifier != harness.Secondary);

        Func<Task> correcting = () => harness.RunAsync(action, harness.PrimaryScope(SecondaryTenant));

        StudioNotAuthorizedException refused = (await correcting.Should().ThrowAsync<StudioNotAuthorizedException>(
            "the tenant lives in the secondary, which is where Marten would write")).Which;

        refused.Scope.DatabaseId.Should().Be(harness.Secondary);
        refused.Scope.TenantId.Should().Be(SecondaryTenant);
    }

    /// <summary>
    /// The anti-vacuity half: with every database allowed the check asks about both and lets the call
    /// through to Marten - which then fails to connect, because these databases do not exist. That failure
    /// is the proof that the refusals above came from the check.
    /// </summary>
    [Theory]
    [MemberData(nameof(Corrections))]
    public async Task With_every_database_allowed_the_correction_goes_through_to_Marten(string action)
    {
        await using Harness harness = await Harness.CreateAsync(storePolicy: StorePolicy, writePolicy: WritePolicy);

        Func<Task> correcting = () => harness.RunAsync(action, harness.PrimaryScope(tenantId: null));

        (await correcting.Should().ThrowAsync<Exception>("nothing is listening at these connection strings"))
            .Which.Should().NotBeOfType<StudioNotAuthorizedException>();

        harness.Policies.Calls
            .Where(static x => x.Resource.Capability == "CorrectProgression")
            .Select(static x => x.Resource.DatabaseIdentifier)
            .Should().Contain([harness.Primary, harness.Secondary], "every database the call reaches is asked about");
    }

    /// <summary>
    /// A two-database studio over connection strings that are never opened, with the policy in the test's
    /// hand.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, string primary, string secondary)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Policies = policies;
            Primary = primary;
            Secondary = secondary;
        }

        public TestStoreAuthorizationService Policies { get; }

        /// <summary>The primary database's <c>Id.Identity</c>, which is how the studio addresses it.</summary>
        public string Primary { get; }

        /// <summary>The secondary database's <c>Id.Identity</c>.</summary>
        public string Secondary { get; }

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        private IProjectionDataService Service => scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        public StudioScope PrimaryScope(string? tenantId) =>
            new(MartenStoreRegistry.DefaultStoreKey, Primary, tenantId);

        public Task RunAsync(string action, StudioScope on) => action switch
        {
            "AdvanceHighWaterMark" => Service.AdvanceHighWaterMarkAsync(on, Token),
            "CorrectProgression" => Service.CorrectProgressionAsync(on, Token),
            "PauseDaemon" => Service.PauseDaemonAsync(on, Token),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "not an operation this test knows"),
        };

        public static async Task<Harness> CreateAsync(string? storePolicy, string? writePolicy)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(options => options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(PrimaryConnectionString, "primary").ForTenants(PrimaryTenant);
                tenancy.AddMultipleTenantDatabase(SecondaryConnectionString, "secondary").ForTenants(SecondaryTenant);
            }));

            services.AddMartenStudio(options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.StoreAuthorizationPolicy = storePolicy;
                options.WriteAuthorizationPolicy = writePolicy;

                // One list for every database - which is exactly why the resolver accepting a tenant for a
                // database does not prove the tenant lives there.
                options.KnownTenantIds.Add(PrimaryTenant);
                options.KnownTenantIds.Add(SecondaryTenant);
            });

            // After AddMartenStudio, so these win over anything the framework registrations contributed.
            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();
            databases.Should().HaveCount(2);

            string primary = databases.Single(static x => x.Id.Name == "primary").Id.Identity;
            string secondary = databases.Single(static x => x.Id.Name == "secondary").Id.Identity;

            return new Harness(provider, policies, primary, secondary);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
