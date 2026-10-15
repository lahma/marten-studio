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
/// <para>
/// <b>Fenced, and sealed after construction.</b> <c>HealthQueryConventionTests</c> refuses the identifier
/// <c>HealthQuery</c> anywhere in <c>src/</c> outside this folder and the runner, and scans the compiled
/// assembly for a constructor call from any type outside this namespace - so a check holds one only as a
/// <c>var</c> a builder handed it. Both properties are get-only, not the record's default <c>init</c>, so
/// <c>query with { Sql = query.Sql + name }</c> does not compile anywhere: the text a builder made is the text
/// that runs.
/// </para>
/// </remarks>
/// <param name="Sql">The statement.</param>
/// <param name="Parameters">
/// What it binds. The runner binds copies (<c>NpgsqlParameter.Clone()</c>), so a builder may hand the same
/// query out twice.
/// </param>
internal sealed record HealthQuery(string Sql, IReadOnlyList<NpgsqlParameter> Parameters)
{
    /// <summary>A statement with nothing to bind.</summary>
    public HealthQuery(string sql) : this(sql, [])
    {
    }

    /// <summary>The statement - get-only, so no <c>with</c> expression can rewrite it.</summary>
    public string Sql { get; } = Sql;

    /// <summary>What it binds - get-only for the same reason.</summary>
    public IReadOnlyList<NpgsqlParameter> Parameters { get; } = Parameters;

    /// <summary>The parameter names as the statement spells them, for "Show SQL" - never their values.</summary>
    public IReadOnlyList<string> ParameterNames =>
        [.. Parameters.Select(static x => "@" + x.ParameterName.TrimStart('@', ':'))];
}
