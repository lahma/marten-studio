using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using MartenStudio.Tests.Sql;

using Npgsql;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// A count that ran out of time is a different answer from a count that could not be read.
/// </summary>
/// <remarks>
/// <para>
/// The two produce different screens. <c>Unknown</c> draws "?" beside the collection with the "=" button
/// still offered, so pressing it again, narrowing the scope or running <c>ANALYZE</c> are all things a
/// person can do next. <c>Unavailable</c> draws "could not read this table" and takes the badge and the
/// button with it, which from a browser is indistinguishable from the studio having broken — and there is
/// no way back short of reloading the page.
/// </para>
/// <para>
/// So a slow <c>count(*)</c> must never be reported as an unreadable table, and that means recognising a
/// timeout in both of the shapes it arrives in: Postgres' own <c>57014</c>, which a host's
/// <c>statement_timeout</c> produces directly, and the <see cref="NpgsqlException" /> wrapping a
/// <see cref="TimeoutException" /> that Npgsql raises when its client-side
/// <c>CommandTimeout</c> expires before the backend answers the cancellation request. Which one arrives is
/// a race, so catching one of them is catching it on the days it does not matter.
/// </para>
/// </remarks>
public class DocumentCountFailureTests
{
    /// <summary>Postgres' <c>query_canceled</c> is a timeout, whatever raised it.</summary>
    [Fact]
    public void A_57014_is_a_timeout() =>
        DocumentDataService.IsTimeout(Postgres("57014")).Should().BeTrue();

    /// <summary>Npgsql's client-side timeout is the same answer wearing a different exception.</summary>
    [Fact]
    public void And_so_is_Npgsqls_own_command_timeout() =>
        DocumentDataService.IsTimeout(
                new NpgsqlException("Exception while reading from stream", new TimeoutException()))
            .Should().BeTrue();

    /// <summary>A bare <see cref="TimeoutException" />, for the paths that do not wrap it.</summary>
    [Fact]
    public void And_a_bare_TimeoutException() =>
        DocumentDataService.IsTimeout(new TimeoutException()).Should().BeTrue();

    /// <summary>
    /// Everything else is a fault, and has to stay one.
    /// </summary>
    /// <remarks>
    /// The anti-vacuity half. A classifier that said "timeout" to everything would make this packet's fix
    /// look right while turning a missing table, a revoked grant and a broken connection into "nobody has
    /// measured this collection" — a studio that never admits it cannot read something.
    /// </remarks>
    [Theory]
    [InlineData("42P01")] // undefined_table
    [InlineData("42501")] // insufficient_privilege
    [InlineData("55P03")] // lock_not_available
    [InlineData("53300")] // too_many_connections
    public void But_an_ordinary_Postgres_failure_is_not(string sqlState) =>
        DocumentDataService.IsTimeout(Postgres(sqlState)).Should().BeFalse();

    /// <summary>And neither is a plain programming error.</summary>
    [Fact]
    public void And_neither_is_anything_else()
    {
        DocumentDataService.IsTimeout(new InvalidOperationException()).Should().BeFalse();
        DocumentDataService.IsTimeout(new NpgsqlException("the socket went away")).Should().BeFalse();
    }

    private static PostgresException Postgres(string sqlState) =>
        new("something went wrong", "ERROR", "ERROR", sqlState);
}

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
    /// And descending, which is a constant and is asserted as one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A btree walks either way, so the direction costs nothing — it is chosen to keep the behaviour
    /// people had from <c>mt_last_modified desc</c>. Marten's default identity for a <c>Guid</c> is
    /// <c>CombGuidIdGeneration</c>, whose values increase with time, and HiLo, <c>Identity</c> and
    /// <c>Sequence</c> are monotonic by construction.
    /// </para>
    /// <para>
    /// The name used to say "which on every Marten identity is newest first", which is a claim about
    /// identity generators that this assertion does not touch — and one that is <em>false</em> the moment
    /// the application assigns the id itself, because a <c>Guid.NewGuid()</c> is version-4 random. What
    /// the studio does about that is
    /// <see cref="Sorting_by_id_says_that_descending_is_only_newest_first_where_Marten_assigned_it" />,
    /// which is the sentence a person actually reads.
    /// </para>
    /// </remarks>
    [Fact]
    public void And_the_default_direction_is_descending() =>
        DocumentDataService.DefaultDirection.Should().Be(SortDirection.Descending);

    /// <summary>
    /// The chip beside the default sort says what "descending" does and does not mean.
    /// </summary>
    /// <remarks>
    /// The default order is <c>id desc</c> and a list read top-down will be taken for newest-first. That
    /// holds for every identity Marten assigns and for nothing else: an application writing
    /// <c>Guid.NewGuid()</c> gets v4 randomness that orders arbitrarily while looking chronological. The
    /// verdict is the one place the screen can say so, so it says so.
    /// </remarks>
    [Fact]
    public void Sorting_by_id_says_that_descending_is_only_newest_first_where_Marten_assigned_it()
    {
        IndexVerdict verdict = IndexAdvisor.EvaluateSort(SqlTestTables.FullyFeatured(), [], DocumentColumn.ById);

        verdict.Level.Should().Be(IndexVerdictLevel.Green, "the primary key is indexed whoever wrote the id");
        verdict.Reason.Should().Contain("primary key");
        verdict.Reason.Should().Contain("Marten assigned the id");
        verdict.Reason.Should().Contain("Guid.NewGuid()", "the caveat has to name the case it is about");
    }

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
