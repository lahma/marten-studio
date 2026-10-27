using System.Text;
using System.Text.Json;

using JasperFx.Events.Daemon;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;

using MartenStudio.Services.Events;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The JSON keys <c>DeadLetterEvent</c>'s members are stored under, spelled the way the store's serializer
/// writes them.
/// </summary>
/// <param name="TenantId">The key of <c>DeadLetterEvent.TenantId</c>, the failing event's tenant.</param>
/// <param name="ProjectionName">The key of <c>DeadLetterEvent.ProjectionName</c>.</param>
/// <param name="ShardName">The key of <c>DeadLetterEvent.ShardName</c>.</param>
/// <param name="ExceptionType">The key of <c>DeadLetterEvent.ExceptionType</c>.</param>
/// <param name="Timestamp">The key of <c>DeadLetterEvent.Timestamp</c>, which the list is ordered by.</param>
/// <param name="ExceptionMessage">The key of <c>DeadLetterEvent.ExceptionMessage</c>, which the grouped view fingerprints.</param>
/// <param name="EventSequence">
/// The key of <c>DeadLetterEvent.EventSequence</c>, the failing event's sequence: what names the failing event, and what
/// the stepper and the dead-letter screen's link from it filter on (M1).
/// </param>
/// <remarks>
/// <c>Casing.Default</c> writes <c>ProjectionName</c>, <c>CamelCase</c> <c>projectionName</c> and
/// <c>SnakeCase</c> <c>projection_name</c>; Marten's own LINQ reads the member through the same spelling, which is
/// what the predicates below have to agree with. Each key is bound as a parameter and never becomes SQL text.
/// The names come from <see langword="nameof" />, so a JasperFx release that renames a member breaks the build
/// rather than silently filtering on a key nothing writes.
/// </remarks>
internal sealed record DeadLetterJsonKeys(
    string TenantId,
    string ProjectionName,
    string ShardName,
    string ExceptionType,
    string Timestamp,
    string ExceptionMessage,
    string EventSequence)
{
    /// <summary>The keys under the store's naming policy, or the CLR names when it has none.</summary>
    public static DeadLetterJsonKeys For(JsonNamingPolicy? policy) => new(
        Key(nameof(DeadLetterEvent.TenantId), policy),
        Key(nameof(DeadLetterEvent.ProjectionName), policy),
        Key(nameof(DeadLetterEvent.ShardName), policy),
        Key(nameof(DeadLetterEvent.ExceptionType), policy),
        Key(nameof(DeadLetterEvent.Timestamp), policy),
        Key(nameof(DeadLetterEvent.ExceptionMessage), policy),
        Key(nameof(DeadLetterEvent.EventSequence), policy));

    private static string Key(string member, JsonNamingPolicy? policy) => policy?.ConvertName(member) ?? member;
}

/// <summary>What one page of the dead-letter list asks for.</summary>
internal sealed record DeadLetterPageQuery
{
    /// <summary>Only dead letters whose own <c>TenantId</c> property is this; <see langword="null" /> for every tenant.</summary>
    public string? TenantId { get; init; }

    /// <summary>Only this projection's; <see langword="null" /> for every projection.</summary>
    public string? ProjectionName { get; init; }

    /// <summary>Only this shard's; <see langword="null" /> for every shard.</summary>
    public string? ShardName { get; init; }

    /// <summary>Only those whose exception type contains this, case-sensitively; <see langword="null" /> for any.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>
    /// Only those about one of these events, by sequence; <see langword="null" /> for any event. The stepper asks for its
    /// window's events, and the dead-letter screen's link from a step for one (M1).
    /// </summary>
    public IReadOnlyList<long>? EventSequences { get; init; }

    /// <summary>
    /// Only this group's letters (M3): <see cref="DeadLetterGroupQueries.AppendGroupPredicates" />, the grouped view's own
    /// rule; <see langword="null" /> for every group.
    /// </summary>
    public DeadLetterGroupKey? Group { get; init; }

    /// <summary>
    /// The events table <see cref="Group" />'s event type is looked up in, or <see langword="null" /> when the database has
    /// none - then only a group whose event type is missing matches.
    /// </summary>
    public DeadLetterEventSource? Events { get; init; }

    /// <summary>
    /// With <see cref="Group" />: how many of the scope's newest letters the group's letters are found among - the grouped
    /// view's own bound, so the list shows the letters the group counts and fingerprints no more (SEC-FIX). Never more than
    /// <see cref="DeadLetterGroupLimits.MaxLettersGrouped" />, whatever is asked.
    /// </summary>
    public int GroupWindow { get; init; } = DeadLetterGroupLimits.MaxLettersGrouped;

    /// <summary>How many to skip.</summary>
    public int Offset { get; init; }

    /// <summary>How many to read - the caller asks for one more than it shows, to know whether there is another page.</summary>
    public int Limit { get; init; } = 51;
}

/// <summary>
/// The dead-letter screen's list, read from <c>mt_doc_deadletterevent</c> with the studio's own SQL instead of a
/// Marten LINQ query.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not <c>session.Query&lt;DeadLetterEvent&gt;().ToListAsync()</c>.</b> Every Marten LINQ terminal opens
/// with <c>EnsureStorageExistsAsync</c> for the document type - read at <c>V9.39.0</c> and <c>V9.50.2</c>,
/// <c>MartenLinqQueryProvider.EnsureStorageExistsAsync</c>, identical at both - and under the database's own
/// <c>AutoCreate</c> that is Weasel's <c>ensureStorageExistsAsync</c> (<c>9.33.0</c>): it creates the schemas, calls
/// <c>AllObjects()</c> for post-processing (which on a store with a HiLo id applies <c>mt_hilo</c>, hard rule 14)
/// and runs the feature's migration, once per type per database per process. The list used to probe the table
/// first, which kept it from <em>creating</em> one; it could not keep it from the rest, because the probe answers
/// "the table exists" and the migration is about whether it matches.
/// </para>
/// <para>
/// <b>What it keeps of the LINQ query it replaces.</b> Marten registers <c>DeadLetterEvent</c>
/// <c>SingleTenanted()</c> unconditionally (<c>StoreOptions.ApplyConfiguration</c> at both tags), so the session's
/// tenant applied no filter and the tenant axis was a predicate over the body's own <c>TenantId</c> - kept, as
/// <c>data -&gt;&gt; @tenant_key = @tenant</c>, which is what <c>x.TenantId == tenant</c> translated to. Equality on the
/// projection and the shard likewise. <c>ExceptionType.Contains(x)</c> was Marten's escaped <c>LIKE '%x%'</c>
/// (<c>StringContainsFilter</c>, case-sensitive); <c>strpos(…) &gt; 0</c> matches the same rows and has no wildcard to
/// escape. The order is <c>Timestamp</c> descending - through a plain <c>::timestamptz</c> cast, which is exactly
/// the body of Marten's <c>mt_immutable_timestamptz</c> and needs no function Marten may not have installed - and
/// <c>d.id</c> breaks ties, which the LINQ query never did, so a page boundary between two failures in the same
/// instant is now stable. A soft-deleted table - <c>Policies.AllDocumentsSoftDeleted()</c> reaches <c>DeadLetterEvent</c>,
/// and so can a host's own <c>Schema.For&lt;DeadLetterEvent&gt;()</c> - keeps Marten's default <c>mt_deleted = false</c>
/// filter (<see cref="LiveRows" />), which the badge's count shares.
/// </para>
/// <para>
/// Columns: <c>id</c> (uuid) - the row's key, which is what Discard deletes by - and <c>data</c> (jsonb), read back
/// through <c>store.Options.Serializer()</c> by the caller (hard rule 10).
/// </para>
/// </remarks>
internal static class DeadLetterQueries
{
    /// <summary>The largest page this builder will emit, however many the caller asks for.</summary>
    public const int MaxLimit = 1_000;

    private const string Alias = DocumentTableInfo.SqlAlias;

    /// <summary>The alias Marten gives <c>DeadLetterEvent</c>'s table, <c>mt_doc_deadletterevent</c>.</summary>
    private const string MartenAlias = "deadletterevent";

    /// <summary>
    /// The dead-letter table as this store describes it, before the catalog has been asked whether it is there.
    /// </summary>
    /// <param name="options">The store's configuration.</param>
    /// <remarks>
    /// <para>
    /// The mapping, when Marten has one: <c>AllKnownDocumentTypes()</c> runs <c>BuildAllMappings()</c>, which
    /// materialises <c>DeadLetterEvent</c> exactly when the event store is active (#4303's gate, read at
    /// <c>V9.39.0</c>), so a host's own <c>Schema.For&lt;DeadLetterEvent&gt;()</c> - an alias, a schema, soft delete -
    /// is honoured. Never <c>FindOrResolveDocumentType</c>, which would materialise the mapping for a store whose
    /// event store is not active and put a dead-letter table into the host's next migration.
    /// </para>
    /// <para>
    /// Otherwise <see cref="MartensRegistration" />: the event store's schema and <c>mt_doc_deadletterevent</c>, which
    /// is where the table is whenever Marten has not been told otherwise.
    /// </para>
    /// <para>
    /// <b>Soft delete is the table's, not this description's.</b> The caller settles the result against the catalog
    /// with <c>WithPhysicalColumns</c>, which sets <c>SoftDeleteEnabled</c> from whether <c>mt_deleted</c> is there and
    /// adds its column - so a store whose <c>Policies.AllDocumentsSoftDeleted()</c> or own
    /// <c>Schema.For&lt;DeadLetterEvent&gt;().SoftDeleted()</c> made the table soft-deleted is filtered by
    /// <see cref="LiveRows" /> whether or not the mapping was ever materialised, and the fallback's starting point of
    /// "removed" decides nothing. A table that is not there is an empty list; it is never created.
    /// </para>
    /// </remarks>
    public static DocumentTableInfo TableFor(IReadOnlyStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (IDocumentType documentType in options.AllKnownDocumentTypes())
        {
            if (documentType.DocumentType == typeof(DeadLetterEvent))
            {
                return DocumentTableInfo.FromDocumentType(documentType);
            }
        }

        return MartensRegistration(options.Events.DatabaseSchemaName);
    }

    /// <summary>
    /// The dead-letter table as Marten registers it at both tags - <c>mt_doc_deadletterevent</c> in the event store's
    /// schema, single-tenanted, with a <c>uuid</c> key - for a store whose mapping was never materialised.
    /// </summary>
    /// <param name="eventSchema">The event store's schema.</param>
    /// <remarks>Its soft delete is settled by the catalog, as <see cref="TableFor" /> says.</remarks>
    public static DocumentTableInfo MartensRegistration(string eventSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSchema);

        return new DocumentTableInfo
        {
            Schema = eventSchema,
            Table = EventTableInfo.DeadLetterTable,
            Alias = MartenAlias,
            DocumentTypeName = nameof(DeadLetterEvent),
            IdColumnType = DocumentIdColumnType.Uuid,
            TenancyStyle = TenancyStyle.Single,
        };
    }

    /// <summary>
    /// The predicate that leaves soft-deleted dead letters out - Marten's own default for a soft-deleted type - or
    /// <see langword="null" /> when the table has no <c>mt_deleted</c>. One rule for the list and for the badge's count.
    /// </summary>
    /// <param name="table">The table, settled against the catalog.</param>
    /// <param name="alias">The alias the statement gives the table, or <see langword="null" /> for none.</param>
    public static string? LiveRows(DocumentTableInfo table, string? alias)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (!table.SoftDeleteEnabled || table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) is not { } deleted)
        {
            return null;
        }

        return (alias is null ? string.Empty : alias + ".") + SqlIdentifier.Quote(deleted) + " = false";
    }

    /// <summary>One page of dead letters, newest failure first.</summary>
    /// <param name="table">The dead-letter table, settled against the catalog with <c>WithPhysicalColumns</c>.</param>
    /// <param name="keys">The JSON keys, under the store's casing.</param>
    /// <param name="query">The filters and the page.</param>
    /// <param name="commandTimeout">A bound on how long the read may take, when the caller sets one.</param>
    /// <remarks>
    /// <para>
    /// Every filter is a nullable parameter, so one statement - and one plan - serves every combination, the same
    /// rule as <see cref="EventQueryBuilder" />. No value and no key is ever interpolated: the table name and the
    /// soft-delete column are the only identifiers, and both go through <see cref="SqlIdentifier" />.
    /// </para>
    /// <para>
    /// <b>A group's letters are found in a window</b> (SEC-FIX): with <see cref="DeadLetterPageQuery.Group" />, the table is
    /// read as the scope's newest <see cref="DeadLetterPageQuery.GroupWindow" /> live letters - the grouped view's own read,
    /// newest first - and the group's predicates, the fingerprint's <c>regexp_replace</c> chain among them, run over those
    /// only. Without the window a <c>?group=</c> written by hand ran the chain over every letter whose exact parts it matched.
    /// Whether the window cut is <see cref="DeadLetterGroupQueries.BuildLetterWindowCount" />'s.
    /// </para>
    /// </remarks>
    public static NpgsqlCommand BuildPage(
        DocumentTableInfo table,
        DeadLetterJsonKeys keys,
        DeadLetterPageQuery query,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(query);

        var command = new NpgsqlCommand();

        try
        {
            string data = Column(DocumentTableInfo.DataColumn);
            string id = Column(DocumentTableInfo.IdColumn);

            // Bound here, once: a group's window below and the clauses after it name the same values.
            string tenant = "(" + Nullable(command, "tenant", query.TenantId) + " is null or "
                + data + " ->> " + Key(command, "tenant_key", keys.TenantId) + " = @tenant)";
            string newestFirst = "(" + data + " ->> " + Key(command, "timestamp_key", keys.Timestamp) + ")::timestamptz desc, " + id + " desc";
            string? live = LiveRows(table, Alias);

            var sql = new StringBuilder();

            sql.Append("select ").Append(id).Append(", ").Append(data).Append('\n');

            if (query.Group is null)
            {
                sql.Append("from ").Append(table.QualifiedName).Append(" as " + Alias + "\n");
            }
            else
            {
                // SEC-FIX: a group's letters are looked for only among the scope's newest letters the grouped view groups,
                // newest first as it reads them - so the list shows the letters the group counts, and the fingerprint's
                // regexp_replace chain below runs over that many at most, whatever ?group= a visitor writes by hand. The
                // window holds live letters only, and its rows carry no mt_deleted, so the soft-delete filter is applied here.
                sql.Append("from (select ").Append(id).Append(", ").Append(data).Append('\n');
                sql.Append("      from ").Append(table.QualifiedName).Append(" as " + Alias + "\n");
                sql.Append("      where ").Append(tenant).Append('\n');

                if (live is not null)
                {
                    sql.Append("        and ").Append(live).Append('\n');
                    live = null;
                }

                sql.Append("      order by ").Append(newestFirst).Append('\n');
                sql.Append("      limit ")
                    .Append(Value(command, "group_window", NpgsqlDbType.Integer, Math.Clamp(query.GroupWindow, 1, DeadLetterGroupLimits.MaxLettersGrouped)))
                    .Append(") as " + Alias + "\n");
            }

            // A group's letters: each letter's event looked up for its type, kept whether or not it is found (a group
            // whose event is gone has no type). The lookup reads @tenant and @tenant_key, bound above.
            if (query is { Group: not null, Events: { } events })
            {
                sql.Append(DeadLetterGroupQueries.EventLookup(command, keys, events, inner: false));
            }

            sql.Append("where ").Append(tenant).Append('\n');

            sql.Append("  and (").Append(Nullable(command, "projection", query.ProjectionName)).Append(" is null or ")
                .Append(data).Append(" ->> ").Append(Key(command, "projection_key", keys.ProjectionName)).Append(" = @projection)\n");

            sql.Append("  and (").Append(Nullable(command, "shard", query.ShardName)).Append(" is null or ")
                .Append(data).Append(" ->> ").Append(Key(command, "shard_key", keys.ShardName)).Append(" = @shard)\n");

            sql.Append("  and (").Append(Nullable(command, "exception", query.ExceptionType)).Append(" is null or ")
                .Append("pg_catalog.strpos(").Append(data).Append(" ->> ")
                .Append(Key(command, "exception_key", keys.ExceptionType)).Append(", @exception) > 0)\n");

            // The sequence as the body holds it, compared as text: the daemon writes a JSON number, which ->> prints the way
            // a long is printed - and a cast would fail the whole list on one body somebody wrote by hand. A group's event
            // lookup above has bound the same key already, so it is reused: one value per name in one statement.
            sql.Append("  and (").Append(Sequences(command, query.EventSequences)).Append(" is null or ")
                .Append(data).Append(" ->> ")
                .Append(DeadLetterGroupQueries.Ensure(command, "sequence_key", NpgsqlDbType.Text, keys.EventSequence))
                .Append(" = any(@sequences))\n");

            if (live is not null)
            {
                sql.Append("  and ").Append(live).Append('\n');
            }

            if (query.Group is { } group)
            {
                DeadLetterGroupQueries.AppendGroupPredicates(sql, command, keys, group, eventLookedUp: query.Events is not null);
            }

            sql.Append("order by ").Append(newestFirst).Append('\n');

            sql.Append("limit ").Append(Value(command, "limit", NpgsqlDbType.Integer, Math.Clamp(query.Limit, 1, MaxLimit)));
            sql.Append(" offset ").Append(Value(command, "offset", NpgsqlDbType.Integer, Math.Max(0, query.Offset)));

            command.CommandText = sql.ToString();

            if (commandTimeout is { } timeout)
            {
                command.CommandTimeout = Math.Max((int) Math.Ceiling(timeout.TotalSeconds), 1);
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private static string Column(string name) => Alias + "." + SqlIdentifier.Quote(name);

    private static string Key(NpgsqlCommand command, string name, string key) =>
        Value(command, name, NpgsqlDbType.Text, key);

    private static string Value(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        return "@" + name;
    }

    /// <summary>A parameter that may be null, typed, because <c>@p is null</c> against an untyped one is a question Postgres cannot answer.</summary>
    private static string Nullable(NpgsqlCommand command, string name, string? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Varchar) { Value = (object?) value ?? DBNull.Value });
        return "@" + name;
    }

    /// <summary>The event sequences as a typed <c>text[]</c> parameter, null for none.</summary>
    private static string Sequences(NpgsqlCommand command, IReadOnlyList<long>? sequences)
    {
        object value = sequences is null
            ? DBNull.Value
            : sequences.Select(static x => x.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        command.Parameters.Add(new NpgsqlParameter("sequences", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = value });
        return "@sequences";
    }
}
