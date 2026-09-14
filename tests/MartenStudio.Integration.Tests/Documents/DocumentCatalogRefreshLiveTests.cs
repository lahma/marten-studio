using Marten;

using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;
using MartenStudio.Services.Schema;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// A schema change the studio itself applied is visible to the studio's own catalogs at once.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-perf deliverable 4.</b> <see cref="MartenStudio.Internal.Sql.ColumnCatalog" /> and
/// <see cref="MartenStudio.Internal.Sql.IndexCatalog" /> are DI singletons that cache for sixty seconds,
/// which is right for migrations the studio did not make — a deployment, a DBA — and wrong for the one it
/// did. Without a <c>Clear()</c>, somebody pressing Apply on the Schema screen watched the documents
/// browser go on selecting the old column set and the index verdict go on recommending the index that had
/// just been created, for up to a minute, with no way to tell whether the apply had worked.
/// </para>
/// <para>
/// <b>The direction of the test is what makes it honest.</b> The column is removed behind Marten's back
/// <em>first</em>, so the warm cache holds the smaller shape and the apply is what adds it. Going the
/// other way — caching a column and then dropping it — would prove nothing about invalidation, because the
/// next list read would simply fail on a column that is not there.
/// </para>
/// </remarks>
public class DocumentCatalogRefreshLiveTests(DocumentCatalogRefreshLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentCatalogRefreshLiveTests.Fixture>
{
    private const string DuplicatedColumnKey = "dup:email";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A collection with a duplicated field, whose column and index are taken away before the tests run.
    /// </summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options) =>
            // Duplicate() gives the table an `email` column and an index on it, which is exactly the pair
            // the two catalogs cache.
            options.Schema.For<CatalogThing>().Duplicate(x => x.Email);

        /// <inheritdoc />
        protected override void ConfigureStudio(MartenStudioOptions options) =>
            options.Capabilities = MartenStudioCapabilities.All();

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using (IDocumentSession session = Marten.Store.LightweightSession())
            {
                session.Store(new CatalogThing { Id = Guid.NewGuid(), Email = "bob@example.com" });
                session.Store(new CatalogThing { Id = Guid.NewGuid(), Email = "alice@example.com" });

                await session.SaveChangesAsync();
            }

            // Behind Marten's back, so that StoreOptions declares a duplicated field the database does not
            // have. Dropping the column drops its index with it, which is the other half of what is being
            // invalidated.
            await using NpgsqlConnection connection = await Postgres.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"alter table \"{Schema}\".\"mt_doc_catalogthing\" drop column \"email\"", connection);

            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// The list reconciles against the physical columns before the apply, and sees the new one after it.
    /// </summary>
    /// <remarks>
    /// One test rather than two, because the whole claim is about the sequence: the first read is what
    /// warms the caches, and a second test could not depend on that having happened.
    /// </remarks>
    [PostgresFact]
    public async Task A_migration_the_studio_applied_is_seen_by_its_own_catalogs_without_waiting_for_the_ttl()
    {
        using var documents = Documents();
        using var schema = Marten.Resolve<ISchemaDataService>();

        // 1. Warm. The configuration says there is a duplicated `email` column; the database says there is
        //    not, and the physical column set is what the studio believes (hard rule 10).
        DocumentPage before = await documents.Service.ListAsync(
            Scope, "catalogthing", new DocumentListRequest { PageSize = 10 }, Token);

        before.State.Should().Be(DocumentListState.Loaded, before.Error);
        before.Columns.Select(x => x.Key).Should().NotContain(DuplicatedColumnKey);
        before.Rows.Should().HaveCount(2);

        // ... and the index verdict, warming IndexCatalog, has no duplicated column to be served by.
        DocumentPage filteredBefore = await documents.Service.ListAsync(
            Scope, "catalogthing", new DocumentListRequest { Search = "Email = bob@example.com" }, Token);

        filteredBefore.Verdict.FilterLevel.Should().Be(
            IndexVerdictLevel.Red, "the property is only in the JSON while the column is missing");

        // 2. Apply, through the studio's own service, with the capability and the typed confirmation.
        var identity = await schema.Service.DatabaseIdentityAsync(Scope, Token);
        SchemaApplyResult applied = await schema.Service.ApplyAsync(Scope, identity, Token);

        applied.Succeeded.Should().BeTrue(applied.Message);

        // 3. Immediately - no sixty-second wait, no new process, the same singleton catalogs.
        DocumentPage after = await documents.Service.ListAsync(
            Scope, "catalogthing", new DocumentListRequest { PageSize = 10 }, Token);

        after.State.Should().Be(DocumentListState.Loaded, after.Error);
        after.Columns.Select(x => x.Key).Should().Contain(
            DuplicatedColumnKey,
            "ColumnCatalog.Clear() runs after the apply, so the list sees the column the apply created");

        after.Rows.Select(x => x.Cells[after.Columns.ToList().FindIndex(c => c.Key == DuplicatedColumnKey)])
            .Should().Contain("bob@example.com", "and Marten backfilled the duplicated column");

        DocumentPage filteredAfter = await documents.Service.ListAsync(
            Scope, "catalogthing", new DocumentListRequest { Search = "Email = bob@example.com" }, Token);

        filteredAfter.Verdict.FilterLevel.Should().Be(
            IndexVerdictLevel.Green,
            "IndexCatalog.Clear() runs after the apply too, so the verdict sees the index the apply created");
        filteredAfter.State.Should().Be(DocumentListState.Loaded, filteredAfter.Error);
        filteredAfter.Rows.Should().ContainSingle();
    }
}

/// <summary>A document with one duplicated field, which is all this class needs.</summary>
public sealed class CatalogThing
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Duplicated into a column of its own, with an index.</summary>
    public string Email { get; set; } = string.Empty;
}
