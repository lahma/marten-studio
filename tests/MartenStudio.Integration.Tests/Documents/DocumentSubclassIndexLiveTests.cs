using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// Marten indexes <c>mt_doc_type</c> on a hierarchy, so the subclass verdict is green.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-perf deliverable 5.</b> <c>IndexAdvisor.EvaluateSubclass</c> carried a remark saying "Marten
/// creates no index on <c>mt_doc_type</c>, so this is normally amber", and its amber branch said the same
/// thing to the user. It is not true: <c>Marten.Storage.DocumentTable</c>'s constructor runs
/// <c>if (mapping.IsHierarchy()) { Indexes.Add(new DocumentIndex(_mapping, SchemaConstants.DocumentTypeColumn)); … }</c>.
/// </para>
/// <para>
/// <b>Pinned live rather than by reflection.</b> <c>DocumentTable</c> is internal and builds its index
/// list in a constructor, so a unit test could only assert that the <c>DocumentIndex</c> it would
/// construct has a given name — which is a fact about <c>DocumentIndex</c> and not about what a migration
/// creates. This asserts the thing the advice actually depends on: that a migrated hierarchy's table has
/// the index in <c>pg_indexes</c>, read through the studio's own <see cref="IndexCatalog" />, and that the
/// advisor reading it says green.
/// </para>
/// </remarks>
public class DocumentSubclassIndexLiveTests(DocumentSubclassIndexLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentSubclassIndexLiveTests.Fixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The demo store, whose <c>Vehicle</c> / <c>Car</c> / <c>Truck</c> is the hierarchy.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres);

    /// <summary>The migrated hierarchy's table has an index leading with the discriminator.</summary>
    [PostgresFact]
    public async Task Marten_creates_an_index_on_mt_doc_type_for_a_hierarchy()
    {
        IReadOnlyList<PostgresIndex> indexes = await IndexesAsync("mt_doc_vehicle");

        indexes.Should().Contain(
            x => IndexAdvisor.LeadingColumn(x.Definition.ToLowerInvariant()) == "mt_doc_type",
            "Marten.Storage.DocumentTable adds a DocumentIndex on SchemaConstants.DocumentTypeColumn for " +
            "every hierarchy, and the advisor's advice depends on it");
    }

    /// <summary>And the advisor, reading it, says the subclass filter is served.</summary>
    [PostgresFact]
    public async Task The_subclass_verdict_over_the_real_schema_is_green()
    {
        IReadOnlyList<PostgresIndex> indexes = await IndexesAsync("mt_doc_vehicle");
        DocumentTableInfo table = TableFor("vehicle");

        IndexVerdict verdict = IndexAdvisor.Evaluate(
            table, indexes, new DocumentPredicate.SubclassIs("car"));

        verdict.Level.Should().Be(IndexVerdictLevel.Green);
        verdict.Reason.Should().Contain("mt_doc_type");
        verdict.Reason.Should().NotContain("creates no index");
    }

    /// <summary>
    /// A hierarchy is browsable without the read being withheld, which is what green buys.
    /// </summary>
    /// <remarks>
    /// The subclass predicate is inserted by the data service for every subclass alias, so an amber or red
    /// verdict on it would colour the strip of every page of every hierarchy — and a red one would have
    /// withheld the read entirely.
    /// </remarks>
    [PostgresFact]
    public async Task Opening_a_subclass_collection_is_not_withheld()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "car", new DocumentListRequest { PageSize = 10 }, Token);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Verdict.FilterLevel.Should().Be(IndexVerdictLevel.Green);
    }

    private async Task<IReadOnlyList<PostgresIndex>> IndexesAsync(string table)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(Token);

        return await new IndexCatalog().GetAsync(connection, Schema, table, Token);
    }

    private DocumentTableInfo TableFor(string alias)
    {
        foreach (Marten.Schema.IDocumentType documentType in Store.Options.AllKnownDocumentTypes())
        {
            if (string.Equals(documentType.Alias, alias, StringComparison.OrdinalIgnoreCase))
            {
                return DocumentTableInfo.FromDocumentType(documentType);
            }
        }

        throw new InvalidOperationException($"The demo store has no '{alias}' document type.");
    }
}
