using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// Builds tenant discovery's last tier: one bounded read of <c>tenant_id</c> over every tenanted table of a store, de-duplicated by the
/// database (hard rule 4 - the statement is built here and nowhere else; the tables' names are quoted through
/// <see cref="SqlIdentifier" />, and the one value in it is a constant the builder owns).
/// </summary>
internal static class TenantIdQueries
{
    /// <summary>
    /// The statement: each table contributes at most <paramref name="limit" /> + 1 distinct ids of its own, so the union cannot carry
    /// more than the number of tables times that however large the tables are, and the whole is capped again on the way out - "more
    /// than <paramref name="limit" />" is answerable without counting.
    /// </summary>
    /// <param name="tables">Each tenanted table, as its schema and name.</param>
    /// <param name="limit">The most tenants the caller lists.</param>
    public static string DistinctTenantIds(IReadOnlyList<(string Schema, string Table)> tables, int limit)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentOutOfRangeException.ThrowIfLessThan(tables.Count, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        string cap = (limit + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        string branches = string.Join(
            " union ",
            tables.Select(table => $"(select distinct tenant_id from {SqlIdentifier.Qualify(table.Schema, table.Table)} order by tenant_id limit {cap})"));

        return $"select tenant_id from ({branches}) as tenants where tenant_id is not null order by tenant_id limit {cap}";
    }
}
