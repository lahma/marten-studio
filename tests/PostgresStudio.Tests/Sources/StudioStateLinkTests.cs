using System.Runtime.CompilerServices;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// D9 for a second source: a pasted link to one of its databases reopens that database. The layout hands the URL's scope
/// to <see cref="StudioState.SetScopeAsync" />, which used to drop the database whenever the store changed - so every
/// link to a host source's tenant past the first opened the first, found by PS-5's browser suite.
/// </summary>
/// <remarks>
/// The unkeyed data source and the host's list are never connected to: listing a source's databases reads only what
/// the application lists.
/// </remarks>
public class StudioStateLinkTests
{
    private const string SourcePolicy = "link-tests-source";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_link_to_another_sources_database_opens_that_database()
    {
        await using ServiceProvider provider = Build();
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();
        StudioState state = circuit.ServiceProvider.GetRequiredService<StudioState>();

        await state.EnsureInitializedAsync(Token);
        state.ActiveScope.Should().Be(new StudioScope("default", "default", null), "the premise: the unkeyed data source opens first");

        await state.SetScopeAsync("tenants", "tenant_b", null, Token);

        state.ActiveScope.Should().Be(new StudioScope("tenants", "tenant_b", null));
    }

    [Fact]
    public async Task Another_source_named_without_a_database_or_with_one_it_does_not_list_opens_its_first()
    {
        await using ServiceProvider provider = Build();
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();
        StudioState state = circuit.ServiceProvider.GetRequiredService<StudioState>();
        await state.EnsureInitializedAsync(Token);

        await state.SetScopeAsync("tenants", null, null, Token);
        state.ActiveScope.Should().Be(new StudioScope("tenants", "tenant_a", null), "the scope selector names no database when a store is picked");

        await state.SetScopeAsync("default", "tenant_c", null, Token);
        state.ActiveScope.Should().Be(new StudioScope("default", "default", null), "a name the store does not list is ignored, silently");
    }

    /// <summary>
    /// A link to a database the source policy withholds opens the first database the policy allows - never the linked
    /// one, and never the first the source lists when the policy withholds that one too. The withheld names are not
    /// among the databases offered either: the listing the link is matched against is the policy's.
    /// </summary>
    [Fact]
    public async Task A_link_to_a_database_the_source_policy_withholds_opens_the_first_one_it_allows()
    {
        await using ServiceProvider provider = Build(static resource => resource.DatabaseIdentifier is not ("tenant_a" or "tenant_b"));
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();
        StudioState state = circuit.ServiceProvider.GetRequiredService<StudioState>();
        await state.EnsureInitializedAsync(Token);

        await state.SetScopeAsync("tenants", "tenant_b", null, Token);

        state.AvailableDatabases.Select(static x => x.Identity).Should().Equal(["tenant_c", "tenant_d"], "the policy withholds tenant_a and tenant_b");
        state.ActiveScope.Should().Be(new StudioScope("tenants", "tenant_c", null),
            "tenant_b is withheld, and tenant_c is the first database the policy allows - tenant_a, the first listed, is withheld too");
    }

    private static ServiceProvider Build(Func<PostgresStudioResource, bool>? sourcePolicy = null)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization(options =>
        {
            if (sourcePolicy is not null)
            {
                options.AddPolicy(SourcePolicy, policy => policy.RequireAssertion(
                    context => context.Resource is PostgresStudioResource resource && sourcePolicy(resource)));
            }
        });
        services.AddSingleton(_ => NpgsqlDataSource.Create("Host=postgres-studio-link-tests.invalid;Database=none;Username=none;Password=none"));
        services.AddPostgresStudio(options =>
        {
            options.AddDatabaseSource("tenants", static _ => new ListedDatabases());
            options.SourceAuthorizationPolicy = sourcePolicy is null ? null : SourcePolicy;
        });
        services.AddScoped<AuthenticationStateProvider>(static _ =>
        {
            TestAuthenticationStateProvider visitor = new();
            visitor.SignIn("admin");
            return visitor;
        });

        return services.BuildServiceProvider();
    }

    /// <summary>Four tenant names and no database behind any of them: nothing here opens one.</summary>
    private sealed class ListedDatabases : IPostgresDatabaseSource
    {
        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string name in (string[]) ["tenant_a", "tenant_b", "tenant_c", "tenant_d"])
            {
                await Task.Yield();
                yield return name;
            }
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Listing and choosing a database opens no data source.");
    }
}
