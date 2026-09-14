using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// Which way a collection opens, and why it is the same answer for every collection.
/// </summary>
/// <remarks>
/// <para>
/// <b>P2-perf deliverable 1.</b> The default used to be <c>mt_last_modified desc</c> wherever the column
/// existed, which is nearly everywhere — and Marten declares no index on it, so opening any collection was
/// a sequential scan plus a top-N sort whose cost was proportional to the collection. Measured through the
/// studio's own services against the generated data set: 88 ms at 100 000 documents, 784 ms at 1.2 M,
/// against a 250 ms budget, with an offset page at the 10 000 cap costing a full second.
/// </para>
/// <para>
/// These are cheap assertions about a decision rather than about a mechanism, and that is what they are
/// for: the shape is easy to reintroduce by reflex — "a document list should be newest first" — and this
/// is the line that says the cost of doing it that way was measured and is not paid by default.
/// </para>
/// </remarks>
public class DocumentDefaultSortTests
{
    /// <summary>The primary key, which is the one column every Marten table is guaranteed an index on.</summary>
    [Fact]
    public void A_collection_opens_ordered_by_its_primary_key() =>
        DocumentDataService.DefaultSort.Should().Be(DocumentColumn.ById);

    /// <summary>
    /// Descending, because on every identity Marten ships that is "newest first".
    /// </summary>
    /// <remarks>
    /// A btree walks either way, so the direction costs nothing — it is chosen to keep the behaviour
    /// people had from <c>mt_last_modified desc</c>. Marten's default identity for a <c>Guid</c> is
    /// <c>CombGuidIdGeneration</c>, whose values increase with time, and HiLo, <c>Identity</c> and
    /// <c>Sequence</c> are monotonic by construction.
    /// </remarks>
    [Fact]
    public void And_descending_which_on_every_Marten_identity_is_newest_first() =>
        DocumentDataService.DefaultDirection.Should().Be(SortDirection.Descending);

    /// <summary>
    /// The same answer whatever the collection keeps, including one that has <c>mt_last_modified</c>.
    /// </summary>
    /// <remarks>
    /// The old rule branched on the column's existence, which meant the fast path was the one a store with
    /// <c>DisableInformationalFields()</c> happened to fall into. One rule for every collection is also one
    /// URL shape, one SQL shape and one thing to explain.
    /// </remarks>
    [Fact]
    public void And_it_does_not_depend_on_which_metadata_columns_the_store_kept()
    {
        SqlTestTables.FullyFeatured().HasMetadata(DocumentMetadataColumn.LastModified).Should().BeTrue();
        SqlTestTables.MetadataLess().HasMetadata(DocumentMetadataColumn.LastModified).Should().BeFalse();

        // Neither of them changes the answer - it is a property, not a function of the table.
        DocumentDataService.DefaultSort.Should().Be(DocumentColumn.ById);
        DocumentDataService.DefaultDirection.Should().Be(SortDirection.Descending);
    }

    /// <summary>
    /// <c>mt_last_modified</c> is still a sort anybody can choose, and it is still the honest red.
    /// </summary>
    /// <remarks>
    /// Changing the default is not a claim that sorting by modification time is wrong — it is a claim that
    /// the studio should not put every visitor on that read without being asked. One click on the header
    /// still gets it, and the verdict beside it still says what it costs and what would fix it.
    /// </remarks>
    [Fact]
    public void Sorting_by_last_modified_is_still_offered_and_still_says_what_it_costs()
    {
        DocumentTableInfo table = SqlTestTables.FullyFeatured();

        DocumentColumn? resolved = DocumentColumnKeys.Resolve(table, "meta:LastModified");

        resolved.Should().Be(new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified));

        IndexVerdict verdict = IndexAdvisor.EvaluateSort(table, [], resolved!);

        verdict.Level.Should().Be(IndexVerdictLevel.Red);
        verdict.Suggestion.Should().Be("options.Schema.For<SqlTestCustomer>().IndexLastModified();");
    }
}
