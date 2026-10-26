using Marten;

using MartenStudio.Tests.Health;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Storage;

namespace MartenStudio.Tests.Storage;

/// <summary>
/// The Storage page's authorization (W5-CAP-fix item 4), through a real studio container over a store that never connects:
/// the visitor's tenant is dropped, the store policy is asked about the database as a whole, asked again under the registered
/// key and the database's own identity when the visitor spelt either otherwise, and a refusal reads no facts.
/// </summary>
public class StorageServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_tenant_is_dropped_and_every_question_is_about_the_database_as_a_whole()
    {
        await using Harness harness = Harness.Create();
        harness.Policies.Allow(static _ => true);

        StorageView view = await harness.Service.GetAsync(new StudioScope("default", harness.Identity, "tenant-a"), Token);

        view.Refusal.Should().Be(DatabaseRefusal.None);
        harness.Policies.Calls.Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null,
            "sizes and growth are the database's, which no tenant narrows");
        harness.Policies.Calls.Should().OnlyContain(static x => x.Resource.Capability == null, "nothing here is a write");
        harness.Facts.Reads.Should().Be(1);
        harness.Facts.Asked.Should().ContainSingle().Which.Should().Be(("default", harness.Identity));
    }

    [Fact]
    public async Task A_scope_spelt_otherwise_is_asked_again_under_the_registered_key_and_the_identity()
    {
        await using Harness harness = Harness.Create();
        harness.Policies.Allow(static _ => true);
        string identity = harness.Identity;

        StorageView view = await harness.Service.GetAsync(new StudioScope("DEFAULT", string.Empty, "tenant-a"), Token);

        view.Refusal.Should().Be(DatabaseRefusal.None);
        harness.Policies.Calls.Should().Contain(static x => x.Resource.StoreName == "DEFAULT" && x.Resource.DatabaseIdentifier.Length == 0,
            "the resolver asks about the scope as the visitor spelt it");
        harness.Policies.Calls.Should().Contain(x => x.Resource.StoreName == "default" && x.Resource.DatabaseIdentifier == identity,
            "and the page asks again about the resource its facts are keyed on");
        harness.Policies.Calls.Should().OnlyContain(static x => x.Resource.TenantId == null);
        harness.Facts.Asked.Should().ContainSingle().Which.Should().Be(("default", identity));
    }

    [Fact]
    public async Task A_policy_that_passes_only_the_visitors_spelling_is_refused_and_reads_no_facts()
    {
        await using Harness harness = Harness.Create();
        harness.Policies.Allow(static resource => resource.DatabaseIdentifier.Length == 0);

        StorageView view = await harness.Service.GetAsync(new StudioScope("default", string.Empty, null), Token);

        view.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        view.Reason.Should().Contain("MartenStudioOptions.StoreAuthorizationPolicy");
        harness.Facts.Reads.Should().Be(0, "a refusal is decided before anything is read");
    }

    [Fact]
    public async Task A_store_policy_refusal_reads_no_facts()
    {
        await using Harness harness = Harness.Create();
        harness.Policies.Allow(static _ => false);

        StorageView view = await harness.Service.GetAsync(new StudioScope("default", harness.Identity, "tenant-a"), Token);

        view.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        harness.Facts.Reads.Should().Be(0);
        harness.Policies.Calls.Should().ContainSingle().Which.Resource.TenantId.Should().BeNull("the tenant was dropped before the first question");
    }

    [Fact]
    public async Task With_the_same_spelling_the_resolvers_question_is_the_only_one()
    {
        await using Harness harness = Harness.Create();
        harness.Policies.Allow(static _ => true);

        await harness.Service.GetAsync(new StudioScope("default", harness.Identity, null), Token);

        harness.Policies.Calls.Should().ContainSingle();
        harness.Facts.Reads.Should().Be(1);
    }

    /// <summary>The facts read, counted: what it was asked for, and an empty database's facts back.</summary>
    private sealed class CountingFacts : IStorageFacts
    {
        private int reads;

        public int Reads => Volatile.Read(ref reads);

        public List<(string StoreKey, string DatabaseId)> Asked { get; } = [];

        public Task<StorageFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref reads);
            Asked.Add((resolved.Source.Key, resolved.Target.Identity));

            return Task.FromResult(StorageFacts.Build(
                resolved.Source.Key,
                resolved.Target.Identity,
                new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
                window: null,
                new StorageDatabaseRow(DatabaseBytes: 1L << 20, EstimatedBytes: 1L << 20, BlockSize: 8192, ServerVersion: 170_011),
                new EmptySet(),
                []));
        }
    }

    private sealed class EmptySet : IStudioTableSet
    {
        public IReadOnlyList<StudioTable> Tables => [];

        public string? Coverage => null;

        public StudioTable? Find(string schema, string table) => null;

        public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table) => IndexOrigin.Host;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly AsyncServiceScope circuit;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, CountingFacts facts)
        {
            Provider = provider;
            Policies = policies;
            Facts = facts;
            circuit = provider.CreateAsyncScope();
        }

        public ServiceProvider Provider { get; }

        public TestStoreAuthorizationService Policies { get; }

        public CountingFacts Facts { get; }

        public IStorageService Service => circuit.ServiceProvider.GetRequiredService<IStorageService>();

        /// <summary>The store's one database's own identity, which an empty <c>?db=</c> resolves to.</summary>
        public string Identity => Provider.GetRequiredService<IDocumentStore>().Storage.Database.Id.Identity;

        public static Harness Create()
        {
            TestAuthenticationStateProvider users = new();
            users.SignIn("operator");

            TestStoreAuthorizationService authorization = new();
            CountingFacts facts = new();

            ServiceCollection services = new();
            services.AddLogging();
            services.AddMarten(static options =>
            {
                options.Connection(HealthHarness.RefusedConnectionString);
                options.DatabaseSchemaName = "storage_unit";
            });

            services.AddMartenStudio(static options =>
            {
                options.StoreAuthorizationPolicy = "store-policy";
                options.WriteAuthorizationPolicy = "write-policy";
            });

            services.AddSingleton<IAuthorizationService>(authorization);
            services.AddScoped<AuthenticationStateProvider>(_ => users);
            services.AddSingleton<IStorageFacts>(facts);

            return new Harness(services.BuildServiceProvider(), authorization, facts);
        }

        public async ValueTask DisposeAsync()
        {
            await circuit.DisposeAsync();
            await Provider.DisposeAsync();
        }
    }
}
