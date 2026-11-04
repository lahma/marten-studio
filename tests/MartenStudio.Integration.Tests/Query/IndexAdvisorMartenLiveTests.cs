using System.Globalization;
using System.Security.Claims;

using JasperFx;

using Marten;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using MartenStudio.Services;

using PostgresStudio.Services;
using PostgresStudio.Services.Statements;

using Testcontainers.PostgreSql;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>
/// 1.2-INDEX-A's server for the Marten suite: <see cref="MartenHypopgServer" />'s recipe (the Debian <c>postgres:17.11</c> with
/// hypopg from PGDG's apt), with <c>pg_stat_statements</c> preloaded, because the advisor plans the statements the statement
/// checks read. Never reused.
/// </summary>
public sealed class MartenAdvisorServer : IAsyncLifetime
{
    private PostgreSqlContainer? container;

    public string? Unavailable { get; private set; }

    public void SkipIfUnavailable()
    {
        if (Unavailable is { } why)
        {
            Assert.Skip(why);
        }
    }

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            Unavailable = "Docker is not available: start it so that `docker info` succeeds.";
            return;
        }

        container = new PostgreSqlBuilder(MartenHypopgServer.Image)
            .WithReuse(false)
            .WithName("ms-marten-advisor-" + Guid.NewGuid().ToString("N")[..8])
            .WithLabel("ms-purpose", "hypopg")
            .WithCommand("-c", "shared_preload_libraries=pg_stat_statements")
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration)
        {
            await container.DisposeAsync();
            container = null;
            Unavailable = "The hypopg server could not start " + MartenHypopgServer.Image + " here (" + exception.GetType().Name + ").";
            return;
        }

        var installed = await container.ExecAsync(["bash", "-c", MartenHypopgServer.Install]);

        if (installed.ExitCode != 0)
        {
            string why = "hypopg could not be installed (`" + MartenHypopgServer.Install + "` exited " + Convert.ToString(installed.ExitCode, CultureInfo.InvariantCulture) + ").";

            if (DockerAvailability.OnContinuousIntegration)
            {
                throw new InvalidOperationException(why + " On CI this is a failure, not a skip.");
            }

            Unavailable = why + " It needs the network for apt.postgresql.org.";
        }
    }

    /// <summary>A database of the test's own with hypopg and <c>pg_stat_statements</c>, both installed by the superuser.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        string root = (container ?? throw new InvalidOperationException(Unavailable)).GetConnectionString();

        await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(root) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand create = new("drop database if exists " + name + " with (force)", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand again = new("create database " + name, connection);
            await again.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        string connectionString = new NpgsqlConnectionStringBuilder(root) { Database = name }.ConnectionString;

        await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand extension = new("create extension hypopg; create extension pg_stat_statements", connection);
            await extension.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        return connectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}

/// <summary>
/// 1.2-INDEX-A against a real Marten store and hypopg: a document type queried through Marten LINQ on a duplicated field is a statement
/// the advisor plans, and its recommendation is the <c>StoreOptions</c> line that declares the index - never SQL for an
/// <c>mt_doc_</c> table - with the index used by the plan under it. (Marten 9.57 writes a JSON member's key as a parameter,
/// <c>data -&gt;&gt; $2 = $1</c>, so a statement on an undeclared JSON member names no member in <c>pg_stat_statements</c> and the
/// advisor can offer none for it.)
/// </summary>
public class IndexAdvisorMartenLiveTests(MartenAdvisorServer server) : IClassFixture<MartenAdvisorServer>
{
    private const string Schema = "adv_marten";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly StudioScope Scope = new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

    [PostgresFact]
    public async Task A_duplicated_field_that_lost_its_index_is_recommended_as_its_StoreOptions_line_not_as_SQL()
    {
        server.SkipIfUnavailable();

        string connectionString = await server.CreateDatabaseAsync("adv_marten_linq");
        ServiceCollection services = new();

        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedIn>();
        services.AddMarten(options =>
        {
            options.Connection(connectionString);
            options.DatabaseSchemaName = Schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<WhatIfPaint>().Duplicate(x => x.Color);
        });
        services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.QueryTimeout = TimeSpan.FromSeconds(30);
        });

        await using ServiceProvider provider = services.BuildServiceProvider();
        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();

        await store.BulkInsertAsync(
            [.. Enumerable.Range(1, 20_000).Select(static i => new WhatIfPaint { Id = Guid.NewGuid(), Color = "colour" + (i % 100).ToString(CultureInfo.InvariantCulture), Litres = i % 50 })],
            cancellation: Token);

        await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(Token);
            // Duplicate() gives the table a column and an index on it; somebody dropped the index by hand, as the advisor's
            // "a duplicated field that has lost its own" has it.
            await using NpgsqlCommand analyze = new(
                "drop index " + Schema + ".mt_doc_whatifpaint_idx_color; analyze " + Schema + ".mt_doc_whatifpaint", connection);
            await analyze.ExecuteNonQueryAsync(Token);
        }

        for (int i = 0; i < 30; i++)
        {
            await using IQuerySession session = store.QuerySession();
            string color = "colour" + (i % 5).ToString(CultureInfo.InvariantCulture);
            (await session.Query<WhatIfPaint>().Where(x => x.Color == color).ToListAsync(Token)).Should().NotBeEmpty();
        }

        await using AsyncServiceScope circuit = provider.CreateAsyncScope();
        IndexAdviceView view = await circuit.ServiceProvider.GetRequiredService<IStatementsService>().AdviseIndexesAsync(Scope, new IndexAdviceRequest(), Token);

        view.Reason.Should().BeNull();
        view.Unavailable.Should().BeNull();
        IndexAdviceItem item = view.Recommended.Should().ContainSingle().Subject;
        item.Index.Origin.Should().Be(PostgresStudio.Services.Query.HypotheticalIndexOrigin.Marten);
        item.Remedy.Should().Be("options.Schema.For<WhatIfPaint>().Index(x => x.Color);", "the line that declares the index, never SQL for an mt_doc_ table");
        item.Remedy.Should().NotContain("CREATE INDEX");
        item.Statements.Should().Contain(static x => x.Used && x.After < x.Before);
        item.RemedyNote.Should().Contain("Declare it in StoreOptions");

        provider.GetRequiredService<StudioActionLogService>().GetLatest()
            .Should().ContainSingle(static x => x.Action == StatementsService.IndexAdviceAction && x.Succeeded);
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ops")], "test"))));
    }
}
