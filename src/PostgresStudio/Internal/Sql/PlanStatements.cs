using PostgresStudio.Services.Server;

namespace PostgresStudio.Internal.Sql;

/// <summary>What the Plan page asks Postgres for (X3, issue #28).</summary>
internal enum PlanMode
{
    /// <summary><c>EXPLAIN</c>: the statement planned, never run.</summary>
    Plan,

    /// <summary><c>EXPLAIN ANALYZE</c>: the statement run, in the console's read-only transaction, and measured.</summary>
    Analyze,

    /// <summary>
    /// The generic plan of a statement with <c>$n</c> placeholders: prepared and explained under
    /// <c>plan_cache_mode = force_generic_plan</c>, never <c>EXPLAIN (GENERIC_PLAN)</c> (D32).
    /// </summary>
    Generic,
}

/// <summary>The <c>EXPLAIN</c> options the Plan page offers, each off unless it says otherwise.</summary>
internal sealed record PlanOptions
{
    /// <summary>
    /// <c>BUFFERS</c>, on by default: the blocks each node touched. Asked only with <c>ANALYZE</c> - before PostgreSQL 18 a plain
    /// <c>EXPLAIN (BUFFERS)</c> is an error - and from 18 an analyzed plan counts buffers unless it says <c>buffers off</c>.
    /// </summary>
    public bool Buffers { get; init; } = true;

    /// <summary><c>TIMING</c>, on by default: each node's time. <c>ANALYZE</c> only; off is cheaper on a slow clock.</summary>
    public bool Timing { get; init; } = true;

    /// <summary><c>VERBOSE</c>: every node's output columns, and every relation's schema.</summary>
    public bool Verbose { get; init; }

    /// <summary><c>SETTINGS</c>: the planner settings that differ from the server's defaults.</summary>
    public bool Settings { get; init; }

    /// <summary><c>WAL</c>: the write-ahead log each node produced. <c>ANALYZE</c> only.</summary>
    public bool Wal { get; init; }

    /// <summary><c>MEMORY</c>: the memory planning used. PostgreSQL 17 and later.</summary>
    public bool Memory { get; init; }

    /// <summary><c>SERIALIZE</c>: the cost of turning the result into what a client receives. <c>ANALYZE</c> only, PostgreSQL 17 and later.</summary>
    public bool Serialize { get; init; }
}

/// <summary>The commands one plan run sends, and what is known about them before anything is sent.</summary>
internal sealed record PlanStatementSet
{
    /// <summary>The <c>EXPLAIN</c> command: of the statement itself, or - for the generic plan - of <c>execute</c> of the prepared one.</summary>
    public required string Explain { get; init; }

    /// <summary>
    /// The <c>prepare</c> command, or <see langword="null" /> when nothing is prepared: it is how the types Postgres infers for a
    /// statement's placeholders are read (<see cref="InferTypesOnly" />), and how the generic plan is made.
    /// </summary>
    public string? Prepare { get; init; }

    /// <summary>
    /// Whether <see cref="Prepare" /> is only there to read the placeholders' inferred types and is deallocated at once - the
    /// custom plan, which is <see cref="Explain" /> of the statement with its values bound - rather than explained
    /// under <see cref="PlanCacheMode" />, which is the generic plan.
    /// </summary>
    public bool InferTypesOnly { get; init; }

    /// <summary>The <c>plan_cache_mode</c> the prepared statement is explained under, or <see langword="null" />.</summary>
    public string? PlanCacheMode { get; init; }

    /// <summary>How many <c>$n</c> values the <c>EXPLAIN</c> command binds: the statement's placeholders, for a custom plan.</summary>
    public int BoundParameters { get; init; }

    /// <summary>What <c>EXPLAIN</c> was asked for, as its option list: <c>analyze, buffers, format json</c>.</summary>
    public required string Options { get; init; }

    /// <summary>The options the server is too old for, named with the release that has them, and so not sent.</summary>
    public IReadOnlyList<string> LeftOut { get; init; } = [];

    /// <summary>How many characters come before the statement in <see cref="Explain" />, when it is not prepared; a Postgres error position counts them.</summary>
    public int ExplainOffset { get; init; }

    /// <summary>How many characters come before the statement in <see cref="Prepare" />.</summary>
    public int PrepareOffset { get; init; }

    /// <summary>What the audit ring and the log record: the commands in order, with <c>$n</c> in place of every value, which is never recorded.</summary>
    public required string Described { get; init; }
}

/// <summary>
/// The Plan page's statements (issue #28): the commands that explain what somebody typed, in the SQL console's three ways -
/// planned, analyzed, and as a generic or custom plan of a statement with placeholders. One of the places an identifier or a
/// keyword becomes SQL text (hard rule 4): what is typed is the visitor's, checked by the console's guard first
/// (<c>SqlConsoleService</c>); everything else here is constant text, the one quoted prepared-statement name, and numbers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Values are parameters and are never interpolated.</b> A placeholder's value is bound to the <c>explain</c> command as an
/// untyped parameter, so Postgres reads it with the input function of the type it infers for the placeholder - an integer, a
/// timestamp, an enum - exactly as it reads a literal of that type; no type name is ever written into the text. The inferred types
/// themselves are read from <c>pg_prepared_statements</c>, after a <c>prepare</c> of the statement in the same transaction, for the
/// page to show beside each box. (The values cannot be bound to an <c>execute</c>: see <see cref="Build" />.)
/// </para>
/// <para>
/// <b>Which options, and where.</b> <c>BUFFERS</c>, <c>TIMING</c>, <c>WAL</c> and <c>SERIALIZE</c> are asked only with
/// <c>ANALYZE</c>; <c>MEMORY</c> and <c>SERIALIZE</c> only on a server that has them (PostgreSQL 17, <see cref="PostgresFeature" />),
/// and are left out, and named, on an older one. <c>FORMAT JSON</c> is always last, so what is read back is the one
/// <c>json</c> value <c>QueryPlanReader</c> reads.
/// </para>
/// </remarks>
internal static class PlanStatements
{
    /// <summary>The most <c>$n</c> placeholders the page offers a box for.</summary>
    public const int MaxParameters = 100;

    /// <summary>The longest value one box carries, in characters: a circuit's message is bounded as well.</summary>
    public const int MaxValueCharacters = 8_192;

    /// <summary>The highest <c>$n</c> placeholder in <paramref name="text" />, outside every string, quoted name, comment and identifier - or zero.</summary>
    /// <remarks>Read from <see cref="SqlLexer" />'s tokens, so a <c>$1</c> inside a literal is a string and asks for no value.</remarks>
    public static int Placeholders(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<SqlToken> tokens = [];
        SqlLexer lexer = new(text);

        while (lexer.TryRead(out SqlToken token))
        {
            if (token.Kind == SqlTokenKind.Unreadable)
            {
                break;
            }

            tokens.Add(token);
        }

        int highest = 0;

        for (int i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].Is('$') || (i > 0 && tokens[i - 1].End == tokens[i].Start && tokens[i - 1].Kind == SqlTokenKind.Word))
            {
                continue;
            }

            long number = 0;
            int digits = 0;
            int end = tokens[i].End;

            for (int j = i + 1; j < tokens.Count && tokens[j].Start == end && tokens[j].Kind == SqlTokenKind.Punctuation && char.IsAsciiDigit(tokens[j].Text[0]); j++)
            {
                number = Math.Min((number * 10) + (tokens[j].Text[0] - '0'), int.MaxValue);
                digits++;
                end = tokens[j].End;
            }

            if (digits > 0)
            {
                highest = (int) Math.Max(highest, number);
            }
        }

        return highest;
    }

    /// <summary>
    /// The commands for <paramref name="statement" />, as a server of <paramref name="versionNumber" /> (<c>server_version_num</c>,
    /// or <see cref="int.MaxValue" /> when it is not known yet) is asked.
    /// </summary>
    /// <param name="statement">The statement as typed: it has passed the console's guard.</param>
    /// <param name="mode">Planned, analyzed or the generic plan.</param>
    /// <param name="options">The options asked for.</param>
    /// <param name="placeholders">The statement's highest <c>$n</c> (<see cref="Placeholders" />).</param>
    /// <param name="versionNumber">The server's <c>server_version_num</c>.</param>
    public static PlanStatementSet Build(string statement, PlanMode mode, PlanOptions options, int placeholders, int versionNumber)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(options);

        bool analyze = mode == PlanMode.Analyze;
        List<string> asked = [];
        List<string> leftOut = [];

        if (analyze)
        {
            asked.Add("analyze");
        }

        if (options.Verbose)
        {
            asked.Add("verbose");
        }

        // ANALYZE counts buffers and times every node unless told otherwise (from 18 buffers too); a plain EXPLAIN asks for neither.
        if (analyze)
        {
            asked.Add(options.Buffers ? "buffers" : "buffers off");
        }

        if (options.Settings)
        {
            asked.Add("settings");
        }

        if (analyze && options.Wal)
        {
            asked.Add("wal");
        }

        if (analyze && !options.Timing)
        {
            asked.Add("timing off");
        }

        if (options.Memory)
        {
            if (PostgresFeatures.Has(PostgresFeature.ExplainMemory, versionNumber))
            {
                asked.Add("memory");
            }
            else
            {
                leftOut.Add("MEMORY (" + PostgresFeatures.Needs(PostgresFeature.ExplainMemory) + ")");
            }
        }

        if (analyze && options.Serialize)
        {
            if (PostgresFeatures.Has(PostgresFeature.ExplainSerialize, versionNumber))
            {
                asked.Add("serialize");
            }
            else
            {
                leftOut.Add("SERIALIZE (" + PostgresFeatures.Needs(PostgresFeature.ExplainSerialize) + ")");
            }
        }

        asked.Add("format json");

        string list = string.Join(", ", asked);
        bool prepared = mode == PlanMode.Generic || placeholders > 0;
        string prefix = "explain (" + list + ") ";

        if (!prepared)
        {
            return new PlanStatementSet
            {
                Explain = prefix + statement,
                Options = list,
                LeftOut = leftOut,
                ExplainOffset = prefix.Length,
                Described = prefix + statement,
            };
        }

        string name = SqlIdentifier.Quote(HealthStatementQueries.PlanStatementName);
        string prepare = "prepare " + name + " as ";

        if (mode != PlanMode.Generic)
        {
            // A custom plan: the statement itself explained with its values bound to it. A protocol-level parameter cannot reach
            // an EXECUTE - a utility statement's arguments are not analyzed with the command's own parameters, and Postgres says
            // "could not determine data type of parameter $1" (measured on 14, 17 and 18) - but it reaches the statement an
            // EXPLAIN explains, and the planner folds a bound value in as a constant, so the plan is the one for that value. The
            // prepare is only there to read the types Postgres infers for the placeholders, and is deallocated at once.
            return new PlanStatementSet
            {
                Prepare = prepare + statement,
                InferTypesOnly = true,
                Explain = prefix + statement,
                BoundParameters = placeholders,
                Options = list,
                LeftOut = leftOut,
                ExplainOffset = prefix.Length,
                PrepareOffset = prepare.Length,
                Described = prefix + statement + ";\n" + prepare + statement,
            };
        }

        // The generic plan is planned without values whatever execute passes, so it passes nulls, as text.
        const string GenericMode = "force_generic_plan";
        string arguments = "(" + string.Join(", ", Enumerable.Repeat("null", placeholders)) + ")";
        string explain = prefix + "execute " + name + arguments;

        return new PlanStatementSet
        {
            Prepare = prepare + statement,
            PlanCacheMode = GenericMode,
            Explain = explain,
            Options = list,
            LeftOut = leftOut,
            PrepareOffset = prepare.Length,
            Described = prepare + statement + ";\nset local plan_cache_mode = " + GenericMode + ";\n" + explain,
        };
    }

    /// <summary>
    /// <c>set local plan_cache_mode = …</c>, for the one mode <see cref="Build" /> names and nothing else: the value is text in a
    /// statement, so it is held to its constant.
    /// </summary>
    public static string SetPlanCacheMode(string mode) =>
        mode is "force_generic_plan"
            ? "set local plan_cache_mode = " + mode
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only the generic plan's cache mode is sent.");
}
