using System.Security.Claims;

using Marten;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Schema;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>An event, so the store has an event store to migrate.</summary>
/// <param name="Name">Anything.</param>
public sealed record FloorDriftHappened(string Name);

/// <summary>
/// B, live: Marten 9.52 adds <c>mt_event_projection_floors</c> to every event store (#5740, not gated on any option), so a
/// store migrated below 9.52 shows it as one <c>CREATE TABLE</c> of drift - which the Schema screen's preview calls
/// non-destructive, with no note, because building a new table removes nothing and blocks no writer.
/// </summary>
/// <remarks>
/// Such a store is made here the way an upgrade leaves one: the schema is applied, then the table is dropped by hand. Below
/// 9.52 the table is not declared at all, and the preview has nothing to say about it - the floor half.
/// </remarks>
public class ProjectionFloorsDriftLiveTests(PostgresFixture postgres)
{
    private const string Schema = "deps2_floors_drift";

    private const string FloorsTable = "mt_event_projection_floors";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_store_migrated_below_9_52_previews_the_floors_table_as_one_create_and_nothing_destructive()
    {
        await postgres.CreateSchemaAsync(Schema, Token);

        var services = new ServiceCollection();
        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedIn>();
        services.AddMarten(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Schema;
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;
            options.Events.AddEventType<FloorDriftHappened>();
        });
        services.AddMartenStudio(static options => options.AuthorizationPolicy = null);

        await using ServiceProvider provider = services.BuildServiceProvider();

        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        bool declared = MartenRuntime.Current.VersionOf(RuntimePackage.Marten) >= new Version(9, 52, 0);
        RuntimeFeature.ProjectionStartFloors.Probe().Should().Be(declared, "the floors and their table are both Marten 9.52's (#5740)");

        // The premise, each end's: from 9.52 the table exists once the schema is applied; below it, nothing declares it.
        (await TableExistsAsync()).Should().Be(declared);

        // An upgrade's starting point: the event store as a Marten below 9.52 built it.
        await DropTableAsync();

        MigrationPreview preview;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            preview = await scope.ServiceProvider.GetRequiredService<ISchemaDataService>().PreviewAsync(Scope, Token);
        }

        preview.Withheld.Should().BeNull();
        preview.IsDestructive.Should().BeFalse("building a new table removes nothing");
        preview.DestructiveStatements.Should().BeEmpty();

        if (!declared)
        {
            preview.Sql.Should().NotContain(FloorsTable, "below 9.52 the table is not declared, so nothing is missing");
            return;
        }

        preview.Sql.Should().Contain(FloorsTable).And.ContainEquivalentOf("create table");
        preview.Notes.Should().NotContain(static x => x.Statement.Contains(FloorsTable, StringComparison.Ordinal),
            "a new table blocks no writer and needs no note of its own");
        (await TableExistsAsync()).Should().BeFalse("previewing applies nothing");
    }

    private async Task<bool> TableExistsAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            "select exists (select 1 from pg_catalog.pg_tables where schemaname = @schema and tablename = @table)",
            connection);
        command.Parameters.AddWithValue("schema", Schema);
        command.Parameters.AddWithValue("table", FloorsTable);

        return (bool) (await command.ExecuteScalarAsync(Token))!;
    }

    private async Task DropTableAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand("drop table if exists " + SqlIdentifier.Qualify(Schema, FloorsTable), connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Somebody is signed in.</summary>
    private sealed class SignedIn : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}
