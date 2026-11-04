using System.Globalization;

using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Events;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.Options;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Services.Health;

/// <summary>
/// E06's on-demand action (1.2): the 20 streams with the most events, the aggregate each was started as, and whether this process
/// rebuilds that aggregate from every event on each fetch.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it reads.</b> One statement against <c>mt_streams</c> - never <c>mt_events</c> - in a read-only transaction as the
/// console's role, under <c>QueryTimeout</c> as <c>statement_timeout</c> (<see cref="EventQueryBuilder.BuildLongestStreams" />).
/// No index orders streams by length, so it is a sequential scan of one row per live stream feeding a top-20 sort. It opens no Marten
/// session, calls nothing that ensures storage and enumerates no tenancy (hard rule 14).
/// </para>
/// <para>
/// <b>Reach (D27).</b> On a conjoined event store a stream belongs to one tenant: with a tenant selected only its streams are read.
/// With none, the rows name tenants, which on the Health page is the Activity gate's (the capability, the store policy and the write
/// policy with the capability named); a visitor short of it is refused before any connection, and past it every row's tenant is asked
/// of the store policy and the streams of a tenant it hides are dropped and counted. Where the events are not conjoined the streams are
/// the database's and the finding's gate is the whole of it, and a tenant that is selected narrows the list as Events › Streams
/// narrows it.
/// </para>
/// <para>
/// <b>Hidden types are never named.</b> While <c>IsDocumentTypeVisible</c> hides anything, the statement keeps only streams started as
/// an aggregate it shows (or with no type): a hidden type's streams are not read, listed, counted or named, and an alias this process
/// cannot map to a type is treated as hidden.
/// </para>
/// </remarks>
internal sealed class LongestStreamsAction(IOptions<MartenStudioOptions> options, ColumnCatalog catalog) : IHealthFindingAction
{
    /// <summary>The action's id.</summary>
    public const string ActionId = "e06.longest-streams";

    /// <inheritdoc />
    public string Id => ActionId;

    /// <inheritdoc />
    public string Code => "E06";

    /// <inheritdoc />
    public string Label => "Find the 20 longest streams";

    /// <inheritdoc />
    public string Description =>
        "Reads every live stream's row of mt_streams once - no index orders streams by length - under QueryTimeout as "
        + "statement_timeout, and lists the 20 with the most events and the aggregate each was started as. For you only, "
        + "audited, and kept nowhere.";

    /// <inheritdoc />
    public string AuditAction => "Find the longest streams";

    /// <inheritdoc />
    public string AuditTarget => "the event store's streams (mt_streams)";

    /// <inheritdoc />
    public HealthGate Gate => HealthGate.Database;

    /// <inheritdoc />
    public StudioCapability? Capability => null;

    /// <inheritdoc />
    public bool AppliesTo(HealthFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return string.Equals(finding.Code, Code, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<HealthActionResult> RunAsync(HealthActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (context.Resolved is not ResolvedScope resolved)
        {
            return HealthActionResult.Failed("This action reads a Marten store's event tables, and this source is not one.", now);
        }

        IReadOnlyStoreOptions storeOptions = resolved.Store.Options;
        bool conjoined = storeOptions.Events.TenancyStyle == TenancyStyle.Conjoined;
        string? tenant = resolved.Scope.TenantId;

        // B4: a conjoined store's rows name their tenants, which on this page is the Activity gate's. Asked before any connection is
        // opened, so a visitor short of it costs the database nothing.
        bool nameTenants = conjoined && tenant is null;

        if (nameTenants && !await context.Gate.PassesAsync(HealthGate.Activity).ConfigureAwait(false))
        {
            return HealthActionResult.Refused(
                "On this store each stream belongs to one tenant, so listing streams across tenants names them. Pick a tenant in the header, or this "
                + context.Gate.WhyNot(HealthGate.Activity) + ".",
                now) with
            {
                AuditCapability = StudioCapability.ViewDatabaseActivity,
            };
        }

        StreamAggregateMap map = StreamAggregateMap.From(storeOptions, options.Value.IsDocumentTypeVisible);

        string schema = storeOptions.Events.DatabaseSchemaName;

        try
        {
            await using NpgsqlConnection connection = resolved.Target.CreateConnection(StudioConnectionUsage.Read);
            await PostgresFailure.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

            // The column sets before the transaction, as every events read does: the catalog reads information_schema on this
            // connection and caches what it read.
            TableColumns events = await catalog.GetAsync(connection, schema, EventTableInfo.EventsTable, cancellationToken).ConfigureAwait(false);
            TableColumns streams = await catalog.GetAsync(connection, schema, EventTableInfo.StreamsTable, cancellationToken).ConfigureAwait(false);

            if (!streams.Exists)
            {
                return HealthActionResult.Refused(
                    "The event store's tables do not exist in this database yet, so there are no streams to measure.", now);
            }

            EventTableInfo table = EventTableInfo.FromColumns(schema, storeOptions.Events.StreamIdentity, events, streams);

            LongestStreamsQuery query = new()
            {
                TenantId = tenant,
                IncludeTenant = nameTenants,
                ShownTypes = map.ShownAliases,
            };

            using NpgsqlCommand command = EventQueryBuilder.BuildLongestStreams(table, query);
            HealthStatement statement = new(command.CommandText, [.. command.Parameters.Select(static x => "@" + x.ParameterName)])
            {
                Label = "the longest streams",
            };

            ReadOnlySqlSession session = new(context.Session);

            List<LongStreamRow> read = await session.InTransactionAsync(
                    connection,
                    async (transaction, token) =>
                    {
                        command.Connection = connection;
                        command.Transaction = transaction;
                        command.CommandTimeout = context.CommandTimeoutSeconds;

                        return await ReadAsync(command, table, query, token).ConfigureAwait(false);
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            // B3 step 7: the rows name tenants, so each is asked of the store policy; a tenant it hides is dropped and counted.
            List<LongStreamRow> shown = [];
            int notShown = 0;

            foreach (LongStreamRow row in read)
            {
                if (nameTenants && (row.TenantId is not { } rowTenant || !await context.Gate.TenantAsync(rowTenant).ConfigureAwait(false)))
                {
                    notShown++;
                    continue;
                }

                shown.Add(row);
            }

            return Present(
                map,
                shown,
                new LongestStreamsShape(
                    Archived: table.HasStreamColumn("is_archived"),
                    TenantFilter: query.TenantId is not null && table.HasStreamColumn("tenant_id") ? query.TenantId : null,
                    TenantColumn: nameTenants && table.HasStreamColumn("tenant_id"),
                    Conjoined: conjoined,
                    NotShown: notShown,
                    Created: table.HasStreamColumn("created")),
                statement,
                now);
        }
        catch (Exception exception) when (PostgresFailure.IsConnectionFailure(exception))
        {
            return HealthActionResult.Failed(PostgresFailure.CouldNotConnect(exception), now);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.QueryCanceled)
        {
            return TimedOut(context, nameTenants, now);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return HealthActionResult.TimedOut(
                "mt_streams is locked for a schema change right now, so the read would queue behind it; try again later.", now);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            return HealthActionResult.Refused("The role the studio reads as may not select from mt_streams.", now);
        }
        catch (Exception exception) when (PostgresFailure.IsTimeout(exception))
        {
            return TimedOut(context, nameTenants, now);
        }
    }

    private static HealthActionResult TimedOut(HealthActionContext context, bool canNarrow, DateTimeOffset now) =>
        HealthActionResult.TimedOut(
            "It ran past its statement_timeout (" + StudioText.Marten.OptionName("QueryTimeout") + ", "
            + EventHealth.Duration(context.Session.StatementTimeout) + "). "
            + (canNarrow ? "Pick a tenant to narrow it, or raise " : "Raise ") + StudioText.Marten.OptionName("QueryTimeout") + ".",
            now);

    /// <summary>Reads the statement's rows, at most <see cref="LongestStreamsQuery.MaxTake" />.</summary>
    private static async Task<List<LongStreamRow>> ReadAsync(
        NpgsqlCommand command,
        EventTableInfo table,
        LongestStreamsQuery query,
        CancellationToken cancellationToken)
    {
        List<LongStreamRow> rows = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // Positions are the builder's: id, type, version, then tenant_id when asked, then created when the table has it.
        bool tenantColumn = query.IncludeTenant && table.HasStreamColumn("tenant_id");
        bool created = table.HasStreamColumn("created");

        while (rows.Count < LongestStreamsQuery.MaxTake && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int at = 0;

            string id = reader.GetString(at++);
            string? type = reader.IsDBNull(at) ? null : reader.GetString(at);
            at++;
            long version = reader.IsDBNull(at) ? 0 : Convert.ToInt64(reader.GetValue(at), CultureInfo.InvariantCulture);
            at++;

            string? tenantId = null;
            if (tenantColumn)
            {
                tenantId = reader.IsDBNull(at) ? null : reader.GetString(at);
                at++;
            }

            DateTimeOffset? startedAt = null;
            if (created && !reader.IsDBNull(at))
            {
                startedAt = reader.GetValue(at) switch
                {
                    DateTimeOffset offset => offset,
                    DateTime stamp => new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc)),
                    _ => null,
                };
            }

            rows.Add(new LongStreamRow(id, type, version, tenantId, startedAt));
        }

        return rows;
    }

    /// <summary>
    /// The result a set of rows becomes: the table, the summary, the notes and the remedies. Pure - no I/O, no policy - so every
    /// sentence is testable without a database.
    /// </summary>
    /// <param name="map">What the process knows of its aggregates.</param>
    /// <param name="rows">The rows this visitor may be shown, longest first.</param>
    /// <param name="shape">How the statement was shaped.</param>
    /// <param name="statement">The statement, for "Show SQL".</param>
    /// <param name="now">When.</param>
    internal static HealthActionResult Present(
        StreamAggregateMap map,
        IReadOnlyList<LongStreamRow> rows,
        LongestStreamsShape shape,
        HealthStatement statement,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(statement);

        List<string> columns = ["Stream", "Events", "Started as", "On each fetch"];

        if (shape.TenantColumn)
        {
            columns.Add("Tenant");
        }

        if (shape.Created)
        {
            columns.Add("Started");
        }

        List<StreamClass> classes = [.. rows.Select(x => map.Classify(x.Type))];
        List<HealthActionRow> table = [];

        for (int i = 0; i < rows.Count; i++)
        {
            LongStreamRow row = rows[i];
            List<HealthActionCell> cells =
            [
                new(row.Id)
                {
                    Link = new HealthLink("Open the stream", EventLinks.StreamDetailRoute, [new("id", row.Id)]),
                    Mono = true,
                    Copy = true,
                },
                new(row.Version.ToString("N0", CultureInfo.InvariantCulture)) { Number = true },
                new(classes[i].StartedAs),
                new(FetchText(classes[i].Fetch)),
            ];

            if (shape.TenantColumn)
            {
                cells.Add(new(row.TenantId ?? string.Empty) { Mono = true });
            }

            if (shape.Created)
            {
                cells.Add(row.Created is { } started ? new(started.ToString("u", CultureInfo.InvariantCulture)) { Time = started } : new(string.Empty));
            }

            table.Add(new HealthActionRow(cells));
        }

        // Remedies: the aggregate each long live-only stream belongs to, once each.
        List<HealthRemedy> remedies =
        [
            .. classes
                .Where(static x => x.Fetch == StreamFetch.RebuiltFromEveryEvent && x.LiveName is not null)
                .Select(static x => x.LiveName!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(static name => new HealthRemedy(
                    LongStreamsWithoutSnapshotCheck.SnapshotRemedyLine(name),
                    LongStreamsWithoutSnapshotCheck.SnapshotRemedyExplanation)),
        ];

        List<string> notes = [];

        if (shape.Archived)
        {
            notes.Add("Archived streams are left out.");
        }

        if (map.Hides)
        {
            notes.Add("Streams of aggregate types this application hides from the studio are left out.");
        }

        if (shape.NotShown > 0)
        {
            notes.Add(shape.NotShown.ToString("N0", CultureInfo.InvariantCulture)
                + " of the longest streams " + (shape.NotShown == 1 ? "is" : "are")
                + " of tenants your account may not see, and " + (shape.NotShown == 1 ? "is" : "are") + " not listed.");
        }

        if (shape.TenantFilter is { } filtered && !shape.Conjoined)
        {
            notes.Add("Only streams appended under tenant " + filtered + " are listed, as Events › Streams lists them.");
        }

        notes.Add("A stream's type is the aggregate it was started as; code may also aggregate it as another type, which no table records.");

        return new HealthActionResult
        {
            Outcome = HealthActionOutcome.Done,
            RanAt = now,
            Summary = Summary(classes, rows, shape),
            TableLabel = "The longest streams",
            Columns = columns,
            Rows = table,
            Notes = notes,
            Remedies = remedies,
            Statements = [statement],
            AuditMessage = "Listed " + rows.Count.ToString("N0", CultureInfo.InvariantCulture) + " of the longest live streams"
                + (shape.NotShown > 0 ? ", " + shape.NotShown.ToString("N0", CultureInfo.InvariantCulture) + " of tenants not shown" : string.Empty) + ".",
            AuditCapability = shape.TenantColumn ? StudioCapability.ViewDatabaseActivity : null,
        };
    }

    private static string FetchText(StreamFetch fetch) => fetch switch
    {
        StreamFetch.RebuiltFromEveryEvent => "rebuilt from every event",
        StreamFetch.KeptByProjection => "kept by a projection",
        _ => "not an aggregate this process knows",
    };

    private static string Summary(IReadOnlyList<StreamClass> classes, IReadOnlyList<LongStreamRow> rows, LongestStreamsShape shape)
    {
        string count = rows.Count.ToString("N0", CultureInfo.InvariantCulture);
        string where = shape.TenantFilter is { } tenant && shape.Conjoined ? " of tenant " + tenant : string.Empty;

        if (rows.Count == 0)
        {
            return "No live stream " + (shape.NotShown > 0 ? "your account may see " : string.Empty) + "was found in this database" + where + ".";
        }

        List<string> parts = [];

        foreach (IGrouping<(string StartedAs, StreamFetch Fetch), StreamClass> group in classes.GroupBy(static x => (x.StartedAs, x.Fetch)))
        {
            int n = group.Count();
            string number = n.ToString("N0", CultureInfo.InvariantCulture);

            parts.Add(group.Key switch
            {
                (StreamAggregateMap.NotRecorded, _) => number + " with no type",
                (_, StreamFetch.RebuiltFromEveryEvent) => number + (n == 1 ? " was" : " were") + " started as " + group.Key.StartedAs + ", rebuilt from every event on each fetch",
                (_, StreamFetch.KeptByProjection) => number + (n == 1 ? " was" : " were") + " started as " + group.Key.StartedAs + ", kept by a projection",
                _ => number + (n == 1 ? " was" : " were") + " started as " + group.Key.StartedAs + ", not an aggregate this process knows",
            });
        }

        int over = rows.Count(static x => x.Version > LongStreamsWithoutSnapshotCheck.LongStream);
        string overText = over == 0
            ? string.Empty
            : " " + over.ToString("N0", CultureInfo.InvariantCulture) + (over == 1 ? " is" : " are") + " over "
                + LongStreamsWithoutSnapshotCheck.LongStream.ToString("N0", CultureInfo.InvariantCulture) + " events.";

        return "The " + (rows.Count == 1 ? string.Empty : count + " ") + "longest live stream" + (rows.Count == 1 ? string.Empty : "s")
            + " in this database" + where + ": " + string.Join("; ", parts) + "." + overText;
    }
}

/// <summary>One stream the statement returned.</summary>
/// <param name="Id">Its id, as text.</param>
/// <param name="Type">The aggregate type alias it was started as, or <see langword="null" />.</param>
/// <param name="Version">How many events it holds.</param>
/// <param name="TenantId">Its tenant, when the statement selected it.</param>
/// <param name="Created">When it was started, when the table records it.</param>
internal sealed record LongStreamRow(string Id, string? Type, long Version, string? TenantId, DateTimeOffset? Created);

/// <summary>How the statement was shaped, for the notes and the table's columns.</summary>
/// <param name="Archived">Whether archived streams were left out (the column exists).</param>
/// <param name="TenantFilter">The tenant the read was narrowed to, or <see langword="null" />.</param>
/// <param name="TenantColumn">Whether the rows name their tenants.</param>
/// <param name="Conjoined">Whether the event store is conjoined-tenanted.</param>
/// <param name="NotShown">How many of the longest were of tenants this visitor may not see.</param>
/// <param name="Created">Whether the table records when a stream began.</param>
internal sealed record LongestStreamsShape(bool Archived, string? TenantFilter, bool TenantColumn, bool Conjoined, int NotShown, bool Created = true);

/// <summary>How a stream's aggregate is fetched.</summary>
internal enum StreamFetch
{
    /// <summary>Replayed from its first event on every fetch: nothing snapshots it.</summary>
    RebuiltFromEveryEvent,

    /// <summary>An inline or async projection keeps it.</summary>
    KeptByProjection,

    /// <summary>This process does not know the aggregate.</summary>
    Unknown,
}

/// <summary>What the process knows of one stream's type.</summary>
/// <param name="StartedAs">The aggregate's name in code, or the words that stand for it.</param>
/// <param name="Fetch">How it is fetched.</param>
/// <param name="LiveName">For a live-only aggregate: its name in code, for the remedy.</param>
internal sealed record StreamClass(string StartedAs, StreamFetch Fetch, string? LiveName);

/// <summary>
/// The aggregate types this process knows, by the alias Marten files their streams under (case ignored, as Marten matches them),
/// with which are live-only, which the host hides, and what a stream's stored type means.
/// </summary>
/// <remarks>
/// In memory throughout: <c>AggregateAliasFor</c> fills no storage and opens no connection. A delegate that throws for a type has not
/// said it may be shown, so the type is hidden (fail closed), and a type an alias is shared with that is hidden hides the alias.
/// </remarks>
internal sealed class StreamAggregateMap
{
    /// <summary>What a stream with no recorded type is started as.</summary>
    public const string NotRecorded = "not recorded";

    private readonly Dictionary<string, Group> byAlias = new(StringComparer.OrdinalIgnoreCase);

    private StreamAggregateMap(bool hides)
    {
        Hides = hides;
    }

    /// <summary>Whether <c>IsDocumentTypeVisible</c> is configured: whether anything may be hidden.</summary>
    public bool Hides { get; }

    /// <summary>
    /// The lower-case aliases the statement may keep, or <see langword="null" /> when nothing is hidden. While anything may be hidden,
    /// an alias this process cannot map is not among them.
    /// </summary>
    public IReadOnlyCollection<string>? ShownAliases => Hides
        ? [.. byAlias.Where(static x => !x.Value.Hidden).Select(static x => x.Key.ToLowerInvariant()).Order(StringComparer.Ordinal)]
        : null;

    /// <summary>Reads the map from the store's options.</summary>
    /// <param name="options">The store's options.</param>
    /// <param name="isVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>.</param>
    public static StreamAggregateMap From(IReadOnlyStoreOptions options, Func<Type, bool>? isVisible)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Events is not EventGraph graph || options is not StoreOptions concrete)
        {
            return Of([], isVisible);
        }

        HashSet<Type> live = [.. LongStreamsWithoutSnapshotCheck.LiveOnly(concrete).Select(static x => x.Type)];

        return Of(
            [.. concrete.Projections.AllAggregateTypes().Distinct().Select(type => (type, graph.AggregateAliasFor(type), live.Contains(type)))],
            isVisible);
    }

    /// <summary>The map over aggregate types already paired with the alias Marten files their streams under.</summary>
    /// <param name="aggregates">Each aggregate type, its alias, and whether nothing snapshots it.</param>
    /// <param name="isVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>.</param>
    internal static StreamAggregateMap Of(IEnumerable<(Type Type, string Alias, bool LiveOnly)> aggregates, Func<Type, bool>? isVisible)
    {
        ArgumentNullException.ThrowIfNull(aggregates);

        StreamAggregateMap map = new(isVisible is not null);

        foreach ((Type type, string alias, bool liveOnly) in aggregates)
        {
            if (!map.byAlias.TryGetValue(alias, out Group? group))
            {
                group = new Group();
                map.byAlias[alias] = group;
            }

            group.Types.Add(type);
            group.Hidden |= !Visible(isVisible, type);
            group.LiveOnly |= liveOnly;
        }

        return map;
    }

    /// <summary>What a stream's stored type means to this process.</summary>
    /// <param name="storedType">The stream's <c>type</c> column.</param>
    public StreamClass Classify(string? storedType)
    {
        if (string.IsNullOrWhiteSpace(storedType))
        {
            return new StreamClass(NotRecorded, StreamFetch.Unknown, null);
        }

        if (!byAlias.TryGetValue(storedType, out Group? group))
        {
            // The statement never returns an alias it cannot map while anything may be hidden; if one arrives, it is not named.
            return new StreamClass(Hides ? "a type this application does not show" : storedType, StreamFetch.Unknown, null);
        }

        if (group.Hidden)
        {
            return new StreamClass("a type this application does not show", StreamFetch.Unknown, null);
        }

        if (group.Types.Count > 1)
        {
            return new StreamClass(storedType + " (more than one aggregate type has this name)", StreamFetch.Unknown, null);
        }

        string name = group.Types[0].ShortNameInCode();

        return group.LiveOnly
            ? new StreamClass(name, StreamFetch.RebuiltFromEveryEvent, name)
            : new StreamClass(name, StreamFetch.KeptByProjection, null);
    }

    private static bool Visible(Func<Type, bool>? isVisible, Type type)
    {
        if (isVisible is null)
        {
            return true;
        }

        try
        {
            return isVisible(type);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class Group
    {
        public List<Type> Types { get; } = [];

        public bool Hidden { get; set; }

        public bool LiveOnly { get; set; }
    }
}
