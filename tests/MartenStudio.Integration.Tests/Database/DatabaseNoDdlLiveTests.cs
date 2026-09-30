using System.Security.Claims;

using Marten;
using Marten.Schema;

using MartenStudio.Services;
using MartenStudio.Services.Database;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// Every database-browser service method, against schemas that start completely empty: nothing may be
/// created (AGENTS.md hard rule 14).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <c>SchemaNoDdlLiveTests</c>, and for the same reason. The store below is built to
/// make a migration happen if anything lets one: a document type with a <c>long</c> id (so Marten's lazy
/// HiLo feature is active), Marten's own default <c>AutoCreate.CreateOrUpdate</c>, and two schemas with
/// nothing in them. The browser is opened as wide as it goes - the capability on and
/// <c>BrowsableSchemas = ["*"]</c> - and every method is called, the row gate included. If any of them
/// reached <c>AllSchemaNames()</c>, <c>AllObjects()</c> or a Weasel migration, <c>mt_hilo</c> would appear.
/// </para>
/// <para>
/// The anti-vacuity theory is in the same place: <c>SchemaNoDdlLiveTests</c> proves on every run that
/// <c>AllSchemaNames()</c> really does create the HiLo objects on this Marten, which is what makes an
/// unchanged schema here mean something.
/// </para>
/// </remarks>
public class DatabaseNoDdlLiveTests(PostgresFixture fixture)
{
    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Opening_every_database_browser_method_creates_no_object_at_all()
    {
        const string schema = "dbn_empty";
        const string eventSchema = schema + "_events";

        await fixture.CreateSchemaAsync(schema);
        await fixture.CreateSchemaAsync(eventSchema);

        await using ServiceProvider provider = Build(schema, eventSchema);

        SchemaObjects before = await ReadObjectsAsync(schema, eventSchema);
        before.Should().Be(SchemaObjects.Empty, "the test created two empty schemas and nothing else");

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            IDatabaseObjectService objects = scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>();
            DatabaseAccess access = scope.ServiceProvider.GetRequiredService<DatabaseAccess>();

            DatabaseBrowserOverview overview = await objects.GetOverviewAsync(Scope, Token);
            overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);
            overview.Schemas.Select(static x => x.Name).Should().Contain(schema);

            foreach (DatabaseObjectCategory category in Enum.GetValues<DatabaseObjectCategory>())
            {
                DatabaseObjectList all = await objects.ListAsync(Scope, new DatabaseObjectQuery(category), Token);
                all.Refusal.Should().Be(DatabaseRefusal.None, all.Reason);

                DatabaseObjectList one = await objects.ListAsync(Scope, new DatabaseObjectQuery(category, schema, NameFilter: "mt_"), Token);
                one.Refusal.Should().Be(DatabaseRefusal.None, one.Reason);
            }

            (await objects.GetObjectAsync(Scope, schema, "mt_doc_numberedthing", Token)).Refusal
                .Should().Be(DatabaseRefusal.NotFound);
            (await objects.GetObjectAsync(Scope, "public", "anything", Token)).Refusal
                .Should().Be(DatabaseRefusal.NotFound);

            foreach (DatabaseObjectKind kind in new[]
                     {
                         DatabaseObjectKind.View, DatabaseObjectKind.MaterializedView, DatabaseObjectKind.Function,
                         DatabaseObjectKind.Procedure, DatabaseObjectKind.Trigger, DatabaseObjectKind.EnumType,
                         DatabaseObjectKind.DomainType,
                     })
            {
                DatabaseObjectDefinition definition = await objects.GetDefinitionAsync(
                    Scope, new DatabaseObjectRef(kind, schema, "mt_get_next_hi", string.Empty, "mt_hilo"), Token);

                definition.Refusal.Should().Be(DatabaseRefusal.NotFound, kind + ": " + definition.Reason);
            }

            DatabaseRowAccessResult rows = await access.RequireRowAccessAsync(Scope, schema, "mt_hilo", cancellationToken: Token);
            rows.Refusal.Should().Be(DatabaseRefusal.NotFound);
        }

        SchemaObjects after = await ReadObjectsAsync(schema, eventSchema);

        after.Should().Be(before,
            "reading the database browser must never run DDL - not even Marten's own HiLo bookkeeping");
    }

    private ServiceProvider Build(string schema, string eventSchema)
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = eventSchema;

            // Marten's own default, said out loud: the mode a read path would have migrated under.
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;

            // A long id puts the lazy HiLo feature into AllActiveFeatures.
            options.Schema.For<NumberedThing>();
        });

        services.AddMartenStudio(static options =>
        {
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add("*");
        });

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// What <c>pg_catalog</c> says is in the two schemas, read on a connection of the test's own - so
    /// nothing of the studio's is in the measurement.
    /// </summary>
    private async Task<SchemaObjects> ReadObjectsAsync(string schema, string eventSchema)
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        async Task<IReadOnlyList<string>> ReadAsync(string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("schemas", new[] { schema, eventSchema });

            List<string> names = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        return new SchemaObjects(
            await ReadAsync("select c.relname::text from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = any(@schemas) order by 1"),
            await ReadAsync("select p.proname::text from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = any(@schemas) order by 1"),
            await ReadAsync("select t.typname::text from pg_type t join pg_namespace n on n.oid = t.typnamespace where n.nspname = any(@schemas) order by 1"));
    }

    /// <summary>A document type with a numeric id, so Marten's HiLo sequence feature is active.</summary>
    [DocumentAlias("numberedthing")]
    public class NumberedThing
    {
        /// <summary>A <c>long</c> id, which Marten backs with the HiLo sequence.</summary>
        public long Id { get; set; }
    }

    /// <summary>Every relation (tables, views, sequences, indexes), routine and type in the two schemas.</summary>
    private sealed record SchemaObjects(
        IReadOnlyList<string> Relations,
        IReadOnlyList<string> Routines,
        IReadOnlyList<string> Types)
    {
        public static SchemaObjects Empty { get; } = new([], [], []);

        public bool Equals(SchemaObjects? other) =>
            other is not null
            && Relations.SequenceEqual(other.Relations)
            && Routines.SequenceEqual(other.Routines)
            && Types.SequenceEqual(other.Types);

        public override int GetHashCode() => HashCode.Combine(Relations.Count, Routines.Count, Types.Count);

        public override string ToString() =>
            $"relations [{string.Join(", ", Relations)}], routines [{string.Join(", ", Routines)}], types [{string.Join(", ", Types)}]";
    }

    /// <summary>Somebody is signed in.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}
