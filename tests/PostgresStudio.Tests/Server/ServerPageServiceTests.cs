using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Server;
using PostgresStudio.Tests.Support;

namespace PostgresStudio.Tests.Server;

/// <summary>
/// The Server page's gate, in the service (W5-S2-fix): the store policy is asked about the database as a whole, with no tenant,
/// before anything is read - and asked again, about the registered key and the resolved identity, when the visitor spelt
/// either another way (D29).
/// </summary>
public class ServerPageServiceTests
{
    /// <summary>A data source nothing here ever opens.</summary>
    private const string ConnectionString = "Host=127.0.0.1;Port=1;Database=app;Username=app;Password=secret;Timeout=1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ServiceProvider Container(TestSourceAuthorizationService policy)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));
        services.AddPostgresStudio(static options => options.SourceAuthorizationPolicy = "src");
        services.AddSingleton<IAuthorizationService>(policy);
        services.AddScoped<AuthenticationStateProvider, TestAuthenticationStateProvider>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_store_policy_that_refuses_the_database_as_a_whole_is_a_refusal_and_reads_nothing()
    {
        TestSourceAuthorizationService policy = new();
        policy.Allow(static _ => false);
        await using ServiceProvider provider = Container(policy);
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        ServerPageView view = await circuit.ServiceProvider.GetRequiredService<IServerPageService>()
            .GetAsync(new StudioScope("default", "default", "a-tenant"), Token);

        view.Refusal.Should().Be(ServerRefusal.StorePolicy);
        view.Reason.Should().Contain("PostgresStudioOptions.SourceAuthorizationPolicy").And.Contain("with no tenant selected");
        provider.GetRequiredService<ServerSettingsFactsCache>().ReadCount.Should().Be(0, "nothing of the server is read for a refused visitor");
        // PostgresStudioResource carries no tenant to assert on; that the question is the tenant-less one is
        // MartenStudio.Tests' ServerPageStorePolicyTests, through MartenStoreResource.TenantId.
        policy.Calls.Should().NotBeEmpty().And.OnlyContain(static x => x.Policy == "src" && x.Resource.Capability == null);
    }

    [Fact]
    public async Task A_key_spelt_another_way_is_asked_about_again_by_its_registered_name_and_that_refusal_holds()
    {
        TestSourceAuthorizationService policy = new();

        // The resolver's own question carries the visitor's spelling; the service's second one the registered key.
        policy.Allow(static x => x.SourceName == "DEFAULT");
        await using ServiceProvider provider = Container(policy);
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        ServerPageView view = await circuit.ServiceProvider.GetRequiredService<IServerPageService>()
            .GetAsync(new StudioScope("DEFAULT", "default", null), Token);

        policy.Calls.Select(static x => x.Resource.SourceName).Should().Contain("DEFAULT").And.Contain("default",
            "the store policy is asked again about the source the scope resolved to, by its own name");
        view.Refusal.Should().Be(ServerRefusal.StorePolicy);
        provider.GetRequiredService<ServerSettingsFactsCache>().ReadCount.Should().Be(0);
    }
}
