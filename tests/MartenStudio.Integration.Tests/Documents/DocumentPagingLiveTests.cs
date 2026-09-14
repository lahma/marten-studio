using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

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
public class DocumentPagingLiveTests(PostgresFixture postgres) : MartenTestBase(postgres)
{
    private const int TotalCustomers = 10_000;
    private const int PageSize = 500;

    /// <inheritdoc />
    protected override bool SeedSampleData => false;

    /// <inheritdoc />
    protected override async Task SeedAsync()
    {
        await using IDocumentSession session = Store.LightweightSession();

        // Deliberately written in one batch per thousand: Marten's UpdateBatchSize decides the rest, and a
        // single SaveChangesAsync for ten thousand documents is one very large command.
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

        DocumentCount exact = await documents.Service.CountExactAsync(
            Scope, "customer", TestContext.Current.CancellationToken);

        exact.IsEstimate.Should().BeFalse();
        exact.Value.Should().Be(TotalCustomers);
    }

    [PostgresFact]
    public async Task Sorting_by_last_modified_is_the_default_and_pages_without_dropping_a_row()
    {
        // Every one of these ten thousand rows was written within the same few seconds, so the sort column
        // is full of ties - which is exactly the case a keyset page without an id tiebreaker silently
        // loses rows in.
        using var documents = Documents();

        HashSet<string> seen = new(StringComparer.Ordinal);
        DocumentKeysetCursor? cursor = null;

        for (var page = 0; page < 25; page++)
        {
            DocumentPage read = await documents.Service.ListAsync(
                Scope,
                "customer",
                new DocumentListRequest { PageSize = 200, Cursor = cursor },
                TestContext.Current.CancellationToken);

            read.SortKey.Should().Be("meta:LastModified");
            read.Direction.Should().Be(SortDirection.Descending);

            foreach (DocumentRow row in read.Rows)
            {
                seen.Add(row.Id).Should().BeTrue();
            }

            if (!read.HasMore)
            {
                break;
            }

            cursor = read.NextCursor;
        }

        seen.Should().HaveCount(5_000, "twenty-five pages of two hundred");
    }
}
