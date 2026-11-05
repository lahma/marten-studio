using Microsoft.Extensions.Logging.Abstractions;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-3-FU4: the real catalog, reached through <see cref="IStudioScopeCatalog" />, tells "this visitor may see no database" from
/// "the source's databases could not be listed" - which Find's "Search in" words differently ("Nothing found" and "could not search").
/// </summary>
public class ScopeCatalogTryListTests
{
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
    public async Task A_source_whose_databases_cannot_be_enumerated_is_a_failure_not_an_empty_listing()
    {
        FakeStudioSourceProvider sources = new FakeStudioSourceProvider().WithSource("default", "primary");
        sources.FailingSources.Add("default");

        IStudioScopeCatalog catalog = Catalog(sources);
        StoreDatabaseListing listing = await catalog.TryListDatabasesAsync("default", TestContext.Current.CancellationToken);

        listing.Failed.Should().BeTrue();
        listing.Databases.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_that_lists_is_not_a_failure()
    {
        FakeStudioSourceProvider sources = new FakeStudioSourceProvider().WithSource("default", "primary", "replica");

        IStudioScopeCatalog catalog = Catalog(sources);
        StoreDatabaseListing listing = await catalog.TryListDatabasesAsync("default", TestContext.Current.CancellationToken);

        listing.Failed.Should().BeFalse();
        listing.Databases.Select(static x => x.Identity).Should().Equal("primary", "replica");
    }

    [Fact]
    public async Task A_source_nobody_registered_is_an_empty_listing_and_not_a_failure()
    {
        IStudioScopeCatalog catalog = Catalog(new FakeStudioSourceProvider().WithSource("default", "primary"));
        StoreDatabaseListing listing = await catalog
            .TryListDatabasesAsync("nothing", TestContext.Current.CancellationToken);

        listing.Failed.Should().BeFalse();
        listing.Databases.Should().BeEmpty();
    }
}
