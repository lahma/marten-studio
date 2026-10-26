using System.Globalization;
using System.Text;

using MartenStudio.Services.Events;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The events table a dead-letter statement looks a letter's event up in, and how a letter names its event.
/// </summary>
/// <param name="Table">The event tables, as the catalog has them.</param>
/// <param name="PerTenantSequences">
/// Whether the store numbers events per tenant (<c>UseTenantPartitionedEvents</c>): then a letter's <c>EventSequence</c>
/// is a number in its own <c>TenantId</c>'s sequence, and the event is <c>(seq_id, tenant_id)</c>, never <c>seq_id</c> alone.
/// </param>
internal sealed record DeadLetterEventSource(EventTableInfo Table, bool PerTenantSequences);

/// <summary>
/// The grouped dead-letter view's statements (M3): the letters the view groups, and one group's letters again - for the
/// list's filter, the skip dialog's count and the skip itself - <c>mt_doc_deadletterevent</c> read with the studio's own
/// SQL, as the list is (<see cref="DeadLetterQueries" />).
/// </summary>
/// <remarks>
/// <para>
/// <b>Grouped in C#, over a bounded read; a group's members found in SQL.</b> Measured on 10,000 dead letters (PostgreSQL
/// 17, M3's report): an aggregate that fingerprints every message in SQL - ten nested <c>regexp_replace</c> passes a row -
/// spent about 950 ms of the database's CPU per read, nearly all of it in those passes, where reading the five fields and
/// each letter's event type (<see cref="BuildGroupRows" />, an index probe per letter) and fingerprinting in C# took about
/// 180 ms end to end. So the view reads columns - never a document - newest first, at most
/// <see cref="DeadLetterGroupLimits.MaxLettersGrouped" /> letters, under the command timeout, once per interval across
/// circuits (D10), and groups them in the web process. What a group <em>is</em> is then found again in SQL
/// (<see cref="AppendGroupPredicates" />): the exact parts of the key first, and the fingerprint as nested
/// <c>regexp_replace</c> calls over <see cref="DeadLetterFingerprint.Rules" /> - every pattern and replacement a
/// parameter - only over the letters those leave, and only when a person opens a group or skips one.
/// </para>
/// <para>
/// <b>A letter's event is looked up in the scope.</b> <c>DeadLetterEvent</c> does not record the event's type, so each
/// letter's event is found in <c>mt_events</c> by a <c>lateral</c> lookup on <c>seq_id</c> - an index probe per letter,
/// never a scan of the events table - narrowed exactly as <c>EventQueryBuilder.BuildEventBySequence</c> narrows the
/// expansion: to the scope's tenant on a table with <c>tenant_id</c>, and under per-tenant sequences to the letter's own
/// tenant too. An event the scope cannot see lends a letter no type and is not counted as one of the group's events.
/// </para>
/// <para>
/// <b>Every key is a parameter</b>, under the store's naming policy (<see cref="DeadLetterJsonKeys" />), and every value
/// is a parameter. The identifiers are the two tables' names and their columns, through <see cref="SqlIdentifier" />.
/// </para>
/// </remarks>
internal static class DeadLetterGroupQueries
{
    private const string Alias = DocumentTableInfo.SqlAlias;

    /// <summary>The alias of a letter's event lookup.</summary>
    private const string EventAlias = "ev";

    /// <summary>The alias of the events table inside the lookup.</summary>
    private const string EventsTableAlias = "e";

    /// <summary>
    /// The letters the grouped view groups, newest first: what each letter's key is made of, and its event's type and
    /// whether that event is skipped - columns out of the body, never the body.
    /// </summary>
    /// <param name="table">The dead-letter table, settled against the catalog.</param>
    /// <param name="keys">The JSON keys under the store's casing.</param>
    /// <param name="events">The events table, or <see langword="null" /> when the database has none.</param>
    /// <param name="tenantId">The scope's tenant, or <see langword="null" /> for every tenant.</param>
    /// <param name="limit">
    /// The most letters to read: the caller asks for one more than it groups, to know that there were more. Never more than
    /// <see cref="DeadLetterGroupLimits.MaxLettersGrouped" /> and one.
    /// </param>
    /// <param name="commandTimeout">A bound on the read, when the caller sets one.</param>
    /// <remarks>
    /// Columns: <c>projection</c>, <c>shard</c>, <c>exception_type</c>, <c>tenant</c> (text), <c>at</c> (timestamptz),
    /// <c>message</c> (the first <see cref="DeadLetterFingerprint.MaxInputLength" /> characters - what the fingerprint
    /// reads), <c>event_type</c> (text), <c>is_skipped</c> (boolean, null where the table has no <c>is_skipped</c>) and
    /// <c>event_key</c> (text: the event's identity, null when the scope cannot see it) - each nullable. Ordered as the list
    /// is, by the body's <c>Timestamp</c> and then the row key, so a read cut at its bound groups the newest letters.
    /// </remarks>
    public static NpgsqlCommand BuildGroupRows(
        DocumentTableInfo table,
        DeadLetterJsonKeys keys,
        DeadLetterEventSource? events,
        string? tenantId,
        int limit,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keys);

        var command = new NpgsqlCommand();

        try
        {
            string data = Data;
            string tenant = Tenant(command, keys, tenantId);
            string timestamp = "(" + data + " ->> " + Key(command, "timestamp_key", keys.Timestamp) + ")::timestamptz";

            var sql = new StringBuilder();

            sql.Append("select ").Append(data).Append(" ->> ").Append(Key(command, "projection_key", keys.ProjectionName)).Append(" as projection,\n");
            sql.Append("       ").Append(data).Append(" ->> ").Append(Key(command, "shard_key", keys.ShardName)).Append(" as shard,\n");
            sql.Append("       ").Append(data).Append(" ->> ").Append(Key(command, "exception_key", keys.ExceptionType)).Append(" as exception_type,\n");
            sql.Append("       ").Append(data).Append(" ->> @tenant_key as tenant,\n");
            sql.Append("       ").Append(timestamp).Append(" as at,\n");
            sql.Append("       pg_catalog.left(").Append(Message(command, keys)).Append(", ")
                .Append(Ensure(command, "fp_length", NpgsqlDbType.Integer, DeadLetterFingerprint.MaxInputLength)).Append(") as message,\n");

            if (events is null)
            {
                sql.Append("       null::text as event_type, null::boolean as is_skipped, null::text as event_key\n");
                sql.Append("from ").Append(table.QualifiedName).Append(" as " + Alias + "\n");
            }
            else
            {
                sql.Append("       ").Append(EventAlias).Append(".event_type, ").Append(EventAlias).Append(".is_skipped, ")
                    .Append(EventAlias).Append(".event_key\n");
                sql.Append("from ").Append(table.QualifiedName).Append(" as " + Alias + "\n");
                sql.Append(EventLookup(command, keys, events, inner: false));
            }

            sql.Append("where ").Append(tenant).Append('\n');

            if (DeadLetterQueries.LiveRows(table, Alias) is { } live)
            {
                sql.Append("  and ").Append(live).Append('\n');
            }

            sql.Append("order by ").Append(timestamp).Append(" desc, ")
                .Append(Alias + "." + SqlIdentifier.Quote(DocumentTableInfo.IdColumn)).Append(" desc\n");
            sql.Append("limit ").Append(Ensure(
                command, "limit", NpgsqlDbType.Integer, Math.Clamp(limit, 1, DeadLetterGroupLimits.MaxLettersGrouped + 1)));

            command.CommandText = sql.ToString();
            ApplyTimeout(command, commandTimeout);

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>How many of one group's events are not skipped yet, and how many are.</summary>
    /// <param name="table">The dead-letter table, settled against the catalog.</param>
    /// <param name="keys">The JSON keys under the store's casing.</param>
    /// <param name="events">The events table; it must record skips (<c>is_skipped</c>).</param>
    /// <param name="tenantId">The scope's tenant, or <see langword="null" /> for every tenant.</param>
    /// <param name="group">The group.</param>
    /// <param name="commandTimeout">A bound, when the caller sets one.</param>
    /// <remarks>Columns: <c>not_skipped</c>, <c>skipped</c> (bigint). An event counts once however many letters name it.</remarks>
    public static NpgsqlCommand BuildGroupEventCounts(
        DocumentTableInfo table,
        DeadLetterJsonKeys keys,
        DeadLetterEventSource events,
        string? tenantId,
        DeadLetterGroupKey group,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        RequireSkips(events);

        return BuildGroupEvents(
            table,
            keys,
            events,
            tenantId,
            group,
            "select count(distinct " + EventAlias + ".event_key) filter (where not " + EventAlias + ".is_skipped) as not_skipped,\n"
            + "       count(distinct " + EventAlias + ".event_key) filter (where " + EventAlias + ".is_skipped) as skipped\n",
            commandTimeout);
    }

    /// <summary>
    /// The sequences of one group's events that are not skipped yet, lowest first, at most <paramref name="limit" /> - the
    /// events a bulk skip hands to <c>MarkEventsAsSkipped</c>, after proving each one is in scope.
    /// </summary>
    /// <param name="table">The dead-letter table, settled against the catalog.</param>
    /// <param name="keys">The JSON keys under the store's casing.</param>
    /// <param name="events">The events table; it must record skips (<c>is_skipped</c>).</param>
    /// <param name="tenantId">
    /// The scope's tenant. Under per-tenant sequences the caller has one - a sequence names an event in every tenant - and
    /// the statement refuses to be built without it.
    /// </param>
    /// <param name="group">The group.</param>
    /// <param name="limit">The most sequences to read: the cap and one more, so "there are more" needs no count.</param>
    /// <param name="commandTimeout">A bound, when the caller sets one.</param>
    /// <remarks>
    /// Lowest first because that is the order the daemon reaches them in. One column, <c>seq_id</c> (bigint); a sequence
    /// appears once however many letters name it.
    /// </remarks>
    public static NpgsqlCommand BuildGroupSequencesToSkip(
        DocumentTableInfo table,
        DeadLetterJsonKeys keys,
        DeadLetterEventSource events,
        string? tenantId,
        DeadLetterGroupKey group,
        int limit,
        TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        RequireSkips(events);

        if (events.PerTenantSequences && tenantId is null)
        {
            throw new ArgumentException(
                "Under per-tenant sequences a sequence names an event in every tenant, so the events to skip are read in one tenant.",
                nameof(tenantId));
        }

        var command = BuildGroupEvents(
            table,
            keys,
            events,
            tenantId,
            group,
            "select distinct " + EventAlias + ".seq_id\n",
            commandTimeout);

        try
        {
            command.CommandText += "  and not " + EventAlias + ".is_skipped\n"
                + "order by " + EventAlias + ".seq_id\n"
                + "limit " + Ensure(command, "limit", NpgsqlDbType.Integer, Math.Clamp(limit, 1, DeadLetterGroupLimits.MaxEventsPerSkip + 1));

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The fingerprint of <paramref name="source" />, as SQL: <see cref="DeadLetterFingerprint.Rules" /> applied in order by
    /// nested <c>regexp_replace(…, 'g')</c> calls over its first <see cref="DeadLetterFingerprint.MaxInputLength" />
    /// characters, then trimmed of spaces - what <see cref="DeadLetterFingerprint.Of" /> does in C#.
    /// </summary>
    /// <param name="command">The command the patterns and replacements are bound to, each once.</param>
    /// <param name="source">A text expression - <c>d."data" -&gt;&gt; @message_key</c>.</param>
    public static string FingerprintOf(NpgsqlCommand command, string source)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        string expression = "pg_catalog.left(" + source + ", "
            + Ensure(command, "fp_length", NpgsqlDbType.Integer, DeadLetterFingerprint.MaxInputLength) + ")";

        for (int index = 0; index < DeadLetterFingerprint.Rules.Count; index++)
        {
            DeadLetterFingerprintRule rule = DeadLetterFingerprint.Rules[index];
            string suffix = index.ToString(CultureInfo.InvariantCulture);

            expression = "pg_catalog.regexp_replace(" + expression + ", "
                + Ensure(command, "fp_pattern_" + suffix, NpgsqlDbType.Text, rule.Pattern) + ", "
                + Ensure(command, "fp_replacement_" + suffix, NpgsqlDbType.Text, rule.PostgresReplacement) + ", 'g')";
        }

        return "pg_catalog.btrim(" + expression + ", ' ')";
    }

    /// <summary>
    /// The predicates that hold a statement over the dead-letter table to one group: projection, shard and exception type
    /// from the body, the fingerprint of its message, and its event's type from the lookup - each <c>is not distinct
    /// from</c>, because a missing part is a value of the key. One rule for the list's filter, the counts and the skip.
    /// </summary>
    /// <param name="sql">Where the predicates go, each on its own line, each starting <c>  and</c>.</param>
    /// <param name="command">The command; the keys are added if they are not there yet.</param>
    /// <param name="keys">The JSON keys under the store's casing.</param>
    /// <param name="group">The group.</param>
    /// <param name="eventLookedUp">
    /// Whether the statement looks each letter's event up (<see cref="EventLookup" />). Without it, only a group whose
    /// event type is missing can match.
    /// </param>
    public static void AppendGroupPredicates(
        StringBuilder sql,
        NpgsqlCommand command,
        DeadLetterJsonKeys keys,
        DeadLetterGroupKey group,
        bool eventLookedUp)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(group);

        string data = Data;

        sql.Append("  and ").Append(data).Append(" ->> ").Append(Key(command, "projection_key", keys.ProjectionName))
            .Append(" is not distinct from ").Append(Nullable(command, "group_projection", group.ProjectionName)).Append('\n');
        sql.Append("  and ").Append(data).Append(" ->> ").Append(Key(command, "shard_key", keys.ShardName))
            .Append(" is not distinct from ").Append(Nullable(command, "group_shard", group.ShardName)).Append('\n');
        sql.Append("  and ").Append(data).Append(" ->> ").Append(Key(command, "exception_key", keys.ExceptionType))
            .Append(" is not distinct from ").Append(Nullable(command, "group_exception", group.ExceptionType)).Append('\n');
        sql.Append("  and ").Append(FingerprintOf(command, Message(command, keys)))
            .Append(" is not distinct from ").Append(Nullable(command, "group_fingerprint", group.Fingerprint)).Append('\n');

        string eventType = Nullable(command, "group_event_type", group.EventType);
        sql.Append(eventLookedUp
            ? "  and " + EventAlias + ".event_type is not distinct from " + eventType + "\n"
            : "  and " + eventType + " is null\n");
    }

    /// <summary>
    /// The <c>lateral</c> lookup of each letter's event: <c>event_type</c>, <c>is_skipped</c> (null where the table has
    /// none), <c>seq_id</c> and <c>event_key</c> - the event's identity as text, <c>seq_id</c>, and under per-tenant
    /// sequences <c>seq_id/tenant_id</c>.
    /// </summary>
    /// <param name="command">The command; it must carry <c>@tenant</c> and <c>@tenant_key</c> by the time it runs.</param>
    /// <param name="keys">The JSON keys under the store's casing.</param>
    /// <param name="events">The events table.</param>
    /// <param name="inner">
    /// <see langword="true" /> for <c>join lateral</c>, which keeps only letters whose event the scope can see;
    /// <see langword="false" /> for <c>left join lateral</c>, which keeps every letter.
    /// </param>
    /// <remarks>
    /// <para>
    /// The sequence is read only when the body holds a whole number that fits a <c>bigint</c>, so a body written by
    /// something other than the daemon is a letter with no event rather than a statement that fails for everyone.
    /// </para>
    /// <para>
    /// The tenant clause is <c>EventQueryBuilder.BuildEventBySequence</c>'s, so a letter's event is the event the
    /// expansion would show this scope. Under per-tenant sequences the letter's own <c>TenantId</c> is added, because there
    /// its <c>EventSequence</c> is a number in that tenant's sequence.
    /// </para>
    /// </remarks>
    public static string EventLookup(NpgsqlCommand command, DeadLetterJsonKeys keys, DeadLetterEventSource events, bool inner)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(events);

        EventTableInfo table = events.Table;
        string sequenceKey = Key(command, "sequence_key", keys.EventSequence);
        string sequence = "case when " + Data + " ->> " + sequenceKey + " ~ '^[0-9]{1,18}$' then (" + Data + " ->> "
            + sequenceKey + ")::bigint end";

        string seqId = EventColumn("seq_id");
        string tenantId = EventColumn("tenant_id");
        bool perTenant = events.PerTenantSequences && table.HasTenantId;

        var sql = new StringBuilder();
        sql.Append(inner ? "join lateral (" : "left join lateral (");
        sql.Append("select ").Append(EventColumn("type")).Append(" as event_type, ")
            .Append(table.HasIsSkipped ? EventColumn("is_skipped") : "null::boolean").Append(" as is_skipped, ")
            .Append(seqId).Append(" as seq_id, ")
            .Append(perTenant ? seqId + "::text || '/' || " + tenantId : seqId + "::text").Append(" as event_key\n");
        sql.Append("        from ").Append(table.QualifiedEvents).Append(" as " + EventsTableAlias + "\n");
        sql.Append("        where ").Append(seqId).Append(" = ").Append(sequence).Append('\n');

        if (perTenant)
        {
            sql.Append("          and ").Append(tenantId).Append(" = ").Append(Data).Append(" ->> @tenant_key\n");
        }

        if (table.HasTenantId)
        {
            sql.Append("          and (@tenant is null or ").Append(tenantId).Append(" = @tenant)\n");
        }

        sql.Append("        limit 1) as ").Append(EventAlias).Append(" on true\n");

        return sql.ToString();
    }

    /// <summary>One group's events, looked up in the scope: the select list given, then <c>from</c>, the lookup and the predicates.</summary>
    private static NpgsqlCommand BuildGroupEvents(
        DocumentTableInfo table,
        DeadLetterJsonKeys keys,
        DeadLetterEventSource events,
        string? tenantId,
        DeadLetterGroupKey group,
        string select,
        TimeSpan? commandTimeout)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(group);

        var command = new NpgsqlCommand();

        try
        {
            string tenant = Tenant(command, keys, tenantId);

            var sql = new StringBuilder(select);
            sql.Append("from ").Append(table.QualifiedName).Append(" as " + Alias + "\n");
            sql.Append(EventLookup(command, keys, events, inner: true));
            sql.Append("where ").Append(tenant).Append('\n');

            if (DeadLetterQueries.LiveRows(table, Alias) is { } live)
            {
                sql.Append("  and ").Append(live).Append('\n');
            }

            AppendGroupPredicates(sql, command, keys, group, eventLookedUp: true);

            command.CommandText = sql.ToString();
            ApplyTimeout(command, commandTimeout);

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    private static void RequireSkips(DeadLetterEventSource events)
    {
        if (!events.Table.HasIsSkipped)
        {
            throw new ArgumentException("The events table records no skips (is_skipped), so no event of it can be skipped.", nameof(events));
        }
    }

    private static string Data => Alias + "." + SqlIdentifier.Quote(DocumentTableInfo.DataColumn);

    private static string EventColumn(string name) => EventsTableAlias + "." + SqlIdentifier.Quote(name);

    /// <summary>The scope's tenant clause over the body's own <c>TenantId</c>, the list's (hard rule 15).</summary>
    private static string Tenant(NpgsqlCommand command, DeadLetterJsonKeys keys, string? tenantId) =>
        "(" + Nullable(command, "tenant", tenantId) + " is null or " + Data + " ->> " + Key(command, "tenant_key", keys.TenantId) + " = @tenant)";

    private static string Message(NpgsqlCommand command, DeadLetterJsonKeys keys) =>
        Data + " ->> " + Key(command, "message_key", keys.ExceptionMessage);

    private static string Key(NpgsqlCommand command, string name, string key) => Ensure(command, name, NpgsqlDbType.Text, key);

    /// <summary>A parameter, added the first time it is named and reused after: one value per name in one statement.</summary>
    internal static string Ensure(NpgsqlCommand command, string name, NpgsqlDbType type, object value)
    {
        if (!command.Parameters.Contains(name))
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
        }

        return "@" + name;
    }

    /// <summary>A parameter that may be null, typed, because <c>@p is null</c> against an untyped one is a question Postgres cannot answer.</summary>
    private static string Nullable(NpgsqlCommand command, string name, string? value) =>
        Ensure(command, name, NpgsqlDbType.Varchar, (object?) value ?? DBNull.Value);

    private static void ApplyTimeout(NpgsqlCommand command, TimeSpan? commandTimeout)
    {
        if (commandTimeout is { } timeout)
        {
            command.CommandTimeout = Math.Max((int) Math.Ceiling(timeout.TotalSeconds), 1);
        }
    }
}
