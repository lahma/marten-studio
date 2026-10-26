using Marten;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// The order in which a scope is resolved, which is the security property: authorization runs before
/// anything is looked up.
/// </summary>
public class StudioScopeResolverTest
{
    private const string DummyConnectionString =
        "Host=marten-studio-resolver-test.invalid;Database=none;Username=none;Password=none";

    private const string StorePolicy = "store-policy";

    private const string WritePolicy = "write-policy";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Harness(
        StudioScopeResolver Resolver,
        TestStoreAuthorizationService Authorization,
        MartenStudioOptions Options,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static Harness CreateHarness(Action<MartenStudioOptions>? configure = null, bool registerStore = true)
    {
        var options = new MartenStudioOptions
        {
            StoreAuthorizationPolicy = StorePolicy,
            WriteAuthorizationPolicy = WritePolicy
        };
        configure?.Invoke(options);

        var services = new ServiceCollection();
        if (registerStore)
        {
            services.AddMarten(x => x.Connection(DummyConnectionString));
        }

        var registry = new MartenStoreRegistry(services);
        var authorizationService = new TestStoreAuthorizationService();
        var provider = services.BuildServiceProvider();

        var authorization = MartenSpine.Authorization(
            Options.Create(options),
            authorizationService,
            new TestAuthenticationStateProvider());

        var tenants = new TenantDiscovery(Options.Create(options), NullLogger<TenantDiscovery>.Instance);

        return new Harness(
            MartenSpine.Scope(Options.Create(options), registry, authorization, tenants, provider).Resolver,
            authorizationService,
            options,
            provider);
    }

    /// <summary>
    /// A store the visitor may not see and a store that does not exist are one answer. Anything else
    /// lets a visitor enumerate the stores of a process by watching which refusal comes back.
    /// </summary>
    [Fact]
    public async Task A_denied_scope_is_indistinguishable_from_an_unknown_one()
    {
        using var harness = CreateHarness();
        harness.Authorization.DenyEverything();

        var denied = await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token).AsTask())
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        var unknown = await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("no-such-store", string.Empty, null), null, Token).AsTask())
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        denied.Which.Message.Should().Be(unknown.Which.Message);
        denied.Which.Message.Should().NotContain("default");
        denied.Which.Message.Should().NotContain("no-such-store");
    }

    /// <summary>
    /// Authorization runs first, which is why a denied unknown store never reaches the registry at all -
    /// the resource the policy was asked about carries the key the visitor supplied and nothing else.
    /// </summary>
    [Fact]
    public async Task Authorization_is_asked_before_the_registry_is_consulted()
    {
        using var harness = CreateHarness();
        harness.Authorization.DenyEverything();

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("no-such-store", "no-such-db", "acme"), null, Token).AsTask())
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        harness.Authorization.Calls.Should().ContainSingle();
        harness.Authorization.Calls[0].Policy.Should().Be(StorePolicy);
        harness.Authorization.Calls[0].Resource.Should().Be(new MartenStoreResource("no-such-store", "no-such-db", "acme", null));
    }

    [Fact]
    public async Task An_authorized_unknown_store_is_a_KeyNotFound()
    {
        using var harness = CreateHarness();

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("no-such-store", string.Empty, null), null, Token).AsTask())
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task An_authorized_default_store_resolves_to_its_default_database()
    {
        using var harness = CreateHarness();

        var resolved = await harness.Resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        resolved.Registration.Key.Should().Be("default");
        resolved.Store.Should().NotBeNull();
        resolved.Database.Should().NotBeNull();
        resolved.Database.Id.Identity.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The database is matched against what Marten already knows. A browser string is never handed to
    /// <c>FindOrCreateDatabase</c>, whose name is literal.
    /// </summary>
    [Fact]
    public async Task A_database_identity_that_this_store_does_not_have_is_a_KeyNotFound()
    {
        using var harness = CreateHarness();

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("default", "somebody-elses-database", null), null, Token).AsTask())
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task A_store_that_will_not_build_is_a_StudioStoreUnavailable()
    {
        var options = new MartenStudioOptions();
        var services = new ServiceCollection();
        services.AddSingleton<MartenStoreRegistryTest.IBrokenStore>(static _ =>
            throw new InvalidOperationException("no connection string was configured"));

        var registry = new MartenStoreRegistry(services);
        using var provider = services.BuildServiceProvider();

        var resolver = MartenSpine.Scope(
            Options.Create(options),
            registry,
            MartenSpine.Authorization(Options.Create(options), new TestStoreAuthorizationService(), new TestAuthenticationStateProvider()),
            new TenantDiscovery(Options.Create(options), NullLogger<TenantDiscovery>.Instance),
            provider).Resolver;

        var failure = await resolver
            .Invoking(x => x.ResolveAsync(new StudioScope(nameof(MartenStoreRegistryTest.IBrokenStore), string.Empty, null), null, Token).AsTask())
            .Should().ThrowAsync<StudioStoreUnavailableException>();

        failure.Which.Reason.Should().Contain("no connection string was configured");
    }

    /// <summary>
    /// A write is asked twice: once about the store, once about the capability. Both have to pass, and
    /// the capability name travels on the second resource so one handler can answer both.
    /// </summary>
    [Fact]
    public async Task A_write_is_checked_against_the_write_policy_with_the_capability_named()
    {
        using var harness = CreateHarness();
        string identity = await IdentityAsync(harness);

        await harness.Resolver.ResolveAsync(new StudioScope("default", identity, null), nameof(StudioCapability.EditDocuments), Token);

        harness.Authorization.Calls.Should().HaveCount(2);
        harness.Authorization.Calls[0].Policy.Should().Be(StorePolicy);
        harness.Authorization.Calls[0].Resource.Capability.Should().BeNull();
        harness.Authorization.Calls[1].Policy.Should().Be(WritePolicy);
        harness.Authorization.Calls[1].Resource.Capability.Should().Be(nameof(StudioCapability.EditDocuments));
    }

    [Fact]
    public async Task A_write_refused_by_the_write_policy_alone_is_still_a_refusal()
    {
        using var harness = CreateHarness();
        harness.Authorization.Allow(static resource => resource.Capability is null);

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("default", string.Empty, null), nameof(StudioCapability.DeleteDocuments), Token).AsTask())
            .Should().ThrowAsync<StudioNotAuthorizedException>();
    }

    /// <summary>
    /// With no write policy configured, the store policy answers for writes too - the host configured one
    /// gate and gets one gate, rather than an unguarded second door.
    /// </summary>
    [Fact]
    public async Task Without_a_write_policy_the_store_policy_answers_for_writes()
    {
        using var harness = CreateHarness(static options => options.WriteAuthorizationPolicy = null);
        string identity = await IdentityAsync(harness);

        await harness.Resolver.ResolveAsync(new StudioScope("default", identity, null), nameof(StudioCapability.RunSql), Token);

        harness.Authorization.Calls.Should().HaveCount(2);
        harness.Authorization.Calls.Should().OnlyContain(x => x.Policy == StorePolicy);
        harness.Authorization.Calls[1].Resource.Capability.Should().Be(nameof(StudioCapability.RunSql));
    }

    [Fact]
    public async Task With_no_policy_configured_everything_is_authorized_and_nothing_is_asked()
    {
        using var harness = CreateHarness(static options =>
        {
            options.StoreAuthorizationPolicy = null;
            options.WriteAuthorizationPolicy = null;
        });
        harness.Authorization.DenyEverything();

        var resolved = await harness.Resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        resolved.Registration.Key.Should().Be("default");
        harness.Authorization.Calls.Should().BeEmpty("a studio that configured no store policy asks nobody");
    }

    /// <summary>
    /// A tenant the studio cannot see in the listing is refused, so a browser cannot name one that does
    /// not exist and get a page rendered for it.
    /// </summary>
    [Fact]
    public async Task A_tenant_that_is_not_in_the_listing_is_a_KeyNotFound()
    {
        using var harness = CreateHarness(static options =>
        {
            options.KnownTenantIds.Add("acme");
            options.KnownTenantIds.Add("globex");
        });

        await harness.Resolver.ResolveAsync(new StudioScope("default", string.Empty, "acme"), null, Token);

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("default", string.Empty, "initech"), null, Token).AsTask())
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task A_null_tenant_means_all_of_them_and_is_always_acceptable()
    {
        using var harness = CreateHarness(static options => options.KnownTenantIds.Add("acme"));

        var resolved = await harness.Resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        resolved.TenantId.Should().BeNull();
    }

    /// <summary>
    /// The resource the policy sees is exactly what travels in the URL: a store key, Marten's database
    /// identity and the tenant. Never a connection string.
    /// </summary>
    [Fact]
    public void The_resource_is_built_from_the_scope_and_the_capability()
    {
        var scope = new StudioScope("default", "server/database", "acme");

        scope.ToResource(null).Should().Be(new MartenStoreResource("default", "server/database", "acme", null));
        scope.ToResource("EditDocuments").Should().Be(new MartenStoreResource("default", "server/database", "acme", "EditDocuments"));
    }

    // SEC-fix F1: the policies are asked the registered key and the matched identity, whatever the link spelled

    /// <summary>
    /// A deny-list handler keyed on the registered name (<c>StoreName != "default"</c>, ordinally) passes the spelling
    /// <c>DEFAULT</c>, which the registry matches ignoring case. The resolver must ask again by the name it matched and
    /// refuse - carrying that name for the audit - before the store's databases are checked for a tenant.
    /// </summary>
    [Fact]
    public async Task A_deny_list_policy_refuses_another_spelling_of_the_store_it_names()
    {
        using var harness = CreateHarness();
        string identity = await IdentityAsync(harness);
        harness.Authorization.Allow(static resource => !string.Equals(resource.StoreName, "default", StringComparison.Ordinal));

        var refused = await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("DEFAULT", identity, null), null, Token).AsTask())
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        harness.Authorization.Calls.Select(static x => x.Resource).Should().Equal(
            new MartenStoreResource("DEFAULT", identity, null, null),
            new MartenStoreResource("default", identity, null, null));
        refused.Which.Scope.Should().Be(new StudioScope("default", identity, null), "the audit names the scope that was refused");
    }

    /// <summary>
    /// The same for the database: a link without <c>?db=</c> is the store's first database, and a database id in another
    /// case is the same one. A handler that refuses that database by its identity is asked about the identity.
    /// </summary>
    [Fact]
    public async Task A_deny_list_policy_refuses_an_empty_or_differently_cased_spelling_of_the_database_it_names()
    {
        using var harness = CreateHarness();
        string identity = await IdentityAsync(harness);
        identity.Should().NotBe(identity.ToUpperInvariant(), "the premise: the identity has a letter whose case can differ");
        harness.Authorization.Allow(resource => !string.Equals(resource.DatabaseIdentifier, identity, StringComparison.Ordinal));

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token).AsTask())
            .Should().ThrowAsync<StudioNotAuthorizedException>("an empty id is the first database, which the policy refuses by name");

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("default", identity.ToUpperInvariant(), null), null, Token).AsTask())
            .Should().ThrowAsync<StudioNotAuthorizedException>("another case of the identity is the same database");

        harness.Authorization.Calls.Should().HaveCount(4, "each spelling is asked, and then the identity it matched");
        harness.Authorization.Calls[1].Resource.DatabaseIdentifier.Should().Be(identity);
        harness.Authorization.Calls[3].Resource.DatabaseIdentifier.Should().Be(identity);
    }

    /// <summary>A scope spelled exactly as registered and matched is asked once - the second question is for another spelling only.</summary>
    [Fact]
    public async Task A_scope_spelled_as_registered_is_asked_once()
    {
        using var harness = CreateHarness();
        string identity = await IdentityAsync(harness);

        ResolvedScope resolved = await harness.Resolver.ResolveAsync(new StudioScope("default", identity, null), null, Token);

        resolved.Database.Id.Identity.Should().Be(identity);
        harness.Authorization.Calls.Should().ContainSingle()
            .Which.Resource.Should().Be(new MartenStoreResource("default", identity, null, null));
    }

    /// <summary>
    /// A write spelled another way asks both policies twice, in order: the spelling, then the registered names - each the
    /// store policy and then the write policy with the capability named - and the tenant travels on both unchanged.
    /// </summary>
    [Fact]
    public async Task A_write_spelled_another_way_asks_both_policies_again_by_the_registered_names()
    {
        using var harness = CreateHarness(static options => options.KnownTenantIds.Add("acme"));
        string identity = await IdentityAsync(harness);

        await harness.Resolver.ResolveAsync(new StudioScope("Default", string.Empty, "acme"), nameof(StudioCapability.EditDocuments), Token);

        harness.Authorization.Calls.Should().Equal(
            (StorePolicy, new MartenStoreResource("Default", string.Empty, "acme", null)),
            (WritePolicy, new MartenStoreResource("Default", string.Empty, "acme", nameof(StudioCapability.EditDocuments))),
            (StorePolicy, new MartenStoreResource("default", identity, "acme", null)),
            (WritePolicy, new MartenStoreResource("default", identity, "acme", nameof(StudioCapability.EditDocuments))));
    }

    /// <summary>
    /// A write refused only under the registered names is refused: the write policy is asked again too, not only the
    /// store policy.
    /// </summary>
    [Fact]
    public async Task A_write_policy_that_refuses_only_the_registered_names_refuses_the_write()
    {
        using var harness = CreateHarness();
        string identity = await IdentityAsync(harness);
        harness.Authorization.Allow(static resource => resource.Capability is null || resource.StoreName != "default");

        await harness.Resolver
            .Invoking(x => x.ResolveAsync(new StudioScope("DEFAULT", identity, null), nameof(StudioCapability.DeleteDocuments), Token).AsTask())
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        harness.Authorization.Calls.Should().HaveCount(4);
        harness.Authorization.Calls[3].Should().Be((WritePolicy, new MartenStoreResource("default", identity, null, nameof(StudioCapability.DeleteDocuments))));
    }

    /// <summary>The default store's first database identity, read through the resolver; the questions it asked are forgotten.</summary>
    private static async Task<string> IdentityAsync(Harness harness)
    {
        ResolvedScope resolved = await harness.Resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);
        harness.Authorization.Calls.Clear();
        return resolved.Database.Id.Identity;
    }
}
