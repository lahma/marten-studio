using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// MartenStudio's half of the Relationships reads: how many documents of one collection point at one document.
/// </summary>
/// <remarks>
/// <para>
/// Beside the other document builders because it is built from Marten's mapping (<see cref="DocumentTableInfo" />):
/// the tenant column of a conjoined collection, and <c>mt_deleted</c> of a soft-deleted one, filter the count the way
/// the collection is read everywhere else. The shared foreign-key read and its cap are
/// <see cref="RelationshipQueries" />.
/// </para>
/// <para>
/// Every identifier is quoted through <see cref="SqlIdentifier" /> and comes from Marten's mapping or from the
/// catalog; the id, the tenant and the cap are parameters (AGENTS.md hard rule 4).
/// </para>
/// </remarks>
internal static class DocumentInboundQueries
{
    /// <summary>
    /// The bounded inbound count: how many rows of one collection point at one document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>select count(*) from (select 1 … limit @cap) x</c> rather than a plain <c>count(*)</c>. The
    /// number is exact where it matters — a person wants to know whether a customer has three orders or
    /// three hundred before they delete it — but the studio must not pay for a sequential scan to say
    /// "lots" about a table with ten million rows, and the foreign key's column is not necessarily
    /// indexed. The cap is what bounds it; above the cap the screen says "1000+".
    /// </para>
    /// <para>
    /// The predicates are the ones the source collection is actually read with elsewhere: the tenant when
    /// the <em>source</em> is conjoined (its tenancy, not the target's — the target may well be a
    /// single-tenanted type, and the rows being counted are the source's), and live rows only when the
    /// source is soft-deleted, because a count that included deleted rows would contradict the list the
    /// row links to.
    /// </para>
    /// </remarks>
    /// <param name="table">The pointing collection, with its physical columns already settled.</param>
    /// <param name="column">The foreign-key column on that collection.</param>
    /// <param name="idDbType">The Postgres type of <paramref name="column"/>, for the parameter.</param>
    /// <param name="id">The already-parsed id value, as <c>DocumentQueryBuilder.ParseId</c> produced it.</param>
    /// <param name="tenantId">The tenant in scope, or <see langword="null"/>.</param>
    /// <param name="cap">How many rows to look at before stopping.</param>
    /// <param name="commandTimeoutSeconds">The command timeout.</param>
    public static NpgsqlCommand BuildInboundCount(
        DocumentTableInfo table,
        string column,
        NpgsqlDbType idDbType,
        object id,
        string? tenantId,
        int cap,
        int commandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentNullException.ThrowIfNull(id);

        var tenantColumn = TenantColumn(table, tenantId);
        var command = DocumentQueryBuilder.NewCommand(commandTimeoutSeconds);

        try
        {
            command.CommandText = InboundCountSql(table, column, tenantColumn);

            command.Parameters.Add(new NpgsqlParameter("id", idDbType) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = Math.Max(cap, 1) });

            if (tenantColumn is not null)
            {
                command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenantId! });
            }

            return command;
        }
        catch
        {
            command.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The inbound count's text. Every identifier is quoted and comes from Marten's mapping or from
    /// <c>information_schema</c>; the id, the tenant and the cap are parameters.
    /// </summary>
    /// <param name="table">The pointing collection.</param>
    /// <param name="column">The foreign-key column.</param>
    /// <param name="tenantColumn">The tenant column to filter on, or <see langword="null"/>.</param>
    internal static string InboundCountSql(DocumentTableInfo table, string column, string? tenantColumn)
    {
        ArgumentNullException.ThrowIfNull(table);

        var sql = new System.Text.StringBuilder("select count(*) from (select 1 from ")
            .Append(table.QualifiedName)
            .Append(" where ")
            .Append(SqlIdentifier.Quote(column))
            .Append(" = @id");

        if (tenantColumn is not null)
        {
            sql.Append(" and ").Append(SqlIdentifier.Quote(tenantColumn)).Append(" = @tenant");
        }

        if (table.SoftDeleteEnabled &&
            table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) is { } deletedColumn)
        {
            sql.Append(" and ").Append(SqlIdentifier.Quote(deletedColumn)).Append(" = false");
        }

        return sql.Append(" limit @cap) as bounded").ToString();
    }

    /// <summary>The tenant column to filter the inbound count by, or <see langword="null"/>.</summary>
    internal static string? TenantColumn(DocumentTableInfo table, string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(table);

        return tenantId is not null && table.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
            ? table.MetadataColumnName(DocumentMetadataColumn.TenantId)
            : null;
    }
}
