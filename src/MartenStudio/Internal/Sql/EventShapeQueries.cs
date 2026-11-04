using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Npgsql;

using NpgsqlTypes;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The statements of the events' shape check (D-SHAPE S2, S3 and S4): a size probe, the keys and stored sizes of a bounded sample of
/// one event type, and the chosen rows by key in Marten's own select list. Every identifier is quoted through
/// <see cref="SqlIdentifier" />; every value is a parameter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every ordering is an index order, at every tenancy.</b> <c>mt_events</c>'s primary key is <c>seq_id</c>, or
/// <c>(seq_id, tenant_id)</c> under <c>UseTenantPartitionedEvents</c> (<c>EventsTable.cs</c>, Marten <c>V9.57.0</c>), and
/// <c>EnableEventTypeIndex</c> adds <c>(type, seq_id)</c>. So <c>order by seq_id</c> is the primary key's order, or the type index's
/// for one type, in all of them: a single-tenanted table, a conjoined one with or without a tenant, and a tenant-partitioned one
/// (a tenant prunes to its partition; none is an ordered append of every partition's primary key). It is never <c>order by random()</c>,
/// <c>tablesample</c> or a sort on a column nothing indexes (D8). What an index order does not do is find the rows of a rare type
/// quickly when nothing indexes <c>type</c>: that walk is bounded by <c>statement_timeout</c>, and the caller reads the oldest and newest
/// events only and says so.
/// </para>
/// </remarks>
internal static partial class EventShapeQueries
{
    /// <summary>The part a key came from: the oldest events.</summary>
    public const int PartLowest = 1;

    /// <summary>The part a key came from: the newest events.</summary>
    public const int PartHighest = 2;

    /// <summary>The part a key came from: a probe between the two edges.</summary>
    public const int PartSpread = 3;

    private const string Alias = EventTableInfo.EventsAlias;

    /// <summary>What the scope leaves out of an event check: the facts the statements are built from.</summary>
    /// <param name="TenantId">The scope's tenant, used only when the table has <c>tenant_id</c>.</param>
    /// <param name="SkippingEnabled">Whether <c>EnableEventSkippingInProjectionsOrSubscriptions</c> is on: then skipped events are left out.</param>
    internal sealed record EventScope(string? TenantId, bool SkippingEnabled);

    /// <summary>
    /// <c>select count(*) from (select 1 … limit @probe)</c>: at most <paramref name="probe" /> index entries, to tell an event type
    /// small enough to read whole from one that has to be sampled.
    /// </summary>
    public static NpgsqlCommand BuildSizeProbe(EventTableInfo table, string eventType, EventScope scope, int probe, int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(scope);

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string predicates = Scope(table, eventType, scope, parameters);

            command.CommandText =
                "select count(*)\n"
                + "from (\n"
                + "    select 1\n"
                + "    from " + table.QualifiedEvents + " as " + Alias + "\n"
                + "    where true\n"
                + predicates
                + "    limit " + parameters.Add(NpgsqlDbType.Integer, probe, "probe") + "\n"
                + ") as x";

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>The keys of an event type that fits the sample: all of them, oldest first, with each one's stored size.</summary>
    public static NpgsqlCommand BuildCompleteKeys(EventTableInfo table, string eventType, EventScope scope, int sample, int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(scope);

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string predicates = Scope(table, eventType, scope, parameters);

            command.CommandText =
                "select " + Keys(table, 0) + "\n"
                + "from " + table.QualifiedEvents + " as " + Alias + "\n"
                + "where true\n"
                + predicates
                + "order by " + Alias + ".\"seq_id\"\n"
                + "limit " + parameters.Add(NpgsqlDbType.Integer, sample, "sample");

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>The oldest and the newest <paramref name="edge" /> events of the type, in one statement of two <c>union all</c> branches.</summary>
    public static NpgsqlCommand BuildEdgeKeys(EventTableInfo table, string eventType, EventScope scope, int edge, int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(scope);

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string predicates = Scope(table, eventType, scope, parameters);
            string from = "from " + table.QualifiedEvents + " as " + Alias + "\nwhere true\n" + predicates;
            string edgeParameter = parameters.Add(NpgsqlDbType.Integer, edge, "edge");

            command.CommandText =
                "(select " + Keys(table, PartLowest) + "\n" + from
                + "order by " + Alias + ".\"seq_id\" asc\nlimit " + edgeParameter + ")\n"
                + "union all\n"
                + "(select " + Keys(table, PartHighest) + "\n" + from
                + "order by " + Alias + ".\"seq_id\" desc\nlimit " + edgeParameter + ")";

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One probe per element of <paramref name="probes" />: the first event of the type at or after that sequence. Only meaningful with
    /// an index leading with <c>type</c>, where each probe is one index descent.
    /// </summary>
    public static NpgsqlCommand BuildSpreadKeys(EventTableInfo table, string eventType, EventScope scope, long[] probes, int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(probes);

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string predicates = Scope(table, eventType, scope, parameters);

            command.CommandText =
                "select s.seq, s.tenant_id, s.stored_bytes, s.part\n"
                + "from unnest(" + parameters.Add(NpgsqlDbType.Array | NpgsqlDbType.Bigint, probes, "probes") + ") as p(k)\n"
                + "cross join lateral (\n"
                + "    select " + Keys(table, PartSpread) + "\n"
                + "    from " + table.QualifiedEvents + " as " + Alias + "\n"
                + "    where true\n"
                + predicates
                + "      and " + Alias + ".\"seq_id\" >= p.k\n"
                + "    order by " + Alias + ".\"seq_id\"\n"
                + "    limit 1\n"
                + ") as s";

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The chosen rows by key: Marten's own select list first, unaltered and in its order - its resolver reads by ordinal - and then one
    /// trailing column of the studio's, the event's <c>data</c> as text, which the shape diff needs and the resolver does not give back.
    /// </summary>
    /// <param name="table">The event tables.</param>
    /// <param name="eventType">The event type name as stored.</param>
    /// <param name="scope">The scope.</param>
    /// <param name="selectFields">Marten's <c>IEventStorage.SelectFields()</c>.</param>
    /// <param name="perTenantSequences">Whether <c>seq_id</c> is per tenant (<c>UseTenantPartitionedEvents</c>): then a key is (tenant, sequence).</param>
    /// <param name="sequences">The sequences phase 1 read.</param>
    /// <param name="tenants">Each sequence's tenant, parallel to <paramref name="sequences" />.</param>
    /// <param name="maxItemBytes">The largest event text read: a row over it is not returned, and the caller counts it as too large.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    /// <exception cref="InvalidOperationException">A select field is not a plain column name the builder can quote.</exception>
    public static NpgsqlCommand BuildRows(
        EventTableInfo table,
        string eventType,
        EventScope scope,
        IReadOnlyList<string> selectFields,
        bool perTenantSequences,
        IReadOnlyList<long> sequences,
        IReadOnlyList<string?> tenants,
        int maxItemBytes,
        int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(selectFields);
        ArgumentNullException.ThrowIfNull(sequences);
        ArgumentNullException.ThrowIfNull(tenants);

        if (selectFields.Count == 0)
        {
            throw new InvalidOperationException("Marten's event selector named no columns to read.");
        }

        NpgsqlCommand command = New(commandTimeoutSeconds);

        try
        {
            ParameterBuilder parameters = new(command);
            string predicates = Scope(table, eventType, scope, parameters);

            StringBuilder sql = new("select ");

            foreach (string field in selectFields)
            {
                // The same rule as the replay's: Marten builds the list from EventsTable column names, and anything else is a select
                // list the studio cannot reproduce by quoting - and handing the resolver the wrong ordinals reads the wrong columns.
                if (!PlainColumn().IsMatch(field))
                {
                    throw new InvalidOperationException(
                        $"Marten's event selector reads '{field}', which is not a plain column name, so the studio cannot read events the way Marten would.");
                }

                sql.Append(Alias + ".").Append(SqlIdentifier.Quote(field)).Append(", ");
            }

            sql.Append("t.j as shape_json\n");
            sql.Append("from ").Append(table.QualifiedEvents).Append(" as " + Alias + "\n");

            // "offset 0" keeps the planner from pulling the subquery up: inlined, e.data::text is computed once for the guard and once for
            // the column, and rendering a jsonb as text is the costly thing here. A binary event's data is null, so its text is too.
            sql.Append("cross join lateral (select " + Alias + ".\"data\"::text as j offset 0) as t\n");
            sql.Append("where true\n");
            sql.Append(predicates);
            sql.Append("      and (t.j is null or octet_length(t.j) <= ")
                .Append(parameters.Add(NpgsqlDbType.Integer, maxItemBytes, "maxBytes")).Append(")\n");

            if (perTenantSequences && scope.TenantId is null && table.HasTenantId)
            {
                sql.Append("      and (" + Alias + ".\"tenant_id\", " + Alias + ".\"seq_id\") in (select * from unnest(")
                    .Append(parameters.Add(NpgsqlDbType.Array | NpgsqlDbType.Varchar, tenants.Select(static x => x ?? string.Empty).ToArray(), "tenants"))
                    .Append(", ")
                    .Append(parameters.Add(NpgsqlDbType.Array | NpgsqlDbType.Bigint, sequences.ToArray(), "seqs"))
                    .Append("))\n");
            }
            else
            {
                sql.Append("      and " + Alias + ".\"seq_id\" = any(")
                    .Append(parameters.Add(NpgsqlDbType.Array | NpgsqlDbType.Bigint, sequences.ToArray(), "seqs"))
                    .Append(")\n");
            }

            sql.Append("order by " + Alias + ".\"seq_id\"");

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>The names of a command's parameters, spelled as its statement spells them.</summary>
    /// <param name="command">The command.</param>
    public static IReadOnlyList<string> ParameterNames(NpgsqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return [.. command.Parameters.Select(static x => "@" + x.ParameterName)];
    }

    private static NpgsqlCommand New(int commandTimeoutSeconds)
    {
        NpgsqlCommand command = new();

        if (commandTimeoutSeconds > 0)
        {
            command.CommandTimeout = commandTimeoutSeconds;
        }

        return command;
    }

    /// <summary>
    /// The scope's predicates, each on a line of its own beginning <c>and</c>: the type, the tenant on a table that has <c>tenant_id</c>
    /// (none: every tenant), archived events left out where the column exists - a load or a rebuild reads none - and skipped events
    /// left out where the store skips them.
    /// </summary>
    private static string Scope(EventTableInfo table, string eventType, EventScope scope, ParameterBuilder parameters)
    {
        StringBuilder sql = new();

        sql.Append("      and " + Alias + ".\"type\" = ").Append(parameters.Add(NpgsqlDbType.Varchar, eventType, "type")).Append('\n');

        if (scope.TenantId is not null && table.HasTenantId)
        {
            sql.Append("      and " + Alias + ".\"tenant_id\" = ").Append(parameters.Add(NpgsqlDbType.Varchar, scope.TenantId, "tenant")).Append('\n');
        }

        if (table.HasIsArchived)
        {
            sql.Append("      and " + Alias + ".\"is_archived\" = false\n");
        }

        if (scope.SkippingEnabled && table.HasIsSkipped)
        {
            sql.Append("      and " + Alias + ".\"is_skipped\" = false\n");
        }

        return sql.ToString();
    }

    /// <summary>The key columns of one branch: sequence, tenant, stored size, and which part the key is from.</summary>
    private static string Keys(EventTableInfo table, int part)
    {
        string tenant = table.HasTenantId ? Alias + ".\"tenant_id\"" : "null::text";
        string size = "coalesce(pg_catalog.pg_column_size(" + Alias + ".\"data\"), 0)"
            + (table.HasBinaryData ? " + coalesce(pg_catalog.pg_column_size(" + Alias + ".\"bdata\"), 0)" : string.Empty);

        return Alias + ".\"seq_id\" as seq, "
            + tenant + " as tenant_id, "
            + "(" + size + ")::bigint as stored_bytes, "
            + part.ToString(CultureInfo.InvariantCulture) + " as part";
    }

    /// <summary>Names and adds parameters: the ones this file names keep their names, because "Show SQL" shows them to a human.</summary>
    private sealed class ParameterBuilder(NpgsqlCommand command)
    {
        public string Add(NpgsqlDbType type, object value, string name)
        {
            command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value });
            return "@" + name;
        }
    }

    // \A and \z, because .NET's $ also matches before a final line feed.
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlainColumn();
}
