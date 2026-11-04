using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// 1.2-PROJ (a): removing an orphaned progression row is a <c>CorrectProgression</c> write, asked about the database as a
/// whole whatever tenant the selector is on, and every refusal that does not need the table happens before Marten is touched.
/// </summary>
/// <remarks>
/// No database: the store is two connection strings that are never opened. The refusals here are all the check's, and
/// <see cref="With_everything_allowed_the_removal_goes_on_to_read_the_table" /> is the anti-vacuity half - it shows the
/// chain lets an allowed call through, to the read of the progression table, which then fails to connect. What the re-check
/// finds in the table, what Marten then deletes and what the audit says of it are
/// <c>OrphanProgressionRemovalLiveTests</c>'s.
/// </remarks>
public class OrphanProgressionRemovalTests
{
    private const string PrimaryConnectionString =
        "Host=marten-studio-orphan-removal.invalid;Database=primary;Username=none;Password=none;Timeout=2";

    private const string SecondaryConnectionString =
        "Host=marten-studio-orphan-removal.invalid;Database=secondary;Username=none;Password=none;Timeout=2";

    private const string PrimaryTenant = "tenant-primary";

    private const string SecondaryTenant = "tenant-secondary";

    private const string StorePolicy = "studio-store";

    private const string WritePolicy = "studio-write";

    private const string Row = "RetiredProjection:All";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_the_capability_the_removal_is_refused_before_a_scope_is_resolved_and_audited_as_denied()
    {
        await using Harness harness = await Harness.CreateAsync(correctProgression: false);

        Func<Task> removing = () => harness.Service.RemoveOrphanedProgressionAsync(harness.PrimaryScope(null), Row, Token);

        (await removing.Should().ThrowAsync<StudioCapabilityDeniedException>())
            .Which.Message.Should().Contain("CorrectProgression");

        harness.Policies.Calls.Should().BeEmpty("the capability guard refuses before any policy is asked");

        harness.Ring.GetLatest().Should().ContainSingle(x => x.Action == ProjectionDataService.RemoveOrphanAction && !x.Succeeded);
    }

    [Fact]
    public async Task A_visitor_the_write_policy_refuses_the_database_is_refused_and_audited()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource => resource.Capability is null);

        Func<Task> removing = () => harness.Service.RemoveOrphanedProgressionAsync(harness.PrimaryScope(null), Row, Token);

        await removing.Should().ThrowAsync<StudioNotAuthorizedException>();

        harness.Ring.GetLatest().Should().ContainSingle(x => x.Action == ProjectionDataService.RemoveOrphanAction && !x.Succeeded);
    }

    /// <summary>
    /// The row is the database's bookkeeping, listed by name only to a visitor the store policy admits for the database as a
    /// whole (<c>TenantId = null</c>). A visitor allowed one tenant of a conjoined database, and nothing wider, must not be
    /// able to remove it: the question that refuses them is asked with no tenant, though the selector is on one.
    /// </summary>
    [Fact]
    public async Task A_tenant_scope_still_asks_the_database_as_a_whole_with_no_tenant()
    {
        await using Harness harness = await Harness.CreateAsync();
        harness.Policies.Allow(static resource => resource.TenantId == PrimaryTenant);

        Func<Task> removing = () => harness.Service.RemoveOrphanedProgressionAsync(harness.PrimaryScope(PrimaryTenant), Row, Token);

        StudioNotAuthorizedException refused = (await removing.Should().ThrowAsync<StudioNotAuthorizedException>(
            "the visitor may address the tenant, and not the database as a whole")).Which;

        refused.Scope.TenantId.Should().BeNull("the refused question is the database's, whatever tenant is selected");
        refused.Scope.DatabaseId.Should().Be(harness.Primary);

        // The store policy is asked first, with no capability, and refuses the database; the write policy is never reached.
        harness.Policies.Calls
            .Where(x => x.Resource.DatabaseIdentifier == harness.Primary)
            .Select(static x => x.Resource.TenantId)
            .Should().Contain([PrimaryTenant, null], "the tenant question, then the database's");
    }

    [Fact]
    public async Task With_everything_allowed_the_removal_goes_on_to_read_the_table()
    {
        await using Harness harness = await Harness.CreateAsync();

        Func<Task> removing = () => harness.Service.RemoveOrphanedProgressionAsync(harness.PrimaryScope(null), Row, Token);

        (await removing.Should().ThrowAsync<Exception>("nothing is listening at these connection strings"))
            .Which.Should().NotBeOfType<StudioNotAuthorizedException>().And.NotBeOfType<StudioCapabilityDeniedException>();

        harness.Ring.GetLatest().Should().ContainSingle(x => x.Action == ProjectionDataService.RemoveOrphanAction && !x.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_not_a_row(string name)
    {
        await using Harness harness = await Harness.CreateAsync();

        Func<Task> removing = () => harness.Service.RemoveOrphanedProgressionAsync(harness.PrimaryScope(null), name, Token);

        await removing.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>A two-database studio over connection strings that are never opened, with the policy in the test's hand.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, string primary)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Policies = policies;
            Primary = primary;
        }

        public TestStoreAuthorizationService Policies { get; }

        /// <summary>The primary database's <c>Id.Identity</c>, which is how the studio addresses it.</summary>
        public string Primary { get; }

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public IProjectionDataService Service => scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        public StudioScope PrimaryScope(string? tenantId) =>
            new(MartenStoreRegistry.DefaultStoreKey, Primary, tenantId);

        public static async Task<Harness> CreateAsync(bool correctProgression = true)
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
                options.Capabilities = new MartenStudioCapabilities { CorrectProgression = correctProgression };
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                options.KnownTenantIds.Add(PrimaryTenant);
                options.KnownTenantIds.Add(SecondaryTenant);
            });

            // After AddMartenStudio, so these win over anything the framework registrations contributed.
            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();
            string primary = databases.Single(static x => x.Id.Name == "primary").Id.Identity;

            return new Harness(provider, policies, primary);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
