using System.Text;

using MartenStudio.Services.Query;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// How many documents a collection holds — or the honest admission that the studio could not find out.
/// </summary>
/// <remarks>
/// <para>
/// "Cannot report" is a value, not an error (plan §4.8): a table that has been dropped under the studio,
/// or a database that refused the read, must be drawn differently from a collection that genuinely holds
/// nothing. <see cref="IsEstimate"/> is what the UI prefixes with <c>~</c> (D8).
/// </para>
/// <para>
/// <see cref="Unknown"/> is the third answer, and it is why <see cref="IsUnknown"/> exists beside
/// <see cref="IsUnavailable"/>. Postgres reports <c>reltuples = -1</c> for a table it has never analysed,
/// which is every freshly seeded collection; rendering that as <c>~0</c> beside a collection that visibly
/// has rows in it is the studio telling a lie it could have avoided by asking. So a never-analysed table
/// reports "unknown" — a table that is certainly there and whose size nobody has measured — and the caller
/// decides whether an exact <c>count(*)</c> is worth paying for.
/// </para>
/// </remarks>
/// <param name="Value">The number of rows, or zero when <paramref name="IsUnavailable"/>.</param>
/// <param name="IsEstimate">Whether this came from <c>pg_class.reltuples</c> rather than <c>count(*)</c>.</param>
/// <param name="IsUnavailable">Whether the count could not be established at all.</param>
internal readonly record struct DocumentCount(long Value, bool IsEstimate, bool IsUnavailable)
{
    /// <summary>The count could not be established — the table is gone, or the read was refused.</summary>
    public static DocumentCount Unavailable { get; } = new(0, false, true);

    /// <summary>
    /// The table is there and Postgres has never analysed it, so there is no estimate to report.
    /// </summary>
    /// <remarks>
    /// A kind of <see cref="IsUnavailable"/> on purpose: every caller that already draws "no number" draws
    /// this correctly without being changed, and the ones that can do something about it —
    /// <c>CountAsync</c>, and the list header — branch on <see cref="IsUnknown"/>.
    /// </remarks>
    public static DocumentCount Unknown { get; } = new(0, false, true) { IsUnknown = true };

    /// <summary>Whether the table has simply never been analysed, rather than having refused the read.</summary>
    public bool IsUnknown { get; init; }

    /// <summary>A <c>reltuples</c> estimate.</summary>
    public static DocumentCount Estimate(long value) => new(value, true, false);

    /// <summary>An exact <c>count(*)</c>.</summary>
    public static DocumentCount Exact(long value) => new(value, false, false);
}

/// <summary>
/// Counts documents the cheap way first (D8).
/// </summary>
/// <remarks>
/// <para>
/// An exact <c>count(*)</c> on a ten-million-row collection is a sequential scan, and a dashboard that
/// does one per collection per refresh is a denial of service against the database it is meant to help
/// you understand. So the first question is always <c>pg_class.reltuples</c>, which is free.
/// </para>
/// <para>
/// Two cases make the estimate useless and are the reason <see cref="ExactCountThreshold"/> exists.
/// Postgres reports <c>-1</c> for a table that has never been vacuumed or analysed — a freshly seeded
/// collection, which is exactly what a new user is looking at — and an estimate of "about 12" is worse
/// than the truth on a table where the truth costs nothing. Below the threshold the estimator therefore
/// pays for <c>count(*)</c>; above it, the estimate stands until somebody asks for the exact number.
/// </para>
/// </remarks>
internal sealed class CountEstimator
{
    /// <summary>
    /// The estimate query. <c>to_regclass</c> returns null rather than throwing for a table that is not
    /// there, which is what turns "the table is gone" into <see cref="DocumentCount.Unavailable"/> instead
    /// of an exception on a dashboard tile.
    /// </summary>
    internal const string EstimateSql =
        """
        select c.reltuples::bigint
        from pg_catalog.pg_class c
        where c.oid = to_regclass(@qualified)
        """;

    /// <summary>Rows at or below which the estimate is replaced by an exact count.</summary>
    public long ExactCountThreshold { get; init; } = 10_000;

    /// <summary>How long either query may take. An exact count runs under this and nothing longer.</summary>
    public int CommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// The estimate, upgraded to an exact count when the table is small enough (or has never been
    /// analysed, which Postgres reports as a negative <c>reltuples</c>).
    /// </summary>
    public async Task<DocumentCount> CountAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        var estimate = await EstimateAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);

        // A never-analysed table is the one case where the cheap answer says nothing at all, so it is worth
        // the round trip rather than being reported as "no number" on a collection that plainly has rows.
        if (estimate.IsUnknown)
        {
            return await CountExactAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);
        }

        if (estimate.IsUnavailable)
        {
            return estimate;
        }

        if (estimate.Value > ExactCountThreshold)
        {
            return estimate;
        }

        return await CountExactAsync(connection, schema, table, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The <c>reltuples</c> estimate alone.</summary>
    public async Task<DocumentCount> EstimateAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(EstimateSql, connection) { CommandTimeout = CommandTimeoutSeconds };

        command.Parameters.Add(new NpgsqlParameter("qualified", NpgsqlDbType.Text)
        {
            Value = SqlIdentifier.Qualify(schema, table),
        });

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        if (result is null or DBNull)
        {
            return DocumentCount.Unavailable;
        }

        var rows = Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);

        // -1 is Postgres for "never analysed", not for "minus one row" and not for "empty". Reporting it as
        // an estimate of zero is the studio saying "~0 documents" about a collection it has not looked at.
        return rows < 0 ? DocumentCount.Unknown : DocumentCount.Estimate(rows);
    }

    /// <summary>An exact <c>count(*)</c> of the whole table.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">The usual.</param>
    public Task<DocumentCount> CountExactAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default) =>
        CountExactAsync(connection, schema, table, null, null, cancellationToken);

    /// <summary>An exact <c>count(*)</c>, which the user asked for explicitly or the threshold allowed.</summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="cancellationToken">The usual.</param>
    /// <param name="tenantId">
    /// Counts only this tenant's rows. There is no tenant-scoped equivalent on the estimate side —
    /// <c>reltuples</c> describes the whole table and nothing narrower — which is why
    /// <see cref="CountAsync"/> takes no tenant: a tenant-scoped number is always an exact one, and the
    /// caller has to have decided it is worth paying for.
    /// </param>
    /// <param name="commandTimeout">
    /// A bound on the scan, overriding <see cref="CommandTimeoutSeconds"/>. An exact count is the one query
    /// in the studio whose cost is proportional to how big the problem already is.
    /// </param>
    /// <remarks>
    /// This is an <em>overload</em> rather than two more optional parameters on the four-argument form,
    /// because CA1068 requires the cancellation token to come last and putting the new parameters in front
    /// of it would silently break every existing call site that passes a token positionally.
    /// </remarks>
    public async Task<DocumentCount> CountExactAsync(
        NpgsqlConnection connection,
        string schema,
        string table,
        string? tenantId,
        TimeSpan? commandTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var command = new NpgsqlCommand(ExactSql(schema, table, tenantId), connection)
        {
            CommandTimeout = commandTimeout is { } timeout
                ? Math.Max((int)Math.Ceiling(timeout.TotalSeconds), 1)
                : CommandTimeoutSeconds,
        };

        if (tenantId is not null)
        {
            command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenantId });
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull
            ? DocumentCount.Unavailable
            : DocumentCount.Exact(Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// An exact <c>count(*)</c> that counts the same rows the list is showing: this tenant's, and the
    /// soft-delete tri-state the page is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three-argument overloads count the whole table, which is right for a rail whose estimate is a
    /// whole-table <c>reltuples</c> and wrong everywhere a scope narrows what is on screen. A number under
    /// a list of three invoices that says six is not a rounding error — it is the studio answering a
    /// different question from the one the page asked.
    /// </para>
    /// <para>
    /// It takes a <see cref="DocumentTableInfo"/> rather than a schema and table name because that is the
    /// only way to know whether <c>tenant_id</c> and <c>mt_deleted</c> exist and what they are called: on a
    /// single-tenant collection there is nothing to filter by, and a table with
    /// <c>DisableInformationalFields()</c> has no soft-delete column at all.
    /// </para>
    /// </remarks>
    /// <param name="connection">An open connection.</param>
    /// <param name="table">The collection, with its physical columns already settled.</param>
    /// <param name="tenantId">The tenant in scope, or <see langword="null"/> for all of them.</param>
    /// <param name="deleted">The soft-delete tri-state; ignored on a collection that is not soft-deleted.</param>
    /// <param name="commandTimeout">A bound on the scan, overriding <see cref="CommandTimeoutSeconds"/>.</param>
    /// <param name="cancellationToken">The usual.</param>
    public async Task<DocumentCount> CountExactAsync(
        NpgsqlConnection connection,
        DocumentTableInfo table,
        string? tenantId,
        DeletedFilter deleted,
        TimeSpan? commandTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(table);

        var tenantColumn = tenantId is not null && table.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined
            ? table.MetadataColumnName(DocumentMetadataColumn.TenantId)
            : null;

        await using var command = new NpgsqlCommand(ExactSql(table, tenantColumn, deleted), connection)
        {
            CommandTimeout = commandTimeout is { } timeout
                ? Math.Max((int)Math.Ceiling(timeout.TotalSeconds), 1)
                : CommandTimeoutSeconds,
        };

        if (tenantColumn is not null)
        {
            command.Parameters.Add(new NpgsqlParameter("tenant", NpgsqlDbType.Varchar) { Value = tenantId! });
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result is null or DBNull
            ? DocumentCount.Unavailable
            : DocumentCount.Exact(Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The exact-count query. The only things interpolated are quoted identifiers; the tenant is a
    /// parameter, and its predicate is emitted only when there is a tenant, because this method is given a
    /// table name rather than a <see cref="DocumentTableInfo"/> and cannot ask whether the column exists.
    /// </summary>
    internal static string ExactSql(string schema, string table, string? tenantId = null) =>
        "select count(*) from " + SqlIdentifier.Qualify(schema, table) +
        (tenantId is null ? string.Empty : " where " + SqlIdentifier.Quote("tenant_id") + " = @tenant");

    /// <summary>
    /// The scoped exact-count query. Identifiers are quoted and come from the table's own column set; the
    /// tenant is the only value, and it is a parameter.
    /// </summary>
    internal static string ExactSql(DocumentTableInfo table, string? tenantColumn, DeletedFilter deleted)
    {
        ArgumentNullException.ThrowIfNull(table);

        var sql = new StringBuilder("select count(*) from ").Append(table.QualifiedName);
        var clauses = 0;

        if (tenantColumn is not null)
        {
            sql.Append(" where ").Append(SqlIdentifier.Quote(tenantColumn)).Append(" = @tenant");
            clauses++;
        }

        if (table.SoftDeleteEnabled &&
            deleted != DeletedFilter.Include &&
            table.MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) is { } deletedColumn)
        {
            sql.Append(clauses == 0 ? " where " : " and ")
                .Append(SqlIdentifier.Quote(deletedColumn))
                .Append(deleted == DeletedFilter.Only ? " = true" : " = false");
        }

        return sql.ToString();
    }
}
