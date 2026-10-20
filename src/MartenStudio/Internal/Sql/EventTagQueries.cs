using System.Globalization;
using System.Text;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The feed's tag filter, resolved against the store's tag configuration and ready for
/// <see cref="EventQueryBuilder.BuildFeed" />.
/// </summary>
/// <remarks>
/// Two shapes, one per DCB storage mode. In tag-table mode the filter is a semi-join against
/// <c>mt_event_tag_&lt;suffix&gt;</c> on <c>value = @tagValue</c>, which the table's primary key - it leads
/// with <c>value</c> (Marten's <c>EventTagTable</c>, 9.39.0 and 9.50.2) - serves. In hstore mode it is
/// <c>mt_events.tags @&gt; @tagSet</c>, which Marten's GIN index <c>idx_mt_events_tags</c> serves. The value
/// is always a typed parameter; the table name is quoted through <see cref="SqlIdentifier" />.
/// </remarks>
internal sealed record EventTagPredicate
{
    /// <summary>The tag table, in tag-table mode. Unused in hstore mode.</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>The value, already parsed to the tag type's primitive (or its hstore text).</summary>
    public required object Value { get; init; }

    /// <summary>How the value is bound: <c>Text</c>, <c>Uuid</c>, <c>Integer</c>, <c>Bigint</c> or <c>Smallint</c>.</summary>
    public NpgsqlDbType ValueType { get; init; } = NpgsqlDbType.Text;

    /// <summary>The hstore key - the tag type's suffix - when the store keeps tags inline.</summary>
    public string? HStoreKey { get; init; }

    /// <summary>Whether the tag table carries <c>tenant_id</c>, which a conjoined store's does.</summary>
    public bool TableHasTenant { get; init; }

    /// <summary>Whether this is the hstore shape.</summary>
    public bool IsHStore => HStoreKey is not null;

    /// <summary>The Npgsql type a tag table's <c>value</c> column binds as, for its primitive.</summary>
    /// <remarks>
    /// Marten's <c>EventTagTable.PostgresqlTypeFor</c> at 9.39.0: <c>text</c>, <c>uuid</c>, <c>integer</c>,
    /// <c>bigint</c>, <c>smallint</c>. Anything else Marten refuses to register, so it never reaches here.
    /// </remarks>
    public static NpgsqlDbType DbTypeFor(Type simpleType)
    {
        ArgumentNullException.ThrowIfNull(simpleType);

        if (simpleType == typeof(Guid))
        {
            return NpgsqlDbType.Uuid;
        }

        if (simpleType == typeof(int))
        {
            return NpgsqlDbType.Integer;
        }

        if (simpleType == typeof(long))
        {
            return NpgsqlDbType.Bigint;
        }

        return simpleType == typeof(short) ? NpgsqlDbType.Smallint : NpgsqlDbType.Text;
    }
}

/// <summary>One tag table an explicit tag read covers.</summary>
/// <param name="Suffix">The tag type's suffix, which the row is labelled with.</param>
/// <param name="Table">The table.</param>
/// <param name="HasTenant">Whether it carries <c>tenant_id</c>.</param>
internal sealed record EventTagTableRef(string Suffix, string Table, bool HasTenant);

/// <summary>
/// Builds the tag reads: the tags of one page of events, across every tag table, in one statement.
/// </summary>
/// <remarks>
/// <para>
/// <b>This read is not index-backed, and the service knows it.</b> A tag table's primary key leads with
/// <c>value</c> and Marten declares no other index, so "the tags of these sequences" is a scan of each tag
/// table - there is no way to ask the question the other way round. That is why the event service only
/// runs it with a page when every tag table is small enough that <c>CountEstimator</c> would count it
/// exactly, and otherwise leaves the page's tags for the visitor to ask for (D8's argument, applied).
/// </para>
/// <para>
/// <b>Tenancy.</b> A tag table on a conjoined store carries <c>tenant_id</c>. With a tenant in scope the read
/// is narrowed to it, so a sequence handed in from anywhere - a crafted request included - can only ever
/// return that tenant's tags. With none in scope the tenant comes back with every row, and the caller pairs
/// tags with events on <c>(tenant_id, seq_id)</c>: under <c>UseTenantPartitionedEvents</c> every tenant
/// draws <c>seq_id</c> from its own sequence, so the number alone names one event per tenant.
/// </para>
/// </remarks>
internal static class EventTagQueries
{
    /// <summary>The most events one tag read is asked about.</summary>
    public const int MaxSequences = 1_000;

    /// <summary>The most tag rows one read returns: a page's worth of events with many tags each, and a bound.</summary>
    public const int MaxRows = 10_000;

    private const string Alias = "t";
    private const string AsAlias = " as " + Alias;

    /// <summary>The tags of these events, from every tag table, labelled with their tag type's suffix.</summary>
    /// <param name="schema">The event store's schema.</param>
    /// <param name="tables">The tag tables that exist; at least one.</param>
    /// <param name="sequences">The events' <c>seq_id</c>s; at most <see cref="MaxSequences" /> are used.</param>
    /// <param name="tenantId">The scope's tenant, or <see langword="null" /> for all of them.</param>
    /// <returns>
    /// A command whose rows are <c>tag_type text, seq_id bigint, tenant_id varchar, tag_value text</c>,
    /// ordered by sequence, type and value.
    /// </returns>
    public static NpgsqlCommand BuildTagsForEvents(
        string schema,
        IReadOnlyList<EventTagTableRef> tables,
        IReadOnlyCollection<long> sequences,
        string? tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(sequences);

        if (tables.Count == 0)
        {
            throw new ArgumentException("A tag read needs at least one tag table.", nameof(tables));
        }

        var command = new NpgsqlCommand();

        try
        {
            var sql = new StringBuilder();

            command.Parameters.Add(new NpgsqlParameter("seqs", NpgsqlDbType.Array | NpgsqlDbType.Bigint)
            {
                Value = sequences.Distinct().Take(MaxSequences).ToArray(),
            });

            bool anyTenant = false;

            for (int index = 0; index < tables.Count; index++)
            {
                EventTagTableRef table = tables[index];
                string typeParameter = "type" + index.ToString(CultureInfo.InvariantCulture);

                command.Parameters.Add(new NpgsqlParameter(typeParameter, NpgsqlDbType.Text) { Value = table.Suffix });

                if (index > 0)
                {
                    sql.Append("union all\n");
                }

                sql.Append("select @").Append(typeParameter).Append("::text as tag_type, ")
                    .Append(Column("seq_id")).Append(" as seq_id, ")
                    .Append(table.HasTenant ? Column("tenant_id") + "::varchar" : "null::varchar").Append(" as tenant_id, ")
                    .Append(Column("value")).Append("::text as tag_value\n");
                sql.Append("from ").Append(SqlIdentifier.Qualify(schema, table.Table)).Append(AsAlias).Append('\n');
                sql.Append("where ").Append(Column("seq_id")).Append(" = any(@seqs)\n");

                if (table.HasTenant)
                {
                    anyTenant = true;
                    sql.Append("  and (@tenant is null or ").Append(Column("tenant_id")).Append(" = @tenant)\n");
                }
            }

            if (anyTenant)
            {
                command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar)
                {
                    Value = (object?) tenantId ?? DBNull.Value,
                });
            }

            sql.Append("order by 2, 1, 4\n");
            sql.Append("limit ").Append(MaxRows.ToString(CultureInfo.InvariantCulture));

            command.CommandText = sql.ToString();
            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The feed's tag predicate, appended to a statement over <c>mt_events as e</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tag-table mode, single-tenanted: <c>e.seq_id in (select t.seq_id from tag t where t.value = @tagValue)</c>.
    /// Conjoined: <c>(e.tenant_id, e.seq_id) in (select t.tenant_id, t.seq_id ... and (@tenant is null or
    /// t.tenant_id = @tenant))</c> - a row-valued IN, uncorrelated, so Postgres can still plan it as a
    /// semi-join driven from the tag table's primary key, and correct under tenant-partitioned events where
    /// <c>seq_id</c> repeats across tenants. Marten's own <c>buildTagConditionsFilter</c> writes the
    /// conjoined case as a <em>correlated</em> subquery (<c>tenant_id = d.tenant_id</c>), which cannot be
    /// pulled up and is evaluated once per event row; that is why this one is not.
    /// </para>
    /// <para>
    /// Hstore mode: <c>e.tags @&gt; @tagSet</c>, the containment Marten's GIN index serves. The outer
    /// statement's own tenant predicate narrows the events; an hstore value carries no tenant of its own.
    /// </para>
    /// <para>
    /// <c>@tenant</c> is not added here: the feed adds it for its own tenant predicate whenever the events
    /// table has <c>tenant_id</c>, and this reuses it. <paramref name="hasTenantParameter" /> says whether it
    /// exists; without it the conjoined shape falls back to the unnarrowed row-valued IN.
    /// </para>
    /// </remarks>
    /// <param name="sql">The statement being built.</param>
    /// <param name="command">The command the parameters go on.</param>
    /// <param name="schema">The event store's schema.</param>
    /// <param name="eventsAlias">The alias of <c>mt_events</c> in the statement.</param>
    /// <param name="eventsHaveTenant">Whether <c>mt_events</c> carries <c>tenant_id</c>.</param>
    /// <param name="hasTenantParameter">Whether the statement already declares <c>@tenant</c>.</param>
    /// <param name="predicate">The filter.</param>
    public static void AppendFeedPredicate(
        StringBuilder sql,
        NpgsqlCommand command,
        string schema,
        string eventsAlias,
        bool eventsHaveTenant,
        bool hasTenantParameter,
        EventTagPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(predicate);

        string events = eventsAlias + ".";

        if (predicate.IsHStore)
        {
            command.Parameters.Add(new NpgsqlParameter("tagSet", NpgsqlDbType.Hstore)
            {
                Value = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [predicate.HStoreKey!] = Convert.ToString(predicate.Value, CultureInfo.InvariantCulture),
                },
            });

            sql.Append("  and ").Append(events).Append(SqlIdentifier.Quote("tags")).Append(" @> @tagSet\n");
            return;
        }

        command.Parameters.Add(new NpgsqlParameter("tagValue", predicate.ValueType) { Value = predicate.Value });

        string tagTable = SqlIdentifier.Qualify(schema, predicate.Table);

        if (predicate.TableHasTenant && eventsHaveTenant)
        {
            sql.Append("  and (").Append(events).Append(SqlIdentifier.Quote("tenant_id")).Append(", ")
                .Append(events).Append(SqlIdentifier.Quote("seq_id")).Append(") in (select ")
                .Append(Column("tenant_id")).Append(", ").Append(Column("seq_id"))
                .Append(" from ").Append(tagTable).Append(AsAlias)
                .Append(" where ").Append(Column("value")).Append(" = @tagValue");

            if (hasTenantParameter)
            {
                sql.Append(" and (@tenant is null or ").Append(Column("tenant_id")).Append(" = @tenant)");
            }

            sql.Append(")\n");
            return;
        }

        sql.Append("  and ").Append(events).Append(SqlIdentifier.Quote("seq_id")).Append(" in (select ")
            .Append(Column("seq_id")).Append(" from ").Append(tagTable).Append(AsAlias)
            .Append(" where ").Append(Column("value")).Append(" = @tagValue)\n");
    }

    private static string Column(string name) => Alias + "." + SqlIdentifier.Quote(name);
}
