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
///   and (
///     &lt;the visitor's predicate&gt;
///   )
///   and d."tenant_id" = @tenant
///   and d."mt_deleted" = false
/// order by &lt;the visitor's sort list&gt;
/// limit @limit
/// </code>
/// <para>
/// The visitor's predicate is <b>parenthesised</b>, so no amount of <c>or</c> in it can reach around the
/// studio's own terms: <c>where 1 = 1 and tenant_id = @tenant and (a = 1 or true)</c> still only sees one
/// tenant. That holds only while the parentheses are the ones the composer wrote, which is why
/// <see cref="CheckClause" /> refuses a clause whose own brackets do not balance: <c>1 = 1) or (1 = 1</c>
/// spliced into the shape above would close the studio's bracket early and leave its second half - and
/// with it every row of every tenant - outside the tenant predicate, because <c>and</c> binds tighter than
/// <c>or</c>. It was measured returning four rows where the honest answer was one.
/// </para>
/// <para>
/// <b>The studio's own terms are then repeated after the visitor's predicate</b>, which costs nothing and
/// is defence in depth <em>for a balanced clause</em>: while the predicate really is one parenthesised
/// term, <c>A and (X) and A</c> distributes over whatever <c>or</c> is inside <c>X</c>, so every disjunct
/// of the visitor's own predicate still carries the tenant and soft-delete terms.
/// <b>It is not a guarantee, and must never be described as one.</b> A clause whose brackets do not
/// balance is not "X" at all: an attacker-supplied <c>) … (</c> pair brackets a disjunct of its own that
/// sits between the studio's two copies and carries neither of them - <c>a and (1=1) or (true) or (1=1)
/// and a</c> has a middle disjunct with no predicate on either side, and the carriage-return spelling of
/// that escape was measured returning every tenant's rows against this very shape.
/// <b><see cref="CheckClause" />'s balance rule is the guarantee</b>; the repeat is what narrows the blast
/// radius of an ordinary <c>or</c>, not what stops the escape.
/// </para>
/// <para>
/// <b><c>order by</c>, <c>limit</c> and <c>offset</c> are split off deterministically</b>, because neither
/// belongs inside a parenthesised predicate. The split happens at the first occurrence of
/// <c>order by</c>, <c>limit</c> or <c>offset</c> that is at <em>parenthesis depth zero</em> and outside
/// every string, quoted identifier, dollar-quoted body and comment - so a window function's
/// <c>over (order by …)</c>, an aggregate's <c>order by</c> and a subquery's own <c>limit</c> are all left
/// exactly where they were. What follows must then be
/// <c>[order by &lt;sort list&gt;] [limit &lt;integer&gt;] [offset &lt;integer&gt;]</c> and nothing else -
/// each at most once, <c>order by</c> first, the two counts plain non-negative integers, and the sort list
/// free of <c>for</c>, <c>fetch</c>, <c>into</c>, <c>union</c>, <c>intersect</c> and <c>except</c> at depth
/// zero (<see cref="DisallowedOrderByWords" />). Anything else is refused by name rather than guessed at.
/// <c>order by 1 for update</c> is why the sort list is checked at all: Postgres 17 accepts a locking
/// clause between <c>ORDER BY</c> and <c>LIMIT</c>, and row-level write locks are not something the ungated
/// read mode hands out. A <c>limit</c> the visitor wrote is clamped down to the studio's page size and
/// never up.
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
    /// <c>pg_sleep</c> is here although the console allows it. Both now run inside the same read-only
    /// transaction with the same server-side <c>statement_timeout</c>, so the timeout is no longer the
    /// difference; the capability is. The console is gated on <c>RunSql</c> and a <c>where</c> clause is
    /// gated on nothing at all, and "park a connection for the whole statement timeout, repeatedly, with
    /// no capability" is a cheaper denial of service than the studio should hand to every visitor. The
    /// rest read the catalog, run a statement of their own behind a function call (<c>query_to_xml</c>,
    /// <c>xpath</c>) or move a sequence (<c>nextval</c>, <c>setval</c>) - none of which is a filter on the
    /// collection that was picked, and none of which a read-only transaction refuses.
    /// </para>
    /// </remarks>
    internal static readonly string[] AdditionalDisallowedFunctions =
    [
        "pg_sleep", "current_setting", "query_to_xml", "query_to_xml_and_xmlschema", "table_to_xml",
        "xpath", "xpath_exists", "nextval", "setval",
    ];

    /// <summary>
    /// Functions that read, write or wait outside the row they are given. Refused in a <c>where</c>
    /// clause <b>whatever capabilities the visitor holds</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One list, two callers.</b> <see cref="ReadOnlySqlGuard.DisallowedFunctions" /> is the same
    /// question asked of a different text, so it is unioned in here rather than restated: two lists
    /// answering it differently is exactly the drift a reviewer cannot see, and a function added to the
    /// console's denylist now reaches the ungated <c>where</c>-clause mode for free. What this file owns
    /// is only the difference, <see cref="AdditionalDisallowedFunctions" />.
    /// </para>
    /// <para>
    /// <b><c>RunSql</c> does not lift this list, and used to.</b> That was measured: with the capability
    /// granted, <c>where pg_advisory_lock(42) is not null</c> was allowed and took a <em>session-level</em>
    /// lock on a pooled connection - one that outlives the transaction and the request, on the same
    /// connection pool Marten's own daemon contends for. <c>RunSql</c> lifts
    /// <see cref="NestedReadKeywords" /> and nothing else, because everything that list refuses is
    /// something the same person could type into the console, and every entry <em>here</em> is something
    /// the console refuses too (or, in the case of <see cref="AdditionalDisallowedFunctions" />, something
    /// a filter has no business doing at all).
    /// </para>
    /// </remarks>
    internal static readonly IReadOnlySet<string> DisallowedFunctions =
        new HashSet<string>(
            ReadOnlySqlGuard.DisallowedFunctions.Keys.Concat(AdditionalDisallowedFunctions),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Casts that turn text into a database object, and so into a probe of the catalog. Refused whatever
    /// capabilities the visitor holds, for the same reason <see cref="DisallowedFunctions" /> is.
    /// </summary>
    internal static readonly string[] DisallowedCastTargets = ["regclass", "regproc"];

    /// <summary>
    /// Words that may not appear at parenthesis depth zero inside an <c>order by</c> body, because they
    /// make the tail something other than a sort list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tail is spliced onto the studio's own statement, so whatever is between <c>order by</c> and
    /// <c>limit</c> ends up there verbatim. Postgres 17 accepts <c>… order by 1 for update limit 5</c>,
    /// which is how <c>where a = 1 order by 1 for update</c> reached the server from the ungated read mode
    /// and took row-level write locks - measured, at both capability levels. <c>fetch</c> is the
    /// SQL-standard spelling of the same corner (<c>fetch first … rows with ties</c>, and
    /// <c>for update</c> after it); <c>into</c> writes a new table; <c>union</c>, <c>intersect</c> and
    /// <c>except</c> bolt a second query on past the studio's predicates altogether.
    /// </para>
    /// <para>
    /// Depth zero, so the ordinary <c>from</c>/<c>for</c> forms of <c>substring(x from 2 for 3)</c> and
    /// <c>overlay(… placing … from … for …)</c> - which are inside the function's own parentheses - are
    /// left alone.
    /// </para>
    /// </remarks>
    internal static readonly string[] DisallowedOrderByWords =
        ["for", "fetch", "into", "union", "intersect", "except"];

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
    /// visitor who may is left alone by the <em>nested-read</em> rules, and by nothing else: subqueries,
    /// unions and <c>lateral</c> joins are the point of the mode for them, and everything
    /// <see cref="NestedReadKeywords" /> refuses they could type into the console instead. Every other
    /// rule here holds for them too.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>This is load-bearing.</b> Mode A needs no capability, so a clause that carried a second statement
    /// would <em>be</em> an ungated SQL console: <c>1 = 1; drop table x</c> arrives at Npgsql as one command
    /// holding two statements, and Postgres runs both - and a read-only transaction would not stop the
    /// second one from being, say, <c>select pg_advisory_lock(42)</c>.
    /// </para>
    /// <para>
    /// Six rules, in order, all structural rather than semantic. <b>Always:</b> no <c>;</c> outside a
    /// string, a quoted identifier, a dollar-quoted body or a comment; the clause does not begin a
    /// statement of its own; <b>its parentheses balance</b>; its <c>order by</c> / <c>limit</c> /
    /// <c>offset</c> tail, if it has one, is a shape the composer can place rather than guess at
    /// (<see cref="TryPartition" />); no function that reads, writes or waits outside the row
    /// (<see cref="DisallowedFunctions" />); and no <c>::regclass</c>/<c>::regproc</c> cast.
    /// <b>Without <c>RunSql</c>:</b> no word that reaches another relation
    /// (<see cref="NestedReadKeywords" />). What is left reads the selected table's own columns and JSON -
    /// which the visitor may see anyway, so the clause grants nothing the collection browser did not
    /// already.
    /// </para>
    /// <para>
    /// <b>The balance rule is the one that was missing, and it is not cosmetic.</b> The visitor's predicate
    /// is spliced between a <c>(</c> and a <c>)</c> the composer wrote; a clause holding one more <c>)</c>
    /// than <c>(</c> closes that bracket early and everything after it lands <em>outside</em> the tenant
    /// and soft-delete predicates. <c>1 = 1) or (1 = 1</c> was measured returning four rows - two tenants,
    /// live and deleted - where the honest answer was one. The same escape needs no unbalanced <c>(</c> at
    /// all, because the composer puts its closing bracket on a line of its own: a clause ending in
    /// <c>-- ⏎) or (true</c> supplies the <c>)</c> from the free line. So the walk refuses a <c>)</c> that
    /// closes nothing <em>and</em> a <c>(</c> left open, by position, before the tail is split off - the
    /// split is at parenthesis depth zero and there is no such thing while the depths are wrong.
    /// </para>
    /// <para>
    /// It is a scanner, not a parser. It is not the security boundary for the console (that is the
    /// transaction, D13); it is the first boundary for the <em>ungated</em> mode - the second being that
    /// Mode A now runs inside that same read-only transaction - which is why it errs towards refusing and
    /// why every refusal names what would lift it, if anything would. The tenant and soft-delete
    /// predicates are not its job at all: those are composed in on both sides of the visitor's predicate,
    /// and hold whatever the clause says.
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

        // One walk, and everything below reads it: the balance, the split point and the word rules all have
        // to agree about where the strings, the comments and the dollar bodies are, and two scanners that
        // nearly agree is how a `)` ends up in one of them and not the other.
        ClauseScan scan = ClauseScan.Of(clause);

        if (scan.Rejection is { } unreadable)
        {
            return unreadable;
        }

        if (BalanceRejection(scan) is { } unbalanced)
        {
            return unbalanced;
        }

        // The tail has to be placeable, for everybody: the studio's own predicates surround the visitor's,
        // so `order by`, `limit` and `offset` cannot stay inside the parenthesised predicate and have to be
        // lifted out. A tail the composer cannot read is refused by name rather than guessed at.
        if (!TryPartitionScanned(clause, scan, out _, out SqlGuardResult tail))
        {
            return tail;
        }

        return CheckWords(clause, scan, allowNestedReads);
    }

    /// <summary>
    /// Why the clause's own parentheses cannot be spliced into the composer's, or <see langword="null" />.
    /// </summary>
    private static SqlGuardResult? BalanceRejection(ClauseScan scan)
    {
        if (scan.UnmatchedClose >= 0)
        {
            return SqlGuardResult.Reject(
                SqlRejectionReason.DisallowedStatement,
                "This clause closes a bracket it never opened. Your filter is wrapped in parentheses of the " +
                "studio's own, after the tenant and soft-delete predicates, so a ')' with nothing to close " +
                "would end that wrapper early and put the rest of the clause outside every predicate the " +
                "studio composed. Balance the brackets; no capability lifts this one.",
                ")",
                scan.UnmatchedClose);
        }

        if (scan.Depth > 0)
        {
            return SqlGuardResult.Reject(
                SqlRejectionReason.DisallowedStatement,
                "This clause leaves a '(' open. Your filter is wrapped in parentheses of the studio's own, so " +
                "an unclosed bracket would swallow them and whatever follows. Balance the brackets; no " +
                "capability lifts this one.",
                "(",
                scan.UnmatchedOpen);
        }

        return null;
    }

    /// <summary>
    /// The clause's bare words, looking for the ones that reach outside the table being filtered.
    /// </summary>
    /// <remarks>
    /// Strings, quoted identifiers, dollar-quoted bodies and comments never reach here - the scan skipped
    /// them, the same discipline <see cref="ReadOnlySqlGuard" /> uses - so a document whose value happens
    /// to be the word <c>select</c> is not a refusal, and a keyword hidden inside <c>$$…$$</c> is not an
    /// escape. Only <see cref="NestedReadKeywords" /> answers to <paramref name="allowNestedReads" />.
    /// </remarks>
    private static SqlGuardResult CheckWords(string clause, ClauseScan scan, bool allowNestedReads)
    {
        foreach (ScannedWord scanned in scan.Words)
        {
            string word = scanned.Text;
            int start = scanned.Start;

            if (!allowNestedReads && Contains(NestedReadKeywords, word))
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
                    SqlRejectionReason.DisallowedFunction,
                    $"'{word}' reads, writes or waits outside the row it is given, which is not something a " +
                    "filter does. A where clause refuses it whatever capabilities you hold: " + RunSqlOption +
                    " lifts the rules about reading another relation, and nothing lifts this one.",
                    word,
                    start);
            }

            if (start >= 2 && clause[start - 1] == ':' && clause[start - 2] == ':'
                && Contains(DisallowedCastTargets, word))
            {
                return SqlGuardResult.Reject(
                    SqlRejectionReason.DisallowedStatement,
                    $"A '::{word}' cast turns text into a database object, which is a probe of the catalog " +
                    "rather than a filter. A where clause refuses it whatever capabilities you hold: " +
                    RunSqlOption + " lifts the rules about reading another relation, and nothing lifts this one.",
                    "::" + word,
                    start - 2);
            }
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

        void AppendStudioTerms()
        {
            if (filterTenant && tenantColumn is not null)
            {
                sql.Append("  and ").Append(Column(tenantColumn)).Append(" = @").Append(TenantParameter).Append('\n');
            }

            if (deletedColumn is not null && effectiveDeleted != DeletedFilter.Include)
            {
                sql.Append("  and ").Append(Column(deletedColumn))
                    .Append(effectiveDeleted == DeletedFilter.Only ? " = true\n" : " = false\n");
            }
        }

        if (filterTenant && tenantColumn is not null)
        {
            // Bound once and named twice: the terms appear on both sides of the visitor's predicate, and an
            // Npgsql named parameter may be referenced as often as the statement likes.
            parameters.Add(new QuerySqlParameter(TenantParameter, NpgsqlDbType.Varchar, tenantId!));
        }

        AppendStudioTerms();

        if (parts.Predicate.Length > 0)
        {
            // Parenthesised: an `or` in the visitor's predicate cannot reach around the terms above it,
            // which is the whole point of composing rather than appending. The closing bracket goes on a
            // line of its own because a clause may end in a `--` comment, and a bracket inside one is a
            // bracket that is not there.
            sql.Append("  and (\n    ").Append(parts.Predicate).Append("\n  )\n");

            // And then the same terms again, after it - defence in depth, not a second guarantee. While the
            // brackets balance, the predicate really is one term and `a and (X) and a` distributes over any
            // `or` inside X, so every disjunct of the visitor's own predicate still carries the tenant and
            // soft-delete terms. It does NOT save an unbalanced clause: a `) … (` pair the visitor supplied
            // brackets a disjunct of its own between these two copies, which carries neither. CheckClause's
            // balance rule is what makes that unreachable. This costs one more index condition Postgres
            // folds away, and it is free in the plan.
            AppendStudioTerms();
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
        string text = clause ?? string.Empty;

        return TryPartitionScanned(text, ClauseScan.Of(text), out parts, out rejection);
    }

    /// <summary>
    /// The same, for a caller that has already walked the clause. One scan, so the split point and every
    /// refusal agree about where the strings, comments and dollar bodies are.
    /// </summary>
    private static bool TryPartitionScanned(
        string text,
        ClauseScan scan,
        out ClauseParts parts,
        out SqlGuardResult rejection)
    {
        parts = ClauseParts.Empty;
        rejection = SqlGuardResult.Allow(string.Empty, 0);

        if (text.AsSpan().Trim().Length == 0)
        {
            return true;
        }

        int bodyStart = BodyStart(text);

        if (!scan.Readable)
        {
            // Unreadable quoting. CheckClause refuses it on its own; here the honest answer is "no tail".
            parts = new ClauseParts(text[bodyStart..].Trim(), null, null, null);
            return true;
        }

        List<TailKeyword> keywords = TailKeywordsOf(scan, bodyStart);

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

                // The sort list is spliced onto the studio's statement verbatim, so it has to be a sort
                // list and not a place to hang `for update` off. Postgres accepts the locking clause
                // between `order by` and `limit`, which is exactly where this body ends.
                if (OrderByBodyRejection(scan, keyword.ValueStart, end) is { } badSort)
                {
                    rejection = badSort;
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
    /// Every <c>order by</c>, <c>limit</c> and <c>offset</c> at parenthesis depth zero, in order, from
    /// <paramref name="from" /> onwards.
    /// </summary>
    /// <remarks>
    /// Depth is what makes the split safe: <c>rank() over (order by x)</c>,
    /// <c>string_agg(x, ',' order by y)</c> and <c>id in (select id from t limit 5)</c> all sit at depth
    /// one or more and stay in the predicate. <c>order</c> only counts when the very next word is
    /// <c>by</c>, so a column called <c>order</c> is not a split point; <c>limit</c> and <c>offset</c> are
    /// reserved words in Postgres and cannot be unquoted column names at all.
    /// </remarks>
    private static List<TailKeyword> TailKeywordsOf(ClauseScan scan, int from)
    {
        List<TailKeyword> keywords = [];
        IReadOnlyList<ScannedWord> words = scan.Words;

        for (var i = 0; i < words.Count; i++)
        {
            ScannedWord word = words[i];

            if (word.Depth != 0 || word.Start < from)
            {
                continue;
            }

            if (string.Equals(word.Text, "order", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < words.Count
                    && words[i + 1].Depth == 0
                    && string.Equals(words[i + 1].Text, "by", StringComparison.OrdinalIgnoreCase))
                {
                    keywords.Add(new TailKeyword(TailKind.OrderBy, word.Start, words[i + 1].End));
                }
            }
            else if (string.Equals(word.Text, TailKind.Limit, StringComparison.OrdinalIgnoreCase)
                || string.Equals(word.Text, TailKind.Offset, StringComparison.OrdinalIgnoreCase))
            {
                keywords.Add(new TailKeyword(word.Text.ToLowerInvariant(), word.Start, word.End));
            }
        }

        return keywords;
    }

    /// <summary>
    /// Why the <c>order by</c> body between <paramref name="start" /> and <paramref name="end" /> is not a
    /// sort list, or <see langword="null" />.
    /// </summary>
    private static SqlGuardResult? OrderByBodyRejection(ClauseScan scan, int start, int end)
    {
        foreach (ScannedWord word in scan.Words)
        {
            if (word.Start < start || word.Start >= end || word.Depth != 0)
            {
                continue;
            }

            if (Contains(DisallowedOrderByWords, word.Text))
            {
                return RejectTail(
                    $"'{word.Text}' is not part of a sort list, and a clause's tail has to be " +
                    "'[order by <sort list>] [limit <integer>] [offset <integer>]' and nothing else. " +
                    $"'{word.Text}' there would change what the statement does rather than how its rows are " +
                    "ordered - no capability lifts this one.",
                    word.Text,
                    word.Start);
            }
        }

        return null;
    }

    /// <summary>Where the predicate starts: past any leading whitespace and an optional <c>where</c>.</summary>
    private static int BodyStart(string text)
    {
        var i = 0;

        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        return StartsWithWordAt(text, i, "where") ? i + "where".Length : i;
    }

    /// <summary>One bare word a scan found: what it was, where it was, and how deep in parentheses.</summary>
    /// <param name="Text">The word as typed.</param>
    /// <param name="Start">Where it starts, zero-based.</param>
    /// <param name="End">One past its last character.</param>
    /// <param name="Depth">The parenthesis depth at its first character.</param>
    private readonly record struct ScannedWord(string Text, int Start, int End, int Depth);

    /// <summary>
    /// One walk over a clause: every bare word with its depth, and what the parentheses did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One walk, three questions.</b> The balance check, the <c>order by</c> / <c>limit</c> /
    /// <c>offset</c> split and the word rules all need to know where the strings, quoted identifiers,
    /// dollar-quoted bodies and comments are, and three scanners that <em>nearly</em> agree is how a
    /// <c>)</c> ends up visible to one of them and not to another.
    /// </para>
    /// <para>
    /// <b>The lexing itself is not here.</b> It is <see cref="SqlLexer" />, shared with
    /// <see cref="ReadOnlySqlGuard" />, which is where the five things Postgres does that are easy to get
    /// wrong are written down and tested - a line comment ending at CR as well as LF, nesting block
    /// comments, doubled quotes, <c>E'…'</c> backslashes and dollar tags that may not begin with a digit.
    /// Two copies of that walk is how the same defect came to be fixed twice; this class asks the lexer for
    /// tokens and does the bookkeeping the <em>clause</em> rules need, which is depth, bracket balance and
    /// where each bare word was.
    /// </para>
    /// <para>
    /// Anything the lexer cannot read is a refusal rather than a guess, because "I cannot tell what this
    /// would run" and "this is safe" are not the same answer.
    /// </para>
    /// </remarks>
    private sealed class ClauseScan
    {
        private ClauseScan(List<ScannedWord> words, int depth, int unmatchedClose, int unmatchedOpen)
        {
            Words = words;
            Depth = depth;
            UnmatchedClose = unmatchedClose;
            UnmatchedOpen = unmatchedOpen;
        }

        private ClauseScan(SqlGuardResult rejection)
        {
            Rejection = rejection;
            Words = [];
            UnmatchedClose = -1;
            UnmatchedOpen = -1;
        }

        /// <summary>Every bare word outside a literal, a quoted identifier and a comment, in order.</summary>
        public IReadOnlyList<ScannedWord> Words { get; }

        /// <summary>How many <c>(</c> were still open at the end. Zero for a balanced clause.</summary>
        public int Depth { get; }

        /// <summary>Where the first <c>)</c> that closed nothing was, or <c>-1</c>.</summary>
        public int UnmatchedClose { get; }

        /// <summary>Where the first <c>(</c> that was never closed is, or <c>-1</c>.</summary>
        public int UnmatchedOpen { get; }

        /// <summary>Why the clause could not be read, when it could not.</summary>
        public SqlGuardResult? Rejection { get; }

        /// <summary>Whether the quoting could be read at all.</summary>
        public bool Readable => Rejection is null;

        /// <summary>Walks <paramref name="sql" /> once, through the assembly's one lexer.</summary>
        /// <remarks>
        /// The lexing is <see cref="SqlLexer" />'s and the bookkeeping is this method's: which brackets were
        /// left open, which <c>)</c> closed nothing, and every bare word with the depth it sat at. Nothing
        /// here knows what a string or a comment looks like, which is the point - that knowledge lived in
        /// two places and the same defect had to be fixed in both, twice.
        /// </remarks>
        public static ClauseScan Of(string sql)
        {
            List<ScannedWord> words = [];
            List<int> open = [];
            var unmatchedClose = -1;
            var lexer = new SqlLexer(sql);

            while (lexer.TryRead(out SqlToken token))
            {
                if (token.Kind == SqlTokenKind.Unreadable)
                {
                    return new ClauseScan(Unreadable(token.Text, token.Start, Describe(token.Fault)));
                }

                if (token.Kind == SqlTokenKind.Word)
                {
                    words.Add(new ScannedWord(token.Text, token.Start, token.End, token.Depth));
                    continue;
                }

                if (token.Is('('))
                {
                    open.Add(token.Start);
                    continue;
                }

                if (token.Is(')'))
                {
                    if (token.Depth == 0)
                    {
                        // Remembered, not fatal here: the walk carries on so that a clause which is wrong in
                        // two ways is described by the first thing that is wrong with it.
                        if (unmatchedClose < 0)
                        {
                            unmatchedClose = token.Start;
                        }
                    }
                    else
                    {
                        open.RemoveAt(open.Count - 1);
                    }
                }
            }

            return new ClauseScan(words, lexer.Depth, unmatchedClose, open.Count > 0 ? open[0] : -1);
        }

        /// <summary>How a refusal names the thing that never closed.</summary>
        private static string Describe(SqlLexFault fault) => fault switch
        {
            SqlLexFault.UnterminatedComment => "an unterminated comment",
            SqlLexFault.UnterminatedString => "an unterminated string",
            _ => "an unterminated quoted body",
        };
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

        var lexer = new SqlLexer(sql);

        while (lexer.TryRead(out SqlToken token))
        {
            // Unterminated anything: the guard will refuse it in its own right, and "present" is the safe
            // answer to a question asked of text nobody can read.
            if (token.Kind == SqlTokenKind.Unreadable || token.IsWord(keyword))
            {
                return true;
            }
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

        var lexer = new SqlLexer(sql);

        while (lexer.TryRead(out SqlToken token))
        {
            // A fault is reported where the thing that never closed began, which is the character the
            // editor puts its caret under - and refusing there is the conservative answer, because text
            // nobody can read may hold a separator anywhere in it.
            if (token.Kind == SqlTokenKind.Unreadable || token.Is(';'))
            {
                return token.Start;
            }
        }

        return -1;
    }

    private static bool StartsWithWord(string sql, string word) => StartsWithWordAt(sql, 0, word);

    /// <summary>Whether <paramref name="word" /> begins at <paramref name="index" /> as a whole word.</summary>
    private static bool StartsWithWordAt(string sql, int index, string word)
    {
        if (index + word.Length > sql.Length
            || string.Compare(sql, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) != 0)
        {
            return false;
        }

        int after = index + word.Length;

        return after == sql.Length || !char.IsLetterOrDigit(sql[after]) && sql[after] != '_';
    }

}
