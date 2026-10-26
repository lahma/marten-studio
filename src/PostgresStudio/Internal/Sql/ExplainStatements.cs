namespace PostgresStudio.Internal.Sql;

/// <summary>How the SQL console is asked to run a statement: as it is, or explained (X1).</summary>
internal enum SqlExplain
{
    /// <summary>As typed.</summary>
    None,

    /// <summary><c>EXPLAIN (FORMAT JSON)</c> of it: planned, never run.</summary>
    Plan,

    /// <summary>
    /// <c>EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)</c> of it: run, inside the console's read-only transaction and under its
    /// <c>statement_timeout</c>, and measured (K1).
    /// </summary>
    Analyze,
}

/// <summary>
/// The SQL console's plan statements (X1): the prefixes its Explain buttons put in front of a statement, and the column a plan
/// comes back in. One of the two places an identifier or a keyword becomes SQL text (hard rule 4), though nothing here takes
/// one: the prefixes are constants.
/// </summary>
/// <remarks>
/// <para>
/// <b>The statement after the prefix is the visitor's, checked first.</b> The console's guard reads the statement as it was
/// typed - so a refusal points at the editor's own characters - and the prefix is added only to a statement the guard lets
/// through and that is not an <c>EXPLAIN</c> already (<c>SqlConsoleService</c>). <c>BUFFERS</c> is asked for on every major:
/// PostgreSQL 18 prints buffers under <c>ANALYZE</c> without it, 14 to 17 only with it, and every one accepts it.
/// </para>
/// <para>
/// <b><c>ANALYZE</c> runs the statement.</b> That is why it is the console's alone (K1): under <c>RunSql</c>, for a statement the
/// guard accepts, in the read-only transaction that refuses a write the guard did not spot with <c>25006</c> on every major from
/// 14 to 19 (PROBE, 2026-10-26).
/// </para>
/// </remarks>
internal static class ExplainStatements
{
    /// <summary>What a plain plan is asked with.</summary>
    internal const string PlanPrefix = "explain (format json) ";

    /// <summary>What a measured plan is asked with.</summary>
    internal const string AnalyzePrefix = "explain (analyze, buffers, format json) ";

    /// <summary>The one column <c>EXPLAIN (FORMAT JSON)</c> returns its plan in, as one <c>json</c> value.</summary>
    internal const string PlanColumn = "QUERY PLAN";

    /// <summary>The most characters of plan JSON the console keeps whole for a plan view: 4 MiB.</summary>
    internal const int MaxPlanCharacters = 4 * 1024 * 1024;

    /// <summary><paramref name="statement" /> as <paramref name="mode" /> asks for it to be sent.</summary>
    public static string Wrap(string statement, SqlExplain mode)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return mode switch
        {
            SqlExplain.Plan => PlanPrefix + statement,
            SqlExplain.Analyze => AnalyzePrefix + statement,
            _ => statement,
        };
    }

    /// <summary>Whether a result's columns are the one <c>json</c> column <c>EXPLAIN (FORMAT JSON)</c> returns.</summary>
    public static bool IsPlanColumn(string name, string postgresType) =>
        string.Equals(name, PlanColumn, StringComparison.Ordinal) && string.Equals(postgresType, "json", StringComparison.Ordinal);
}
