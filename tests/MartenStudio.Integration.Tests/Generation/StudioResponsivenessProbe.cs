using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;
using MartenStudio.Services.Query;
using MartenStudio.Services.Schema;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// One call per screen, measured, through the studio's own services.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the medium and the large suites so that the two measure exactly the same things and a
/// number from one can be compared with a number from the other. Every service is resolved once, from a
/// scope, the way a Blazor circuit resolves one - so the scope resolution and the authorization that
/// runs first are inside the measurement, which is where they are in production too.
/// </para>
/// <para>
/// It is deliberately <em>not</em> a set of assertions. The suites assert; this only measures, so that a
/// suite can report a number it is not asserting on.
/// </para>
/// </remarks>
internal sealed class StudioResponsivenessProbe : IDisposable
{
    /// <summary>The uuid that splits a uniformly distributed id space roughly in half.</summary>
    /// <remarks>
    /// The generator derives every id from a hash, so ids are uniform over the uuid range and a cursor
    /// at the midpoint is a genuinely deep page - about three hundred thousand rows into the large
    /// customer collection - without paging there one round trip at a time. That is the whole claim
    /// keyset paging makes: the cost of a page does not depend on how deep it is.
    /// </remarks>
    public const string MidpointId = "80000000-0000-0000-0000-000000000000";

    private readonly MartenFixture marten;
    private readonly MartenFixture.ScopedService<IDocumentDataService> documents;
    private readonly MartenFixture.ScopedService<IEventDataService> events;
    private readonly MartenFixture.ScopedService<IProjectionDataService> projections;
    private readonly MartenFixture.ScopedService<ISchemaDataService> schema;

    public StudioResponsivenessProbe(MartenFixture marten)
    {
        this.marten = marten;
        documents = marten.Resolve<IDocumentDataService>();
        events = marten.Resolve<IEventDataService>();
        projections = marten.Resolve<IProjectionDataService>();
        schema = marten.Resolve<ISchemaDataService>();
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private StudioScope Scope => marten.Scope;

    /// <summary>The collections rail, counts and all.</summary>
    public Task<(TimeSpan Median, CollectionRail Result)> RailAsync() =>
        ResponsivenessBudgets.MeasureAsync(() => documents.Service.GetCollectionsAsync(Scope, Token));

    /// <summary>The first page of a collection, in the shape the browser asks for it.</summary>
    public Task<(TimeSpan Median, DocumentPage Result)> FirstPageAsync(string alias, int pageSize = 50) =>
        ResponsivenessBudgets.MeasureAsync(() => documents.Service.ListAsync(
            Scope,
            alias,
            new DocumentListRequest { PageSize = pageSize },
            Token));

    /// <summary>A page deep inside a collection, reached by cursor rather than by offset.</summary>
    public Task<(TimeSpan Median, DocumentPage Result)> DeepKeysetPageAsync(string alias, int pageSize = 50) =>
        ResponsivenessBudgets.MeasureAsync(() => documents.Service.ListAsync(
            Scope,
            alias,
            new DocumentListRequest
            {
                PageSize = pageSize,
                SortKey = DocumentColumnKeys.Id,
                Direction = SortDirection.Ascending,
                Cursor = new DocumentKeysetCursor(null, MidpointId),
            },
            Token));

    /// <summary>An offset page, which is the thing that does not scale and is capped for that reason.</summary>
    public Task<(TimeSpan Median, DocumentPage Result)> OffsetPageAsync(string alias, int offset) =>
        ResponsivenessBudgets.MeasureAsync(() => documents.Service.ListAsync(
            Scope,
            alias,
            new DocumentListRequest { PageSize = 50, UseOffsetPaging = true, Offset = offset },
            Token));

    /// <summary>A free-text search nothing can serve, which the studio describes instead of running.</summary>
    public Task<(TimeSpan Median, DocumentPage Result)> WithheldSearchAsync(string alias) =>
        ResponsivenessBudgets.MeasureAsync(() => documents.Service.ListAsync(
            Scope,
            alias,
            new DocumentListRequest { PageSize = 50, Search = "Name ~ Customer 0" },
            Token));

    /// <summary>The <c>_recent</c> pseudo-collection.</summary>
    public Task<(TimeSpan Median, RecentDocuments Result)> RecentAsync(int limit = 15) =>
        ResponsivenessBudgets.MeasureAsync(() => documents.Service.ListRecentAsync(Scope, limit, Token));

    /// <summary>The newest page of the global event feed.</summary>
    public Task<(TimeSpan Median, EventPage Result)> FeedFirstPageAsync(int pageSize = 50) =>
        ResponsivenessBudgets.MeasureAsync(() => events.Service.GetFeedAsync(
            Scope,
            new EventFeedRequest { PageSize = pageSize },
            Token));

    /// <summary>A page part-way down the feed, by sequence cursor.</summary>
    public Task<(TimeSpan Median, EventPage Result)> FeedKeysetPageAsync(long afterSequence, int pageSize = 50) =>
        ResponsivenessBudgets.MeasureAsync(() => events.Service.GetFeedAsync(
            Scope,
            new EventFeedRequest { PageSize = pageSize, AfterSequence = afterSequence },
            Token));

    /// <summary>The first page of the stream list.</summary>
    public Task<(TimeSpan Median, StreamPage Result)> StreamsAsync(int pageSize = 50) =>
        ResponsivenessBudgets.MeasureAsync(() => events.Service.ListStreamsAsync(
            Scope,
            new StreamListRequest { PageSize = pageSize },
            Token));

    /// <summary>Everything the projections screen reads.</summary>
    public Task<(TimeSpan Median, ProjectionsView Result)> ProjectionsAsync() =>
        ResponsivenessBudgets.MeasureAsync(() => projections.Service.GetProjectionsAsync(Scope, Token));

    /// <summary>The schema screen's Tables tab.</summary>
    public Task<(TimeSpan Median, SchemaTables Result)> SchemaTablesAsync() =>
        ResponsivenessBudgets.MeasureAsync(() => schema.Service.TablesAsync(Scope, Token));

    /// <summary>The highest event sequence, which the feed's follow mode polls.</summary>
    public Task<long?> HighestSequenceAsync() => events.Service.GetHighestSequenceAsync(Scope, Token);

    /// <summary>An exact <c>count(*)</c>, which is the on-demand escape hatch from D8's estimate.</summary>
    public Task<DocumentCount> CountExactAsync(string alias) =>
        documents.Service.CountExactAsync(Scope, alias, Token);

    /// <inheritdoc />
    public void Dispose()
    {
        schema.Dispose();
        projections.Dispose();
        events.Dispose();
        documents.Dispose();
    }
}
