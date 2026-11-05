using System.Globalization;

using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;
using Marten.Storage;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;

namespace MartenStudio.Services.Tenants;

/// <summary>
/// The Tenants overview (1.2, D-SCOPE Part B): read-only, statistics only, never a <c>count(*)</c> (D8).
/// </summary>
/// <remarks>
/// <para>
/// <b>No enumeration of its own.</b> The scope is resolved with no tenant - the store policy for the database as a whole comes first -
/// and the resolver's one enumeration travels on the result (<see cref="ResolvedScope.ListedDatabases" />). The scope's database lists
/// its tenants through the header's own discovery (<see cref="TenantDiscovery.DiscoverAsync" />, tiers 1 to 3, cached); every other
/// database through tier 2 alone (<see cref="TenantDiscovery.ListedOnlyAsync" />), so no tenant-column scan runs in N databases.
/// Placement asks <see cref="DatabaseReach.IsExclusivelyTenants" /> of that same list.
/// </para>
/// <para>
/// <b>Who sees what.</b> A tenant's id is the picker's own gate: each <c>(store, database, tenant)</c> goes through the store policy as
/// the header's listing does. The columns that put tenants side by side - estimated documents, events and streams, the newest event,
/// and which partition a tenant holds - are the activity gate's, as T18's tenant skew is (<c>ViewDatabaseActivity</c> on, the write
/// policy passing, asked with <c>TenantId = null</c>), and <b>nothing is read for a visitor below it</b>: no volume, last-event or
/// partition statement runs. Projections follow the row's policy alone, as the Projections page shows them to a visitor scoped to that
/// tenant. The first read of the activity-gated columns of a database in a circuit is one Activity entry and event 9265.
/// </para>
/// <para>
/// <b>Bounded.</b> A page is 25 rows; the statistics are read for those rows' tenants only, one database after another on one
/// connection each, every statement under the studio's <c>QueryTimeout</c> and the whole read under a 20-second budget; a database
/// not reached in time shows "not read in time", and so does the footer. The budget is 20 seconds <em>plus one connection
/// <c>Timeout</c></em>: Npgsql's open does not honour the token during the startup handshake, so a host that accepts and never answers
/// holds the connection being opened for its <c>Timeout</c>, and the budget is noticed at the next await. Every statement is built in <c>Internal/Sql</c> (hard rule 4) and reads the catalog
/// and the planner's statistics: no table of the store is read but, for the newest event, <c>mt_events</c>'s primary key.
/// </para>
/// </remarks>
internal sealed class TenantOverviewService : ITenantOverviewService
{
    /// <summary>The whole read's budget, past which the remaining databases are "not read in time".</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private const string TenantColumn = "tenant_id";

    private const string LedgerSchema = "(tenants)";
    private const string LedgerRelation = "volumes";
    private const string LedgerKind = "activity";

    /// <summary>What the audit ring calls the first read of the activity-gated columns.</summary>
    internal const string AuditAction = "Read tenant volumes";

    /// <summary>What the audit ring says was read.</summary>
    internal const string AuditTarget = "per-tenant row estimates and last events";

    private readonly StudioScopeResolver resolver;
    private readonly TenantDiscovery discovery;
    private readonly StudioAuthorization authorization;
    private readonly StudioCapabilityGuard capabilities;
    private readonly StudioActionLog audit;
    private readonly IOptions<MartenStudioOptions> options;
    private readonly ColumnCatalog catalog;
    private readonly ILogger<TenantOverviewService> logger;
    private readonly TableRowAuditLedger ledger = new();
    private readonly TimeSpan readBudget;

    public TenantOverviewService(
        StudioScopeResolver resolver,
        TenantDiscovery discovery,
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        StudioActionLog audit,
        IOptions<MartenStudioOptions> options,
        ColumnCatalog catalog,
        ILogger<TenantOverviewService> logger,
        TimeSpan? budget = null)
    {
        readBudget = budget ?? Budget;
        this.resolver = resolver;
        this.discovery = discovery;
        this.authorization = authorization;
        this.capabilities = capabilities;
        this.audit = audit;
        this.options = options;
        this.catalog = catalog;
        this.logger = logger;
    }

    private int CommandTimeoutSeconds => Math.Max(1, (int) Math.Ceiling(options.Value.QueryTimeout.TotalSeconds));

    /// <inheritdoc />
    public async Task<TenantOverview> GetAsync(StudioScope scope, TenantOverviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        ResolvedScope resolved;

        try
        {
            resolved = await resolver.ResolveAsync(scope with { TenantId = null }, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException exception)
        {
            return TenantOverview.Refused(TenantOverviewRefusal.StorePolicy, exception.Message);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return TenantOverview.Refused(TenantOverviewRefusal.Unavailable, exception.Message);
        }

        IDocumentStore store = resolved.Store;
        string storeKey = resolved.Registration.Key;

        if (!discovery.IsTenantScopeRelevant(store))
        {
            return new TenantOverview { KeepsNoTenants = true };
        }

        IReadOnlyList<IMartenDatabase> listed = resolved.ListedDatabases.Count > 0 ? resolved.ListedDatabases : [resolved.Database];
        List<IMartenDatabase> databases = Distinct(listed);

        // 1. The tenants, per database, from the enumeration the resolver has made.
        (List<Candidate> candidates, bool idsWithoutDatabases, bool listUnavailable, bool listTruncated) =
            await ListCandidatesAsync(storeKey, store, resolved, databases, listed, cancellationToken).ConfigureAwait(false);

        // 2. The picker's gate, for each (store, database, tenant) - the same question the header asks of its listing.
        StudioScope scopeDatabase = new(storeKey, resolved.Database.Id.Identity, null);
        List<Candidate> admitted = await authorization
            .FilterAsync(
                candidates,
                x => new StudioScope(storeKey, (x.Database ?? resolved.Database).Id.Identity, x.TenantId),
                take: null,
                cancellationToken)
            .ConfigureAwait(false);

        admitted.Sort(static (a, b) =>
        {
            int byTenant = string.CompareOrdinal(a.TenantId, b.TenantId);
            return byTenant != 0
                ? byTenant
                : string.Compare(a.Database?.Id.Identity, b.Database?.Id.Identity, StringComparison.OrdinalIgnoreCase);
        });

        bool truncated = listTruncated || admitted.Count > TenantList.MaxListedTenants;
        if (admitted.Count > TenantList.MaxListedTenants)
        {
            admitted.RemoveRange(TenantList.MaxListedTenants, admitted.Count - TenantList.MaxListedTenants);
        }

        // 3. The prefix, then the page.
        string? filter = request.Filter?.Trim() is { Length: > 0 } typed
            ? typed[..Math.Min(typed.Length, TenantOverviewRequest.MaxFilterLength)]
            : null;
        List<Candidate> matching = string.IsNullOrEmpty(filter)
            ? admitted
            : [.. admitted.Where(x => x.TenantId.StartsWith(filter, StringComparison.OrdinalIgnoreCase))];

        int page = PageOf(matching, request.Page, scope.TenantId);
        List<Candidate> shown = [.. matching.Skip(page * TenantOverviewRequest.PageSize).Take(TenantOverviewRequest.PageSize)];
        bool hasNext = matching.Count > (page + 1) * TenantOverviewRequest.PageSize;

        // 4. What the page's databases say, sequentially, under one budget.
        Layout layout = LayoutOf(store);
        List<RowDraft> drafts = [.. shown.Select(x => new RowDraft(x.Database, x.TenantId))];

        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(readBudget);

        bool anyVolumes = false;
        string? withheldReason = null;

        foreach (IGrouping<string, RowDraft> group in drafts.GroupBy(static x => x.Database?.Id.Identity ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            IMartenDatabase? database = group.First().Database;

            if (database is null)
            {
                foreach (RowDraft draft in group)
                {
                    draft.Placement = TenantPlacement.NotPlaced;
                    draft.Volumes = TenantCell.Dash("Known by id only: pick a database in the header to read one.");
                }

                continue;
            }

            StudioScope whole = new(storeKey, database.Id.Identity, null);
            (bool activity, string? reason) = await ActivityAsync(whole, cancellationToken).ConfigureAwait(false);
            anyVolumes |= activity;
            withheldReason ??= reason;

            try
            {
                await ReadDatabaseAsync(store, database, listed, layout, [.. group], activity, whole, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                foreach (RowDraft draft in group)
                {
                    draft.Problem = "not read in time";
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDebug(exception, "Marten Studio could not read one database for the Tenants page");

                foreach (RowDraft draft in group)
                {
                    draft.Problem = "could not be read: " + StudioHostFailure.Words(exception);
                }
            }
        }

        TenantSharedRow? shared = await SharedRowAsync(store, resolved.Database, budget.Token, cancellationToken).ConfigureAwait(false);

        List<string> captions = [];
        if (anyVolumes && layout.Documents.Any(static x => x.SoftDeleted))
        {
            captions.Add("includes soft-deleted documents");
        }

        if (anyVolumes && layout.EventsConjoined)
        {
            captions.Add("includes archived events");
        }

        List<TenantRow> rows = [.. drafts.Select(static x => x.ToRow())];

        return new TenantOverview
        {
            Rows = rows,
            Page = page,
            HasNext = hasNext,
            // Under a store policy the listing is never claimed complete, and "more than 200" is said to nobody: it would tell a visitor a
            // policy filters that discovery hit its cap, which is what the unfiltered list's count would give away (DB-0-fix-2, F7).
            ListTruncated = truncated && string.IsNullOrWhiteSpace(options.Value.StoreAuthorizationPolicy),
            EventsNotPerTenant = rows.Any(static x => x.Events.Title == EventsNotPerTenantReason),
            UnderPolicy = !string.IsNullOrWhiteSpace(options.Value.StoreAuthorizationPolicy),
            ListUnavailable = listUnavailable && candidates.Count == 0,
            IdsWithoutDatabases = idsWithoutDatabases,
            VolumesShown = anyVolumes,
            VolumesWithheldReason = anyVolumes ? null : withheldReason ?? (await ActivityAsync(scopeDatabase, cancellationToken).ConfigureAwait(false)).Reason,
            HasProjectionsColumn = rows.Any(static x => x.Projections is not null),
            ProjectionsAreShared = drafts.Any(static x => x.Database is not null && x.Projections is null && x.Placement != TenantPlacement.OwnDatabase),
            Captions = captions,
            Shared = shared,
        };
    }

    // -------------------------------------------------------------------------------------------------
    // The list
    // -------------------------------------------------------------------------------------------------

    private sealed record Candidate(IMartenDatabase? Database, string TenantId);

    /// <summary>Databases compare by identity ignoring case, as the studio scopes them; tenant ids compare ordinally, as Marten does.</summary>
    private sealed class DatabaseTenantComparer : IEqualityComparer<(string Database, string Tenant)>
    {
        public static DatabaseTenantComparer Instance { get; } = new();

        public bool Equals((string Database, string Tenant) x, (string Database, string Tenant) y) =>
            string.Equals(x.Database, y.Database, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Tenant, y.Tenant, StringComparison.Ordinal);

        public int GetHashCode((string Database, string Tenant) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Database), StringComparer.Ordinal.GetHashCode(value.Tenant));
    }

    private static List<IMartenDatabase> Distinct(IReadOnlyList<IMartenDatabase> listed)
    {
        List<IMartenDatabase> distinct = [];
        HashSet<string> identities = new(StringComparer.OrdinalIgnoreCase);

        foreach (IMartenDatabase database in listed)
        {
            if (identities.Add(database.Id.Identity))
            {
                distinct.Add(database);
            }
        }

        return distinct;
    }

    private async Task<(List<Candidate> Candidates, bool IdsWithoutDatabases, bool ListUnavailable, bool Truncated)> ListCandidatesAsync(
        string storeKey,
        IDocumentStore store,
        ResolvedScope resolved,
        List<IMartenDatabase> databases,
        IReadOnlyList<IMartenDatabase> listed,
        CancellationToken cancellationToken)
    {
        List<Candidate> candidates = [];

        TenantList own = await discovery.DiscoverAsync(storeKey, store, resolved.Database, listed, cancellationToken).ConfigureAwait(false);

        // Tier 1 is one list for every database, so it places nothing on a store with several: the rows are the configured ids.
        if (own.Source == TenantListSource.Configured && databases.Count > 1)
        {
            candidates.AddRange(own.Ids.Select(static x => new Candidate(null, x)));
            return (candidates, true, false, own.IsTruncated);
        }

        bool truncated = own.IsTruncated;
        candidates.AddRange(own.Ids.Select(x => new Candidate(resolved.Database, x)));

        foreach (IMartenDatabase other in databases)
        {
            if (string.Equals(other.Id.Identity, resolved.Database.Id.Identity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The header's database picker hides a database the store policy refuses for the database as a whole, and so does this
            // page: asked once per other database, before a tenant of it is listed (the scope's own was asked by the resolver).
            if (!await authorization.IsAuthorizedAsync(new StudioScope(storeKey, other.Id.Identity, null), capability: null, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            if (await discovery.ListedOnlyAsync(store, other, listed, cancellationToken).ConfigureAwait(false) is { } tenants)
            {
                truncated |= tenants.IsTruncated;
                candidates.AddRange(tenants.Ids.Select(x => new Candidate(other, x)));
            }
        }

        // A tenant listed twice (a twin instance, a duplicated KnownTenantIds entry) is one row: the page keys its rows by
        // (database, tenant), and two siblings under one @key end the circuit.
        HashSet<(string Database, string Tenant)> seen = new(DatabaseTenantComparer.Instance);
        candidates = [.. candidates.Where(x => seen.Add((x.Database?.Id.Identity ?? string.Empty, x.TenantId)))];

        return (candidates, false, own.Source == TenantListSource.Unavailable, truncated);
    }

    /// <summary>The page requested; or, with none requested, the one that holds the scope's tenant, else the first.</summary>
    private static int PageOf(List<Candidate> matching, int? requested, string? scopeTenant)
    {
        int last = Math.Max(0, (matching.Count - 1) / TenantOverviewRequest.PageSize);

        if (requested is { } page)
        {
            return Math.Clamp(page, 0, last);
        }

        if (scopeTenant is not null)
        {
            int index = matching.FindIndex(x => string.Equals(x.TenantId, scopeTenant, StringComparison.Ordinal));
            if (index >= 0)
            {
                return index / TenantOverviewRequest.PageSize;
            }
        }

        return 0;
    }

    // -------------------------------------------------------------------------------------------------
    // The gate
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the activity-gated columns may be read for <paramref name="whole" />: the capability on (and the master switch off), the
    /// store policy for the database, and the write policy with the capability named - the Statements page's gate. The reason names the
    /// option (D4) when it is not.
    /// </summary>
    private async Task<(bool Allowed, string? Reason)> ActivityAsync(StudioScope whole, CancellationToken cancellationToken)
    {
        string option = capabilities.OptionFor(StudioCapability.ViewDatabaseActivity);

        if (!capabilities.IsEnabled(StudioCapability.ViewDatabaseActivity))
        {
            return (false, Reason(option, "the capability is off" + (capabilities.ReadOnly ? " while ReadOnly is on" : string.Empty)));
        }

        if (!await authorization.IsAuthorizedAsync(whole, capability: null, cancellationToken).ConfigureAwait(false)
            || !await authorization.IsAuthorizedAsync(whole, StudioCapability.ViewDatabaseActivity, cancellationToken).ConfigureAwait(false))
        {
            return (false, Reason(option, "your account's write policy does not allow it for this database"));
        }

        return (true, null);
    }

    private static string Reason(string option, string why) =>
        "Row estimates and last events compare tenants with one another, so they are shown with ViewDatabaseActivity, as T18's tenant skew is: "
        + option + " on, and your account's write policy allowing it for this database (" + why + ").";

    // -------------------------------------------------------------------------------------------------
    // The layout of the store's tenanted tables
    // -------------------------------------------------------------------------------------------------

    private sealed record DocumentTable(string Schema, string Table, bool SoftDeleted);

    private sealed record Layout(
        IReadOnlyList<DocumentTable> Documents,
        string EventSchema,
        bool EventsConjoined,
        bool EventsPartitioned,
        bool DocumentsPartitioned);

    private Layout LayoutOf(IDocumentStore store)
    {
        List<DocumentTable> documents = [];

        foreach (IDocumentType documentType in DocumentDataService.VisibleDocumentTypesOf(store, options.Value.IsDocumentTypeVisible))
        {
            if (documentType.TenancyStyle != TenancyStyle.Conjoined)
            {
                continue;
            }

            DocumentTableInfo info = DocumentTableInfo.FromDocumentType(documentType);

            // Subclasses share their root's table and count once.
            DocumentTable? existing = documents.Find(x => string.Equals(x.Schema, info.Schema, StringComparison.Ordinal)
                && string.Equals(x.Table, info.Table, StringComparison.Ordinal));

            if (existing is null)
            {
                documents.Add(new DocumentTable(info.Schema, info.Table, info.SoftDeleteEnabled));
            }
        }

        (_, bool partitioned) = StoreTenancy.Conjoined(store.Options, options.Value.IsDocumentTypeVisible);

        return new Layout(
            documents,
            store.Options.Events.DatabaseSchemaName,
            store.Options.Events.TenancyStyle == TenancyStyle.Conjoined,
            store.Options.Events.UseTenantPartitionedEvents,
            partitioned);
    }

    // -------------------------------------------------------------------------------------------------
    // One database
    // -------------------------------------------------------------------------------------------------

    private sealed class RowDraft(IMartenDatabase? database, string tenantId)
    {
        public IMartenDatabase? Database { get; } = database;

        public string TenantId { get; } = tenantId;

        public TenantPlacement Placement { get; set; } = TenantPlacement.NotPlaced;

        public string PlacementText { get; set; } = "Database: not placed";

        public TenantCell Documents { get; set; } = NotRead;

        public TenantCell Events { get; set; } = NotRead;

        public TenantCell Streams { get; set; } = NotRead;

        public TenantCell LastEvent { get; set; } = NotRead;

        public TenantCell? Projections { get; set; }

        public TenantCell? Volumes { get; set; }

        public string? Problem { get; set; }

        /// <summary>Whether the database holds this tenant's data and nobody else's.</summary>
        public bool Exclusive { get; set; }

        public TenantRow ToRow()
        {
            TenantCell? all = Volumes;

            return new TenantRow(
                Database?.Id.Identity ?? string.Empty,
                TenantId,
                Placement,
                PlacementText,
                all ?? Documents,
                all ?? Events,
                all ?? Streams,
                all ?? LastEvent,
                Projections,
                Problem);
        }
    }

    /// <summary>Why ~Events, ~Streams and Last event are a dash for a tenant of a store whose event store is not kept per tenant.</summary>
    internal const string EventsNotPerTenantReason = "The event store is not kept per tenant.";

    private static readonly TenantCell NotRead = TenantCell.Dash("not read");

    private async Task ReadDatabaseAsync(
        IDocumentStore store,
        IMartenDatabase database,
        IReadOnlyList<IMartenDatabase> listed,
        Layout layout,
        List<RowDraft> rows,
        bool activity,
        StudioScope whole,
        CancellationToken cancellationToken)
    {
        bool pooled = DatabaseReach.IsPooledByDesign(store.Options.Tenancy);

        foreach (RowDraft row in rows)
        {
            bool exclusive = DatabaseReach.IsExclusivelyTenants(store, database, row.TenantId, listed);

            // Placement from configuration alone; the catalog refines it for a visitor through the activity gate (below).
            (row.Placement, row.PlacementText) = exclusive
                ? (TenantPlacement.OwnDatabase, "Own database")
                : Configured(layout, pooled);

            row.Exclusive = exclusive;
        }

        List<RowDraft> exclusiveRows = [.. rows.Where(static x => x.Exclusive)];
        List<RowDraft> sharedRows = [.. rows.Where(static x => !x.Exclusive)];

        bool partitionedEvents = layout.EventsPartitioned && sharedRows.Count > 0;
        bool needsConnection = activity || exclusiveRows.Count > 0 || partitionedEvents;

        if (!needsConnection)
        {
            foreach (RowDraft row in sharedRows)
            {
                row.Volumes ??= WithheldCell;
            }

            return;
        }

        await using NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(database, ConnectionUsage.Read);
        await connection.OpenStudioAsync(cancellationToken).ConfigureAwait(false);

        // Projections: the row's own policy only (the rows are already the picker's).
        await ReadProjectionsAsync(store, connection, layout, exclusiveRows, sharedRows, cancellationToken).ConfigureAwait(false);

        if (!activity)
        {
            foreach (RowDraft row in rows)
            {
                row.Volumes ??= WithheldCell;
            }

            return;
        }

        if (exclusiveRows.Count > 0)
        {
            await ReadExclusiveAsync(connection, store, layout, exclusiveRows, cancellationToken).ConfigureAwait(false);
        }

        if (sharedRows.Count > 0)
        {
            await ReadSharedAsync(connection, layout, sharedRows, cancellationToken).ConfigureAwait(false);
        }

        await RecordVolumesReadAsync(whole, rows.Count).ConfigureAwait(false);
    }

    private static readonly TenantCell WithheldCell = TenantCell.Dash("Shown with ViewDatabaseActivity: see the note above the table.");

    private static (TenantPlacement, string) Configured(Layout layout, bool pooled)
    {
        string inner = layout.DocumentsPartitioned || layout.EventsPartitioned ? "In tables partitioned by tenant" : "Shared tables";

        return pooled
            ? (TenantPlacement.Shard, "A shard: " + inner.ToLowerInvariant())
            : layout.DocumentsPartitioned || layout.EventsPartitioned
                ? (TenantPlacement.PartitionedTables, inner)
                : (TenantPlacement.SharedTables, inner);
    }

    // -------------------------------------------------------------------------------------------------
    // Volumes of a database that is one tenant's alone
    // -------------------------------------------------------------------------------------------------

    private async Task ReadExclusiveAsync(
        NpgsqlConnection connection,
        IDocumentStore store,
        Layout layout,
        List<RowDraft> rows,
        CancellationToken cancellationToken)
    {
        HashSet<string> schemas = new(StringComparer.Ordinal) { layout.EventSchema };
        foreach (DocumentTable table in layout.Documents)
        {
            schemas.Add(table.Schema);
        }

        IReadOnlyDictionary<string, DocumentBrowseQueries.TableEstimate> estimates = await DocumentBrowseQueries
            .EstimateAllAsync(connection, [.. schemas], CommandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        const string Whole = "every row of this database";

        TenantCell documents = EstimateCell(
            layout.Documents.Select(x => estimates.TryGetValue(DocumentBrowseQueries.Key(x.Schema, x.Table), out var e) ? (long?) e.Rows : null).ToList(),
            Whole,
            "no conjoined collection");
        TenantCell events = EstimateCell([Lookup(estimates, layout.EventSchema, EventTableInfo.EventsTable)], Whole, "no event storage yet");
        TenantCell streams = EstimateCell([Lookup(estimates, layout.EventSchema, EventTableInfo.StreamsTable)], Whole, "no event storage yet");

        TenantCell last = await LatestEventOfDatabaseAsync(connection, layout, estimates.ContainsKey(DocumentBrowseQueries.Key(layout.EventSchema, EventTableInfo.EventsTable)), cancellationToken)
            .ConfigureAwait(false);

        foreach (RowDraft row in rows)
        {
            row.Volumes = null;
            row.Documents = documents;
            row.Events = events;
            row.Streams = streams;
            row.LastEvent = last;
        }
    }

    private static long? Lookup(IReadOnlyDictionary<string, DocumentBrowseQueries.TableEstimate> estimates, string schema, string table) =>
        estimates.TryGetValue(DocumentBrowseQueries.Key(schema, table), out var e) ? e.Rows : null;

    /// <summary>~N from <c>reltuples</c>: absent tables are not counted, an unanalysed one (-1) is unknown.</summary>
    private static TenantCell EstimateCell(List<long?> tables, string title, string whenNone)
    {
        List<long?> present = [.. tables.Where(static x => x is not null)];
        if (present.Count == 0)
        {
            return TenantCell.Dash(whenNone);
        }

        long total = present.Sum(static x => Math.Max(x!.Value, 0));
        bool unknown = present.Any(static x => x < 0);

        if (unknown && total == 0)
        {
            return TenantCell.Dash("never analysed");
        }

        return new TenantCell("~" + total.ToString("N0", CultureInfo.InvariantCulture) + (unknown ? "+" : string.Empty), title);
    }

    private async Task<TenantCell> LatestEventOfDatabaseAsync(
        NpgsqlConnection connection,
        Layout layout,
        bool eventsExist,
        CancellationToken cancellationToken)
    {
        if (!eventsExist)
        {
            return TenantCell.Dash("no event storage yet");
        }

        await using NpgsqlCommand command = EventQueryBuilder.BuildLatestEvent(layout.EventSchema, options.Value.QueryTimeout);
        command.Connection = connection;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? LastEventCell(reader) : TenantCell.Dash("no event yet");
    }

    private static TenantCell LastEventCell(NpgsqlDataReader reader)
    {
        long sequence = reader.GetInt64(0);
        DateTimeOffset at = reader.GetFieldValue<DateTimeOffset>(1);

        return new TenantCell("#" + sequence.ToString("N0", CultureInfo.InvariantCulture)) { At = at };
    }

    // -------------------------------------------------------------------------------------------------
    // Volumes of tables shared with other tenants
    // -------------------------------------------------------------------------------------------------

    internal enum Kind
    {
        Count,
        NoPartition,
        NotSingledOut,
        NeverAnalysed,
        Hidden,
    }

    internal readonly record struct Outcome(Kind Kind, double Tuples, string Table);

    private async Task ReadSharedAsync(NpgsqlConnection connection, Layout layout, List<RowDraft> rows, CancellationToken cancellationToken)
    {
        List<HealthTableTarget> targets = [.. layout.Documents.Select(static x => new HealthTableTarget(x.Schema, x.Table))];

        if (layout.EventsConjoined)
        {
            targets.Add(new HealthTableTarget(layout.EventSchema, EventTableInfo.EventsTable));
            targets.Add(new HealthTableTarget(layout.EventSchema, EventTableInfo.StreamsTable));
        }

        HashSet<string> tenants = new(rows.Select(static x => x.TenantId), StringComparer.Ordinal);

        List<ColumnPartitionRow> partitionRows = [];
        List<ColumnStatisticsRow> statisticsRows = [];

        if (targets.Count > 0)
        {
            string[] bounds = [.. tenants.SelectMany(TenantBoundText.Variants)];

            await RunAsync(
                    connection,
                    ColumnValueRowsQueries.PartitionRowsCommand(targets, TenantColumn, bounds, options.Value.QueryTimeout),
                    ColumnValueRowsQueries.ReadPartition,
                    partitionRows,
                    cancellationToken)
                .ConfigureAwait(false);
            await RunAsync(
                    connection,
                    ColumnValueRowsQueries.StatisticsRowsCommand(targets, TenantColumn, [.. tenants], options.Value.QueryTimeout),
                    ColumnValueRowsQueries.ReadStatistics,
                    statisticsRows,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Partitioned tables, and the tuples of the partition each tenant holds in them.
        HashSet<string> partitionedTables = new(partitionRows.Select(static x => Key(x.Schema, x.Table)), StringComparer.Ordinal);
        Dictionary<(string Table, string Tenant), double> partitionTuples = [];

        foreach (ColumnPartitionRow row in partitionRows)
        {
            if (TenantBoundText.TenantOf(row.Bound, tenants) is { } tenant && row.Tuples is { } tuples)
            {
                partitionTuples[(Key(row.Schema, row.Table), tenant)] = tuples;
            }
        }

        Dictionary<(string Table, string Tenant), ColumnStatisticsRow> statistics = [];
        foreach (ColumnStatisticsRow row in statisticsRows)
        {
            statistics[(Key(row.Schema, row.Table), row.Value)] = row;
        }

        foreach (RowDraft row in rows)
        {
            row.Volumes = null;

            // Placement, now that the catalog has been read.
            if (partitionedTables.Count > 0)
            {
                bool has = partitionedTables.Any(t => partitionTuples.ContainsKey((t, row.TenantId)));
                string inner = has ? "Own partition" : "No partition of its own yet";

                (row.Placement, row.PlacementText) = row.Placement == TenantPlacement.Shard
                    ? (TenantPlacement.Shard, "A shard: " + inner.ToLowerInvariant())
                    : (has ? TenantPlacement.OwnPartition : TenantPlacement.NoPartitionYet, inner);
            }
            else if (row.Placement is TenantPlacement.PartitionedTables)
            {
                (row.Placement, row.PlacementText) = (TenantPlacement.SharedTables, "Shared tables");
            }

            Outcome? OutcomeOf(HealthTableTarget table)
            {
                string key = Key(table.Schema, table.Table);

                if (partitionedTables.Contains(key))
                {
                    return partitionTuples.TryGetValue((key, row.TenantId), out double tuples)
                        ? new Outcome(Kind.Count, tuples, key)
                        : new Outcome(Kind.NoPartition, 0, key);
                }

                if (!statistics.TryGetValue((key, row.TenantId), out ColumnStatisticsRow? stat))
                {
                    return null;
                }

                return stat.Leaves > 0 && stat.ReadableLeaves == 0 ? new Outcome(Kind.Hidden, 0, key)
                    : !stat.HasStatistics ? new Outcome(Kind.NeverAnalysed, 0, key)
                    : stat.Tuples > 0 ? new Outcome(Kind.Count, stat.Tuples, key)
                    : new Outcome(Kind.NotSingledOut, 0, key);
            }

            List<Outcome> documents = [.. layout.Documents.Select(x => OutcomeOf(new HealthTableTarget(x.Schema, x.Table))).OfType<Outcome>()];
            row.Documents = layout.Documents.Count == 0
                ? TenantCell.Dash("no conjoined collection")
                : Cell(documents, "collections");

            if (layout.EventsConjoined)
            {
                row.Events = Cell([.. new[] { OutcomeOf(new HealthTableTarget(layout.EventSchema, EventTableInfo.EventsTable)) }.OfType<Outcome>()], "tables");
                row.Streams = Cell([.. new[] { OutcomeOf(new HealthTableTarget(layout.EventSchema, EventTableInfo.StreamsTable)) }.OfType<Outcome>()], "tables");
            }
            else
            {
                row.Events = TenantCell.Dash(EventsNotPerTenantReason);
                row.Streams = row.Events;
            }
        }

        // The newest event, only where an index finds it: a tenant-partitioned event store, one partition each.
        await ReadLastEventsAsync(connection, layout, rows, partitionedTables, cancellationToken).ConfigureAwait(false);
    }

    private static string Key(string schema, string table) => schema + "." + table;

    /// <summary>One category's cell from its tables' outcomes (T6's wording).</summary>
    internal static TenantCell Cell(List<Outcome> outcomes, string noun)
    {
        if (outcomes.Count == 0)
        {
            return TenantCell.Dash("no storage yet");
        }

        double total = outcomes.Where(static x => x.Kind is Kind.Count or Kind.NoPartition).Sum(static x => x.Tuples);
        List<Outcome> unknown = [.. outcomes.Where(static x => x.Kind is Kind.NotSingledOut or Kind.NeverAnalysed or Kind.Hidden)];
        string number = total.ToString("N0", CultureInfo.InvariantCulture);

        if (unknown.Count == 0)
        {
            // Every table is partitioned by tenant and this tenant holds no partition of its own: its rows, if it has any, are in a
            // shared or default partition, and 0 would be a claim the catalog cannot make.
            return total == 0 && outcomes.All(static x => x.Kind == Kind.NoPartition)
                ? TenantCell.Dash("its rows, if any, are in a shared or default partition")
                : new TenantCell("~" + number);
        }

        if (total > 0)
        {
            return new TenantCell(
                "~" + number + "+",
                "at least: " + unknown.Count.ToString(CultureInfo.InvariantCulture) + " " + noun + "' statistics do not single this tenant out");
        }

        if (unknown.All(static x => x.Kind == Kind.Hidden))
        {
            return TenantCell.Dash(
                "The statistics are hidden from the role the studio reads as: it needs "
                + PostgresGrant.ColumnSelect(unknown[0].Table, TenantColumn).Privilege
                + (unknown.Count > 1 ? " (and " + (unknown.Count - 1).ToString(CultureInfo.InvariantCulture) + " more)" : string.Empty));
        }

        return unknown.Any(static x => x.Kind == Kind.NotSingledOut)
            ? TenantCell.Dash("fewer rows than the column statistics single out; count exactly on Documents with this tenant selected")
            : TenantCell.Dash("never analysed");
    }

    private async Task ReadLastEventsAsync(
        NpgsqlConnection connection,
        Layout layout,
        List<RowDraft> rows,
        HashSet<string> partitionedTables,
        CancellationToken cancellationToken)
    {
        if (!layout.EventsConjoined)
        {
            foreach (RowDraft row in rows)
            {
                row.LastEvent = TenantCell.Dash(EventsNotPerTenantReason);
            }

            return;
        }

        if (!layout.EventsPartitioned)
        {
            foreach (RowDraft row in rows)
            {
                row.LastEvent = new TenantCell("not read", "no index finds one tenant's newest event: open the Feed with this tenant selected");
            }

            return;
        }

        if (!partitionedTables.Contains(Key(layout.EventSchema, EventTableInfo.EventsTable)))
        {
            foreach (RowDraft row in rows)
            {
                row.LastEvent = TenantCell.Dash("no event storage yet");
            }

            return;
        }

        // One read-only transaction: each statement is planned for its tenant (a custom plan) and so pruned to one partition, even on a
        // host data source that auto-prepares, where a generic plan would lock every partition.
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var readOnly = new NpgsqlCommand(ReadOnlySqlSession.ReadOnlySql, connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var custom = new NpgsqlCommand(ReadOnlySqlSession.SetConfigSql, connection, transaction))
        {
            custom.Parameters.AddWithValue("name", "plan_cache_mode");
            custom.Parameters.AddWithValue("value", "force_custom_plan");
            await custom.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (RowDraft row in rows)
        {
            await using NpgsqlCommand command = EventQueryBuilder.BuildLatestEventOfTenant(layout.EventSchema, row.TenantId, options.Value.QueryTimeout);
            command.Connection = connection;
            command.Transaction = transaction;

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            row.LastEvent = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? LastEventCell(reader) : TenantCell.Dash("no event yet");
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunAsync<T>(
        NpgsqlConnection connection,
        NpgsqlCommand command,
        Func<NpgsqlDataReader, T> map,
        List<T> into,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand owned = command;
        owned.Connection = connection;

        await using NpgsqlDataReader reader = await owned.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            into.Add(map(reader));
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Projections
    // -------------------------------------------------------------------------------------------------

    private async Task ReadProjectionsAsync(
        IDocumentStore store,
        NpgsqlConnection connection,
        Layout layout,
        List<RowDraft> exclusiveRows,
        List<RowDraft> sharedRows,
        CancellationToken cancellationToken)
    {
        bool perTenant = layout.EventsPartitioned && sharedRows.Count > 0;

        if (exclusiveRows.Count == 0 && !perTenant)
        {
            return;
        }

        bool extended = store.Options.Events.EnableExtendedProgressionTracking;
        TimeSpan timeout = options.Value.QueryTimeout;

        ProgressionRows progression = await ProjectionProgressQueries
            .ReadProgressionRowsAsync(connection, catalog, layout.EventSchema, extended, null, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (exclusiveRows.Count > 0)
        {
            long? mark = await ProjectionProgressQueries
                .ReadHighWaterMarkAsync(connection, catalog, layout.EventSchema, store.Options.Events.UseTenantPartitionedEvents, timeout, cancellationToken)
                .ConfigureAwait(false);

            List<ProgressionRow> shards = [.. progression.Rows.Where(static x => !ProjectionDataService.IsBookkeepingRow(x.Name))];
            TenantCell cell = ProgressCell(progression.TableExists, shards, mark);

            foreach (RowDraft row in exclusiveRows)
            {
                row.Projections = cell;
            }
        }

        if (perTenant)
        {
            HashSet<string>? registered = DatabaseReach.RegisteredShardIdentities(store);

            foreach (RowDraft row in sharedRows)
            {
                List<ProgressionRow> shards = [.. progression.Rows.Where(x => !ProjectionDataService.IsBookkeepingRow(x.Name)
                    && DatabaseReach.TenantOfShard(x.Name, registered) == row.TenantId)];

                long? mark = progression.Rows
                    .FirstOrDefault(x => string.Equals(x.Name, "HighWaterMark:" + row.TenantId, StringComparison.Ordinal))?.Sequence;

                row.Projections = ProgressCell(progression.TableExists, shards, mark);
            }
        }
    }

    /// <summary>One tenant's (or one tenant database's) projections in a cell: caught up, the worst lag and its shard, failures, or none yet.</summary>
    internal static TenantCell ProgressCell(bool tableExists, IReadOnlyList<ProgressionRow> shards, long? highWater)
    {
        if (!tableExists || shards.Count == 0)
        {
            return new TenantCell("no progress yet");
        }

        int failed = shards.Count(static x => x.FailureCategory is { Length: > 0 });
        if (failed > 0)
        {
            return new TenantCell(failed.ToString(CultureInfo.InvariantCulture) + " failed");
        }

        if (highWater is not { } mark)
        {
            return new TenantCell("no high-water mark yet");
        }

        ProgressionRow worst = shards.OrderBy(static x => x.Sequence).First();
        long behind = Math.Max(0, mark - worst.Sequence);

        return behind == 0
            ? new TenantCell("caught up")
            : new TenantCell(behind.ToString("N0", CultureInfo.InvariantCulture) + " behind (" + worst.Name + ")");
    }

    // -------------------------------------------------------------------------------------------------
    // The footer, and the audit
    // -------------------------------------------------------------------------------------------------

    /// <summary>Whole-table estimates of the visible single-tenanted collections of the scope's database: shared by every tenant.</summary>
    private async Task<TenantSharedRow?> SharedRowAsync(
        IDocumentStore store,
        IMartenDatabase database,
        CancellationToken cancellationToken,
        CancellationToken callerToken)
    {
        List<(string Schema, string Table)> tables = [];

        foreach (IDocumentType documentType in DocumentDataService.VisibleDocumentTypesOf(store, options.Value.IsDocumentTypeVisible))
        {
            if (documentType.TenancyStyle == TenancyStyle.Conjoined)
            {
                continue;
            }

            DocumentTableInfo info = DocumentTableInfo.FromDocumentType(documentType);
            if (!tables.Contains((info.Schema, info.Table)))
            {
                tables.Add((info.Schema, info.Table));
            }
        }

        if (tables.Count == 0)
        {
            return null;
        }

        try
        {
            await using NpgsqlConnection connection = MartenStudioDatabase.CreateConnection(database, ConnectionUsage.Read);
            await connection.OpenStudioAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyDictionary<string, DocumentBrowseQueries.TableEstimate> estimates = await DocumentBrowseQueries
                .EstimateAllAsync(connection, [.. tables.Select(static x => x.Schema).Distinct(StringComparer.Ordinal)], CommandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false);

            TenantCell cell = EstimateCell(
                [.. tables.Select(x => Lookup(estimates, x.Schema, x.Table))],
                "whole-table estimates of the collections no tenant owns",
                "no storage yet");

            return new TenantSharedRow(tables.Count, cell);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            // The budget is spent, not the visitor gone: the page says so rather than going blank.
            return new TenantSharedRow(tables.Count, new TenantCell("not read in time", "the 20-second budget for this page was spent before this was read"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Marten Studio could not estimate the single-tenanted collections for the Tenants page");
            return null;
        }
    }

    /// <summary>One ring entry and one event 9265 for the first read, in this circuit, of a database's activity-gated columns.</summary>
    private async Task RecordVolumesReadAsync(StudioScope whole, int tenants)
    {
        if (!ledger.TryRecord(TableRowAuditLedger.Key(whole.StoreKey, whole.DatabaseId, LedgerSchema, LedgerRelation, LedgerKind), "volumes", out _))
        {
            return;
        }

        audit.Record(
            AuditAction,
            AuditTarget,
            succeeded: true,
            "Tenant volumes read: " + tenants.ToString(CultureInfo.InvariantCulture) + " tenants.",
            StudioCapability.ViewDatabaseActivity,
            whole);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.TenantVolumesRead(await authorization.UserNameAsync().ConfigureAwait(false), tenants, whole.StoreKey, whole.DatabaseId);
        }
    }
}
