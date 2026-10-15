using Npgsql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// One statement a health check's gatherer runs: the text, and the parameters it binds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built here, run there.</b> Every health builder lives in this folder (<c>Health*Queries.cs</c>), next to
/// the other builders, because this is the one place an identifier becomes SQL text (AGENTS.md hard rule 4):
/// a schema or table name goes through <see cref="SqlIdentifier" />, and a value is always one of
/// <see cref="Parameters" />. The gatherer hands the result to <c>HealthCheckContext.Sql</c>, which runs it
/// in <see cref="ReadOnlySqlSession" />'s read-only transaction with <c>search_path</c> pinned to
/// <c>pg_catalog</c> - so every name Postgres deparses comes back qualified - and records it for "Show SQL".
/// </para>
/// <para>
/// <b>A catalog read, and nothing more.</b> A health statement reads <c>pg_catalog</c> and the statistics
/// views (tier 1), or a bounded slice of data behind "Run deeper checks" (tier 2). It never calls a function
/// that takes a lock per object it lists - no <c>pg_partition_*</c>, no <c>pg_sequence_last_value</c>, no
/// size function over a list (D27: a list locks nothing it lists) - and the live no-lock helper is what
/// proves a sweep kept to that.
/// </para>
/// </remarks>
/// <param name="Sql">The statement.</param>
/// <param name="Parameters">
/// What it binds. Created fresh by the builder for every query: an <see cref="NpgsqlParameter" /> belongs to
/// one command.
/// </param>
internal sealed record HealthQuery(string Sql, IReadOnlyList<NpgsqlParameter> Parameters)
{
    /// <summary>A statement with nothing to bind.</summary>
    public HealthQuery(string sql) : this(sql, [])
    {
    }

    /// <summary>The parameter names as the statement spells them, for "Show SQL" - never their values.</summary>
    public IReadOnlyList<string> ParameterNames =>
        [.. Parameters.Select(static x => "@" + x.ParameterName.TrimStart('@', ':'))];
}
