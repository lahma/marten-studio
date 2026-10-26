using System.Runtime.CompilerServices;

using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Relationships;

namespace PostgresStudio.Tests;

/// <summary>
/// What the Relationships page's read logs when it cannot draw a graph. A scope the source policy refuses, or one that
/// names no source, is the expected race of a scope that moved under the page: Debug, as the other pages log a refused
/// scope. A read that fails for any other reason is a real failure: Warning, with the exception.
/// </summary>
/// <remarks>
/// The application's data source points at a host nothing resolves, and no case here reaches it: the refused and the
/// unknown scope are turned away by the resolver, and the failing one by the host's own listing.
/// </remarks>
public class RelationshipLogLevelsTests
{
    private const string SourcePolicy = "relationships-log-source";

    /// <summary>The source the policy refuses: the application's unkeyed data source.</summary>
    private const string RefusedSource = "default";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("default", "default")]
    [InlineData("nowhere", "nowhere")]
    public async Task A_scope_that_is_refused_or_names_nothing_is_logged_at_Debug(string source, string database)
    {
        CapturingLoggerProvider logs = new();
        await using ServiceProvider provider = Build(logs);
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        RelationshipGraph graph = await circuit.ServiceProvider.GetRequiredService<IRelationshipDataService>()
            .GetGraphAsync(new StudioScope(source, database, null), Token);

        graph.Error.Should().NotBeNullOrEmpty("the page still says it could not draw the graph");
        logs.Entries.Should().NotContain(static x => x.Level >= LogLevel.Warning, "a refused or unknown scope is no anomaly");
        logs.Entries.Should().ContainSingle(static x => x.Message.Contains("read no relationships", StringComparison.Ordinal))
            .Which.Should().Match<CapturedLogEntry>(x => x.Level == LogLevel.Debug
                && x.Message == "Postgres Studio read no relationships for source " + source + ", database " + database
                    + ": the scope is refused or names nothing");
    }

    [Fact]
    public async Task A_read_that_fails_is_logged_at_Warning()
    {
        CapturingLoggerProvider logs = new();
        await using ServiceProvider provider = Build(logs);
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();

        RelationshipGraph graph = await circuit.ServiceProvider.GetRequiredService<IRelationshipDataService>()
            .GetGraphAsync(new StudioScope("tenants", "tenant_a", null), Token);

        graph.Error.Should().Be(UnreadableDatabases.Failure);
        logs.Entries.Should().NotContain(static x => x.Message.Contains("read no relationships", StringComparison.Ordinal));
        logs.Entries.Should().ContainSingle(static x => x.Message.Contains("could not read the relationships", StringComparison.Ordinal))
            .Which.Should().Match<CapturedLogEntry>(static x => x.Level == LogLevel.Warning
                && x.Message == "Postgres Studio could not read the relationships of tenants");
    }

    /// <summary>
    /// The unkeyed data source, which the source policy refuses, and a host source whose listing fails, which it allows.
    /// </summary>
    private static ServiceProvider Build(CapturingLoggerProvider logs)
    {
        ServiceCollection services = new();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddAuthorization(static options => options.AddPolicy(SourcePolicy, static policy => policy.RequireAssertion(
            static context => context.Resource is PostgresStudioResource { SourceName: not RefusedSource })));
        services.AddSingleton(_ => NpgsqlDataSource.Create("Host=postgres-studio-relationship-log.invalid;Database=none;Username=none;Password=none"));
        services.AddPostgresStudio(static options =>
        {
            options.SourceAuthorizationPolicy = SourcePolicy;
            options.AddDatabaseSource("tenants", static _ => new UnreadableDatabases());
        });
        services.AddScoped<AuthenticationStateProvider>(static _ =>
        {
            TestAuthenticationStateProvider visitor = new();
            visitor.SignIn("admin");
            return visitor;
        });

        return services.BuildServiceProvider();
    }

    /// <summary>A host source whose listing fails - the admin database it reads its tenants from is down.</summary>
    private sealed class UnreadableDatabases : IPostgresDatabaseSource
    {
        public const string Failure = "The admin database the tenants are listed from is down.";

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(Failure);
            }

            yield break;
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A database whose listing failed is never opened.");
    }
}
