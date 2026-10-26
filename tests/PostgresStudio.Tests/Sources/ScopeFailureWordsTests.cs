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

    /// <summary>
    /// H1b: a host's <see cref="IPostgresDatabaseSource" /> that throws anything but a Postgres or an Npgsql exception - an
    /// <see cref="InvalidOperationException" />, a <see cref="TimeoutException" /> - has its words said, because they are the
    /// host's sentence about its own list. They are the host's, so they carry anything: a line break that would start a
    /// second line in whatever records the refusal, a bidirectional override, a megabyte. They reach the page escaped and
    /// bounded, as every other value the studio did not write does (<see cref="StudioLogValue.CapText" />).
    /// </summary>
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    public async Task A_hosts_own_words_about_its_list_reach_the_page_escaped_and_bounded(Type type)
    {
        Exception thrown = (Exception) Activator.CreateInstance(type, HostWords)!;
        await using ServiceProvider provider = Services(() => thrown);
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        StudioScope scope = new("tenants", "acme", null);
        IDatabaseObjectService objects = circuit.ServiceProvider.GetRequiredService<IDatabaseObjectService>();

        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(scope, Token);
        overview.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        ShouldBeEscapedAndBounded(overview.Reason);

        DatabaseObjectDetail detail = await objects.GetObjectAsync(scope, "public", "orders", Token);
        ShouldBeEscapedAndBounded(detail.Reason);

        RelationshipGraph graph = await circuit.ServiceProvider.GetRequiredService<IRelationshipDataService>().GetGraphAsync(scope, Token);
        ShouldBeEscapedAndBounded(graph.Error);

        DatabaseObjectService.ScopeReadFailure(thrown).Should().Be(
            "The scope could not be resolved: " + StudioLogValue.CapText(HostWords), "the sentence is the studio's, the words the host's");
    }

    /// <summary>What a host's list says when it fails: a forged second line, an override, and far past any bound.</summary>
    private static readonly string HostWords =
        "the tenant directory is down\n2026-10-27 INFO forged: all clear ‮evil" + new string('x', StudioLogValue.MaxTextLength * 2);

    private static void ShouldBeEscapedAndBounded(string? reason)
    {
        reason.Should().NotBeNull();
        reason.Should().Contain("the tenant directory is down\\n2026-10-27 INFO forged", "the line break is said, escaped");
        reason.Should().NotContain("\n").And.NotContain("‮");
        reason!.Length.Should().BeLessThan(StudioLogValue.MaxTextLength + 200, "the host's words are bounded, not the page's");
    }

    private static PostgresException Named() =>
        new("relation \"" + Withheld + ".databases\" does not exist", "ERROR", "ERROR", "42P01");

    private static ServiceProvider Services(Func<Exception>? failure = null)
    {
        TestAuthenticationStateProvider users = new();
        users.SignIn("operator");

        ServiceCollection services = new();
        services.AddLogging();
        services.AddPostgresStudio(options => options.AddDatabaseSource("tenants", _ => new FailingSource(failure ?? Named)));
        services.AddScoped<AuthenticationStateProvider>(_ => users);

        return services.BuildServiceProvider();
    }

    /// <summary>A host's database source whose list fails - by default, a query that Postgres refuses, naming a schema.</summary>
    private sealed class FailingSource(Func<Exception> failure) : IPostgresDatabaseSource
    {
        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw failure();
#pragma warning disable CS0162 // An iterator needs a yield to be one.
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken) =>
            ValueTask.FromResult<NpgsqlDataSource?>(null);
    }
}
