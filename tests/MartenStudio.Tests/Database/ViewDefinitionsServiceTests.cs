using Marten;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Issue #22, the enforcement half (AGENTS.md hard rule 5): a definition's read goes through the capability, then the
/// store policy, then the write policy asked with the capability's name, against the database as a whole - and is audited
/// when refused - for either way in, <c>ViewDefinitions</c> or <c>BrowseDatabase</c>; a row's read goes through
/// <c>BrowseDatabase</c> alone. A real studio container over a store that never connects, as
/// <see cref="DatabaseAccessTests" /> uses: every refusal asserted here happens before a connection could be opened. The
/// reads that pass are the live suite's.
/// </summary>
public class ViewDefinitionsServiceTests
{
    private const string DummyConnectionString =
        "Host=marten-studio-view-definitions.invalid;Database=none;Username=none;Password=none";

    private const string StorePolicy = "store-policy";
    private const string WritePolicy = "write-policy";

    /// <summary>The visitor has a tenant selected; nothing the browser reads is that tenant's.</summary>
    private static readonly StudioScope TenantScope = new("default", string.Empty, "acme");

    private static readonly DatabaseObjectRef Definition = new(DatabaseObjectKind.View, "legacy", "job_summary");

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_write_policy_that_refuses_ViewDefinitions_refuses_the_definition_and_audits_it_under_ViewDefinitions()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities.ViewDefinitions = true;
            options.BrowsableSchemas.Add("legacy");
        });

        harness.Policies.Allow(static resource => resource.Capability != nameof(StudioCapability.ViewDefinitions));

        DatabaseObjectDefinition definition = await harness.Objects.GetDefinitionAsync(TenantScope, Definition, Token);

        definition.Found.Should().BeFalse();
        definition.Sql.Should().BeNull();
        definition.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        definition.Reason.Should().Be(StudioText.Marten.DefinitionWritePolicyDenialFor("ViewDefinitions"),
            "only the capability that is on was asked, and the sentence says so");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Action.Should().Be(DatabaseAccess.DefinitionAction);
        entry.Succeeded.Should().BeFalse();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDefinitions));
        entry.TenantId.Should().BeNull("the refused scope is the database as a whole");
        entry.Target.Should().Be("legacy.job_summary");

        harness.Policies.Calls.Where(static x => x.Resource.Capability is not null).Select(static x => x.Resource.Capability)
            .Should().OnlyContain(static x => x == nameof(StudioCapability.ViewDefinitions), "BrowseDatabase is off, so nobody is asked about it");
        harness.Policies.Calls.Where(static x => x.Resource.Capability == nameof(StudioCapability.ViewDefinitions))
            .Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null,
                "a definition is not tenant-scoped, and the policy is asked about the database as a whole");
    }

    [Fact]
    public async Task Both_capabilities_on_asks_the_write_policy_about_each_and_a_refusal_by_both_names_both()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities.ViewDefinitions = true;
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add("legacy");
        });

        harness.Policies.Allow(static resource => resource.Capability is null);

        DatabaseObjectDefinition definition = await harness.Objects.GetDefinitionAsync(TenantScope, Definition, Token);

        definition.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        definition.Reason.Should().Be(StudioText.Marten.DefinitionWritePolicyDenialFor("ViewDefinitions and BrowseDatabase"));

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase),
            "recorded under the first way in that is on, so a host that has not heard of ViewDefinitions sees nothing move");

        harness.Policies.Calls.Where(static x => x.Resource.Capability is not null).Select(static x => x.Resource.Capability)
            .Distinct().Should().BeEquivalentTo(nameof(StudioCapability.BrowseDatabase), nameof(StudioCapability.ViewDefinitions));
        harness.Policies.Calls.Where(static x => x.Resource.Capability is not null)
            .Should().OnlyContain(static x => x.Resource.TenantId == null);
    }

    [Fact]
    public async Task A_store_policy_that_refuses_the_database_refuses_the_definition_before_the_write_policy_is_asked()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities.ViewDefinitions = true;
            options.BrowsableSchemas.Add("legacy");
        });

        // Passes for the visitor's own scope (a tenant selected) and refuses the database as a whole.
        harness.Policies.Allow(static resource => resource.TenantId is not null);

        DatabaseObjectDefinition definition = await harness.Objects.GetDefinitionAsync(TenantScope, Definition, Token);

        definition.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        definition.Reason.Should().Be(StudioText.Marten.BrowseStorePolicyDenial);
        harness.Ring.GetLatest().Should().ContainSingle().Which.Capability.Should().Be(nameof(StudioCapability.ViewDefinitions));
        harness.Policies.Calls.Should().NotContain(static x => x.Resource.Capability == nameof(StudioCapability.ViewDefinitions),
            "the store policy answers for the database before any capability is asked about it");
    }

    [Fact]
    public async Task With_neither_capability_on_a_definition_is_refused_naming_both_and_audited_as_BrowseDatabase_always_was()
    {
        await using Harness harness = Harness.Create(static options => options.BrowsableSchemas.Add("legacy"));

        DatabaseObjectDefinition definition = await harness.Objects.GetDefinitionAsync(TenantScope, Definition, Token);

        definition.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        definition.Reason.Should().Be(StudioText.Marten.DefinitionCapabilityDenial);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase));
        entry.Succeeded.Should().BeFalse();

        harness.Policies.Calls.Should().NotContain(static x => x.Resource.Capability != null,
            "a capability that is off is answered before anybody is asked who is asking");
    }

    [Fact]
    public async Task ReadOnly_refuses_a_definition_whatever_is_enabled_and_names_both_ways_in()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.ReadOnly = true;
            options.BrowsableSchemas.Add("legacy");
        });

        DatabaseObjectDefinition definition = await harness.Objects.GetDefinitionAsync(TenantScope, Definition, Token);
        definition.Refusal.Should().Be(DatabaseRefusal.ReadOnly);
        definition.Reason.Should().Be(StudioText.Marten.DefinitionReadOnlyDenial);

        DatabaseSequenceValue value = await harness.Objects.GetSequenceValueAsync(TenantScope, "legacy", "orders_id_seq", Token);
        value.Refusal.Should().Be(DatabaseRefusal.ReadOnly, "a sequence's value is the definitions' half of the gate");

        harness.Ring.GetLatest().Should().HaveCount(2).And.OnlyContain(static x => !x.Succeeded);
    }

    [Fact]
    public async Task ViewDefinitions_never_opens_a_row_read()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities.ViewDefinitions = true;
            options.BrowsableSchemas.Add("legacy");
        });

        DatabaseRowAccessResult rows = await harness.Access.RequireRowAccessAsync(TenantScope, "legacy", "orders", cancellationToken: Token);

        rows.Allowed.Should().BeFalse();
        rows.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        rows.Reason.Should().Be(StudioText.Marten.BrowseCapabilityDenial, "rows are refused naming BrowseDatabase, in 0.4's sentence");

        harness.Ring.GetLatest().Should().ContainSingle().Which.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase));
        harness.Policies.Calls.Should().NotContain(static x => x.Resource.Capability == nameof(StudioCapability.ViewDefinitions),
            "a row read asks the write policy about BrowseDatabase alone");
    }

    [Fact]
    public async Task The_gate_asks_the_write_policy_about_ViewDefinitions_only_when_it_is_on()
    {
        await using Harness off = Harness.Create(static options => options.Capabilities.BrowseDatabase = true);

        DatabasePolicyAnswer none = await off.Access.EvaluateDefinitionPoliciesAsync(
            new StudioScope("default", string.Empty, null),
            new DatabasePolicyAnswer(true, true, DatabaseRefusal.None),
            Token);

        none.Should().Be(new DatabasePolicyAnswer(false, null, DatabaseRefusal.None));
        off.Policies.Calls.Should().BeEmpty("a host that never enabled ViewDefinitions sees no new policy question");

        await using Harness on = Harness.Create(static options => options.Capabilities.ViewDefinitions = true);
        on.Policies.Allow(static resource => resource.Capability != nameof(StudioCapability.ViewDefinitions));

        DatabasePolicyAnswer refused = await on.Access.EvaluateDefinitionPoliciesAsync(
            new StudioScope("default", string.Empty, "acme"),
            new DatabasePolicyAnswer(false, null, DatabaseRefusal.None),
            Token);

        refused.Should().Be(new DatabasePolicyAnswer(true, false, DatabaseRefusal.WritePolicy));
        on.Policies.Calls.Select(static x => (x.Resource.Capability, x.Resource.TenantId)).Should()
            .Equal([(null, null), (nameof(StudioCapability.ViewDefinitions), null)], "the store policy, then the write policy, both with no tenant");

        // The store policy's refusal, already known from the BrowseDatabase question, is not asked again.
        on.Policies.Calls.Clear();

        DatabasePolicyAnswer storeRefused = await on.Access.EvaluateDefinitionPoliciesAsync(
            new StudioScope("default", string.Empty, null),
            new DatabasePolicyAnswer(true, false, DatabaseRefusal.StorePolicy),
            Token);

        storeRefused.Should().Be(new DatabasePolicyAnswer(true, false, DatabaseRefusal.StorePolicy));
        on.Policies.Calls.Should().BeEmpty();
    }

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

        public DatabaseAccess Access => scope.ServiceProvider.GetRequiredService<DatabaseAccess>();

        public IDatabaseObjectService Objects => scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>();

        public static Harness Create(Action<MartenStudioOptions> configure)
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
