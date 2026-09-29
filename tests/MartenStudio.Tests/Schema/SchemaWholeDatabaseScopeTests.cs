using JasperFx.MultiTenancy;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;
using MartenStudio.Tests.Sql;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// Everything the Schema screen runs against a database is the whole database's - every tenant's tables and
/// partitions - whichever tenant the visitor has selected, so the policies are asked about the database with
/// no tenant, and a visitor a policy restricts to one tenant is refused. No Postgres: every refusal here
/// happens before the database is asked anything, which is the point.
/// </summary>
/// <remarks>
/// The re-review of DB-0-fix-2 (R2): an apply from <c>(db, acme)</c> used to ask the write policy only about
/// <c>(db, acme)</c>, and then ran <c>CreateOrUpdate</c> - which can drop indexes and columns and rewrite a
/// partitioned table - for every tenant. The store policy's half is DB-7-fix's first item.
/// </remarks>
public class SchemaWholeDatabaseScopeTests
{
    private const string StorePolicy = "studio-store";

    private const string WritePolicy = "studio-write";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Allow <c>(db, acme)</c>, refuse <c>(db, null)</c>: the apply is refused, about the tenant-less scope.</summary>
    [Fact]
    public async Task An_apply_from_one_tenants_scope_is_refused_when_only_that_tenant_is_allowed()
    {
        await using Harness harness = await Harness.CreateAsync(static resource => resource.TenantId == "acme");

        Func<Task> apply = () => harness.Schema.ApplyAsync(harness.Acme, harness.DatabaseId, Token);

        StudioNotAuthorizedException refused = (await apply.Should().ThrowAsync<StudioNotAuthorizedException>()).Which;
        refused.Scope.TenantId.Should().BeNull("an apply migrates the database as a whole, and that is what was refused");
        refused.Scope.DatabaseId.Should().Be(harness.DatabaseId);

        harness.Policies.Calls.Should().Contain(static x => x.Resource.TenantId == null,
            "the database as a whole is the question");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should()
            .Contain(static x => x.Action == "Apply schema changes").Which;
        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().BeNull("the refusal is audited against the tenant-less scope");
    }

    /// <summary>
    /// The store policy lets the visitor see everything, and the write policy lets them apply schema changes
    /// for one tenant: still refused, by the write policy, asked with no tenant - and never asked about the
    /// visitor's own tenant at all.
    /// </summary>
    [Fact]
    public async Task An_apply_asks_the_write_policy_about_the_database_with_no_tenant()
    {
        await using Harness harness = await Harness.CreateAsync(static resource => resource.Capability is null || resource.TenantId == "acme");

        Func<Task> apply = () => harness.Schema.ApplyAsync(harness.Acme, harness.DatabaseId, Token);

        (await apply.Should().ThrowAsync<StudioNotAuthorizedException>()).Which.Scope.TenantId.Should().BeNull();

        harness.Policies.Calls.Should().Contain(static x =>
            x.Policy == WritePolicy && x.Resource.Capability == nameof(StudioCapability.ApplySchemaChanges) && x.Resource.TenantId == null);
        harness.Policies.Calls.Should().NotContain(static x =>
            x.Resource.Capability == nameof(StudioCapability.ApplySchemaChanges) && x.Resource.TenantId != null);

        harness.Ring.GetLatest().Should().Contain(static x => x.Action == "Apply schema changes" && !x.Succeeded && x.TenantId == null);
    }

    /// <summary>The same visitor is refused the script, the check and the preview, as values naming the store policy.</summary>
    [Fact]
    public async Task The_script_the_check_and_the_preview_are_refused_to_a_visitor_allowed_one_tenant()
    {
        await using Harness harness = await Harness.CreateAsync(static resource => resource.TenantId == "acme");

        DdlScript ddl = await harness.Schema.DdlAsync(harness.Acme, Token);
        SchemaCheck check = await harness.Schema.CheckAsync(harness.Acme, Token);
        MigrationPreview preview = await harness.Schema.PreviewAsync(harness.Acme, Token);

        ddl.Withheld!.Kind.Should().Be(DatabaseRefusal.StorePolicy);
        check.Withheld!.Kind.Should().Be(DatabaseRefusal.StorePolicy);
        preview.Withheld!.Kind.Should().Be(DatabaseRefusal.StorePolicy);

        harness.Policies.Calls.Should().OnlyContain(static x => x.Resource.TenantId == null,
            "the store policy is asked about the database as a whole first, before any tenant is looked up");
        harness.Ring.GetLatest().Where(static x => x.Action is "Generate schema script" or "Check schema" or "Preview schema migration")
            .Should().HaveCount(3).And.OnlyContain(static x => !x.Succeeded && x.TenantId == null);
    }

    /// <summary>A store over an unreachable host, two known tenants, every capability, and the two policies.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies, string databaseId)
        {
            this.provider = provider;
            Policies = policies;
            DatabaseId = databaseId;
            scope = provider.CreateAsyncScope();
            Schema = scope.ServiceProvider.GetRequiredService<ISchemaDataService>();
        }

        public TestStoreAuthorizationService Policies { get; }

        public string DatabaseId { get; }

        public ISchemaDataService Schema { get; }

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public StudioScope Acme => new(MartenStoreRegistry.DefaultStoreKey, DatabaseId, "acme");

        public static async Task<Harness> CreateAsync(Func<MartenStoreResource, bool> rule)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(options =>
            {
                options.Connection(SqlTestStore.Unreachable);
                options.Policies.AllDocumentsAreMultiTenanted();
                options.Events.TenancyStyle = TenancyStyle.Conjoined;
            });
            services.AddMartenStudio(options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                options.KnownTenantIds.Add("acme");
                options.KnownTenantIds.Add("globex");
            });
            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();
            policies.Allow(rule);

            return new Harness(provider, policies, databases[0].Id.Identity);
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }
}
