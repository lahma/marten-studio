using System.Globalization;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// What the studio composed for a Marten <c>where</c> clause: the statement it shows, and the clause it
/// actually hands to Marten.
/// </summary>
/// <param name="Statement">The full statement, for the SQL tab and for <c>EXPLAIN</c>.</param>
/// <param name="EffectiveClause">
/// The clause as it is passed to <c>session.Query(type, clause)</c> - the typed text, plus the row cap
/// when one was added.
/// </param>
/// <param name="Limit">The row cap that was in force.</param>
/// <param name="LimitApplied">Whether the cap was appended, or the clause already carried its own.</param>
internal sealed record ComposedQuery(string Statement, string EffectiveClause, int Limit, bool LimitApplied);

/// <summary>
/// Builds the statement behind a Marten <c>where</c> clause, the way Marten's own string-query handler
/// builds it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>session.Query(type, "where …")</c> has no <c>ToCommand()</c>: the LINQ
/// helpers that produce SQL (<c>ToCommand</c>, <c>ExplainAsync</c>) are generic in the document type and
/// take an <see cref="IQueryable{T}" />, and a string query never becomes one. The studio only ever has a
/// runtime <see cref="Type" />, so the only way to show a person the SQL behind their clause - and the
/// only way to get a plan for it - is to compose the same statement Marten does.
/// </para>
/// <para>
/// <b>The rule is Marten's, read from <c>UserSuppliedQueryHandler</c> (9.35).</b> A clause that starts
/// with <c>select</c> is the whole statement and the document's own select clause is not applied; a clause
/// starting with <c>where</c> or <c>order</c> is appended after a space; anything else gets <c>where</c>
/// put in front of it unless it already contains one. One deliberate difference: the select list here is
/// always <c>d.id, d.data</c>, while Marten's is whatever the session's storage style needs (it can carry
/// <c>mt_doc_type</c> for a hierarchy, or a duplicated column). That changes which columns come back, never
/// which rows, so the plan and the row count are the same - and the page says which columns it selected
/// rather than implying it is byte-for-byte Marten's.
/// </para>
/// <para>
/// <b>Identifiers are quoted, and nothing else here is generated.</b> The table name goes through
/// <see cref="SqlIdentifier" /> (AGENTS.md hard rule 4, the same way <c>TenantDiscovery</c> does it); the
/// clause is the visitor's own text and is never escaped, because a <c>where</c> clause <em>is</em> SQL -
/// which is why the statement is only ever sent to Postgres through Marten's session (a read) or inside
/// <see cref="ReadOnlySqlSession" />'s read-only transaction (the plan), and never concatenated with
/// anything else.
/// </para>
/// </remarks>
internal static class QuerySqlComposer
{
    /// <summary>The table alias, matching Marten's own. A constant, never input.</summary>
    public const string TableAlias = DocumentTableInfo.SqlAlias;

    /// <summary>The columns the composed statement selects.</summary>
    public const string SelectList = TableAlias + "." + DocumentTableInfo.IdColumn + ", " +
        TableAlias + "." + DocumentTableInfo.DataColumn;

    /// <summary>Words that begin a statement of their own, and therefore may not begin a clause.</summary>
    private static readonly string[] StatementStarters =
    [
        "select", "with", "insert", "update", "delete", "merge", "truncate", "drop", "alter", "create",
        "grant", "revoke", "copy", "explain", "call", "do", "vacuum", "analyze", "set", "reset", "table",
        "values", "begin", "commit", "rollback", "listen", "notify", "lock", "refresh", "reindex", "cluster",
        "comment", "security", "prepare", "execute", "deallocate", "discard", "import", "checkpoint",
    ];

    /// <summary>
    /// Words that make a clause reach outside the table it is filtering. Refused unless the visitor may
    /// run SQL.
    /// </summary>
    /// <remarks>
    /// <c>from</c> is in the list, which also refuses <c>extract(year from …)</c>, <c>substring(x from 1)</c>
    /// and <c>trim(both ' ' from x)</c>. That is a deliberate false positive: the list is structural rather
    /// than semantic, and the escape hatch is the capability rather than a cleverer parser - a parser
    /// treated as a security boundary is how these features get CVEs (D13).
    /// </remarks>
    internal static readonly string[] NestedReadKeywords =
    [
        "select", "from", "union", "intersect", "except", "join", "into", "with", "lateral", "returning",
        "copy", "do", "call", "execute",
    ];

    /// <summary>
    /// Functions that read, write or wait outside the row they are given. Refused unless the visitor may
    /// run SQL.
    /// </summary>
    /// <remarks>
    /// <b>When W2-fix's <c>ReadOnlySqlGuard.DisallowedFunctions</c> lands, union it into this list</b> - it
    /// is the same question asked of a different text, and two lists answering it differently is exactly
    /// the drift a reviewer cannot see. This is what could be named on a base without that set.
    /// </remarks>
    internal static readonly string[] DisallowedFunctions =
    [
        "pg_sleep", "pg_sleep_for", "pg_sleep_until", "set_config", "current_setting", "dblink",
        "dblink_exec", "dblink_connect", "query_to_xml", "query_to_xml_and_xmlschema", "table_to_xml",
        "xpath", "xpath_exists", "pg_read_file", "pg_read_binary_file", "pg_ls_dir", "pg_stat_file",
        "lo_import", "lo_export", "pg_terminate_backend", "pg_cancel_backend", "pg_advisory_lock",
        "pg_advisory_xact_lock", "pg_reload_conf", "pg_rotate_logfile", "nextval", "setval",
    ];

    /// <summary>Casts that turn text into a database object, and so into a probe of the catalog.</summary>
    internal static readonly string[] DisallowedCastTargets = ["regclass", "regproc"];

    /// <summary>
    /// The option a host sets to lift the nested-read rules. Spelled out rather than read from
    /// <c>StudioCapabilityGuard</c> so this file stays inside <c>Internal/Sql</c> and depends on nothing
    /// above it; a unit test holds the two spellings together.
    /// </summary>
    internal const string RunSqlOption = "MartenStudioOptions.Capabilities.RunSql";

    /// <summary>
    /// Whether a <c>where</c> clause may be handed to Marten, and why not.
    /// </summary>
    /// <param name="clause">The clause as typed.</param>
    /// <param name="allowNestedReads">
    /// Whether the visitor may run SQL - <c>RunSql</c> enabled <em>and</em> allowed by the write policy. A
    /// visitor who may is left alone: subqueries, unions and <c>lateral</c> joins are the point of the mode
    /// for them, and everything they would be refused here they could type into the console instead.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>This is load-bearing.</b> Mode A needs no capability, and Marten's string query runs on an
    /// ordinary session - not inside the SQL console's read-only transaction - so a clause that carried a
    /// second statement would <em>be</em> an ungated SQL console: <c>1 = 1; drop table x</c> arrives at
    /// Npgsql as one command holding two statements, and Postgres runs both.
    /// </para>
    /// <para>
    /// Three rules, in order, all structural rather than semantic. <b>Always:</b> no <c>;</c> outside a
    /// string, a quoted identifier, a dollar-quoted body or a comment, and the clause does not begin a
    /// statement of its own - either would stop it being a filter on the collection that was picked, and
    /// neither is something the console's read-only transaction would forgive here, because there is no
    /// transaction. <b>Without <c>RunSql</c>:</b> no word that reaches another relation
    /// (<see cref="NestedReadKeywords" />), no function that reads, writes or waits outside the row
    /// (<see cref="DisallowedFunctions" />), and no <c>::regclass</c>/<c>::regproc</c> cast. What is left
    /// reads the selected table's own columns and JSON - which the visitor may see anyway, so the clause
    /// grants nothing the collection browser did not already.
    /// </para>
    /// <para>
    /// It is a scanner, not a parser. It is not the security boundary for the console (that is the
    /// transaction, D13); it is the boundary for the <em>ungated</em> mode, which is why it errs towards
    /// refusing and why every refusal names the capability that lifts it.
    /// </para>
    /// </remarks>
    public static SqlGuardResult CheckClause(string? clause, bool allowNestedReads)
    {
        if (string.IsNullOrWhiteSpace(clause))
        {
            return SqlGuardResult.Allow(string.Empty, 0);
        }

        int separator = IndexOfStatementSeparator(clause);

        if (separator >= 0)
        {
            return SqlGuardResult.Reject(
                SqlRejectionReason.MultipleStatements,
                "A where clause is part of one statement, so it cannot contain ';'. Whole statements run in " +
                "the SQL console, which is gated on " + RunSqlOption + ".",
                ";",
                separator);
        }

        string trimmed = clause.TrimStart();
        int offset = clause.Length - trimmed.Length;

        foreach (string keyword in StatementStarters)
        {
            if (StartsWithWord(trimmed, keyword))
            {
                return SqlGuardResult.Reject(
                    SqlRejectionReason.DisallowedStatement,
                    $"A where clause filters the collection you picked; it cannot start with '{keyword}', which " +
                    "would replace the query altogether. Whole statements run in the SQL console, which is gated " +
                    "on " + RunSqlOption + ".",
                    keyword,
                    offset);
            }
        }

        return allowNestedReads ? SqlGuardResult.Allow(string.Empty, 0) : CheckForNestedReads(clause);
    }

    /// <summary>
    /// The clause, token by token, looking for the words, functions and casts that reach outside the table
    /// being filtered.
    /// </summary>
    /// <remarks>
    /// Strings, quoted identifiers, dollar-quoted bodies and comments are skipped - the same discipline
    /// <see cref="ReadOnlySqlGuard" /> uses - so a document whose value happens to be the word
    /// <c>select</c> is not a refusal, and a keyword hidden inside <c>$$…$$</c> is not an escape.
    /// </remarks>
    private static SqlGuardResult CheckForNestedReads(string clause)
    {
        var i = 0;

        while (i < clause.Length)
        {
            char c = clause[i];

            if (c == '-' && i + 1 < clause.Length && clause[i + 1] == '-')
            {
                while (i < clause.Length && clause[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < clause.Length && clause[i + 1] == '*')
            {
                var commentEnd = clause.IndexOf("*/", i + 2, StringComparison.Ordinal);

                if (commentEnd < 0)
                {
                    return Unreadable("/*", i, "an unterminated comment");
                }

                i = commentEnd + 2;
                continue;
            }

            if (c is '\'' or '"')
            {
                var quoteEnd = clause.IndexOf(c, i + 1);

                if (quoteEnd < 0)
                {
                    return Unreadable(c.ToString(), i, "an unterminated string");
                }

                i = quoteEnd + 1;
                continue;
            }

            if (c == '$' && TryReadDollarTag(clause, i, out var tag))
            {
                var bodyEnd = clause.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);

                if (bodyEnd < 0)
                {
                    return Unreadable(tag, i, "an unterminated quoted body");
                }

                i = bodyEnd + tag.Length;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;

                while (i < clause.Length && (char.IsLetterOrDigit(clause[i]) || clause[i] == '_'))
                {
                    i++;
                }

                var word = clause[start..i];

                if (Contains(NestedReadKeywords, word))
                {
                    return SqlGuardResult.Reject(
                        SqlRejectionReason.DisallowedStatement,
                        $"'{word}' makes this clause read outside the collection you picked. A filter that needs " +
                        "it is a query, and queries run in the SQL console, which is gated on " + RunSqlOption + ".",
                        word,
                        start);
                }

                if (Contains(DisallowedFunctions, word))
                {
                    return SqlGuardResult.Reject(
                        SqlRejectionReason.DisallowedStatement,
                        $"'{word}' reads, writes or waits outside the row it is given, which is not something a " +
                        "filter does. It runs in the SQL console, which is gated on " + RunSqlOption + ".",
                        word,
                        start);
                }

                if (start >= 2 && clause[start - 1] == ':' && clause[start - 2] == ':'
                    && Contains(DisallowedCastTargets, word))
                {
                    return SqlGuardResult.Reject(
                        SqlRejectionReason.DisallowedStatement,
                        $"A '::{word}' cast turns text into a database object, which is a probe of the catalog " +
                        "rather than a filter. It runs in the SQL console, which is gated on " + RunSqlOption + ".",
                        "::" + word,
                        start - 2);
                }

                continue;
            }

            i++;
        }

        return SqlGuardResult.Allow(string.Empty, 0);
    }

    private static SqlGuardResult Unreadable(string token, int position, string what) =>
        SqlGuardResult.Reject(
            SqlRejectionReason.DisallowedStatement,
            $"This clause has {what}, so the studio cannot tell what it would run. Fix the quoting, or use the " +
            "SQL console, which is gated on " + RunSqlOption + ".",
            token,
            position);

    private static bool Contains(string[] words, string word)
    {
        foreach (string candidate in words)
        {
            if (string.Equals(candidate, word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Composes the statement for <paramref name="clause" /> against <paramref name="qualifiedTable" />,
    /// and says what will actually be handed to Marten.
    /// </summary>
    /// <param name="qualifiedTable">The quoted, schema-qualified table name.</param>
    /// <param name="clause">The clause as typed, which may be empty.</param>
    /// <param name="limit">The row cap. Appended only when the clause has no <c>limit</c> of its own.</param>
    public static ComposedQuery Compose(string qualifiedTable, string? clause, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var trimmed = (clause ?? string.Empty).Trim();

        var hasOwnLimit = ContainsKeyword(trimmed, "limit");

        // An empty clause becomes `where 1 = 1 limit N` rather than a bare `limit N`, because Marten's own
        // handler puts `where` in front of anything that does not start with `where` or `order` - a bare
        // `limit 50` would arrive at Postgres as `… as d where limit 50` and be a syntax error. The clause
        // shown on the SQL tab is the one that ran, so the `1 = 1` is visible rather than implied.
        var effective = hasOwnLimit
            ? trimmed
            : (trimmed.Length == 0 ? "where 1 = 1" : trimmed) + " limit " + limit.ToString(CultureInfo.InvariantCulture);

        return new ComposedQuery(Statement(qualifiedTable, effective), effective, limit, !hasOwnLimit);
    }

    /// <summary>
    /// The statement a clause produces, following Marten's <c>UserSuppliedQueryHandler</c> rules.
    /// </summary>
    public static string Statement(string qualifiedTable, string clause)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
        ArgumentNullException.ThrowIfNull(clause);

        var trimmed = clause.Trim();

        // A clause that is already a select is the whole statement; Marten does not apply the document's
        // select clause to it either.
        if (StartsWithWord(trimmed, "select") || IsWithFollowedBySelect(trimmed))
        {
            return trimmed;
        }

        var selectClause = "select " + SelectList + " from " + qualifiedTable + " as " + TableAlias;

        if (trimmed.Length == 0)
        {
            return selectClause;
        }

        // Marten appends a clause starting with `where` or `order` after a space and puts `where` in front
        // of everything else. `limit` is deliberately not in that list, because it is not in Marten's
        // either: a clause of `limit 5` really does arrive as `… where limit 5`, and showing anything else
        // would be showing a statement that is not the one that ran.
        if (StartsWithWord(trimmed, "where") || StartsWithWord(trimmed, "order"))
        {
            return selectClause + " " + trimmed;
        }

        return ContainsKeyword(trimmed, "where")
            ? selectClause + " " + trimmed
            : selectClause + " where " + trimmed;
    }

    /// <summary>
    /// Wraps <paramref name="statement" /> in an <c>EXPLAIN</c> that plans it without running it.
    /// </summary>
    /// <remarks>
    /// Deliberately no <c>ANALYZE</c>. <c>EXPLAIN ANALYZE</c> <em>executes</em> the statement, which would
    /// turn "show me the plan" into "run this again", and the studio offers the plan for clauses a person
    /// is still writing.
    /// </remarks>
    public static string Explain(string statement, bool json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        return json ? "explain (format json) " + statement : "explain " + statement;
    }

    /// <summary>
    /// Whether <paramref name="sql" /> uses <paramref name="keyword" /> as a bare word - outside string
    /// literals, quoted identifiers and comments.
    /// </summary>
    /// <remarks>
    /// It is deliberately not a parser. It answers one question ("did they already write their own
    /// <c>limit</c>?"), and it answers it conservatively: a keyword inside <c>'…'</c>, <c>"…"</c>,
    /// <c>$$…$$</c>, <c>--</c> or <c>/* */</c> does not count, and anything it is unsure about counts as
    /// present, so the studio adds nothing rather than corrupting somebody's statement.
    /// </remarks>
    public static bool ContainsKeyword(string? sql, string keyword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyword);

        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var i = 0;

        while (i < sql.Length)
        {
            var c = sql[i];

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    // Unterminated: the guard will refuse it anyway, and "present" is the safe answer.
                    return true;
                }

                i = end + 2;
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = sql.IndexOf(c, i + 1);
                if (end < 0)
                {
                    return true;
                }

                i = end + 1;
                continue;
            }

            if (c == '$' && TryReadDollarTag(sql, i, out var tag))
            {
                var end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    return true;
                }

                i = end + tag.Length;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;

                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }

                if (string.Equals(sql[start..i], keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            i++;
        }

        return false;
    }

    /// <summary>
    /// Where the first statement separator is, or <c>-1</c>. Strings, quoted identifiers, dollar-quoted
    /// bodies and comments are skipped; anything unterminated counts as a separator, because the safe
    /// answer to "I cannot tell" is to refuse.
    /// </summary>
    internal static int IndexOfStatementSeparator(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var i = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

            if (c == ';')
            {
                return i;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);

                if (end < 0)
                {
                    return i;
                }

                i = end + 2;
                continue;
            }

            if (c is '\'' or '"')
            {
                var end = sql.IndexOf(c, i + 1);

                if (end < 0)
                {
                    return i;
                }

                i = end + 1;
                continue;
            }

            if (c == '$' && TryReadDollarTag(sql, i, out var tag))
            {
                var end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);

                if (end < 0)
                {
                    return i;
                }

                i = end + tag.Length;
                continue;
            }

            i++;
        }

        return -1;
    }

    private static bool StartsWithWord(string sql, string word)
    {
        if (!sql.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return sql.Length == word.Length || !char.IsLetterOrDigit(sql[word.Length]) && sql[word.Length] != '_';
    }

    /// <summary>
    /// A <c>with … select …</c> common table expression, which Marten also treats as a whole statement.
    /// </summary>
    private static bool IsWithFollowedBySelect(string sql) =>
        StartsWithWord(sql, "with") && ContainsKeyword(sql, "select");

    private static bool TryReadDollarTag(string sql, int position, out string tag)
    {
        var probe = position + 1;

        while (probe < sql.Length && (char.IsLetterOrDigit(sql[probe]) || sql[probe] == '_'))
        {
            probe++;
        }

        if (probe < sql.Length && sql[probe] == '$')
        {
            tag = sql[position..(probe + 1)];
            return true;
        }

        tag = string.Empty;
        return false;
    }
}
