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

/// <summary>A strong-typed id this class tags one event type with.</summary>
/// <param name="Value">The account.</param>
public sealed record DriftAccount(string Value);

/// <summary>An event that carries a DCB tag.</summary>
/// <param name="Account">Whose it is.</param>
public sealed record DriftTagged(string Account);

/// <summary>
/// Item 12, live: the tag-table index Marten 9.51 declares, as the Schema screen shows a store whose tag tables were
/// built below 9.51 - and the note the apply section puts beside it.
/// </summary>
/// <remarks>
/// <para>
/// #5694 declares <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c> on every tag table. A store whose tag table was created
/// below 9.51 has no such index, so from 9.51 Check schema reports the tag table as differing, Preview migration renders
/// one plain <c>CREATE INDEX</c> for it - not concurrently, so it holds a SHARE lock on the tag table and every append of
/// a tagged event waits while it builds - and the Indexes tab lists it as declared but missing. The preview's
/// <c>MigrationRisk.Notes</c> name it; it is not a destructive statement, so the apply does not wait for a tick.
/// </para>
/// <para>
/// Such a store is made here the way an upgrade leaves one: the schema is applied, then the index is dropped by hand. Below
/// 9.51 the index is not declared at all, and the preview has nothing to say about it - which is the floor half.
/// </para>
/// </remarks>
public class TagIndexDriftLiveTests(PostgresFixture postgres)
{
    private const string Schema = "deps_tag_index_drift";

    private const string TagTable = "mt_event_tag_drift_account";

    private const string TagIndex = "idx_mt_event_tag_drift_account_seq_id";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_tag_table_built_before_9_51_previews_one_blocking_index_build_with_a_note_and_nothing_destructive()
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
            options.Events.AddEventType<DriftTagged>();
            options.Events.RegisterTagType<DriftAccount>("drift_account");
            options.Events.TagWith<DriftTagged>(static e => new DriftAccount(e.Account));
        });
        services.AddMartenStudio(static options => options.AuthorizationPolicy = null);

        await using ServiceProvider provider = services.BuildServiceProvider();

        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        bool declared = RuntimeFeature.TaggedStreamCompaction.Probe() == true;

        // The probe against the version, not only against itself: its first anchor (a private method marten#5720 moved in
        // 9.52) answered "absent" at every Marten after it, and this test then asserted the floor's half on a store that
        // has the index.
        declared.Should().Be(MartenRuntime.Current.VersionOf(RuntimePackage.Marten) >= new Version(9, 51, 0),
            "#5694 declares the index from Marten 9.51.0, and the probe has to say so at every Marten from it");

        // The premise, each end's: from 9.51 the index exists once the schema is applied; below it, it is never declared.
        (await IndexExistsAsync()).Should().Be(declared, "Marten 9.51 (#5694) declares the tag table's seq_id index, and below it nothing does");

        // An upgrade's starting point: the tag table as a store below 9.51 built it.
        await DropIndexAsync();

        MigrationPreview preview;
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            preview = await scope.ServiceProvider.GetRequiredService<ISchemaDataService>().PreviewAsync(Scope, Token);
        }

        preview.Withheld.Should().BeNull();
        preview.IsDestructive.Should().BeFalse("an index build removes nothing");

        if (!declared)
        {
            preview.Sql.Should().NotContain(TagIndex, "below 9.51 the index is not declared, so nothing is missing");
            preview.Notes.Should().NotContain(static x => x.Kind == MigrationRisk.BlockingTagIndexBuildKind);
            return;
        }

        preview.Sql.Should().Contain(TagIndex);

        MigrationNote note = preview.Notes.Should().ContainSingle(static x => x.Kind == MigrationRisk.BlockingTagIndexBuildKind).Subject;
        note.Statement.Should().Contain(TagIndex).And.Contain(TagTable);
        note.Statement.Should().NotContainEquivalentOf("concurrently", "Weasel builds it plainly unless the migrator is told otherwise");
    }

    private async Task<bool> IndexExistsAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            "select exists (select 1 from pg_catalog.pg_indexes where schemaname = @schema and tablename = @table and indexname = @index)",
            connection);
        command.Parameters.AddWithValue("schema", Schema);
        command.Parameters.AddWithValue("table", TagTable);
        command.Parameters.AddWithValue("index", TagIndex);

        return (bool) (await command.ExecuteScalarAsync(Token))!;
    }

    private async Task DropIndexAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand("drop index if exists " + SqlIdentifier.Qualify(Schema, TagIndex), connection);
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
