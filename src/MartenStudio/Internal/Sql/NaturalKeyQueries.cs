using System.Text;

using JasperFx.Events;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>One natural-key lookup, as the builder takes it.</summary>
internal sealed record NaturalKeyLookupQuery
{
    /// <summary>The largest number of streams one lookup returns.</summary>
    public const int MaxRows = 50;

    /// <summary>The lookup table, <c>mt_natural_key_&lt;aggregate&gt;</c>.</summary>
    public required string Table { get; init; }

    /// <summary>The value, parsed to the key's primitive.</summary>
    public required object Key { get; init; }

    /// <summary>How the value binds, from the column's own type in <c>information_schema</c>.</summary>
    public NpgsqlDbType KeyType { get; init; } = NpgsqlDbType.Varchar;

    /// <summary>Whether the lookup table carries <c>tenant_id</c>, which a conjoined store's does.</summary>
    public bool TableHasTenant { get; init; }

    /// <summary>The scope's tenant, or <see langword="null" /> for all of them.</summary>
    public string? TenantId { get; init; }

    /// <summary>Whether archived streams are found too, as the Streams page's toggle says.</summary>
    public bool IncludeArchived { get; init; }
}

/// <summary>
/// Builds the Streams box's natural-key lookup: a primary-key probe on <c>mt_natural_key_&lt;aggregate&gt;</c>
/// joined to <c>mt_streams</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shape is Marten's own <c>FetchNaturalKeyPlan.BuildNaturalKeyToStreamQuery</c> (9.39.0): join on
/// <c>s.id = nk.stream_id</c> (or <c>stream_key</c> on a string-identified store), match
/// <c>nk.natural_key_value</c>, and narrow both sides to the tenant on a conjoined store. The archived
/// question is asked of <c>mt_streams</c>, because Marten never copies archival onto the lookup row (#5344);
/// unlike Marten's, it honours the page's "include archived" toggle rather than always excluding them.
/// </para>
/// <para>
/// <c>natural_key_value</c> leads the table's primary key in every tenancy, so the probe is an index lookup.
/// The select list is the stream list's own, so a match renders as an ordinary stream row.
/// </para>
/// </remarks>
internal static class NaturalKeyQueries
{
    private const string KeyAlias = "nk";
    private const string StreamAlias = EventTableInfo.StreamsAlias;

    /// <summary>The streams whose natural key equals <see cref="NaturalKeyLookupQuery.Key" />.</summary>
    /// <param name="table">The discovered event tables.</param>
    /// <param name="query">The lookup.</param>
    public static NpgsqlCommand BuildLookup(EventTableInfo table, NaturalKeyLookupQuery query)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(query);

        string streamColumn = StreamColumnFor(table.StreamIdentity);
        bool streamsHaveTenant = table.HasStreamColumn("tenant_id");
        bool conjoined = query.TableHasTenant && streamsHaveTenant;

        var command = new NpgsqlCommand();

        try
        {
            var sql = new StringBuilder();
            bool first = true;

            sql.Append("select ");
            foreach (string column in EventQueryBuilder.StreamSelectColumns)
            {
                if (!table.HasStreamColumn(column))
                {
                    continue;
                }

                sql.Append(first ? string.Empty : ",\n       ").Append(Stream(column));
                first = false;
            }

            sql.Append('\n');
            sql.Append("from ").Append(SqlIdentifier.Qualify(table.Schema, query.Table)).Append(" as ").Append(KeyAlias).Append('\n');
            sql.Append("join ").Append(table.QualifiedStreams).Append(" as " + StreamAlias)
                .Append(" on ").Append(Stream("id")).Append(" = ").Append(Key(streamColumn));

            if (conjoined)
            {
                sql.Append(" and ").Append(Stream("tenant_id")).Append(" = ").Append(Key("tenant_id"));
            }

            sql.Append('\n');

            command.Parameters.Add(new NpgsqlParameter("key", query.KeyType) { Value = query.Key });
            sql.Append("where ").Append(Key("natural_key_value")).Append(" = @key\n");

            // The stream's own tenant is the authority, as it is for the stream list (AppendStreamFilters):
            // asked of mt_streams whenever it carries the column, whether or not the lookup table does. A lookup
            // table without tenant_id beside a tenanted mt_streams - one created before the store went conjoined,
            // or by hand - would otherwise hand another tenant's stream to this tenant's scope. The lookup table's
            // own tenant is asked too where it has one, so the probe stays on its primary key's prefix.
            if (streamsHaveTenant || query.TableHasTenant)
            {
                command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar)
                {
                    Value = (object?) query.TenantId ?? DBNull.Value,
                });
            }

            if (query.TableHasTenant)
            {
                sql.Append("  and (@tenant is null or ").Append(Key("tenant_id")).Append(" = @tenant)\n");
            }

            if (streamsHaveTenant)
            {
                sql.Append("  and (@tenant is null or ").Append(Stream("tenant_id")).Append(" = @tenant)\n");
            }

            if (table.HasStreamColumn("is_archived"))
            {
                command.Parameters.Add(new NpgsqlParameter("includeArchived", NpgsqlDbType.Boolean) { Value = query.IncludeArchived });
                sql.Append("  and (@includeArchived or ").Append(Stream("is_archived")).Append(" = false)\n");
            }

            sql.Append("order by ").Append(Stream("timestamp")).Append(" desc, ").Append(Stream("id")).Append('\n');

            command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = NaturalKeyLookupQuery.MaxRows });
            sql.Append("limit @limit");

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
    /// The lookup table's stream column for a store's stream identity: <c>stream_id</c> for GUIDs,
    /// <c>stream_key</c> for strings (Marten's <c>NaturalKeyTable</c>).
    /// </summary>
    public static string StreamColumnFor(StreamIdentity identity) =>
        identity == StreamIdentity.AsString ? "stream_key" : "stream_id";

    private static string Key(string column) => KeyAlias + "." + SqlIdentifier.Quote(column);

    private static string Stream(string column) => StreamAlias + "." + SqlIdentifier.Quote(column);
}
