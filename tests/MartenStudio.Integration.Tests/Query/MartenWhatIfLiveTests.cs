using System.Globalization;
using System.Security.Claims;

using JasperFx;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Query;

using Testcontainers.PostgreSql;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>The document X2's Marten what-ifs plan over: 20,000 of them, a hundred colours, nothing declared on either.</summary>
public sealed class WhatIfPaint
{
    public Guid Id { get; set; }

    public string Color { get; set; } = string.Empty;

    public int Litres { get; set; }
}

/// <summary>
/// X2's server for the Marten suite: the Debian <c>postgres:17.11</c> with <c>postgresql-17-hypopg</c> from its PGDG apt source,
/// PROBE's recipe, never reused; each test in a schema and a database of its own. Locally a server or a package that is not
/// there skips with the reason; on GitHub Actions it fails the run (D19).
/// </summary>
public sealed class MartenHypopgServer : IAsyncLifetime
{
    public const string Image = "postgres:17.11";

    public const string Install = "apt-get update -qq && apt-get install -y -qq postgresql-17-hypopg";

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

        container = new PostgreSqlBuilder(Image)
            .WithReuse(false)
            .WithName("ms-marten-hypopg-" + Guid.NewGuid().ToString("N")[..8])
            .WithLabel("ms-purpose", "hypopg")
            .Build();

        try
        {
            await container.StartAsync();
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration)
        {
            await container.DisposeAsync();
            container = null;
            Unavailable = "The hypopg server could not start " + Image + " here (" + exception.GetType().Name + "); `docker pull " + Image + "` makes it run.";
            return;
        }

        var installed = await container.ExecAsync(["bash", "-c", Install]);

        if (installed.ExitCode != 0)
        {
            string why = "hypopg could not be installed in " + Image + " (`" + Install + "` exited "
                + Convert.ToString(installed.ExitCode, CultureInfo.InvariantCulture) + ").";

            if (DockerAvailability.OnContinuousIntegration)
            {
                throw new InvalidOperationException(why + " On CI this is a failure, not a skip.");
            }

            Unavailable = why + " It needs the network for apt.postgresql.org.";
        }
    }

    /// <summary>A database of the test's own, with hypopg installed by the superuser - never by the studio.</summary>
    public async Task<string> CreateDatabaseAsync(string name)
    {
        string root = (container ?? throw new InvalidOperationException(Unavailable)).GetConnectionString();

        await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(root) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand create = new("drop database if exists " + name + " with (force); ", connection);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand again = new("create database " + name, connection);
            await again.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        string connectionString = new NpgsqlConnectionStringBuilder(root) { Database = name }.ConnectionString;

        await using (NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand extension = new("create extension hypopg", connection);
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
/// X2's Marten half against a real hypopg: the index the advisor's line would make Marten create - Marten's own DDL - is the one
/// hypopg imagines, and Postgres uses it for Marten's own LINQ statement and for the Documents list's statement; nothing is created.
/// </summary>
public class MartenWhatIfLiveTests(MartenHypopgServer server) : IClassFixture<MartenHypopgServer>
{
    private const string Schema = "x2_marten";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly StudioScope Scope = new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

    private async Task<(ServiceProvider Provider, string ConnectionString)> HostAsync(string database)
    {
        server.SkipIfUnavailable();

        string connectionString = await server.CreateDatabaseAsync(database);
        ServiceCollection services = new();

        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedIn>();
        services.AddMarten(options =>
        {
            options.Connection(connectionString);
            options.DatabaseSchemaName = Schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Schema.For<WhatIfPaint>();
        });
        services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.QueryTimeout = TimeSpan.FromSeconds(30);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();

        List<WhatIfPaint> paints = [.. Enumerable.Range(1, 20_000).Select(static i =>
            new WhatIfPaint { Id = Guid.NewGuid(), Color = "colour" + (i % 100).ToString(CultureInfo.InvariantCulture), Litres = i % 50 })];
        await store.BulkInsertAsync(paints, cancellation: Token);

        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        // One bulk insert stamps every row with one transaction's timestamp; spread them, as a store's writes would.
        await using NpgsqlCommand analyze = new(
            "update " + Schema + ".mt_doc_whatifpaint set mt_last_modified = mt_last_modified - (pg_catalog.random() * interval '30 days'); "
            + "analyze " + Schema + ".mt_doc_whatifpaint",
            connection);
        await analyze.ExecuteNonQueryAsync(Token);

        return (provider, connectionString);
    }

    /// <summary>Every relation in the store's schema, by name and kind, and pg_class's own size: what a created index would move.</summary>
    private static async Task<IReadOnlySet<string>> RelationsAsync(string connectionString)
    {
        await using NpgsqlConnection connection = new(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(
            "select c.relname || ':' || c.relkind::text from pg_catalog.pg_class c join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = @schema "
            + "union all select 'pg_class:' || count(*)::text || ':' || max(oid::int8)::text from pg_catalog.pg_class", connection);
        command.Parameters.AddWithValue("schema", Schema);
        HashSet<string> names = new(StringComparer.Ordinal);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    [LinqLiveFact]
    public async Task LINQ_modes_advice_imagines_the_computed_index_Marten_would_create_and_Marten_s_own_statement_uses_it()
    {
        (ServiceProvider owned, string connectionString) = await HostAsync("x2_linq");
        await using ServiceProvider provider = owned;
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();
        ILinqQueryService linq = circuit.ServiceProvider.GetRequiredService<ILinqQueryService>();

        LinqModeStatus status = await linq.DescribeAsync(Scope, Token);
        string alias = status.Types.Single(static x => x.TypeName == nameof(WhatIfPaint)).Alias;
        LinqQueryRequest request = new(alias, "Color == \"colour7\"");

        LinqQueryResult run = await linq.RunAsync(Scope, request, Token);
        run.Refusal.Should().BeNull();
        HypotheticalIndex index = run.Advice!.WhatIf.Should().ContainSingle().Subject;
        index.Definition.Should().Be("CREATE INDEX mt_doc_whatifpaint_idx_color ON " + Schema + ".mt_doc_whatifpaint USING btree ((data ->> 'Color'));",
            "the index Marten's Index(x => x.Color) creates, as Marten writes it");

        IReadOnlySet<string> before = await RelationsAsync(connectionString);

        WhatIfResult result = await linq.WhatIfAsync(Scope, request, index, Token);

        result.Reason.Should().BeNull();
        result.Used.Should().BeTrue("Marten's statement compares data ->> 'Color', the expression Marten's computed index is on");
        result.IndexName.Should().MatchRegex("^<[0-9]+>btree_x2_marten_mt_doc_whatifpaint_expr$", "hypopg names an expression index <oid>btree_<schema>_<table>_expr");
        result.Before!.Nodes.Should().Contain(static x => x.NodeType == "Seq Scan");
        (await RelationsAsync(connectionString)).Should().BeEquivalentTo(before, "nothing was created: no relation, no pg_class row");

        provider.GetRequiredService<StudioActionLogService>().GetLatest().Should().Contain(static x => x.Action == LinqQueryService.LinqWhatIfAction && x.Succeeded);
    }

    [PostgresFact]
    public async Task The_Documents_list_sorted_by_mt_last_modified_imagines_IndexLastModified_and_says_which_order_it_serves()
    {
        (ServiceProvider owned, string connectionString) = await HostAsync("x2_documents");
        await using ServiceProvider provider = owned;
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();
        IDocumentDataService documents = circuit.ServiceProvider.GetRequiredService<IDocumentDataService>();

        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();
        string alias = store.Options.FindOrResolveDocumentType(typeof(WhatIfPaint)).Alias;
        string sortKey = DocumentColumnKeys.KeyFor(new DocumentColumn.Metadata(MartenStudio.Internal.Sql.DocumentMetadataColumn.LastModified));
        DocumentListRequest oldestFirst = new() { SortKey = sortKey, Direction = SortDirection.Ascending };

        DocumentPage page = await documents.ListAsync(Scope, alias, oldestFirst, Token);
        HypotheticalIndex index = page.WhatIf.Should().ContainSingle().Subject;
        index.Definition.Should().Be("CREATE INDEX mt_doc_whatifpaint_idx_mt_last_modified ON " + Schema + ".mt_doc_whatifpaint USING btree (mt_last_modified);",
            "the index Marten's IndexLastModified() creates, as Marten writes it");

        IReadOnlySet<string> before = await RelationsAsync(connectionString);

        WhatIfResult ascending = await documents.WhatIfAsync(Scope, alias, oldestFirst, index, Token);

        ascending.Reason.Should().BeNull();
        ascending.Used.Should().BeTrue("oldest first is mt_last_modified asc nulls last: the index's own order, so the page is read alone");
        ascending.After!.Root.TotalCost.Should().BeLessThan(ascending.Before!.Root.TotalCost!.Value);

        // Newest first - the list's default - is desc NULLS LAST, which an ascending btree scanned backwards (desc nulls first)
        // cannot give: the what-if says so, which is the point of asking rather than assuming.
        WhatIfResult descending = await documents.WhatIfAsync(Scope, alias, oldestFirst with { Direction = SortDirection.Descending }, index, Token);

        descending.Reason.Should().BeNull();
        descending.Used.Should().BeFalse("the page's own order is desc nulls last, which the index does not hold");
        descending.Verdict.Should().StartWith("Postgres would not use it for this statement");

        (await RelationsAsync(connectionString)).Should().BeEquivalentTo(before, "nothing was created: no relation, no pg_class row");
        provider.GetRequiredService<StudioActionLogService>().GetLatest().Where(static x => x.Action == DocumentDataService.WhatIfAction)
            .Should().HaveCount(2).And.OnlyContain(static x => x.Succeeded && x.Capability == nameof(StudioCapability.RunLinqQueries));
    }

    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "ops")], "test"))));
    }
}
