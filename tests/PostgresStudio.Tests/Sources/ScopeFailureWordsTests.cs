using System.Runtime.CompilerServices;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Relationships;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// H1, item 9 (the second-pass review of the database browser and Relationships against D27): resolving a scope can itself
/// query the database - a host's list of databases, a dynamic tenancy, tenant discovery - and Postgres' words about such a
/// read can name a schema this visitor's gate withholds. No gate has been read yet to mask them with, so they are withheld,
/// with only the SQLSTATE said, as the Schema screen withholds a failure's text it cannot check (D27); and the browser
/// answers with a value, never by throwing at the page.
/// </summary>
public class ScopeFailureWordsTests
{
    /// <summary>The schema Postgres names, which this visitor's gate would withhold.</summary>
    private const string Withheld = "tenant_directory";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scope_that_fails_to_resolve_in_the_database_is_a_value_that_names_no_schema()
    {
        await using ServiceProvider provider = Services();
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        StudioScope scope = new("tenants", "acme", null);

        IDatabaseObjectService objects = circuit.ServiceProvider.GetRequiredService<IDatabaseObjectService>();

        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(scope, Token);
        overview.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        overview.Reason.Should().Contain("42P01").And.NotContain(Withheld, "Postgres' words are withheld: no gate was read to mask them");

        DatabaseObjectDetail detail = await objects.GetObjectAsync(scope, "public", "orders", Token);
        detail.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        detail.Reason.Should().Contain("42P01").And.NotContain(Withheld);

        RelationshipGraph graph = await circuit.ServiceProvider.GetRequiredService<IRelationshipDataService>().GetGraphAsync(scope, Token);
        graph.Error.Should().Contain("42P01").And.NotContain(Withheld, "the Relationships screen withholds them the same way");
    }

    /// <summary>The anti-vacuity half: the studio's own sentences, which name nothing the visitor did not type, are said as they are.</summary>
    [Fact]
    public void The_studios_own_sentences_are_said_as_they_are_and_Postgres_words_only_past_a_gate()
    {
        RelationshipReads.Describe(new KeyNotFoundException("No data source is registered under 'x'."), gate: null)
            .Should().Be("No data source is registered under 'x'.");

        DatabaseObjectService.ScopeReadFailure(Named()).Should().Contain("42P01").And.NotContain(Withheld);
        RelationshipReads.Describe(Named(), gate: null).Should().Contain("42P01").And.NotContain(Withheld);
    }

    private static PostgresException Named() =>
        new("relation \"" + Withheld + ".databases\" does not exist", "ERROR", "ERROR", "42P01");

    private static ServiceProvider Services()
    {
        TestAuthenticationStateProvider users = new();
        users.SignIn("operator");

        ServiceCollection services = new();
        services.AddLogging();
        services.AddPostgresStudio(static options => options.AddDatabaseSource("tenants", static _ => new FailingSource()));
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        return services.BuildServiceProvider();
    }

    /// <summary>A host's database source whose list is a query that Postgres refuses, naming a schema.</summary>
    private sealed class FailingSource : IPostgresDatabaseSource
    {
        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw Named();
#pragma warning disable CS0162 // An iterator needs a yield to be one.
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NpgsqlDataSource?>(null);
    }
}
