using System.Text;

using MartenStudio.Services.Query;

using Npgsql;

using NpgsqlTypes;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The document half of <see cref="CountEstimator" />: an exact count narrowed the way a Marten document list is -
/// by tenant and by the soft-delete tri-state - which needs the collection's <see cref="DocumentTableInfo" /> to know
/// whether those columns exist and what they are called.
/// </summary>
/// <remarks>
/// C# 14 extension members rather than a second part of a partial class: <see cref="CountEstimator" /> moves to the shared
/// assembly (PS-2), and a partial type cannot span two assemblies - a part left behind would compile as a second
/// <c>CountEstimator</c> in MartenStudio and make every file that sees both namespaces ambiguous. Callers keep writing
/// <c>estimator.CountExactAsync(connection, table, …)</c> and <c>CountEstimator.ExactSql(…)</c>.
/// </remarks>
internal static class DocumentCountEstimator
{
    extension(CountEstimator estimator)
    {
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
        /// <param name="commandTimeout">A bound on the scan, overriding <see cref="CountEstimator.CommandTimeoutSeconds"/>.</param>
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

            await using var command = new NpgsqlCommand(CountEstimator.ExactSql(table, tenantColumn, deleted), connection)
            {
                CommandTimeout = commandTimeout is { } timeout
                    ? Math.Max((int)Math.Ceiling(timeout.TotalSeconds), 1)
                    : estimator.CommandTimeoutSeconds,
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
    }

    extension(CountEstimator)
    {
        /// <summary>
        /// The scoped exact-count query. Identifiers are quoted and come from the table's own column set; the
        /// tenant is the only value, and it is a parameter.
        /// </summary>
        /// <param name="table">The collection, with its physical columns already settled.</param>
        /// <param name="tenantColumn">The tenant column to filter by, or <see langword="null"/> for none.</param>
        /// <param name="deleted">The soft-delete tri-state.</param>
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
}
