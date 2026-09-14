using System.Globalization;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Query;

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
    /// Why a <c>where</c> clause is refused before it is handed to Marten, or <see langword="null" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This one is load-bearing.</b> Mode A needs no capability, and Marten's string query runs on an
    /// ordinary session - not inside the SQL console's read-only transaction - so a clause that could carry
    /// a second statement would <em>be</em> an ungated SQL console: <c>1 = 1; drop table x</c> arrives at
    /// Npgsql as one command holding two statements, and Postgres runs both. So a clause has to be what it
    /// says it is: a fragment of the one statement the studio composed.
    /// </para>
    /// <para>
    /// Two rules, both about the shape rather than the meaning. There is no <c>;</c> outside a string, a
    /// quoted identifier, a dollar-quoted body or a comment; and the clause does not begin a statement of
    /// its own (<c>select</c>, <c>with</c>, …), because Marten would then run it <em>instead of</em> the
    /// document query and the collection picker would be decoration. What remains possible - a subquery or
    /// a <c>union</c> reading another table this connection can reach - is a read, and it is the residual
    /// risk this mode shares with every filter box that takes SQL.
    /// </para>
    /// </remarks>
    public static SqlRejection? RejectionFor(string? clause)
    {
        if (string.IsNullOrWhiteSpace(clause))
        {
            return null;
        }

        int separator = IndexOfStatementSeparator(clause);

        if (separator >= 0)
        {
            return new SqlRejection(
                nameof(SqlRejectionReason.MultipleStatements),
                "A where clause is part of one statement, so it cannot contain ';'. Whole statements run in " +
                "the SQL console, which is gated on " + StudioCapabilityGuard.OptionName(StudioCapability.RunSql) + ".",
                ";",
                separator);
        }

        string trimmed = clause.TrimStart();
        int offset = clause.Length - trimmed.Length;

        foreach (string keyword in StatementStarters)
        {
            if (StartsWithWord(trimmed, keyword))
            {
                return new SqlRejection(
                    nameof(SqlRejectionReason.DisallowedStatement),
                    $"A where clause filters the collection you picked; it cannot start with '{keyword}', which " +
                    "would replace the query altogether. Whole statements run in the SQL console, which is gated " +
                    "on " + StudioCapabilityGuard.OptionName(StudioCapability.RunSql) + ".",
                    keyword,
                    offset);
            }
        }

        return null;
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
