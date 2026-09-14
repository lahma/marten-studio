using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// Keyset paging across a collection big enough for the difference to matter.
/// </summary>
/// <remarks>
/// <para>
/// Ten thousand rows, seeded in this class's schema only: the sample host's own profile must stay small
/// enough that <c>dotnet run</c> is a demo rather than a wait. What this proves is the thing a small
/// collection cannot - that the cursor walks the whole collection exactly once, with no row seen twice and
/// none skipped, which is the failure mode of every keyset implementation that forgets the id tiebreaker.
/// </para>
/// <para>
/// The sample seeder is off here: these tests want one large collection, not the demo.
/// </para>
/// </remarks>
public class DocumentPagingLiveTests(DocumentPagingLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentPagingLiveTests.Fixture>
{
    private const int TotalCustomers = 10_000;
    private const int PageSize = 500;

    /// <summary>
    /// Ten thousand customers, seeded once for the whole class.
    /// </summary>
    /// <remarks>
    /// This is the clearest reason the fixture had to stop being <c>IAsyncLifetime</c> on the test class:
    /// xunit builds one test class instance per test method, so the seed below used to run five times a
    /// run - fifty thousand documents to prove five things about ten thousand.
    /// </remarks>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using IDocumentSession session = Marten.Store.LightweightSession();

            // Deliberately written in one batch per thousand: Marten's UpdateBatchSize decides the rest,
            // and a single SaveChangesAsync for ten thousand documents is one very large command.
            for (var batch = 0; batch < TotalCustomers / 1_000; batch++)
            {
                for (var i = 0; i < 1_000; i++)
                {
                    var index = (batch * 1_000) + i;

                    session.Store(new Customer
                    {
                        Id = Guid.NewGuid(),
                        Name = $"Customer {index:00000}",
                        Email = $"customer{index:00000}@example.com",
                        Address = new Address($"{index} Example Street", "Helsinki", "00100", "FI"),
                        RegisteredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index),
                    });
                }

                await session.SaveChangesAsync();
            }

            // Analysed on purpose, and this is the point of the estimate tests below rather than an aside.
            // Postgres reports reltuples = -1 until the first ANALYZE, which is "never measured" and not
            // "empty" - so without this the collection has no estimate at all, and the D8 assertion that
            // a collection this size is *estimated* rather than scanned would be testing the opposite
            // path. A ten-million-row production table has been autovacuumed; this makes the fixture look
            // like one.
            await using NpgsqlConnection connection = await Postgres.OpenAsync();
            await using var analyze = new NpgsqlCommand($"analyze \"{Schema}\".\"mt_doc_customer\"", connection);

            await analyze.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task The_keyset_cursor_walks_the_whole_collection_exactly_once()
    {
        using var documents = Documents();

        HashSet<string> seen = new(StringComparer.Ordinal);
        DocumentKeysetCursor? cursor = null;
        var pages = 0;

        while (pages++ < 100)
        {
            DocumentPage page = await documents.Service.ListAsync(
                Scope,
                "customer",
                new DocumentListRequest { PageSize = PageSize, Cursor = cursor },
                TestContext.Current.CancellationToken);

            page.State.Should().Be(DocumentListState.Loaded, page.Error);

            foreach (DocumentRow row in page.Rows)
            {
                seen.Add(row.Id).Should().BeTrue("row '{0}' was returned twice", row.Id);
            }

            if (!page.HasMore)
            {
                break;
            }

            cursor = page.NextCursor.Should().NotBeNull().And.Subject as DocumentKeysetCursor;
        }

        seen.Should().HaveCount(TotalCustomers);
    }

    [PostgresFact]
    public async Task A_cursor_survives_the_round_trip_through_the_url()
    {
        using var documents = Documents();

        DocumentPage first = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest { PageSize = 10 }, TestContext.Current.CancellationToken);

        var encoded = DocumentLinks.Encode(first.NextCursor!);

        DocumentPage second = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest { PageSize = 10, Cursor = DocumentLinks.Decode(encoded) },
            TestContext.Current.CancellationToken);

        second.Rows.Select(x => x.Id).Should().NotIntersectWith(first.Rows.Select(x => x.Id));
    }

    [PostgresFact]
    public async Task Offset_paging_works_up_to_its_cap_and_refuses_past_it()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest { UseOffsetPaging = true, Offset = 5_000, PageSize = 50 },
            TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Rows.Should().HaveCount(50);

        DocumentPage past = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest { UseOffsetPaging = true, Offset = DocumentQueryBuilder.MaxOffset + 1, PageSize = 50 },
            TestContext.Current.CancellationToken);

        past.State.Should().Be(DocumentListState.Failed);
        past.Error.Should().Contain("keyset");
    }

    [PostgresFact]
    public async Task A_collection_this_size_is_counted_as_an_estimate_rather_than_scanned()
    {
        // D8: the first question is always pg_class.reltuples, which is free.
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest { PageSize = 10 }, TestContext.Current.CancellationToken);

        page.Estimate.IsUnavailable.Should().BeFalse();
        page.Estimate.IsEstimate.Should().BeTrue();
        page.Estimate.IsUnknown.Should().BeFalse("the fixture analysed the table, so there is an estimate");

        // The value, not only the flag. A `reltuples` of -1 used to become Estimate(0), so a header reading
        // "~0 documents" over ten thousand rows passed an IsEstimate assertion perfectly happily.
        page.Estimate.Value.Should().Be(TotalCustomers,
            "ANALYZE over a table of this size reads every page, so the estimate is exact here");

        DocumentCount exact = await documents.Service.CountExactAsync(
            Scope, "customer", TestContext.Current.CancellationToken);

        exact.IsEstimate.Should().BeFalse();
        exact.Value.Should().Be(TotalCustomers);
    }

    /// <summary>
    /// The primary key is the default order, and it pages without dropping or repeating a row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>P2-perf deliverable 1.</b> The default was <c>mt_last_modified desc</c>, which Marten declares no
    /// index on — so opening any collection was a sequential scan plus a top-N sort, at 88 ms per hundred
    /// thousand documents and 784 ms at 1.2 M against a 250 ms budget. <c>id</c> is the one column every
    /// Marten table is guaranteed an index on, and descending on it is "newest first" for every identity
    /// Marten ships (comb <c>Guid</c>, HiLo, <c>Identity</c>, <c>Sequence</c>).
    /// </para>
    /// <para>
    /// Paging the whole collection is the assertion that matters, not the sort key: <c>id</c> is a total
    /// order, so the keyset cursor needs no tiebreaker and no null branch — and a walk that repeated or
    /// skipped a row would say so here, over ten thousand of them.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task The_primary_key_is_the_default_order_and_pages_without_dropping_a_row()
    {
        using var documents = Documents();

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> order = [];
        DocumentKeysetCursor? cursor = null;

        for (var page = 0; page < 25; page++)
        {
            DocumentPage read = await documents.Service.ListAsync(
                Scope,
                "customer",
                new DocumentListRequest { PageSize = 200, Cursor = cursor },
                TestContext.Current.CancellationToken);

            read.SortKey.Should().Be(DocumentColumnKeys.Id);
            read.Direction.Should().Be(SortDirection.Descending);
            read.Sql.Should().Contain("order by d.\"id\" desc", "and the SQL disclosure shows it");

            foreach (DocumentRow row in read.Rows)
            {
                seen.Add(row.Id).Should().BeTrue("row '{0}' was returned twice", row.Id);
                order.Add(row.Id);
            }

            if (!read.HasMore)
            {
                break;
            }

            cursor = read.NextCursor;
        }

        seen.Should().HaveCount(5_000, "twenty-five pages of two hundred");
        order.Should().BeInDescendingOrder(StringComparer.Ordinal);
    }

    /// <summary>
    /// The default order is the same order by cursor and by offset, so a bookmark means one thing.
    /// </summary>
    /// <remarks>
    /// The page offers both, and they have to agree: "page 3" reached by offset and the third page reached
    /// by cursor are the same rows in the same order, or the two controls are describing different lists.
    /// A sort with no total order — which is what <c>mt_last_modified</c> alone was — cannot promise this,
    /// and it is why the tiebreaker was mandatory before and why nothing needs one now.
    /// </remarks>
    [PostgresFact]
    public async Task The_default_order_is_stable_across_keyset_and_offset_paging()
    {
        using var documents = Documents();

        List<string> byCursor = [];
        DocumentKeysetCursor? cursor = null;

        for (var page = 0; page < 3; page++)
        {
            DocumentPage read = await documents.Service.ListAsync(
                Scope,
                "customer",
                new DocumentListRequest { PageSize = 50, Cursor = cursor },
                TestContext.Current.CancellationToken);

            read.State.Should().Be(DocumentListState.Loaded, read.Error);
            byCursor.AddRange(read.Rows.Select(x => x.Id));
            cursor = read.NextCursor;
        }

        List<string> byOffset = [];

        for (var page = 0; page < 3; page++)
        {
            DocumentPage read = await documents.Service.ListAsync(
                Scope,
                "customer",
                new DocumentListRequest { PageSize = 50, UseOffsetPaging = true, Offset = page * 50 },
                TestContext.Current.CancellationToken);

            read.State.Should().Be(DocumentListState.Loaded, read.Error);
            byOffset.AddRange(read.Rows.Select(x => x.Id));
        }

        byOffset.Should().Equal(byCursor);
    }

    /// <summary>
    /// <c>mt_last_modified</c> is still a sort anyone can pick, and it still pages correctly.
    /// </summary>
    /// <remarks>
    /// Every one of these ten thousand rows was written within the same few seconds, so that column is full
    /// of ties — which is exactly the case a keyset page without an id tiebreaker silently loses rows in.
    /// Changing the default did not make that sort go away; it made it something a person chooses, with the
    /// index verdict beside it saying what it costs.
    /// </remarks>
    [PostgresFact]
    public async Task Sorting_by_last_modified_is_still_offered_and_still_pages_without_dropping_a_row()
    {
        using var documents = Documents();

        HashSet<string> seen = new(StringComparer.Ordinal);
        DocumentKeysetCursor? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            DocumentPage read = await documents.Service.ListAsync(
                Scope,
                "customer",
                new DocumentListRequest
                {
                    PageSize = 200,
                    SortKey = "meta:LastModified",
                    Direction = SortDirection.Descending,
                    Cursor = cursor,
                },
                TestContext.Current.CancellationToken);

            read.State.Should().Be(DocumentListState.Loaded, read.Error);
            read.SortKey.Should().Be("meta:LastModified");
            read.Sql.Should().Contain("order by d.\"mt_last_modified\" desc nulls last, d.\"id\"");

            foreach (DocumentRow row in read.Rows)
            {
                seen.Add(row.Id).Should().BeTrue("row '{0}' was returned twice", row.Id);
            }

            if (!read.HasMore)
            {
                break;
            }

            cursor = read.NextCursor;
        }

        seen.Should().HaveCount(2_000, "ten pages of two hundred");
    }
}
