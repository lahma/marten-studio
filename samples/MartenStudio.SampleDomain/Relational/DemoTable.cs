using MartenStudio.SampleDomain.Generation;

using Npgsql;

namespace MartenStudio.SampleDomain.Relational;

/// <summary>The two catalog questions the relational seeding asks before it writes.</summary>
internal static class DemoTable
{
    /// <summary>Whether a table has at least one row. The name is quoted; it is always a constant here.</summary>
    internal static async Task<bool> HasRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists (select 1 from " + DemoDataSql.QuoteQualified(schema, table) + ")",
            connection,
            transaction);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    /// <summary>Whether a table or view of this name exists, without failing when it does not.</summary>
    internal static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select pg_catalog.to_regclass(@name) is not null",
            connection,
            transaction);

        command.Parameters.AddWithValue("name", DemoDataSql.QuoteQualified(schema, table));

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }
}
