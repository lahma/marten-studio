using System.Globalization;

using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Services.Query;

/// <summary>
/// 1.0-P3A (issue #28, phase 3): the static lint - four plan insights judged from the statement's text, not from a plan
/// (<see cref="PlanInsightInfo.FromText" />). They read nothing and run nothing, so the Plan page shows them as the statement is
/// written, before any run.
/// </summary>
/// <remarks>
/// <para>
/// <b>The studio's own lexer, never a pattern over raw text.</b> Every rule walks <see cref="SqlLexer" />'s tokens, so a word in a
/// comment, a string, a quoted identifier or a dollar-quoted body never matches, and each word carries the parenthesis depth
/// Postgres would give it. Digits are the lexer's single-character punctuation, so a number is read back from consecutive digit
/// tokens. Text the lexer cannot read stops the walk: what was read before it is still judged.
/// </para>
/// <para>
/// <b>Not a parser.</b> Each rule looks for a short, unambiguous sequence of tokens; anything subtler is the plan's to judge.
/// <c>SELECT *</c> is judged only at the top level, because <c>EXISTS (SELECT * …)</c> is idiomatic and costs nothing.
/// </para>
/// </remarks>
internal static partial class PlanInsights
{
    /// <summary>P23's "large": an <c>OFFSET</c> literal at least this large.</summary>
    public const long LargeOffsetLiteral = 10_000;

    /// <summary>The node a text finding names: there is none.</summary>
    internal const int StatementNodeId = -1;

    /// <summary>What a text finding is about, in place of a node.</summary>
    internal const string StatementSubject = "The statement";

    /// <summary>The words that end a <c>FROM</c> clause at its own depth.</summary>
    private static readonly HashSet<string> AfterFrom = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "group", "having", "window", "order", "limit", "offset", "fetch", "for", "union", "intersect", "except", "returning",
    };

    /// <summary>
    /// The static lint's findings on <paramref name="statement" />, in code order, at most <see cref="MaxPerInsight" /> an insight.
    /// Empty for empty text. Never throws on any text.
    /// </summary>
    /// <param name="statement">The statement as the visitor wrote it.</param>
    public static IReadOnlyList<PlanInsightFinding> EvaluateText(string? statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
        {
            return [];
        }

        List<SqlToken> tokens = [];
        SqlLexer lexer = new(statement);

        while (lexer.TryRead(out SqlToken token) && token.Kind != SqlTokenKind.Unreadable && tokens.Count < 100_000)
        {
            tokens.Add(token);
        }

        return
        [
            .. NotInSubqueries(statement, tokens).Take(MaxPerInsight),
            .. LargeOffsetLiterals(statement, tokens).Take(MaxPerInsight),
            .. StarsOverJoins(statement, tokens).Take(MaxPerInsight),
            .. RandomOrders(statement, tokens).Take(MaxPerInsight),
        ];
    }

    /// <summary>P22: <c>NOT IN (SELECT …)</c>, with any number of parentheses before the <c>SELECT</c>.</summary>
    private static IEnumerable<PlanInsightFinding> NotInSubqueries(string statement, List<SqlToken> tokens)
    {
        for (int i = 0; i + 3 < tokens.Count; i++)
        {
            if (!tokens[i].IsWord("not") || !tokens[i + 1].IsWord("in") || !tokens[i + 2].Is('('))
            {
                continue;
            }

            int next = i + 2;

            while (next < tokens.Count && tokens[next].Is('('))
            {
                next++;
            }

            if (next < tokens.Count && (tokens[next].IsWord("select") || tokens[next].IsWord("with") || tokens[next].IsWord("values")))
            {
                yield return Finding(
                    NotInSubquery,
                    statement,
                    tokens[i],
                    "uses NOT IN over a subquery. If the subquery returns a single NULL, NOT IN is never true and the statement returns no "
                    + "rows; and because of that rule the planner cannot turn it into an anti-join, so it hashes the subquery or, when that "
                    + "does not fit in work_mem, runs it again for every row.",
                    "Write NOT EXISTS (SELECT 1 FROM … WHERE … = outer.column) instead: it means what was intended when the subquery can return "
                    + "NULL, and the planner can run it as an anti-join.");
            }
        }
    }

    /// <summary>P23: <c>OFFSET</c> followed by an integer literal of at least <see cref="LargeOffsetLiteral" />.</summary>
    private static IEnumerable<PlanInsightFinding> LargeOffsetLiterals(string statement, List<SqlToken> tokens)
    {
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (!tokens[i].IsWord("offset") || NumberAt(tokens, i + 1) is not { } offset || offset < LargeOffsetLiteral)
            {
                continue;
            }

            yield return Finding(
                LargeOffsetLiteralInText,
                statement,
                tokens[i],
                "skips " + offset.ToString("N0", CultureInfo.InvariantCulture) + " rows with OFFSET. Postgres still produces every row it skips, "
                + "so each page costs more than the one before it.",
                "Page by the last key seen instead (WHERE (sort_key, id) > ($1, $2) ORDER BY sort_key, id LIMIT n): with an index on the "
                + "sort key every page costs the same.");
        }
    }

    /// <summary>P24: a top-level <c>SELECT *</c> (not <c>t.*</c>) whose <c>FROM</c> has a join, written or comma.</summary>
    private static IEnumerable<PlanInsightFinding> StarsOverJoins(string statement, List<SqlToken> tokens)
    {
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (tokens[i].Depth != 0 || !tokens[i].IsWord("select"))
            {
                continue;
            }

            int star = i + 1;

            if (star < tokens.Count && (tokens[star].IsWord("distinct") || tokens[star].IsWord("all")))
            {
                star++;
            }

            if (star >= tokens.Count || !tokens[star].Is('*'))
            {
                continue;
            }

            if (FromHasJoin(tokens, star + 1))
            {
                yield return Finding(
                    StarOverJoin,
                    statement,
                    tokens[i],
                    "selects * over a join, so every column of every joined table is read, carried through each join and sort, and sent - "
                    + "and an index-only scan is never possible.",
                    "Name the columns the caller uses. Where a join only filters, EXISTS keeps its table's columns out of the result.");
            }
        }
    }

    /// <summary>P25: <c>ORDER BY random()</c> (or <c>pg_catalog.random()</c>) as the first sort key.</summary>
    private static IEnumerable<PlanInsightFinding> RandomOrders(string statement, List<SqlToken> tokens)
    {
        for (int i = 0; i + 4 < tokens.Count; i++)
        {
            if (!tokens[i].IsWord("order") || !tokens[i + 1].IsWord("by"))
            {
                continue;
            }

            int at = i + 2;

            if (tokens[at].IsWord("pg_catalog") && at + 2 < tokens.Count && tokens[at + 1].Is('.'))
            {
                at += 2;
            }

            if (at + 2 < tokens.Count && tokens[at].IsWord("random") && tokens[at + 1].Is('(') && tokens[at + 2].Is(')'))
            {
                yield return Finding(
                    OrderByRandom,
                    statement,
                    tokens[i],
                    "orders by random(), so Postgres reads every row the rest of the statement keeps, gives each a random number and sorts "
                    + "them all - to return a few.",
                    "For a sample of a large table use TABLESAMPLE SYSTEM (p) or BERNOULLI (p); for one random row, pick a random key between "
                    + "the table's least and greatest and take the first row at or above it.");
            }
        }
    }

    /// <summary>Whether the <c>FROM</c> clause after <paramref name="start" />, at the <c>SELECT</c>'s own depth, joins.</summary>
    private static bool FromHasJoin(List<SqlToken> tokens, int start)
    {
        int depth = tokens[start - 1].Depth;
        int at = start;

        // To the FROM at this depth; a FROM in a subquery of the select list is deeper.
        while (at < tokens.Count && !(tokens[at].Depth == depth && tokens[at].IsWord("from")))
        {
            if (tokens[at].Depth < depth || (tokens[at].Depth == depth && tokens[at].Is(';')))
            {
                return false;
            }

            at++;
        }

        for (at++; at < tokens.Count; at++)
        {
            SqlToken token = tokens[at];

            if (token.Depth < depth || (token.Depth == depth && (token.Is(';') || (token.Kind == SqlTokenKind.Word && AfterFrom.Contains(token.Text)))))
            {
                return false;
            }

            if (token.Depth == depth && (token.IsWord("join") || token.Is(',')))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The integer literal whose digits start at token <paramref name="index" />, or <see langword="null" />.</summary>
    private static long? NumberAt(List<SqlToken> tokens, int index)
    {
        long value = 0;
        int digits = 0;
        int expected = tokens[index].Start;
        int next = index;

        // Digits of one literal are adjacent single-character tokens. PostgreSQL 16's separator makes the rest a word the lexer reads
        // whole (10_000 is "1", "0" and "_000"), which is digits too when it is only underscores and digits after a digit.
        while (next < tokens.Count && tokens[next] is var token && token.Start == expected)
        {
            bool digit = token.Kind == SqlTokenKind.Punctuation && char.IsAsciiDigit(token.Text[0]);
            bool separated = token.Kind == SqlTokenKind.Word && digits > 0 && token.Text[0] == '_'
                && token.Text.All(static c => c == '_' || char.IsAsciiDigit(c));

            if (!digit && !separated)
            {
                break;
            }

            foreach (char c in token.Text)
            {
                if (c != '_')
                {
                    digits++;
                    value = digits > 18 ? long.MaxValue : (value * 10) + (c - '0');
                }
            }

            expected = token.End;
            next++;
        }

        // A word right after the digits (10e5, 10000abc) or a decimal point makes it something other than an integer literal.
        bool followed = next < tokens.Count && tokens[next].Start == expected && (tokens[next].Kind == SqlTokenKind.Word || tokens[next].Is('.'));

        return digits == 0 || followed ? null : value;
    }

    private static PlanInsightFinding Finding(PlanInsightInfo insight, string statement, SqlToken at, string message, string remedy)
    {
        (int line, int column) = LineAndColumn(statement, at.Start);
        string where = "line " + line.ToString(CultureInfo.InvariantCulture) + ", column " + column.ToString(CultureInfo.InvariantCulture);

        return new PlanInsightFinding(insight, StatementNodeId, StatementSubject + " at " + where, StatementSubject + " " + message, remedy);
    }

    private static (int Line, int Column) LineAndColumn(string text, int position)
    {
        int line = 1;
        int lineStart = 0;

        for (int i = 0; i < position && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, position - lineStart + 1);
    }
}
