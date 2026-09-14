using System.Security.Claims;

using Marten;
using Marten.Schema;

using MartenStudio.Services;
using MartenStudio.Services.Relationships;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Relationships;

/// <summary>
/// Opening the Relationships screen creates nothing.
/// </summary>
/// <remarks>
/// <para>
/// The relationships half of what <c>DocumentsNoDdlLiveTests</c> and <c>SchemaNoDdlLiveTests</c> prove
/// for their own screens. The obvious way to find a store's foreign keys is Weasel's own table reader,
/// reached through <c>IMartenDatabase.DocumentTables()</c> — and that is <c>AllObjects()</c>, which is
/// <c>BuildFeatureSchemas().SelectMany(...)</c>, which reaches the lazy HiLo <c>Sequences</c> feature of
/// any store with a numeric id and blocks on <c>Migrator.ApplyAllAsync</c> under the database's own
/// <c>AutoCreate</c>. So the obvious way makes <c>mt_hilo</c> and <c>mt_get_next_hi</c> appear because
/// somebody opened a tab (AGENTS.md hard rule 14).
/// </para>
/// <para>
/// The store below is shaped to make that happen if it still can: a document type with a <c>long</c> id
/// so the HiLo feature is active, <c>AutoCreate.CreateOrUpdate</c> — Marten's own default, and the mode
/// such a migration would run under — and two schemas with nothing in them.
/// </para>
/// </remarks>
public class RelationshipsNoDdlLiveTests(PostgresFixture fixture)
{
    private static readonly StudioScope Scope = new("default", string.Empty, null);

    [PostgresFact]
    public async Task Reading_the_relationships_of_an_empty_schema_creates_no_object_at_all()
    {
        await using Host host = await StartAsync("empty");

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Should().Be(SchemaObjects.Empty, "the fixture created two empty schemas and nothing else");

        RelationshipGraph graph = await host.RelationshipsAsync(x => x.GetGraphAsync(Scope));
        ReferencedBy referenced = await host.RelationshipsAsync(
            x => x.GetReferencedByAsync(Scope, "numbered", "1"));

        // Each answered, rather than failing in a way that would make the counts trivially unchanged.
        graph.Error.Should().BeNull();
        graph.Nodes.Select(x => x.Alias).Should().Contain("numbered");
        referenced.Entries.Should().ContainSingle();

        // Declared but not physical, because nothing has ever applied this schema - which is the drift
        // state the diagram exists to show, arrived at here without a single statement being run.
        graph.Edges.Should().ContainSingle()
            .Which.Physical.Should().BeFalse("the two schemas are empty");

        (await host.ReadObjectsAsync()).Should().Be(SchemaObjects.Empty,
            "drawing the relationships diagram must never run DDL - not even Marten's own HiLo bookkeeping");
    }

    [PostgresFact]
    public async Task Reading_the_relationships_of_a_real_schema_creates_nothing_beyond_the_migration()
    {
        await using Host host = await StartAsync("applied");

        // The host's own deployment applied the schema. Everything after this line is the studio reading.
        await host.ApplyAsync();

        SchemaObjects before = await host.ReadObjectsAsync();
        before.Tables.Should().Contain("mt_doc_numbered");

        RelationshipGraph graph = await host.RelationshipsAsync(x => x.GetGraphAsync(Scope));

        graph.Error.Should().BeNull();
        graph.Edges.Should().ContainSingle(x => x.FromAlias == "numberednote" && x.ToAlias == "numbered")
            .Which.Physical.Should().BeTrue();

        ReferencedBy referenced = await host.RelationshipsAsync(
            x => x.GetReferencedByAsync(Scope, "numbered", "1"));

        referenced.Entries.Should().ContainSingle();

        (await host.ReadObjectsAsync()).Should().Be(before, "reading the relationships creates nothing");
    }

    private async Task<Host> StartAsync(string name)
    {
        string schema = "p10n_" + name;

        await fixture.CreateSchemaAsync(schema);
        await fixture.CreateSchemaAsync(schema + "_events");

        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema + "_events";

            // Marten's own default, said out loud: this is the mode a read path would have run a
            // migration under, and leaving it at the default is what makes the test meaningful.
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;

            // A long id means StorageFeatures.SequenceIsRequired() is true, which is what puts the lazy
            // HiLo feature into AllActiveFeatures and makes a careless read create schema.
            options.Schema.For<NumberedThing>();
            options.Schema.For<NumberedNote>().ForeignKey<NumberedThing>(x => x.ThingId);
        });

        services.AddMartenStudio(options => options.AuthorizationPolicy = null);

        return new Host(services.BuildServiceProvider(), fixture.ConnectionString, schema);
    }

    /// <summary>A document type with a numeric id, so Marten's HiLo sequence feature is active.</summary>
    [DocumentAlias("numbered")]
    public class NumberedThing
    {
        /// <summary>A <c>long</c> id, which Marten backs with the HiLo sequence.</summary>
        public long Id { get; set; }

        /// <summary>Something to read back.</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>A document type pointing at <see cref="NumberedThing" />, so there is a key to find.</summary>
    [DocumentAlias("numberednote")]
    public class NumberedNote
    {
        /// <summary>The id.</summary>
        public Guid Id { get; set; }

        /// <summary>The thing this note is about.</summary>
        public long ThingId { get; set; }
    }

    /// <summary>Every object in the two schemas, by name.</summary>
    private sealed record SchemaObjects(
        IReadOnlyList<string> Tables,
        IReadOnlyList<string> Routines,
        IReadOnlyList<string> Sequences)
    {
        public static SchemaObjects Empty { get; } = new([], [], []);

        public bool Equals(SchemaObjects? other) =>
            other is not null
            && Tables.SequenceEqual(other.Tables)
            && Routines.SequenceEqual(other.Routines)
            && Sequences.SequenceEqual(other.Sequences);

        public override int GetHashCode() => HashCode.Combine(Tables.Count, Routines.Count, Sequences.Count);

        public override string ToString() =>
            $"tables [{string.Join(", ", Tables)}], routines [{string.Join(", ", Routines)}], " +
            $"sequences [{string.Join(", ", Sequences)}]";
    }

    /// <summary>One built studio over one pair of schemas.</summary>
    private sealed class Host(ServiceProvider provider, string connectionString, string schema) : IAsyncDisposable
    {
        public string Schema => schema;

        public string EventSchema => schema + "_events";

        internal async Task<T> RelationshipsAsync<T>(Func<IRelationshipDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();

            return await action(scope.ServiceProvider.GetRequiredService<IRelationshipDataService>());
        }

        /// <summary>What the host's own deployment would have done, which is not the studio's business.</summary>
        public async Task ApplyAsync()
        {
            Marten.IDocumentStore store = provider.GetRequiredService<Marten.IDocumentStore>();

            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        /// <summary>
        /// What <c>information_schema</c> says is in the two schemas, read straight from the catalog on a
        /// connection of the test's own, so nothing about the studio's own code is in the measurement.
        /// </summary>
        public async Task<SchemaObjects> ReadObjectsAsync()
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            return new SchemaObjects(
                await ReadAsync(connection, "select table_name from information_schema.tables where table_schema = any(@schemas) order by table_name"),
                await ReadAsync(connection, "select routine_name from information_schema.routines where routine_schema = any(@schemas) order by routine_name"),
                await ReadAsync(connection, "select sequence_name from information_schema.sequences where sequence_schema = any(@schemas) order by sequence_name"));
        }

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();

        private async Task<IReadOnlyList<string>> ReadAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("schemas", new[] { Schema, EventSchema });

            List<string> names = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }
    }

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}
