using Microsoft.Extensions.Logging.Abstractions;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// TENANT-DB-FIX F1: the real catalog's search for the database that holds a tenant enumerates the source's databases once for the whole
/// search, not once per database, and answers from the same per-database listings <see cref="IStudioScopeCatalog.DescribeAsync" /> does.
/// </summary>
public class StudioScopeCatalogFindTenantTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static FakeStudioSourceProvider ThreeDatabases() => new FakeStudioSourceProvider()
        .WithSource("default", "db_a", "db_b", "db_c")
        .WithTenants("default", "db_a", "acme")
        .WithTenants("default", "db_b", "globex")
        .WithTenants("default", "db_c", "initech");

    private static StudioScopeCatalog Catalog(FakeStudioSourceProvider sources)
    {
        StudioAuthorization authorization = new(
            new MartenStudioSettings(Microsoft.Extensions.Options.Options.Create(new MartenStudioOptions())),
            MartenStudioDescriptor.Instance,
            new TestStoreAuthorizationService(),
            new TestAuthenticationStateProvider());

        return new StudioScopeCatalog(sources, authorization, NullLogger<StudioScopeCatalog>.Instance);
    }

    [Fact]
    public async Task One_search_enumerates_the_sources_databases_once_whatever_it_looks_in()
    {
        FakeStudioSourceProvider sources = ThreeDatabases();
        IStudioScopeCatalog catalog = Catalog(sources);
        int before = sources.DatabaseListings;

        TenantDatabaseHit? hit = await catalog.FindTenantDatabaseAsync("default", ["db_a", "db_b", "db_c"], "initech", Token);

        hit.Should().NotBeNull();
        hit!.DatabaseId.Should().Be("db_c");
        sources.DatabaseListings.Should().Be(before + 1, "one DatabasesAsync call for three databases, not one per database");
    }

    [Fact]
    public async Task The_hit_carries_the_facts_of_the_database_it_was_found_in()
    {
        IStudioScopeCatalog catalog = Catalog(ThreeDatabases());

        TenantDatabaseHit? hit = await catalog.FindTenantDatabaseAsync("default", ["db_a", "db_b", "db_c"], "globex", Token);

        hit!.DatabaseId.Should().Be("db_b");
        hit.Facts.Tenants.Ids.Should().Equal("globex");
        hit.Facts.ShowTenantSelector.Should().BeTrue();
    }

    [Fact]
    public async Task The_search_stops_at_the_first_database_that_lists_the_tenant()
    {
        FakeStudioSourceProvider sources = ThreeDatabases().WithTenants("default", "db_c", "globex");
        IStudioScopeCatalog catalog = Catalog(sources);

        TenantDatabaseHit? hit = await catalog.FindTenantDatabaseAsync("default", ["db_a", "db_b", "db_c"], "globex", Token);

        hit!.DatabaseId.Should().Be("db_b", "listing order");
    }

    [Fact]
    public async Task A_tenant_no_database_lists_is_not_found_and_only_the_databases_given_are_looked_in()
    {
        IStudioScopeCatalog catalog = Catalog(ThreeDatabases());

        (await catalog.FindTenantDatabaseAsync("default", ["db_a", "db_b", "db_c"], "umbrella", Token)).Should().BeNull();
        (await catalog.FindTenantDatabaseAsync("default", ["db_a", "db_b"], "initech", Token)).Should().BeNull("db_c was not given");
        (await catalog.FindTenantDatabaseAsync("default", [], "acme", Token)).Should().BeNull();
        (await catalog.FindTenantDatabaseAsync("nothing", ["db_a"], "acme", Token)).Should().BeNull("no such source");
    }

    [Fact]
    public async Task A_source_whose_databases_cannot_be_enumerated_finds_nothing_instead_of_throwing()
    {
        FakeStudioSourceProvider sources = ThreeDatabases();
        sources.FailingSources.Add("default");
        IStudioScopeCatalog catalog = Catalog(sources);

        (await catalog.FindTenantDatabaseAsync("default", ["db_a", "db_b"], "globex", Token)).Should().BeNull();
    }
}
