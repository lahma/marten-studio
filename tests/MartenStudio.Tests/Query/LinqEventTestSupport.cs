using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Events;
using Marten.Services;

using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The event pipeline with no database: the closed member list, the event graph and guard, JasperFx's parse and Marten's
/// own <c>QueryRawEvents()</c> translated to SQL - everything LINQ mode over events does short of running the statement.
/// </summary>
internal static class LinqEventTestSupport
{
    /// <summary>The store's event options as the service reads them: <c>IReadOnlyStoreOptions.Events</c>.</summary>
    public static IReadOnlyEventStoreOptions ReadOnlyEvents(this DocumentStore store) => ((IDocumentStore) store).Options.Events;

    /// <summary>A store whose events are configured by <paramref name="configure" />; nothing connects.</summary>
    public static DocumentStore Store(Action<StoreOptions>? configure = null) => LinqTestStore.Create(configure);

    /// <summary>A store with every optional metadata column on and conjoined events.</summary>
    public static DocumentStore ConjoinedStore(Action<StoreOptions>? more = null) => Store(options =>
    {
        options.Events.TenancyStyle = TenancyStyle.Conjoined;
        options.Events.MetadataConfig.CorrelationIdEnabled = true;
        options.Events.MetadataConfig.CausationIdEnabled = true;
        options.Events.MetadataConfig.UserNameEnabled = true;
        more?.Invoke(options);
    });

    /// <summary>
    /// The graph the service builds for this store and tenant (<see cref="LinqEventMembers.Offered" />), or - with
    /// <paramref name="offerTenantId" /> - the one that also offers <c>TenantId</c>, to prove Marten's own tenant term holds.
    /// </summary>
    public static LinqDocumentGraph Graph(DocumentStore store, string? tenant, bool offerTenantId = false)
    {
        IReadOnlyEventStoreOptions events = store.ReadOnlyEvents();
        bool narrowed = events.TenancyStyle == TenancyStyle.Conjoined && tenant is not null && !offerTenantId;

        return LinqDocumentGraph.ForEvents(LinqEventMembers.Offered(events, narrowed));
    }

    /// <summary>The screen over both boxes, as the service runs it.</summary>
    public static LinqRefusal? Screen(DocumentStore store, string? tenant, string where, string orderBy = "") =>
        LinqTextScreen.Check(LinqClause.Where, where, Graph(store, tenant))
        ?? LinqTextScreen.Check(LinqClause.OrderBy, orderBy, Graph(store, tenant));

    /// <summary>
    /// The composition the service makes for an event run: the session pinned as hard rule 15 says, Marten's
    /// <c>QueryRawEvents()</c>, the visitor's text through JasperFx and the guard, and Marten's SQL.
    /// </summary>
    public static ComposedLinqQuery Compose(
        DocumentStore store,
        string? tenant,
        string where,
        string orderBy = "",
        ArchivedFilter archived = ArchivedFilter.Exclude,
        bool offerTenantId = false,
        int offset = 0)
    {
        IReadOnlyEventStoreOptions events = store.ReadOnlyEvents();
        bool conjoined = events.TenancyStyle == TenancyStyle.Conjoined;
        LinqDocumentGraph graph = Graph(store, tenant, offerTenantId);

        return LinqQueryComposer.Compose(new LinqComposition(
            store,
            tenant is null ? SessionOptions.ForDatabase(store.Storage.Database) : SessionOptions.ForDatabase(tenant, store.Storage.Database),
            typeof(IEvent),
            DynamicLinqAdapter.Current!,
            new LinqExpressionGuard(graph, static _ => null, jsonEnumsAsName: false),
            where,
            orderBy,
            DeletedFilter.Exclude,
            CrossTenant: conjoined && tenant is null,
            Offset: offset,
            PageSize: 50)
        {
            Target = LinqQueryTarget.Events,
            Archived = archived,
            PerTenantSequences = LinqEventMembers.IsPerTenantSequences(events),
        });
    }

    /// <summary>The statement text, failing the test with the refusal when there is none.</summary>
    public static string Sql(ComposedLinqQuery composed)
    {
        composed.Refusal.Should().BeNull("the query is meant to reach Marten's SQL; it was refused: {0}", composed.Refusal?.Message);
        composed.Command.Should().NotBeNull();

        return composed.Command!.CommandText;
    }
}
