using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Support;

/// <summary>
/// What the documents pages are given, said outright by a test.
/// </summary>
/// <remarks>
/// Hand-written rather than a mocking framework (AGENTS.md package budget): the seam is our own interface,
/// and a fake that records the <see cref="DocumentListRequest" /> it was handed is both smaller and more
/// readable than a call-matching DSL. The recorded request is what lets a test assert that pressing "Run
/// anyway" actually changes what is asked for, rather than only what is drawn.
/// </remarks>
internal sealed class FakeDocumentDataService : IDocumentDataService
{
    /// <summary>The rail the pages are given.</summary>
    public CollectionRail Rail { get; set; } = new([]);

    /// <summary>The page the list is given.</summary>
    public DocumentPage Page { get; set; } = new();

    /// <summary>The recent documents, and the reason there are none when there are none.</summary>
    public RecentDocuments RecentResult { get; set; } = RecentDocuments.None;

    /// <summary>The recent documents.</summary>
    public IReadOnlyList<RecentDocument> Recent
    {
        get => RecentResult.Rows;
        set => RecentResult = RecentDocuments.From(value);
    }

    /// <summary>What one document read answers.</summary>
    public DocumentDetailResult Detail { get; set; } = DocumentDetailResult.Missing("nothing set up");

    /// <summary>The related documents.</summary>
    public RelatedDocuments Related { get; set; } = RelatedDocuments.None;

    /// <summary>Whether a stream with the document's id exists.</summary>
    public bool StreamExists { get; set; }

    /// <summary>What probing the other collections answers.</summary>
    public IReadOnlyList<DocumentIdProbe> Probes { get; set; } = [];

    /// <summary>The exact count, when one is asked for.</summary>
    public DocumentCount ExactCount { get; set; } = DocumentCount.Exact(7);

    /// <summary>Every list request the page made, in order.</summary>
    public List<DocumentListRequest> Requests { get; } = [];

    /// <summary>How many times the rail was asked for.</summary>
    public int RailLoads { get; private set; }

    /// <summary>
    /// How many times the detail page asked for related documents by id, which is the overload that has
    /// to read the row again to find out what it points at.
    /// </summary>
    /// <remarks>
    /// The detail page has the document in hand by the time it asks, so this should stay at zero: a page
    /// that reads a document and then reads it again for its foreign keys costs two round trips for one
    /// screen, and the second one is the same row.
    /// </remarks>
    public int RelatedByIdCalls { get; private set; }

    /// <summary>How many times it asked with the document it had already read.</summary>
    public int RelatedByDetailCalls { get; private set; }

    /// <summary>What every method throws, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    public Task<CollectionRail> GetCollectionsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        RailLoads++;
        return Failure is null ? Task.FromResult(Rail) : Task.FromException<CollectionRail>(Failure);
    }

    public Task<DocumentCount> CountExactAsync(StudioScope scope, string alias, CancellationToken cancellationToken = default) =>
        Task.FromResult(ExactCount);

    public Task<DocumentPage> ListAsync(
        StudioScope scope,
        string alias,
        DocumentListRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Failure is null ? Task.FromResult(Page) : Task.FromException<DocumentPage>(Failure);
    }

    public Task<DocumentDetailResult> GetDocumentAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default) =>
        Failure is null ? Task.FromResult(Detail) : Task.FromException<DocumentDetailResult>(Failure);

    public Task<RelatedDocuments> GetRelatedAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        RelatedByIdCalls++;
        return Task.FromResult(Related);
    }

    public Task<RelatedDocuments> GetRelatedAsync(
        StudioScope scope,
        string alias,
        DocumentDetail detail,
        CancellationToken cancellationToken = default)
    {
        RelatedByDetailCalls++;
        return Task.FromResult(Related);
    }

    public Task<bool> StreamExistsAsync(StudioScope scope, string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(StreamExists);

    public Task<IReadOnlyList<DocumentIdProbe>> ProbeIdAsync(
        StudioScope scope,
        string id,
        CancellationToken cancellationToken = default) => Task.FromResult(Probes);

    public Task<RecentDocuments> ListRecentAsync(
        StudioScope scope,
        int limit,
        CancellationToken cancellationToken = default) => Task.FromResult(RecentResult);
}

/// <summary>Builders for the shapes the documents pages are handed, so a test reads as what it is about.</summary>
internal static class FakeDocuments
{
    /// <summary>A collection entry with the flags a test cares about.</summary>
    public static CollectionInfo Collection(
        string alias,
        long count = 12,
        bool estimate = true,
        bool registered = true,
        bool softDeleted = false,
        bool conjoined = false,
        bool hierarchy = false,
        params CollectionInfo[] subclasses) => new()
    {
        Alias = alias,
        ClrTypeName = char.ToUpperInvariant(alias[0]) + alias[1..],
        FullTypeName = "Sample." + alias,
        Schema = "studio_sample",
        Table = "mt_doc_" + alias,
        Hue = CollectionColorizer.HueFor(alias),
        Count = estimate ? DocumentCount.Estimate(count) : DocumentCount.Exact(count),
        IsRegistered = registered,
        Flags = new CollectionFlags(softDeleted, conjoined, OptimisticConcurrency: false, hierarchy),
        IdColumnType = DocumentIdColumnType.Uuid,
        SubCollections = subclasses,
    };

    /// <summary>A rail with a Recent band, a Documents band and, optionally, a Discovered band.</summary>
    public static CollectionRail Rail(
        IReadOnlyList<CollectionInfo>? documents = null,
        IReadOnlyList<CollectionInfo>? discovered = null)
    {
        // The Recent pseudo-collection has no count of its own - it is a query across every table - which
        // is what the real service reports too.
        CollectionInfo recent = Collection(CollectionAliases.Recent, registered: false) with
        {
            Count = DocumentCount.Unavailable,
        };

        List<CollectionGroup> groups =
        [
            new(CollectionGroupKind.Recent, "Recent", [recent]),
            new(CollectionGroupKind.Documents, "Documents", documents ?? [Collection("customer")]),
        ];

        if (discovered is { Count: > 0 })
        {
            groups.Add(new CollectionGroup(CollectionGroupKind.Discovered, "Discovered (unregistered)", discovered));
        }

        return new CollectionRail(groups);
    }

    /// <summary>A loaded page with an id column and whatever else is asked for.</summary>
    public static DocumentPage Page(
        IReadOnlyList<DocumentRow>? rows = null,
        IReadOnlyList<DocumentColumnHeader>? columns = null,
        SearchVerdict? verdict = null,
        DocumentListState state = DocumentListState.Loaded,
        bool hasMore = false)
    {
        IReadOnlyList<DocumentColumnHeader> headers = columns ??
        [
            DocumentColumnKeys.HeaderFor(DocumentColumn.ById),
            DocumentColumnKeys.HeaderFor(new DocumentColumn.Metadata(DocumentMetadataColumn.LastModified)),
        ];

        return new DocumentPage
        {
            Columns = headers,
            AvailableColumns = headers,
            Rows = rows ?? [],
            Estimate = DocumentCount.Estimate(12),
            Sql = "select d.\"id\" from \"studio_sample\".\"mt_doc_customer\" as d",
            ParameterNames = ["@limit"],
            Verdict = verdict ?? SearchVerdict.Empty,
            State = state,
            HasMore = hasMore,
            NextCursor = hasMore ? new DocumentKeysetCursor(null, rows?[^1].Id ?? "1") : null,
            SortKey = DocumentColumnKeys.Id,
        };
    }

    /// <summary>One row.</summary>
    public static DocumentRow Row(
        string id,
        string? json = """{"Name":"Customer 01"}""",
        bool deleted = false,
        string? tenant = null,
        long size = 42) => new()
    {
        Id = id,
        Json = json,
        SizeBytes = size,
        Cells = [id, "2026-09-14 12:00:00.000+00:00"],
        IsDeleted = deleted,
        TenantId = tenant,
        LastModified = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero),
    };
}
