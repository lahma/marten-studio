using System.Security.Claims;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Database;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// Acceptance 9, hard rule 14: every row method, called against a store built to migrate if anything lets
/// it, creates no object at all.
/// </summary>
/// <remarks>
/// The <see cref="DatabaseNoDdlLiveTests" /> shape: a <c>long</c>-id document type puts Marten's lazy HiLo
/// feature in play, <c>AutoCreate</c> is Marten's own <c>CreateOrUpdate</c>, and the store's two schemas
/// start empty - so a read that reached <c>AllSchemaNames()</c>, <c>AllObjects()</c> or a Weasel migration
/// would leave <c>mt_hilo</c> behind. A third schema holds a plain keyed table, a table with no key and a
/// view, so that every method actually reads rows rather than stopping at a refusal.
/// </remarks>
/// <param name="fixture">The assembly's container.</param>
public class TableRowNoDdlLiveTests(PostgresFixture fixture)
{
    private const string StoreSchema = "trn_empty";
    private const string EventSchema = StoreSchema + "_events";
    private const string PlainSchema = "trn_plain";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Calling_every_row_method_creates_no_object_at_all()
    {
        await fixture.CreateSchemaAsync(StoreSchema);
        await fixture.CreateSchemaAsync(EventSchema);
        await fixture.CreateSchemaAsync(PlainSchema);

        await using (NpgsqlConnection setup = await fixture.OpenAsync())
        await using (var command = new NpgsqlCommand(
                         """
                         create table trn_plain.parents (id int primary key, name text);
                         create table trn_plain.things (id int primary key, parent_id int references trn_plain.parents (id), body text, blob bytea);
                         create table trn_plain.log (at text);
                         create view trn_plain.thing_names as select id, body from trn_plain.things;
                         insert into trn_plain.parents values (1, 'one');
                         insert into trn_plain.things values (1, 1, 'x', '\x0102'), (2, null, 'y', null);
                         insert into trn_plain.log values ('a'), ('a');
                         """,
                         setup))
        {
            await command.ExecuteNonQueryAsync(Token);
        }

        await using ServiceProvider provider = Build();

        IReadOnlyList<string> before = await ReadObjectsAsync();
        before.Should().Contain("trn_plain.things").And.NotContain(x => x.StartsWith(StoreSchema + ".", StringComparison.Ordinal),
            "the store's schemas start empty");

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            ITableRowService rows = scope.ServiceProvider.GetRequiredService<ITableRowService>();
            var one = new Dictionary<string, string> { ["id"] = "1" };

            (await rows.ListRowsAsync(Scope, PlainSchema, "things", new TableRowRequest { Filter = "body ~ x", SortColumn = "body" }, Token))
                .State.Should().Be(TableRowPageState.Loaded);
            (await rows.ListRowsAsync(Scope, PlainSchema, "log", new TableRowRequest(), Token)).Rows.Should().HaveCount(2);
            (await rows.ListRowsAsync(Scope, PlainSchema, "thing_names", new TableRowRequest(), Token)).Rows.Should().HaveCount(2);
            (await rows.GetRowAsync(Scope, PlainSchema, "things", one, Token)).Found.Should().BeTrue();
            (await rows.GetReferencesAsync(Scope, PlainSchema, "things", one, Token)).Outbound.Should().ContainSingle()
                .Which.State.Should().Be(RowReferenceState.Present);
            (await rows.GetReferencesAsync(Scope, PlainSchema, "parents", one, Token)).Inbound.Should().ContainSingle()
                .Which.Count.Should().Be(1);
            (await rows.CountExactAsync(Scope, PlainSchema, "things", null, Token)).Count.Value.Should().Be(2);
            (await rows.CountExactAsync(Scope, PlainSchema, "things", "parent_id is:null", Token)).Count.Value.Should().Be(1);
            (await rows.GetCellAsync(Scope, PlainSchema, "things", one, "blob", Token)).Bytes.Should().Equal(1, 2);

            (await rows.ListRowsAsync(Scope, StoreSchema, "mt_hilo", new TableRowRequest(), Token)).Refusal
                .Should().Be(DatabaseRefusal.NotFound, "there is no mt_hilo - and asking must not make one");
            (await rows.CountExactAsync(Scope, StoreSchema, "mt_doc_numberedthing", null, Token)).Refusal
                .Should().Be(DatabaseRefusal.NotFound);
        }

        IReadOnlyList<string> after = await ReadObjectsAsync();

        after.Should().Equal(before, "reading rows must never run DDL - not even Marten's own HiLo bookkeeping");
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = StoreSchema;
            options.Events.DatabaseSchemaName = EventSchema;

            // Marten's own default, said out loud: the mode a read path would have migrated under.
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;

            // A long id puts the lazy HiLo feature into AllActiveFeatures.
            options.Schema.For<DatabaseNoDdlLiveTests.NumberedThing>();
        });

        services.AddMartenStudio(static options =>
        {
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add("*");
        });

        return services.BuildServiceProvider();
    }

    /// <summary>Every relation, routine and type in the three schemas, read on a connection of the test's own.</summary>
    private async Task<IReadOnlyList<string>> ReadObjectsAsync()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            select n.nspname || '.' || c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = any(@schemas)
            union all
            select n.nspname || '.' || p.proname || '()' from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = any(@schemas)
            union all
            select n.nspname || '.' || t.typname || ' (type)' from pg_type t join pg_namespace n on n.oid = t.typnamespace where n.nspname = any(@schemas)
            order by 1
            """,
            connection);

        command.Parameters.AddWithValue("schemas", new[] { StoreSchema, EventSchema, PlainSchema });

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);

        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>Somebody is signed in.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}
