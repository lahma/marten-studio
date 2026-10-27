using System.Globalization;
using System.Text;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Query;

/// <summary>Where a hypothetical index came from.</summary>
internal enum HypotheticalIndexOrigin
{
    /// <summary>Read off a plan: a sequential scan's filter on plain columns of the table it reads (X2).</summary>
    Plan,

    /// <summary>The index Marten would create for the advisor's <c>StoreOptions</c> line - Marten's own DDL, from MartenStudio.</summary>
    Marten,
}

/// <summary>
/// One index a what-if may imagine (X2): the <c>CREATE INDEX</c> hypopg is handed - built by a builder, never typed - and what
/// it is in words.
/// </summary>
internal sealed record HypotheticalIndex
{
    /// <summary>What names it within one plan or one piece of advice: the server finds it again by this, never by its text.</summary>
    public required string Key { get; init; }

    /// <summary>The one <c>CREATE INDEX</c> statement hypopg is handed, as a parameter.</summary>
    public required string Definition { get; init; }

    /// <summary>What it is, in words: <c>an index on public.orders (customer_id)</c>.</summary>
    public required string Caption { get; init; }

    /// <summary>Where it came from.</summary>
    public HypotheticalIndexOrigin Origin { get; init; }

    /// <summary>The table's schema, when known.</summary>
    public string? Schema { get; init; }

    /// <summary>The table.</summary>
    public required string Table { get; init; }

    /// <summary>Its columns, for an index read off a plan.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>Anything a person should know before acting on what the what-if shows.</summary>
    public string? Note { get; init; }

    /// <summary>Whether <paramref name="other" /> is this index: the same key, origin and definition - what a server finds it again by.</summary>
    public bool Same(HypotheticalIndex? other) =>
        other is not null && Origin == other.Origin
        && string.Equals(Key, other.Key, StringComparison.Ordinal)
        && string.Equals(Definition, other.Definition, StringComparison.Ordinal);
}

/// <summary>Whether hypopg can be used in a database, by the role the studio plans as.</summary>
internal enum HypotheticalIndexState
{
    /// <summary>Installed, and the role may use it.</summary>
    Available,

    /// <summary>Not in <c>pg_extension</c>.</summary>
    NotInstalled,

    /// <summary>Installed, and the role may not call it.</summary>
    NoPrivilege,
}

/// <summary>What the studio knows of hypopg in one database (X2), cached per database for the catalog cache's time to live.</summary>
/// <param name="State">Whether it can be used.</param>
/// <param name="Schema">The schema it was installed in.</param>
/// <param name="Version">Its version.</param>
internal sealed record HypotheticalIndexAvailability(HypotheticalIndexState State, string? Schema, string? Version)
{
    /// <summary>What a what-if says where hypopg is not installed in the database.</summary>
    internal const string NotInstalledSentence =
        "hypopg is not installed in this database, so the studio cannot imagine an index here. Installing it - its package on the "
        + "database server, then CREATE EXTENSION hypopg in this database - is the host's decision; the studio never installs an "
        + "extension, and never creates an index.";

    /// <summary>What a what-if says where hypopg is installed and the role the studio plans as may not use it.</summary>
    internal const string NoPrivilegeSentence =
        "hypopg is installed in this database, but the role the studio plans as (SqlConsoleRole where the host sets one, the data "
        + "source's own login otherwise) may not use it: that takes USAGE on hypopg's schema and EXECUTE on hypopg_create_index and "
        + "hypopg_reset. Granting them is the host's decision; the studio never creates an index.";

    /// <summary>Why a what-if cannot run here, in words, or <see langword="null" /> when it can.</summary>
    public string? Sentence => State switch
    {
        HypotheticalIndexState.NotInstalled => NotInstalledSentence,
        HypotheticalIndexState.NoPrivilege => NoPrivilegeSentence,
        _ => null,
    };

    /// <summary>What a probe read comes to.</summary>
    public static HypotheticalIndexAvailability Of(HypopgCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return !catalog.Installed
            ? new HypotheticalIndexAvailability(HypotheticalIndexState.NotInstalled, null, null)
            : new HypotheticalIndexAvailability(
                catalog.Usable ? HypotheticalIndexState.Available : HypotheticalIndexState.NoPrivilege, catalog.Schema, catalog.Version);
    }
}

/// <summary>What one what-if came to (X2): the plan now and the plan under the hypothetical index - or why there are none.</summary>
internal sealed record WhatIfResult
{
    /// <summary>The index imagined.</summary>
    public required HypotheticalIndex Index { get; init; }

    /// <summary>Why nothing was read, when the surface's own gate refused it; <see cref="DatabaseRefusal.None" /> otherwise.</summary>
    public DatabaseRefusal Refusal { get; init; }

    /// <summary>
    /// Why there are no plans to compare, in words: the gate's sentence, hypopg's absence, a plan Postgres refused, a failure.
    /// <see langword="null" /> when there are plans.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>hypopg's state, when it was read.</summary>
    public HypotheticalIndexState? Availability { get; init; }

    /// <summary>The plan without the index, masked as the plan it accompanies is.</summary>
    public QueryPlan? Before { get; init; }

    /// <summary>The plan with it.</summary>
    public QueryPlan? After { get; init; }

    /// <summary>The name the planner prints for the hypothetical index: <c>&lt;13630&gt;btree_t_b</c>.</summary>
    public string? IndexName { get; init; }

    /// <summary>Whether a node of <see cref="After" /> reads the hypothetical index.</summary>
    public bool Used { get; init; }

    /// <summary>A what-if the surface's gate refused, with its sentence.</summary>
    public static WhatIfResult Refused(HypotheticalIndex index, DatabaseRefusal refusal, string reason) =>
        new() { Index = index, Refusal = refusal, Reason = reason };

    /// <summary>A what-if that passed the gate and has no plans to compare, and why: hypopg's absence, a refusal from Postgres.</summary>
    public static WhatIfResult NotCompared(HypotheticalIndex index, string reason, HypotheticalIndexState? availability = null) =>
        new() { Index = index, Reason = reason, Availability = availability };

    /// <summary>
    /// The two plans compared, in a line: whether Postgres would use the index, and the plan's estimated cost before and after.
    /// </summary>
    public string? Verdict
    {
        get
        {
            if (Before?.Root.TotalCost is not { } before || After?.Root.TotalCost is not { } after)
            {
                return null;
            }

            string costs = before == after
                ? "the plan's estimated cost stays " + Cost(before)
                : "the plan's estimated cost goes from " + Cost(before) + " to " + Cost(after) + " (" + Change(before, after) + ")";

            return Used
                ? "Postgres would use it: " + costs + "."
                : "Postgres would not use it for this statement: " + costs + ".";
        }
    }

    /// <summary>What the comparison can and cannot say.</summary>
    internal const string Caveat =
        "Both plans are plain plans, planned now: hypopg estimates the index's size from the table's statistics, and a hypothetical "
        + "index serves EXPLAIN alone, never EXPLAIN ANALYZE. A cost is the planner's estimate, not a time. Every write maintains "
        + "every index, so an index pays for itself only where reads like this one outweigh that.";

    private static string Cost(double cost) => cost.ToString("#,0.00", CultureInfo.InvariantCulture);

    private static string Change(double before, double after)
    {
        if (before <= 0)
        {
            return after > before ? "more" : "less";
        }

        double share = Math.Abs(after - before) / before;
        string percent = share < 0.01 ? "under 1%" : share.ToString("0%", CultureInfo.InvariantCulture);

        return percent + (after < before ? " less" : " more");
    }

    /// <summary>
    /// What a read comes to: hypopg's absence or a vetting sentence as the reason, or the two plans - read, then masked by
    /// <paramref name="mask" /> - and whether the second reads the index, judged before the mask touches a name.
    /// </summary>
    /// <param name="index">The index imagined.</param>
    /// <param name="read">What the engine read.</param>
    /// <param name="mask">The mask a surface applies to every name a plan prints, or <see langword="null" />.</param>
    internal static WhatIfResult From(HypotheticalIndex index, HypotheticalIndexRead read, Func<string, string>? mask = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(read);

        HypotheticalIndexState state = read.Availability.State;

        if (read.Availability.Sentence is { } unavailable)
        {
            return NotCompared(index, unavailable, state);
        }

        if (read.Refusal is { } refused)
        {
            return NotCompared(index, refused, state);
        }

        if (read.BeforeJson is not { } beforeJson)
        {
            return NotCompared(index, "Not compared: Postgres returned no plan for this statement.", state);
        }

        if (read.AfterJson is not { } afterJson || read.Made is not { } made)
        {
            return NotCompared(index, "Not compared: Postgres returned no plan under the hypothetical index.", state);
        }

        bool used = Reads(QueryPlanReader.Read(afterJson).Plan, made.Name);
        QueryPlanRead before = QueryPlanReader.Read(beforeJson, mask);
        QueryPlanRead after = QueryPlanReader.Read(afterJson, mask);

        if (before.Plan is null || after.Plan is null)
        {
            return NotCompared(index, before.Failure ?? after.Failure ?? "Not compared: a plan could not be read.", state);
        }

        return new WhatIfResult
        {
            Index = index,
            Availability = state,
            Before = before.Plan,
            After = after.Plan,
            IndexName = made.Name,
            Used = used,
        };
    }

    /// <summary>The studio's sentence for a what-if Postgres or hypopg refused, by SQLSTATE: never Postgres' own words.</summary>
    internal static string Failed(string sqlState) =>
        "Not compared: Postgres refused the what-if (SQLSTATE " + sqlState + ")" + sqlState switch
        {
            "XX000" => " - hypopg could not imagine this index: it imagines btree, BRIN, hash and bloom indexes, on columns and expressions the table has.",
            "42501" => " - the role the studio plans as may not read a table the statement names, or may not use hypopg.",
            "57014" => " - it ran out of the statement_timeout.",
            "55P03" => " - somebody holds a lock on a table it names that a plan has to wait for.",
            "42P01" or "42703" or "42883" => " - a name in it does not resolve for the role and search_path the studio plans with.",
            _ => ".",
        };

    /// <summary>What the audit ring says a what-if came to: never a name, never the text.</summary>
    internal string Outcome() =>
        Before?.Root.TotalCost is { } before && After?.Root.TotalCost is { } after
            ? (Used ? "Compared: the hypothetical index would be used" : "Compared: the hypothetical index would not be used")
              + " (estimated cost " + Cost(before) + " to " + Cost(after) + ")."
            : Reason ?? "Not compared.";

    /// <summary>Whether <paramref name="plan" /> reads the index hypopg named <paramref name="indexName" />.</summary>
    internal static bool Reads(QueryPlan? plan, string? indexName) =>
        plan is not null && indexName is not null && plan.Nodes.Any(x => string.Equals(x.IndexName, indexName, StringComparison.Ordinal));
}

/// <summary>
/// What the "What if?" panel is handed (X2): how to run a what-if for one index, through the gate of the plan it accompanies,
/// and - for advice that is not read off the plan - the indexes to offer.
/// </summary>
internal sealed class WhatIfSource
{
    /// <summary>Runs one what-if, through the surface's own gate and audit. Never throws but for cancellation.</summary>
    public required Func<HypotheticalIndex, CancellationToken, Task<WhatIfResult>> RunAsync { get; init; }

    /// <summary>The indexes to offer; <see langword="null" /> reads them off the plan the panel is under.</summary>
    public IReadOnlyList<HypotheticalIndex>? Candidates { get; init; }

    /// <summary>A line under the panel's lead: what the indexes are, for this surface.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The indexes a plan suggests (X2): for each sequential scan that filters, an index on the plain columns of the table it reads
/// that the filter compares - the shape P04 judges, and the shape a nested loop's inner side lacks (P05). Pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read off Postgres' own deparse, conservatively.</b> A column counts only where the filter compares it as it is: never an
/// argument of a function call (an index on <c>c</c> does not serve <c>lower(c)</c>), never the left side of a JSON operator
/// (an index on a <c>jsonb</c> column does not serve <c>-&gt;&gt;</c>), never a <c>LIKE</c> operand (a btree serves no leading
/// wildcard, and hypopg imagines no trigram index), never a type after <c>::</c>. Where the plan prefixes column names with
/// their table - <c>VERBOSE</c>, or a statement that reads more than one - only the scan's own alias counts; where it does not,
/// the statement reads one table and every bare name in the filter is one of its columns.
/// </para>
/// <para>
/// <b>Equality first.</b> The columns compared with <c>=</c> lead the index, in the order the filter names them, then the rest,
/// up to <see cref="MaxColumns" /> - the order a btree serves a mixed filter in.
/// </para>
/// <para>
/// <b>Never trusted from a page.</b> A surface whose plan the visitor may not see whole - the Statements drill-in - reads these
/// off the plan it planned again, and imagines only an index that is among them.
/// </para>
/// </remarks>
internal static class HypotheticalIndexCandidates
{
    /// <summary>The most columns one imagined index has.</summary>
    public const int MaxColumns = 3;

    /// <summary>The most indexes one plan offers.</summary>
    public const int MaxCandidates = 4;

    /// <summary>What a candidate on a table named like Marten's says.</summary>
    internal const string MartenTableNote =
        "This table is named like Marten's: an index on it is declared in the store's StoreOptions, never created by hand - Marten's next "
        + "schema apply drops an index its configuration does not declare.";

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "not", "is", "null", "true", "false", "any", "all", "some", "in", "like", "ilike", "similar", "between",
        "case", "when", "then", "else", "end", "distinct", "from", "array", "row", "collate", "subplan", "hashed", "initplan",
        "alternatives", "unknown", "at", "time", "zone", "interval", "exists", "overlaps", "escape", "to", "symmetric", "default",
        "current_date", "current_time", "current_timestamp", "localtime", "localtimestamp", "current_user", "session_user", "user",
        "current_role", "current_catalog", "current_schema", "only", "nulls", "first", "last", "asc", "desc", "using", "of", "cte",
    };

    /// <summary>The indexes <paramref name="plan" /> suggests, the first <see cref="MaxCandidates" />.</summary>
    public static IReadOnlyList<HypotheticalIndex> FromPlan(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        List<HypotheticalIndex> found = [];
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (PlanNode node in plan.Nodes)
        {
            if (found.Count >= MaxCandidates)
            {
                break;
            }

            if (IsSequentialScan(node) && node.RelationName is { } relation && node.Filter is { } filter)
            {
                Add(node.Schema, relation, Columns(filter, node.Alias ?? relation));
            }

            // P05's shape: a nested loop that tests its join on the inner side's every row, which a sequential scan reads whole.
            if (node.NodeType == "Nested Loop" && node.JoinFilter is { } joinFilter
                && node.Children.FirstOrDefault(static x => x.ParentRelationship == "Inner") is { } inner
                && IsSequentialScan(inner) && inner.RelationName is { } innerRelation)
            {
                Add(inner.Schema, innerRelation, Columns(joinFilter, inner.Alias ?? innerRelation, prefixedOnly: true));
            }
        }

        return found;

        void Add(string? schema, string table, IReadOnlyList<string> columns)
        {
            if (columns.Count == 0 || MigrationSqlText.CreateIndex(schema, table, columns) is not { } definition)
            {
                return;
            }

            string key = Key(schema, table, columns);

            if (keys.Add(key))
            {
                found.Add(new HypotheticalIndex
                {
                    Key = key,
                    Definition = definition,
                    Caption = "An index on " + (schema is null ? table : schema + "." + table) + " (" + string.Join(", ", columns) + ")",
                    Origin = HypotheticalIndexOrigin.Plan,
                    Schema = schema,
                    Table = table,
                    Columns = columns,
                    Note = DatabaseObjectClassifier.IsMartenName(table) ? MartenTableNote : null,
                });
            }
        }
    }

    /// <summary>The key a plan-read index is found again by: its schema, table and columns, each length-prefixed.</summary>
    internal static string Key(string? schema, string table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(columns);

        StringBuilder key = new("plan:");
        Part(schema ?? string.Empty);
        Part(table);

        foreach (string column in columns)
        {
            Part(column);
        }

        return key.ToString();

        void Part(string value) => key.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append(';');
    }

    private static bool IsSequentialScan(PlanNode node) => node.NodeType == "Seq Scan";

    /// <summary>
    /// The plain columns of the table <paramref name="alias" /> names that <paramref name="expression" /> compares, equality first,
    /// at most <see cref="MaxColumns" />. Pure.
    /// </summary>
    /// <param name="expression">A filter as a plan printed it.</param>
    /// <param name="alias">The scan's alias, or its table's name when it has none.</param>
    /// <param name="prefixedOnly">Whether a bare name never counts - a join filter always names two tables.</param>
    internal static IReadOnlyList<string> Columns(string expression, string alias, bool prefixedOnly = false)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(alias);

        List<Token> tokens = Tokenize(expression);

        if (tokens.Count == 0 || tokens.Any(static x => x.Kind == TokenKind.Unreadable))
        {
            return [];
        }

        // Postgres prefixes every column with its table's alias or none at all, for the whole expression.
        bool prefixed = prefixedOnly || Enumerable.Range(0, tokens.Count - 2).Any(i => IsName(tokens[i]) && tokens[i + 1].Is(".") && IsName(tokens[i + 2]));

        List<(string Column, bool Equality)> columns = [];
        Stack<bool> calls = new();

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];

            if (token.Is("("))
            {
                calls.Push(i > 0 && (IsName(tokens[i - 1]) && !IsKeyword(tokens[i - 1]) || tokens[i - 1].IsWord("any") || tokens[i - 1].IsWord("all")
                    || tokens[i - 1].IsWord("some") || tokens[i - 1].IsWord("array") || tokens[i - 1].IsWord("row") || tokens[i - 1].IsWord("exists")));
                continue;
            }

            if (token.Is(")"))
            {
                if (calls.Count > 0)
                {
                    calls.Pop();
                }

                continue;
            }

            if (token.Kind == TokenKind.Cast)
            {
                i = SkipType(tokens, i + 1) - 1;
                continue;
            }

            if (!IsName(token) || IsKeyword(token) || (i + 1 < tokens.Count && tokens[i + 1].Is("(")))
            {
                continue;
            }

            string? column = null;
            int start = i;
            int last = i;

            if (i + 2 < tokens.Count && tokens[i + 1].Is(".") && IsName(tokens[i + 2]))
            {
                // alias.column - one of this scan's columns only where the alias is its own.
                if (string.Equals(token.Text, alias, StringComparison.Ordinal))
                {
                    column = tokens[i + 2].Text;
                }

                last = i + 2;
            }
            else if (!prefixed)
            {
                column = token.Text;
            }

            i = last;

            if (column is null || calls.Contains(true))
            {
                continue;
            }

            Token? after = last + 1 < tokens.Count ? tokens[last + 1] : null;
            Token? before = start > 0 ? tokens[start - 1] : null;

            if (after is { Kind: TokenKind.Operator } op && (IsJsonOperator(op.Text) || IsLikeOperator(op.Text)))
            {
                continue;
            }

            if (before is { Kind: TokenKind.Operator } prior && IsLikeOperator(prior.Text))
            {
                continue;
            }

            bool equality = (after is { Kind: TokenKind.Operator } a && a.Text == "=") || (before is { Kind: TokenKind.Operator } b && b.Text == "=");

            if (!columns.Any(x => string.Equals(x.Column, column, StringComparison.Ordinal)))
            {
                columns.Add((column, equality));
            }
        }

        return [.. columns.Where(static x => x.Equality).Concat(columns.Where(static x => !x.Equality)).Select(static x => x.Column).Take(MaxColumns)];
    }

    private static bool IsName(Token token) => token.Kind is TokenKind.Word or TokenKind.Quoted;

    private static bool IsKeyword(Token token) => token.Kind == TokenKind.Word && Keywords.Contains(token.Text);

    private static bool IsJsonOperator(string op) =>
        op.StartsWith("->", StringComparison.Ordinal) || op.StartsWith("#>", StringComparison.Ordinal) || op.StartsWith("#-", StringComparison.Ordinal)
        || op is "@>" or "<@" or "?" or "?|" or "?&" or "@?" or "@@";

    private static bool IsLikeOperator(string op) => op is "~~" or "~~*" or "!~~" or "!~~*";

    /// <summary>Skips a type name after <c>::</c>: one name, its schema, the words of a multi-word type, and <c>[]</c>.</summary>
    private static int SkipType(List<Token> tokens, int i)
    {
        if (i >= tokens.Count || !IsName(tokens[i]))
        {
            return i;
        }

        string first = tokens[i].Text.ToLowerInvariant();
        i++;

        if (i + 1 < tokens.Count && tokens[i].Is(".") && IsName(tokens[i + 1]))
        {
            first = tokens[i + 1].Text.ToLowerInvariant();
            i += 2;
        }

        if (first is "character" or "bit" && i < tokens.Count && tokens[i].IsWord("varying"))
        {
            i++;
        }
        else if (first == "double" && i < tokens.Count && tokens[i].IsWord("precision"))
        {
            i++;
        }
        else if (first is "timestamp" or "time" && i + 2 < tokens.Count && (tokens[i].IsWord("with") || tokens[i].IsWord("without"))
                 && tokens[i + 1].IsWord("time") && tokens[i + 2].IsWord("zone"))
        {
            i += 3;
        }

        // A length or precision - character varying(20) - and an array's brackets.
        if (i < tokens.Count && tokens[i].Is("("))
        {
            int depth = 0;

            for (; i < tokens.Count; i++)
            {
                depth += tokens[i].Is("(") ? 1 : tokens[i].Is(")") ? -1 : 0;

                if (depth == 0)
                {
                    i++;
                    break;
                }
            }
        }

        while (i < tokens.Count && (tokens[i].Is("[") || tokens[i].Is("]")))
        {
            i++;
        }

        return i;
    }

    private enum TokenKind
    {
        Word,
        Quoted,
        Literal,
        Number,
        Parameter,
        Cast,
        Punctuation,
        Operator,
        Unreadable,
    }

    private readonly record struct Token(TokenKind Kind, string Text)
    {
        public bool Is(string punctuation) => Kind == TokenKind.Punctuation && Text == punctuation;

        public bool IsWord(string word) => Kind == TokenKind.Word && string.Equals(Text, word, StringComparison.OrdinalIgnoreCase);
    }

    private const string OperatorCharacters = "+-*/<>=~!@#%^&|`?";

    /// <summary>Postgres' deparse of an expression, as tokens: names unquoted, literals and numbers kept apart, operators whole.</summary>
    private static List<Token> Tokenize(string text)
    {
        List<Token> tokens = [];
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '\'')
            {
                int end = Closing(text, i, '\'');

                if (end < 0)
                {
                    return [new Token(TokenKind.Unreadable, text[i..])];
                }

                tokens.Add(new Token(TokenKind.Literal, text[i..(end + 1)]));
                i = end + 1;
            }
            else if (c == '"')
            {
                int end = Closing(text, i, '"');

                if (end < 0)
                {
                    return [new Token(TokenKind.Unreadable, text[i..])];
                }

                tokens.Add(new Token(TokenKind.Quoted, text[(i + 1)..end].Replace("\"\"", "\"", StringComparison.Ordinal)));
                i = end + 1;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int start = i;

                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Word, text[start..i]));
            }
            else if (char.IsDigit(c))
            {
                int start = i;

                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, text[start..i]));
            }
            else if (c == '$' && i + 1 < text.Length && char.IsDigit(text[i + 1]))
            {
                int start = i++;

                while (i < text.Length && char.IsDigit(text[i]))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Parameter, text[start..i]));
            }
            else if (c == ':' && i + 1 < text.Length && text[i + 1] == ':')
            {
                tokens.Add(new Token(TokenKind.Cast, "::"));
                i += 2;
            }
            else if (OperatorCharacters.Contains(c, StringComparison.Ordinal))
            {
                int start = i;

                while (i < text.Length && OperatorCharacters.Contains(text[i], StringComparison.Ordinal))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Operator, text[start..i]));
            }
            else
            {
                tokens.Add(new Token(TokenKind.Punctuation, c.ToString()));
                i++;
            }
        }

        return tokens;
    }

    /// <summary>Where the quote that opened at <paramref name="start" /> closes, a doubled quote being one character; -1 when it never does.</summary>
    private static int Closing(string text, int start, char quote)
    {
        for (int i = start + 1; i < text.Length; i++)
        {
            if (text[i] != quote)
            {
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == quote)
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }
}
