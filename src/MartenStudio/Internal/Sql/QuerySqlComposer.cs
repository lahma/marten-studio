using System.Globalization;
using System.Text;

using JasperFx.MultiTenancy;

using MartenStudio.Services.Query;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>One value the composed Mode A statement binds.</summary>
/// <remarks>
/// A value, not text: the statement carries <c>@tenant</c>, <c>@limit</c> and <c>@offset</c> as
/// placeholders and the caller binds them onto whichever command it is about to run - the query itself,
/// and the <c>EXPLAIN</c> of the very same statement. That is what makes "the SQL tab shows what ran"
/// true rather than nearly true.
/// </remarks>
/// <param name="Name">The parameter name, without the <c>@</c>.</param>
/// <param name="DbType">The Postgres type it is bound as.</param>
/// <param name="Value">The value.</param>
internal sealed record QuerySqlParameter(string Name, NpgsqlDbType DbType, object Value)
{
    /// <summary>The placeholder as it appears in the statement.</summary>
    public string Placeholder => "@" + Name;

    /// <summary>How the page lists the binding under the SQL tab.</summary>
    public string Describe() => Placeholder + " = " + (Value is string text
        ? "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'"
        : Convert.ToString(Value, CultureInfo.InvariantCulture) ?? string.Empty);

    /// <summary>Binds this value onto <paramref name="command" />.</summary>
    public void AddTo(NpgsqlCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        command.Parameters.Add(new NpgsqlParameter(Name, DbType) { Value = Value });
    }
}

/// <summary>
/// The three parts a Mode A clause is taken apart into: the predicate, and the <c>order by</c> /
/// <c>limit</c> / <c>offset</c> tail that cannot live inside a parenthesised predicate.
/// </summary>
/// <param name="Predicate">
/// The filter, with any leading <c>where</c> removed. Empty when the clause was only a tail.
/// </param>
/// <param name="OrderBy">The sort list as typed, without the <c>order by</c> keywords.</param>
/// <param name="Limit">The row cap the clause asked for, before the studio's own clamp.</param>
/// <param name="Offset">The offset the clause asked for.</param>
internal readonly record struct ClauseParts(string Predicate, string? OrderBy, int? Limit, int? Offset)
{
    /// <summary>An empty clause: everything matches and nothing is ordered.</summary>
    public static ClauseParts Empty { get; } = new(string.Empty, null, null, null);
}

/// <summary>
/// What the studio composed for a Marten <c>where</c> clause: the statement it runs, the values it binds,
/// and what its own predicates did to the result.
/// </summary>
/// <param name="Statement">The statement, which is both what runs and what the SQL tab shows.</param>
/// <param name="Parameters">The values bound to it.</param>
/// <param name="Predicate">The visitor's own predicate, as it appears inside the parentheses.</param>
/// <param name="OrderBy">The visitor's sort list, or <see langword="null" />.</param>
/// <param name="Limit">The row cap that was applied.</param>
/// <param name="LimitApplied">
/// Whether the cap is the studio's page size rather than a <c>limit</c> the clause carried.
/// </param>
/// <param name="Offset">The offset the clause asked for, or <see langword="null" />.</param>
/// <param name="TenantId">The tenant the statement filters on, or <see langword="null" /> for none.</param>
/// <param name="CrossTenant">
/// Whether this is a conjoined-tenancy collection being read across every tenant, because the scope named
/// none. The result header says so: a grid silently mixing tenants is the failure this flag exists for.
/// </param>
/// <param name="Deleted">What the statement does about soft-deleted rows.</param>
/// <param name="TenantOrdinal">Where <c>tenant_id</c> is in the select list, or <c>-1</c>.</param>
/// <param name="DeletedOrdinal">Where <c>mt_deleted</c> is in the select list, or <c>-1</c>.</param>
internal sealed record ComposedQuery(
    string Statement,
    IReadOnlyList<QuerySqlParameter> Parameters,
    string Predicate,
    string? OrderBy,
    int Limit,
    bool LimitApplied,
    int? Offset,
    string? TenantId,
    bool CrossTenant,
    DeletedFilter Deleted,
    int TenantOrdinal,
    int DeletedOrdinal)
{
    /// <summary>The ordinal of the <c>id</c> column, which every composed statement selects first.</summary>
    public const int IdOrdinal = 0;

    /// <summary>The ordinal of the <c>data</c> column, which every composed statement selects second.</summary>
    public const int DataOrdinal = 1;

    /// <summary>The bindings as the page lists them under the SQL tab.</summary>
    public IReadOnlyList<string> DescribeParameters()
    {
        List<string> described = new(Parameters.Count);

        foreach (QuerySqlParameter parameter in Parameters)
        {
            described.Add(parameter.Describe());
        }

        return described;
    }

    /// <summary>Binds every value onto <paramref name="command" />.</summary>
    public void Bind(NpgsqlCommand command)
    {
        foreach (QuerySqlParameter parameter in Parameters)
        {
            parameter.AddTo(command);
        }
    }
}

/// <summary>
/// Builds the statement behind a Marten <c>where</c> clause - the studio's own statement, not Marten's.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, and why it no longer imitates Marten.</b> Mode A used to hand the clause to
/// <c>session.QueryAsync(type, clause)</c>. Marten composes that through
/// <c>UserSuppliedQueryHandler</c> + <c>DocumentStorage.Apply</c>, which produce
/// <c>select … from &lt;table&gt; as d &lt;clause&gt;</c> and <b>nothing else</b>: no tenant filter and no
/// soft-delete filter, because a user-supplied string query bypasses the LINQ pipeline that would add
/// them. On a conjoined, soft-deleted collection that meant <c>where 1 = 1</c> typed by somebody scoped to
/// one tenant returned every tenant's rows and every deleted row (verified live against Marten 9.35). The
/// studio therefore composes and runs its own statement, with its own predicates in front of the
/// visitor's - which also means the SQL tab is byte-identical to what ran and <c>EXPLAIN</c> explains the
/// same statement.
/// </para>
/// <para>
/// <b>The shape.</b> <c>where 1 = 1</c> and then one <c>and</c> per term, the same way
/// <see cref="DocumentQueryBuilder" /> does it, so the statement reads the same whatever the scope is:
/// </para>
/// <code>
/// select d."id", d."data"::text[, d."tenant_id"][, d."mt_deleted"]
/// from "schema"."table" as d
/// where 1 = 1
///   and d."tenant_id" = @tenant
///   and d."mt_deleted" = false
///   and (&lt;the visitor's predicate&gt;)
/// order by &lt;the visitor's sort list&gt;
/// limit @limit
/// </code>
/// <para>
/// The visitor's predicate is <b>parenthesised and last</b>, so no amount of <c>or</c> in it can reach
/// around the studio's own terms: <c>where 1 = 1 and tenant_id = @tenant and (a = 1 or true)</c> still
/// only sees one tenant.
/// </para>
/// <para>
/// <b><c>order by</c>, <c>limit</c> and <c>offset</c> are split off deterministically</b>, because neither
/// belongs inside a parenthesised predicate. The split happens at the first occurrence of
/// <c>order by</c>, <c>limit</c> or <c>offset</c> that is at <em>parenthesis depth zero</em> and outside
/// every string, quoted identifier, dollar-quoted body and comment - so a window function's
/// <c>over (order by …)</c>, an aggregate's <c>order by</c> and a subquery's own <c>limit</c> are all left
/// exactly where they were. What follows must then be
/// <c>[order by …] [limit &lt;integer&gt;] [offset &lt;integer&gt;]</c> and nothing else; anything else is
/// refused by name rather than guessed at. A <c>limit</c> the visitor wrote is clamped down to the
/// studio's page size and never up.
/// </para>
/// <para>
/// <b>Identifiers are quoted, values are parameters</b> (AGENTS.md hard rule 4). The table and every
/// column go through <see cref="SqlIdentifier" />; the tenant, the row cap and the offset are bound. The
/// predicate and the sort list are the visitor's own SQL and are never escaped - a <c>where</c> clause
/// <em>is</em> SQL - which is exactly why <see cref="CheckClause" /> holds it to being one fragment of one
/// statement.
/// </para>
/// </remarks>
internal static class QuerySqlComposer
{
    /// <summary>The table alias, matching Marten's own. A constant, never input.</summary>
    public const string TableAlias = DocumentTableInfo.SqlAlias;

    /// <summary>The parameter the tenant predicate binds.</summary>
    public const string TenantParameter = "tenant";

    /// <summary>The parameter the row cap binds.</summary>
    public const string LimitParameter = "limit";

    /// <summary>The parameter the offset binds.</summary>
    public const string OffsetParameter = "offset";

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
    /// The entries this composer refuses on top of <see cref="ReadOnlySqlGuard.DisallowedFunctions" />,
    /// each because a <c>where</c> clause is not a console statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>pg_sleep</c> is here although the console allows it: the console bounds it with a server-side
    /// <c>statement_timeout</c> inside its own read-only transaction, while a Mode A read carries only a
    /// client-side <c>CommandTimeout</c> - which breaks the connection rather than producing a 57014 the
    /// page can render - so the same call is a way to park a connection. The rest read the catalog, run a
    /// statement of their own behind a function call (<c>query_to_xml</c>, <c>xpath</c>) or move a sequence
    /// (<c>nextval</c>, <c>setval</c>) - none of which is a filter on the collection that was picked.
    /// </para>
    /// </remarks>
    internal static readonly string[] AdditionalDisallowedFunctions =
    [
        "pg_sleep", "current_setting", "query_to_xml", "query_to_xml_and_xmlschema", "table_to_xml",
        "xpath", "xpath_exists", "nextval", "setval",
    ];

    /// <summary>
    /// Functions that read, write or wait outside the row they are given. Refused unless the visitor may
    /// run SQL.
    /// </summary>
    /// <remarks>
    /// <b>One list, two callers.</b> <see cref="ReadOnlySqlGuard.DisallowedFunctions" /> is the same
    /// question asked of a different text, so it is unioned in here rather than restated: two lists
    /// answering it differently is exactly the drift a reviewer cannot see, and a function added to the
    /// console's denylist now reaches the ungated <c>where</c>-clause mode for free. What this file owns
    /// is only the difference, <see cref="AdditionalDisallowedFunctions" />.
    /// </remarks>
    internal static readonly IReadOnlySet<string> DisallowedFunctions =
        new HashSet<string>(
            ReadOnlySqlGuard.DisallowedFunctions.Keys.Concat(AdditionalDisallowedFunctions),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Casts that turn text into a database object, and so into a probe of the catalog.</summary>
    internal static readonly string[] DisallowedCastTargets = ["regclass", "regproc"];

    /// <summary>
    /// The option a host sets to lift the nested-read rules. Spelled out rather than read from
    /// <c>StudioCapabilityGuard</c> so this file stays inside <c>Internal/Sql</c> and depends on nothing
    /// above it; a unit test holds the two spellings together.
    /// </summary>
    internal const string RunSqlOption = "MartenStudioOptions.Capabilities.RunSql";

    /// <summary>
    /// Whether a <c>where</c> clause may be run, and why not.
    /// </summary>
    /// <param name="clause">The clause as typed.</param>
    /// <param name="allowNestedReads">
    /// Whether the visitor may run SQL - <c>RunSql</c> enabled <em>and</em> allowed by the write policy. A
    /// visitor who may is left alone by the nested-read rules: subqueries, unions and <c>lateral</c> joins
    /// are the point of the mode for them, and everything they would be refused here they could type into
    /// the console instead.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>This is load-bearing.</b> Mode A needs no capability and does not run inside the SQL console's
    /// read-only transaction, so a clause that carried a second statement would <em>be</em> an ungated SQL
    /// console: <c>1 = 1; drop table x</c> arrives at Npgsql as one command holding two statements, and
    /// Postgres runs both.
    /// </para>
    /// <para>
    /// Four rules, in order, all structural rather than semantic. <b>Always:</b> no <c>;</c> outside a
    /// string, a quoted identifier, a dollar-quoted body or a comment; the clause does not begin a
    /// statement of its own; and its <c>order by</c> / <c>limit</c> / <c>offset</c> tail, if it has one, is
    /// a shape the composer can place rather than guess at (<see cref="TryPartition" />).
    /// <b>Without <c>RunSql</c>:</b> no word that reaches another relation
    /// (<see cref="NestedReadKeywords" />), no function that reads, writes or waits outside the row
    /// (<see cref="DisallowedFunctions" />), and no <c>::regclass</c>/<c>::regproc</c> cast. What is left
    /// reads the selected table's own columns and JSON - which the visitor may see anyway, so the clause
    /// grants nothing the collection browser did not already.
    /// </para>
    /// <para>
    /// It is a scanner, not a parser. It is not the security boundary for the console (that is the
    /// transaction, D13); it is the boundary for the <em>ungated</em> mode, which is why it errs towards
    /// refusing and why every refusal names the capability that lifts it. The tenant and soft-delete
    /// predicates are not its job at all - those are composed in, in front of the visitor's predicate, and
    /// hold whatever the clause says.
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

        // The tail has to be placeable, for everybody: the studio's own predicates go in front of the
        // visitor's, so `order by`, `limit` and `offset` cannot stay inside the parenthesised predicate and
        // have to be lifted out. A tail the composer cannot read is refused by name rather than guessed at.
        if (!TryPartition(clause, out _, out SqlGuardResult tail))
        {
            return tail;
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

                if (DisallowedFunctions.Contains(word))
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
    /// Composes the statement for <paramref name="clause" /> against <paramref name="table" />, with the
    /// studio's own tenant and soft-delete predicates in front of the visitor's.
    /// </summary>
    /// <param name="table">The document table, as Marten describes it and the catalog confirmed it.</param>
    /// <param name="clause">The clause as typed, which may be empty.</param>
    /// <param name="limit">The studio's row cap. A <c>limit</c> in the clause is clamped down to it.</param>
    /// <param name="tenantId">
    /// The tenant in scope, or <see langword="null" />. Only a conjoined-tenancy collection gets a
    /// predicate; a single-tenant table has no <c>tenant_id</c> column to filter on and filtering a shared
    /// collection by a tenant nobody wrote would hide every row.
    /// </param>
    /// <param name="deleted">
    /// What to do about soft-deleted rows. Coerced to <see cref="DeletedFilter.Exclude" /> - which emits
    /// no predicate at all - on a collection that is not soft-deleted, because such a table has no
    /// <c>mt_deleted</c> column and no deleted rows to show.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The table is conjoined or soft-deleted and the corresponding metadata column is not there, so the
    /// predicate that would keep the read honest cannot be written. Refusing is the only safe answer:
    /// composing without it is the leak this whole method exists to close.
    /// </exception>
    public static ComposedQuery Compose(
        DocumentTableInfo table,
        string? clause,
        int limit,
        string? tenantId = null,
        DeletedFilter deleted = DeletedFilter.Exclude)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        // A clause that will not partition has already been refused by CheckClause; composing one anyway
        // only ever happens on the refusal path, where the statement is shown and never sent.
        if (!TryPartition(clause, out ClauseParts parts, out _))
        {
            parts = new ClauseParts(StripLeadingWhere((clause ?? string.Empty).Trim()), null, null, null);
        }

        bool conjoined = table.TenancyStyle == TenancyStyle.Conjoined;
        bool filterTenant = conjoined && !string.IsNullOrEmpty(tenantId);
        DeletedFilter effectiveDeleted = table.SoftDeleteEnabled ? deleted : DeletedFilter.Exclude;

        string? tenantColumn = conjoined ? RequireColumn(table, DocumentMetadataColumn.TenantId) : null;
        string? deletedColumn = table.SoftDeleteEnabled
            ? RequireColumn(table, DocumentMetadataColumn.IsSoftDeleted)
            : null;

        int effectiveLimit = parts.Limit is { } ownLimit ? Math.Clamp(ownLimit, 0, limit) : limit;
        List<QuerySqlParameter> parameters = [];

        var sql = new StringBuilder();
        var tenantOrdinal = -1;
        var deletedOrdinal = -1;
        var ordinal = 2;

        sql.Append("select ").Append(Column(DocumentTableInfo.IdColumn))
            .Append(", ").Append(Column(DocumentTableInfo.DataColumn)).Append("::text");

        if (tenantColumn is not null)
        {
            tenantOrdinal = ordinal++;
            sql.Append(", ").Append(Column(tenantColumn));
        }

        if (deletedColumn is not null)
        {
            deletedOrdinal = ordinal;
            sql.Append(", ").Append(Column(deletedColumn));
        }

        sql.Append('\n');
        sql.Append("from ").Append(table.QualifiedName).Append(" as " + TableAlias).Append('\n');

        // `where 1 = 1` and then one `and` per term, so the shape of the statement does not change with the
        // number of terms - which is what makes the SQL tab readable, and what DocumentQueryBuilder does.
        sql.Append("where 1 = 1\n");

        if (filterTenant && tenantColumn is not null)
        {
            parameters.Add(new QuerySqlParameter(TenantParameter, NpgsqlDbType.Varchar, tenantId!));
            sql.Append("  and ").Append(Column(tenantColumn)).Append(" = @").Append(TenantParameter).Append('\n');
        }

        if (deletedColumn is not null && effectiveDeleted != DeletedFilter.Include)
        {
            sql.Append("  and ").Append(Column(deletedColumn))
                .Append(effectiveDeleted == DeletedFilter.Only ? " = true\n" : " = false\n");
        }

        if (parts.Predicate.Length > 0)
        {
            // Parenthesised, and last: an `or` in the visitor's predicate cannot reach around the terms
            // above it, which is the whole point of composing rather than appending. The closing bracket
            // goes on a line of its own because a clause may end in a `--` comment, and a bracket inside
            // one is a bracket that is not there.
            sql.Append("  and (\n    ").Append(parts.Predicate).Append("\n  )\n");
        }

        if (parts.OrderBy is { Length: > 0 } orderBy)
        {
            sql.Append("order by ").Append(orderBy).Append('\n');
        }

        parameters.Add(new QuerySqlParameter(LimitParameter, NpgsqlDbType.Integer, effectiveLimit));
        sql.Append("limit @").Append(LimitParameter);

        if (parts.Offset is { } offset)
        {
            parameters.Add(new QuerySqlParameter(OffsetParameter, NpgsqlDbType.Integer, offset));
            sql.Append("\noffset @").Append(OffsetParameter);
        }

        return new ComposedQuery(
            sql.ToString(),
            parameters,
            parts.Predicate,
            parts.OrderBy,
            effectiveLimit,
            parts.Limit is null,
            parts.Offset,
            filterTenant ? tenantId : null,
            conjoined && !filterTenant,
            effectiveDeleted,
            tenantOrdinal,
            deletedOrdinal);
    }

    /// <summary>
    /// Takes a clause apart into the predicate and its <c>order by</c> / <c>limit</c> / <c>offset</c> tail.
    /// </summary>
    /// <param name="clause">The clause as typed.</param>
    /// <param name="parts">The pieces, when it could be taken apart.</param>
    /// <param name="rejection">Why it could not be, when it could not.</param>
    /// <returns><see langword="true" /> when the clause was partitioned.</returns>
    /// <remarks>
    /// <para>
    /// The split point is the first <c>order by</c>, <c>limit</c> or <c>offset</c> at <b>parenthesis depth
    /// zero</b>, outside every string, quoted identifier, dollar-quoted body and comment. Depth is what
    /// makes it safe: <c>rank() over (order by x)</c>, <c>string_agg(x, ',' order by y)</c> and
    /// <c>id in (select id from t limit 5)</c> all sit at depth one or more and are left in the predicate
    /// where they belong. <c>order</c> only counts when the very next word is <c>by</c>, so a column called
    /// <c>order</c> is not a split point; <c>limit</c> and <c>offset</c> are reserved words in Postgres and
    /// cannot be unquoted column names at all.
    /// </para>
    /// <para>
    /// After the split the tail must be exactly <c>[order by …] [limit n] [offset n]</c>, each at most
    /// once, <c>order by</c> first, and the two counts plain non-negative integers. Anything else - a
    /// second <c>limit</c>, <c>limit all</c>, an expression where a number should be - is refused with the
    /// token named. A clause whose quoting is unreadable is not partitioned either; the guard refuses that
    /// separately, and on the display path the whole clause stays in the predicate.
    /// </para>
    /// </remarks>
    internal static bool TryPartition(string? clause, out ClauseParts parts, out SqlGuardResult rejection)
    {
        parts = ClauseParts.Empty;
        rejection = SqlGuardResult.Allow(string.Empty, 0);

        string text = (clause ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return true;
        }

        int bodyStart = StartsWithWord(text, "where") ? "where".Length : 0;

        if (!TryFindTailKeywords(text, bodyStart, out List<TailKeyword> keywords))
        {
            // Unreadable quoting. CheckClause refuses it on its own; here the honest answer is "no tail".
            parts = new ClauseParts(text[bodyStart..].Trim(), null, null, null);
            return true;
        }

        if (keywords.Count == 0)
        {
            parts = new ClauseParts(text[bodyStart..].Trim(), null, null, null);
            return true;
        }

        string predicate = text[bodyStart..keywords[0].Start].Trim();
        string? orderBy = null;
        int? limit = null;
        int? offset = null;

        for (var i = 0; i < keywords.Count; i++)
        {
            TailKeyword keyword = keywords[i];
            int end = i + 1 < keywords.Count ? keywords[i + 1].Start : text.Length;
            string value = text[keyword.ValueStart..end].Trim();

            if (string.Equals(keyword.Kind, TailKind.OrderBy, StringComparison.Ordinal))
            {
                if (i != 0)
                {
                    rejection = RejectTail(
                        "'order by' has to come before 'limit' and 'offset', and may only appear once.",
                        TailKind.OrderBy,
                        keyword.Start);
                    return false;
                }

                if (value.Length == 0)
                {
                    rejection = RejectTail(
                        "'order by' needs something to sort on.", TailKind.OrderBy, keyword.Start);
                    return false;
                }

                orderBy = value;
                continue;
            }

            bool isLimit = string.Equals(keyword.Kind, TailKind.Limit, StringComparison.Ordinal);

            if (isLimit ? limit is not null : offset is not null)
            {
                rejection = RejectTail(
                    $"A clause can only carry one '{keyword.Kind}'.", keyword.Kind, keyword.Start);
                return false;
            }

            if (!TryParseCount(value, out int parsed))
            {
                rejection = RejectTail(
                    $"'{keyword.Kind} {value}' is not a whole number the studio can place. Write " +
                    $"'{keyword.Kind}' and a non-negative integer, or leave it out - the Rows selector is the " +
                    "row cap.",
                    keyword.Kind,
                    keyword.Start);
                return false;
            }

            if (isLimit)
            {
                limit = parsed;
            }
            else
            {
                offset = parsed;
            }
        }

        parts = new ClauseParts(predicate, orderBy, limit, offset);
        return true;
    }

    private static SqlGuardResult RejectTail(string message, string token, int position) =>
        SqlGuardResult.Reject(SqlRejectionReason.DisallowedStatement, message, token, position);

    /// <summary>Which of the three tail keywords this is.</summary>
    private static class TailKind
    {
        public const string OrderBy = "order by";
        public const string Limit = "limit";
        public const string Offset = "offset";
    }

    /// <summary>One tail keyword: where it starts, and where its value starts.</summary>
    private readonly record struct TailKeyword(string Kind, int Start, int ValueStart);

    /// <summary>
    /// Every <c>order by</c>, <c>limit</c> and <c>offset</c> at parenthesis depth zero, in order.
    /// </summary>
    /// <returns><see langword="false" /> when the clause's quoting cannot be read.</returns>
    private static bool TryFindTailKeywords(string sql, int from, out List<TailKeyword> keywords)
    {
        keywords = [];

        var depth = 0;
        var i = 0;
        var sawOrder = false;
        var pendingOrderStart = 0;

        while (i < sql.Length)
        {
            char c = sql[i];

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
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);

                if (end < 0)
                {
                    return false;
                }

                i = end + 2;
                continue;
            }

            if (c is '\'' or '"')
            {
                int end = sql.IndexOf(c, i + 1);

                if (end < 0)
                {
                    return false;
                }

                i = end + 1;
                continue;
            }

            if (c == '$' && TryReadDollarTag(sql, i, out string tag))
            {
                int end = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);

                if (end < 0)
                {
                    return false;
                }

                i = end + tag.Length;
                continue;
            }

            if (c == '(')
            {
                depth++;
                i++;
                continue;
            }

            if (c == ')')
            {
                depth--;
                i++;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;

                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                {
                    i++;
                }

                string word = sql[start..i];

                if (sawOrder)
                {
                    if (depth == 0 && string.Equals(word, "by", StringComparison.OrdinalIgnoreCase))
                    {
                        keywords.Add(new TailKeyword(TailKind.OrderBy, pendingOrderStart, i));
                    }

                    sawOrder = false;
                }

                if (depth != 0 || start < from)
                {
                    continue;
                }

                if (string.Equals(word, "order", StringComparison.OrdinalIgnoreCase))
                {
                    sawOrder = true;
                    pendingOrderStart = start;
                }
                else if (string.Equals(word, TailKind.Limit, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(word, TailKind.Offset, StringComparison.OrdinalIgnoreCase))
                {
                    keywords.Add(new TailKeyword(word.ToLowerInvariant(), start, i));
                }

                continue;
            }

            i++;
        }

        return true;
    }

    private static bool TryParseCount(string value, out int count) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count >= 0;

    private static string StripLeadingWhere(string text) =>
        StartsWithWord(text, "where") ? text["where".Length..].Trim() : text;

    private static string RequireColumn(DocumentTableInfo table, DocumentMetadataColumn column) =>
        table.MetadataColumnName(column)
        ?? throw new ArgumentException(
            $"'{table.Alias}' needs the {column} column to be read safely, and the table does not have it. " +
            "Running the clause without that predicate would show rows this scope must not see.",
            nameof(table));

    private static string Column(string name) => TableAlias + "." + SqlIdentifier.Quote(name);

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
    /// It is deliberately not a parser, and it is not depth-aware: use <see cref="TryPartition" /> when the
    /// answer has to distinguish a clause's own <c>limit</c> from one inside a subquery. This answers the
    /// simpler question conservatively - a keyword inside <c>'…'</c>, <c>"…"</c>, <c>$$…$$</c>, <c>--</c>
    /// or <c>/* */</c> does not count, and anything it is unsure about counts as present.
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
