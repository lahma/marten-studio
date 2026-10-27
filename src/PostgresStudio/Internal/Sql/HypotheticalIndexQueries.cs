using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>What <see cref="HypotheticalIndexQueries.ReadAvailabilityAsync" /> found of hypopg in one database.</summary>
/// <param name="Installed">Whether the extension is in <c>pg_extension</c>.</param>
/// <param name="Schema">The schema it was installed in, which its functions are called through.</param>
/// <param name="Version">Its <c>extversion</c>.</param>
/// <param name="Usable">
/// Whether the role asking - <c>SqlConsoleRole</c> inside the read-only transaction, or the connection's own login - may use it:
/// <c>USAGE</c> on its schema and <c>EXECUTE</c> on both <c>hypopg_create_index</c> and <c>hypopg_reset</c>, each the
/// extension's own member.
/// </param>
internal sealed record HypopgCatalog(bool Installed, string? Schema, string? Version, bool Usable);

/// <summary>One hypothetical index hypopg made: its object id in this backend, and the name the planner prints for it.</summary>
/// <param name="Oid">The hypothetical index's oid, which exists in this backend's memory alone.</param>
/// <param name="Name">The name a plan prints for it: <c>&lt;13630&gt;btree_t_b</c>.</param>
internal sealed record HypotheticalIndexMade(long Oid, string Name);

/// <summary>
/// X2's calls to hypopg (<c>hypopg_create_index</c>, <c>hypopg_reset</c>) and the one statement that ends the studio's own session
/// when a reset fails - one of the two places a Postgres identifier becomes SQL text (hard rule 4): the extension's schema is
/// quoted through <see cref="SqlIdentifier" />, and the index definition is a parameter, never interpolated.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never <c>CREATE EXTENSION</c>, never a real index.</b> The studio reads whether hypopg is installed and usable
/// (<see cref="AvailabilitySql" />) and asks it for a hypothetical index: an entry in this backend's memory that the planner
/// sees and no other session does, and no catalog row (PROBE, hypopg 1.4.4 on PostgreSQL 17, 2026-10-26). Installing the
/// extension is the host's decision.
/// </para>
/// <para>
/// <b>A hypothetical index outlives its transaction.</b> PROBE measured it surviving <c>ROLLBACK</c> in that backend, and a
/// pooled backend is handed to the host's next caller - whose planner would then see an index that does not exist. So every
/// what-if ends with <see cref="Reset" /> on the same connection, and one whose reset fails ends its own session
/// (<see cref="TerminateSelfSql" />), which takes the backend - and the index with it - out of the pool. Measured on the X2
/// probe (2026-10-27): a role short of a superuser may end its own backend, and Npgsql then holds the connection broken, so
/// it is never pooled again.
/// </para>
/// <para>
/// <b>The extension's own functions, called by their schema.</b> The probe joins <c>pg_depend</c>, so a function of the
/// same name another role created elsewhere is never the one called; and every call names the schema the extension was
/// installed in, so it does not depend on the <c>search_path</c> a caller set for the plan.
/// </para>
/// </remarks>
internal static class HypotheticalIndexQueries
{
    /// <summary>The extension's name.</summary>
    internal const string ExtensionName = "hypopg";

    /// <summary>
    /// Whether hypopg is installed in this database, where, at which version, and whether the role asking may use it. No row
    /// when it is not installed.
    /// </summary>
    internal const string AvailabilitySql =
        """
        select n.nspname,
               e.extversion,
               pg_catalog.has_schema_privilege(n.oid, 'usage')
                 and coalesce(pg_catalog.has_function_privilege(c.oid, 'execute'), false)
                 and coalesce(pg_catalog.has_function_privilege(r.oid, 'execute'), false) as usable
        from pg_catalog.pg_extension e
        join pg_catalog.pg_namespace n on n.oid = e.extnamespace
        left join pg_catalog.pg_proc c
          on c.pronamespace = e.extnamespace and c.proname = 'hypopg_create_index' and c.pronargs = 1
         and exists (select 1 from pg_catalog.pg_depend d
                     where d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass and d.objid = c.oid
                       and d.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass and d.refobjid = e.oid and d.deptype = 'e')
        left join pg_catalog.pg_proc r
          on r.pronamespace = e.extnamespace and r.proname = 'hypopg_reset' and r.pronargs = 0
         and exists (select 1 from pg_catalog.pg_depend d
                     where d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass and d.objid = r.oid
                       and d.refclassid = 'pg_catalog.pg_extension'::pg_catalog.regclass and d.refobjid = e.oid and d.deptype = 'e')
        where e.extname = 'hypopg'
        """;

    /// <summary>
    /// Ends the studio's own session: the last resort when a reset failed, so a backend that may still hold a hypothetical
    /// index never goes back to the host's pool. No parameter and no identifier.
    /// </summary>
    internal const string TerminateSelfSql = "select pg_catalog.pg_terminate_backend(pg_catalog.pg_backend_pid())";

    /// <summary>The parameter the index definition travels in.</summary>
    internal const string DefinitionParameter = "definition";

    /// <summary>
    /// <c>select indexrelid, indexname from "schema".hypopg_create_index(@definition)</c>, or <see langword="null" /> when the
    /// schema is not one <see cref="SqlIdentifier" /> quotes.
    /// </summary>
    /// <param name="schema">The schema hypopg was installed in.</param>
    public static string? Create(string schema) =>
        Quotable(schema) ? "select indexrelid, indexname from " + SqlIdentifier.Quote(schema) + ".hypopg_create_index(@" + DefinitionParameter + ")" : null;

    /// <summary><c>select "schema".hypopg_reset()</c>, or <see langword="null" />.</summary>
    /// <param name="schema">The schema hypopg was installed in.</param>
    public static string? Reset(string schema) =>
        Quotable(schema) ? "select " + SqlIdentifier.Quote(schema) + ".hypopg_reset()" : null;

    /// <summary>Reads <see cref="AvailabilitySql" /> in <paramref name="transaction" />, as the role that transaction runs as.</summary>
    public static async Task<HypopgCatalog> ReadAvailabilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(AvailabilitySql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new HypopgCatalog(false, null, null, false);
        }

        return new HypopgCatalog(
            true,
            reader.GetString(0),
            await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
            !await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) && reader.GetBoolean(2));
    }

    /// <summary>
    /// Asks hypopg for the one hypothetical index <paramref name="definition" /> describes, the definition a parameter. Every
    /// row it made is read, and more or fewer than one is an error: hypopg makes one index per <c>CREATE INDEX</c> in the text
    /// it is given and none for anything else (measured, X2 probe), and the studio's builders write exactly one.
    /// </summary>
    /// <exception cref="InvalidOperationException">hypopg made no index, or more than one.</exception>
    public static async Task<HypotheticalIndexMade> CreateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string definition,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);

        string sql = Create(schema) ?? throw new InvalidOperationException("hypopg's schema is not a name the studio quotes.");

        await using NpgsqlCommand command = new(sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter(DefinitionParameter, NpgsqlDbType.Text) { Value = definition });

        List<HypotheticalIndexMade> made = [];

        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // indexrelid is an oid, which Npgsql reads as a uint.
                made.Add(new HypotheticalIndexMade(await reader.GetFieldValueAsync<uint>(0, cancellationToken).ConfigureAwait(false), reader.GetString(1)));
            }
        }

        return made.Count == 1
            ? made[0]
            : throw new InvalidOperationException("hypopg made " + made.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " hypothetical indexes for one definition; the studio asks for exactly one.");
    }

    /// <summary>Runs <paramref name="sql" /> - <see cref="Reset" />, or a test's stand-in for it - in <paramref name="transaction" />.</summary>
    public static async Task ResetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        await using NpgsqlCommand command = new(sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <see cref="TerminateSelfSql" /> on <paramref name="connection" />, outside any transaction.</summary>
    public static async Task TerminateSelfAsync(NpgsqlConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new(TerminateSelfSql, connection) { CommandTimeout = commandTimeoutSeconds };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool Quotable(string? schema) =>
        !string.IsNullOrWhiteSpace(schema) && !schema.Contains('"', StringComparison.Ordinal) && !schema.Contains('\0', StringComparison.Ordinal);
}
