using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Server;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The Server page's gate through MartenStudio's own resource (W5-S2-fix2): the store policy is asked about the database as a
/// whole, with <c>TenantId = null</c>, whatever tenant the visitor's scope names (D29) - which only a
/// <see cref="MartenStoreResource" /> can show, the shared studio's resource having no tenant at all.
/// </summary>
public class ServerPageStorePolicyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_store_policy_that_refuses_is_asked_with_no_tenant_whatever_the_scope_names_and_nothing_is_read()
    {
        await using HealthHarness harness = HealthHarness.Create(policies: true);
        harness.Policies.Allow(static _ => false);

        ServerPageView view = await harness.Circuit.GetRequiredService<IServerPageService>()
            .GetAsync(new StudioScope("default", harness.Store.Storage.Database.Id.Identity, "a-tenant"), Token);

        view.Refusal.Should().Be(ServerRefusal.StorePolicy);
        view.Reason.Should().Contain("MartenStudioOptions.StoreAuthorizationPolicy").And.Contain("with no tenant selected");
        harness.Policies.Calls.Should().NotBeEmpty().And.OnlyContain(
            static x => x.Policy == HealthHarness.StorePolicy && x.Resource.TenantId == null && x.Resource.Capability == null,
            "the page's gate is the database as a whole, not the tenant the visitor selected");
        harness.Services.GetRequiredService<ServerSettingsFactsCache>().ReadCount.Should().Be(0, "nothing of the server is read for a refused visitor");
    }

    [Fact]
    public async Task An_allowed_visitor_is_asked_about_with_no_tenant_too()
    {
        await using HealthHarness harness = HealthHarness.Create(policies: true);

        await harness.Circuit.GetRequiredService<IServerPageService>()
            .GetAsync(new StudioScope("default", harness.Store.Storage.Database.Id.Identity, "a-tenant"), Token);

        harness.Policies.Calls.Where(static x => x.Policy == HealthHarness.StorePolicy)
            .Should().NotBeEmpty().And.OnlyContain(static x => x.Resource.TenantId == null);
    }
}
