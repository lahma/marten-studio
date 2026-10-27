using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;
using PostgresStudio.Services.Relationships;

namespace PostgresStudio.Integration.Tests.Database;

/// <summary>
/// Issue #18: Postgres Studio's Relationships draws the keys between the schemas <c>BrowsableSchemas</c> names exactly
/// with <c>BrowseDatabase</c> off, as they are the source's own structure; only <c>"*"</c> waits for the capability.
/// </summary>
public class RelationshipsWithoutBrowseLiveTests(RelationshipsWithoutBrowseLiveTests.Fixture databases)
    : IClassFixture<RelationshipsWithoutBrowseLiveTests.Fixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The class's one database, with two schemas and a key between them.</summary>
    public sealed class Fixture(PostgresFixture postgres) : IAsyncLifetime
    {
        private OwnedDatabases? owned;

        /// <summary>The database's connection string.</summary>
        public string Connection => owned![0];

        public async ValueTask InitializeAsync()
        {
            if (!DockerAvailability.IsAvailable)
            {
                return;
            }

            owned = await OwnedDatabases.CreateAsync(postgres, ["ps_rel_nobrowse"], CancellationToken.None);

            await OwnedDatabases.ExecuteAsync(
                Connection,
                "create schema crm; create schema sales; "
                + "create table crm.customer (id int primary key); "
                + "create table sales.orders (id int primary key, customer_id int not null references crm.customer (id));",
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (owned is not null)
            {
                await owned.DisposeAsync();
            }
        }
    }

    [PostgresFact]
    public async Task Exactly_named_schemas_draw_their_keys_without_BrowseDatabase()
    {
        await using StudioHost host = Host(static options =>
        {
            options.BrowsableSchemas.Add("crm");
            options.BrowsableSchemas.Add("sales");
        });
        await using StudioCircuit circuit = host.Circuit();

        RelationshipGraph graph = await circuit.Get<IRelationshipDataService>().GetGraphAsync(new StudioScope("default", "default", null), Token);

        graph.Error.Should().BeNull();
        RelationshipEdge edge = graph.Edges.Should().ContainSingle().Subject;
        edge.FromAlias.Should().Be(RelationshipTableNode.KeyFor("sales", "orders"));
        edge.ToAlias.Should().Be(RelationshipTableNode.KeyFor("crm", "customer"));
    }

    [PostgresFact]
    public async Task Star_alone_still_waits_for_BrowseDatabase()
    {
        await using StudioHost host = Host(static options => options.BrowsableSchemas.Add("*"));
        await using StudioCircuit circuit = host.Circuit();

        RelationshipGraph graph = await circuit.Get<IRelationshipDataService>().GetGraphAsync(new StudioScope("default", "default", null), Token);

        graph.Edges.Should().BeEmpty();
    }

    private StudioHost Host(Action<PostgresStudioOptions> configure) =>
        StudioHost.Create(
            services => services.AddSingleton(_ => NpgsqlDataSource.Create(databases.Connection)),
            configure);
}
