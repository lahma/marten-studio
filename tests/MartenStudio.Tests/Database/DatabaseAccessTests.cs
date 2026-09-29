using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Acceptance 2: <c>BrowseDatabase</c> against the capability, <c>ReadOnly</c> and the write policy - and
/// the resource the policy is shown, which never carries a tenant.
/// </summary>
/// <remarks>
/// A real studio container over a store that never connects, as <c>CapabilityGatingMatrixTests</c> uses:
/// every refusal asserted here happens before a connection could be opened, so a gate that ran too late
/// would fail on the unreachable host rather than quietly pass.
/// </remarks>
public class DatabaseAccessTests
{
    private const string DummyConnectionString =
        "Host=marten-studio-database-access.invalid;Database=none;Username=none;Password=none";

    private const string StorePolicy = "store-policy";
    private const string WritePolicy = "write-policy";

    /// <summary>The visitor has a tenant selected; nothing the browser reads is that tenant's.</summary>
    private static readonly StudioScope TenantScope = new("default", string.Empty, "acme");

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole truth table: only capability on, <c>ReadOnly</c> off and the policy's yes let a visitor
    /// through, and each refusal is the one the first closed gate owns.
    /// </summary>
    /// <remarks>
    /// The expected refusal is passed by name: xunit needs a public test method, and
    /// <see cref="DatabaseRefusal" /> is internal.
    /// </remarks>
    [Theory]
    [InlineData(false, false, true, nameof(DatabaseRefusal.CapabilityOff))]
    [InlineData(false, false, false, nameof(DatabaseRefusal.CapabilityOff))]
    [InlineData(false, true, true, nameof(DatabaseRefusal.ReadOnly))]
    [InlineData(false, true, false, nameof(DatabaseRefusal.ReadOnly))]
    [InlineData(true, true, true, nameof(DatabaseRefusal.ReadOnly))]
    [InlineData(true, true, false, nameof(DatabaseRefusal.ReadOnly))]
    [InlineData(true, false, false, nameof(DatabaseRefusal.WritePolicy))]
    [InlineData(true, false, true, nameof(DatabaseRefusal.None))]
    public async Task Capability_ReadOnly_and_the_write_policy_decide_in_that_order(
        bool capability,
        bool readOnly,
        bool policyAllows,
        string expectedRefusal)
    {
        DatabaseRefusal expected = Enum.Parse<DatabaseRefusal>(expectedRefusal);

        await using Harness harness = Harness.Create(options =>
        {
            options.Capabilities.BrowseDatabase = capability;
            options.ReadOnly = readOnly;
        });

        harness.Policies.Allow(resource => resource.Capability is null || policyAllows);

        DatabaseBrowseGrant grant = await harness.Access.RequireBrowseAsync(TenantScope, "Test", "quartz.qrtz_triggers", Token);

        grant.Refusal.Should().Be(expected);
        grant.Allowed.Should().Be(expected == DatabaseRefusal.None);

        if (expected == DatabaseRefusal.None)
        {
            grant.Resolved!.Scope.TenantId.Should().BeNull("the scope is resolved as the database as a whole");
            harness.Ring.GetLatest().Should().BeEmpty("a grant is recorded by whoever then reads");
            return;
        }

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be("Test");
        entry.Target.Should().Be("quartz.qrtz_triggers");

        grant.Reason.Should().Contain(expected switch
        {
            DatabaseRefusal.CapabilityOff => "MartenStudioOptions.Capabilities.BrowseDatabase",
            DatabaseRefusal.ReadOnly => "MartenStudioOptions.ReadOnly",
            _ => "WriteAuthorizationPolicy",
        });

        if (expected is DatabaseRefusal.CapabilityOff or DatabaseRefusal.ReadOnly)
        {
            harness.Policies.Calls.Should().BeEmpty(
                "a capability that is off is answered before anybody is asked who is asking");
        }
    }

    /// <summary>
    /// The resource the policy sees has no tenant, although the visitor's scope has one - and the write
    /// policy is asked with the capability named.
    /// </summary>
    [Fact]
    public async Task The_policy_is_asked_about_the_database_as_a_whole_with_no_tenant()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities.BrowseDatabase = true);

        await harness.Access.RequireBrowseAsync(TenantScope, "Test", "x.y", Token);

        harness.Policies.Calls.Should().NotBeEmpty();
        harness.Policies.Calls.Should().OnlyContain(static x => x.Resource.TenantId == null,
            "non-Marten data is not tenant-scoped, so a read of it is a read for every tenant");

        harness.Policies.Calls.Should().Contain(static x =>
            x.Policy == WritePolicy && x.Resource.Capability == nameof(StudioCapability.BrowseDatabase));
        harness.Policies.Calls.Should().Contain(static x => x.Policy == StorePolicy && x.Resource.Capability == null);
    }

    /// <summary>
    /// The page's question asks exactly what the enforcement asks, so a control drawn enabled is one whose
    /// service call passes - and a tenant-restricted handler says no to both.
    /// </summary>
    [Fact]
    public async Task The_pages_question_and_the_enforcement_agree_for_a_tenant_restricted_visitor()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities.BrowseDatabase = true);

        // A handler that only ever lets this visitor see their own tenant - the README's shape.
        harness.Policies.Allow(static resource => resource.TenantId == "acme");

        (bool enabled, bool? authorized) = await harness.Access.EvaluateAsync(TenantScope, Token);

        enabled.Should().BeTrue();
        authorized.Should().BeFalse();

        DatabaseBrowseGrant grant = await harness.Access.RequireBrowseAsync(TenantScope, "Test", "x.y", Token);

        grant.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
    }

    [Fact]
    public async Task With_the_capability_off_the_page_asks_no_policy_at_all()
    {
        await using Harness harness = Harness.Create(static _ => { });

        (bool enabled, bool? authorized) = await harness.Access.EvaluateAsync(TenantScope, Token);

        enabled.Should().BeFalse();
        authorized.Should().BeNull();
        harness.Policies.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The row gate refuses a schema <c>BrowsableSchemas</c> cannot admit from the options alone - before
    /// the database is asked, which on this unreachable host is the only way it can answer at all.
    /// </summary>
    [Fact]
    public async Task The_row_gate_refuses_a_schema_the_list_cannot_admit_before_touching_the_database()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add("quartz");
        });

        DatabaseRowAccessResult result = await harness.Access.RequireRowAccessAsync(TenantScope, "legacy", "orders", cancellationToken: Token);

        result.Allowed.Should().BeFalse();
        result.Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable);
        result.Reason.Should().Contain("MartenStudioOptions.BrowsableSchemas");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be(DatabaseAccess.RowsAction);
        entry.TenantId.Should().BeNull();
        entry.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase));
    }

    /// <summary>
    /// Acceptance 4, fail closed across stores: a registered store that will not build is a store whose
    /// tables would all read as "Other", so the whole classification is refused, named after it.
    /// </summary>
    [Fact]
    public async Task A_registered_store_that_will_not_build_fails_the_classification_closed()
    {
        await using Harness harness = Harness.Create(
            static _ => { },
            static services => services.AddSingleton<IBrokenStore>(
                static _ => throw new InvalidOperationException("its connection string is wrong")));

        StudioScopeResolver resolver = harness.Resolve<StudioScopeResolver>();
        ResolvedScope resolved = await resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        DatabaseDeclarations declarations = harness.Access.ReadDeclarations(resolved);

        declarations.Succeeded.Should().BeFalse();
        declarations.Classifier.Should().BeNull();
        declarations.Failure.Should().Contain(nameof(IBrokenStore)).And.Contain("could not be built");

        DatabaseBrowserOverview overview = await harness.Resolve<IDatabaseObjectService>()
            .GetOverviewAsync(new StudioScope("default", string.Empty, null), Token);

        overview.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        overview.Schemas.Should().BeEmpty("nothing is classified 'Other' when the classification cannot be completed");
        overview.Reason.Should().Contain(nameof(IBrokenStore));
    }

    [Fact]
    public async Task Every_registered_stores_declarations_are_read_and_the_resolved_stores_schemas_are_its_own()
    {
        await using Harness harness = Harness.Create(static _ => { });

        StudioScopeResolver resolver = harness.Resolve<StudioScopeResolver>();
        ResolvedScope resolved = await resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        DatabaseDeclarations declarations = harness.Access.ReadDeclarations(resolved);

        declarations.Succeeded.Should().BeTrue(declarations.Failure);
        declarations.StoreSchemas.Should().Contain("public");
        declarations.Classifier!.Stores.Should().ContainSingle(static x => x.StoreKey == "default");
    }

    /// <summary>
    /// Every failure is a value: a scope the visitor may not have comes back as a result the page draws,
    /// never as an exception - and a read that was refused its scope is not a <c>BrowseDatabase</c> read, so
    /// nothing is gated or audited for it.
    /// </summary>
    [Fact]
    public async Task A_scope_the_visitor_may_not_have_is_a_value_and_not_an_exception()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities.BrowseDatabase = true);
        harness.Policies.DenyEverything();

        IDatabaseObjectService objects = harness.Resolve<IDatabaseObjectService>();

        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(TenantScope, Token);
        overview.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        overview.Reason.Should().Be("Not authorized for the requested store, database or tenant.");

        DatabaseObjectList list = await objects.ListAsync(TenantScope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables), Token);
        list.Refusal.Should().Be(DatabaseRefusal.Unavailable);

        DatabaseObjectDetail detail = await objects.GetObjectAsync(TenantScope, "public", "orders", Token);
        detail.Refusal.Should().Be(DatabaseRefusal.Unavailable);

        DatabaseObjectDefinition definition = await objects.GetDefinitionAsync(
            TenantScope, new DatabaseObjectRef(DatabaseObjectKind.View, "public", "v"), Token);
        definition.Refusal.Should().Be(DatabaseRefusal.Unavailable);

        harness.Ring.GetLatest().Should().BeEmpty();
    }

    [Fact]
    public async Task A_table_a_sequence_and_an_aggregate_have_no_definition_to_ask_for()
    {
        await using Harness harness = Harness.Create(static _ => { });

        IDatabaseObjectService objects = harness.Resolve<IDatabaseObjectService>();

        foreach (DatabaseObjectKind kind in new[] { DatabaseObjectKind.Table, DatabaseObjectKind.Sequence, DatabaseObjectKind.Aggregate })
        {
            DatabaseObjectDefinition definition = await objects.GetDefinitionAsync(
                TenantScope, new DatabaseObjectRef(kind, "public", "anything"), Token);

            definition.Refusal.Should().Be(DatabaseRefusal.NotApplicable, kind.ToString());
        }

        harness.Policies.Calls.Should().BeEmpty("nothing is asked for a request that does not apply");
    }

    /// <summary>A store an application registered that cannot be built.</summary>
    public interface IBrokenStore : IDocumentStore;

    /// <summary>A real studio container over a store that never connects.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, IServiceScope scope, TestStoreAuthorizationService policies)
        {
            this.provider = provider;
            this.scope = scope;
            Policies = policies;
        }

        public TestStoreAuthorizationService Policies { get; }

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public DatabaseAccess Access => Resolve<DatabaseAccess>();

        public T Resolve<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        public static Harness Create(Action<MartenStudioOptions> configure, Action<IServiceCollection>? extra = null)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(x => x.Connection(DummyConnectionString));
            services.AddMartenStudio(options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                configure(options);
            });

            extra?.Invoke(services);

            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            return new Harness(provider, provider.CreateScope(), policies);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
