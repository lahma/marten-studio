using JasperFx.Descriptors;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// <see cref="StudioState.SetScopeAsync" /> with a tenant the listing does not carry: it keeps the previous tenant only
/// while the store and database it lands on accept that tenant too. Before W4-fix a store change carried the old store's
/// tenant into the new scope unchecked - a scope no listing ever offered, which every page then read for.
/// </summary>
public class StudioStateTenantFallbackTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task On_a_store_change_a_previous_tenant_the_new_store_does_not_list_is_dropped()
    {
        StudioState state = await OnAcmeAsync(new TenantList(["initech"], IsTruncated: false, TenantListSource.Configured));

        await state.SetScopeAsync("b", null, "umbrella", Token);

        state.ActiveScope.Should().Be(new StudioScope("b", "db_b", null),
            "neither umbrella nor the previous store's acme is a tenant store b lists, so the scope is all tenants");
    }

    [Fact]
    public async Task On_a_store_change_a_previous_tenant_the_new_store_also_lists_is_kept()
    {
        StudioState state = await OnAcmeAsync(new TenantList(["acme", "initech"], IsTruncated: false, TenantListSource.Configured));

        await state.SetScopeAsync("b", null, "umbrella", Token);

        state.ActiveScope.Should().Be(new StudioScope("b", "db_b", "acme"),
            "umbrella is ignored as any unoffered name is, and acme is a tenant store b lists too");
    }

    [Fact]
    public async Task Within_one_store_a_tenant_it_does_not_list_keeps_the_previous_one()
    {
        StudioState state = await OnAcmeAsync(new TenantList(["initech"], IsTruncated: false, TenantListSource.Configured));

        await state.SetScopeAsync("a", null, "umbrella", Token);

        state.ActiveScope.Should().Be(new StudioScope("a", "db_a", "acme"), "a name the store was never offered changes nothing");
    }

    /// <summary>Two stores, the first listing acme and globex, and a circuit on the first store's acme.</summary>
    private static async Task<StudioState> OnAcmeAsync(TenantList storeB)
    {
        FakeStudioScopeCatalog catalog = new FakeStudioScopeCatalog()
            .WithStore("a", "Store A", DatabaseCardinality.Single, "db_a")
            .WithTenants("a", new TenantList(["acme", "globex"], IsTruncated: false, TenantListSource.Configured))
            .WithStore("b", "Store B", DatabaseCardinality.Single, "db_b")
            .WithTenants("b", storeB);

        StudioState state = new(catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());
        await state.EnsureInitializedAsync(Token);
        await state.SetScopeAsync("a", null, "acme", Token);

        state.ActiveScope.Should().Be(new StudioScope("a", "db_a", "acme"), "the premise: the circuit is on store a's acme");

        return state;
    }
}
