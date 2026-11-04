using System.Collections.Concurrent;
using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Schema;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Statements;

/// <summary>One database's index advice at a time, process-wide: the runs going now, by store and database.</summary>
/// <remarks>
/// A singleton, because a database is everybody's. A second visitor is <em>refused, never joined</em>: joining would hand one
/// visitor's advice to another whose masks and gates differ (as <see cref="Health.HealthActionFlights" /> does for the same reason).
/// </remarks>
internal sealed class IndexAdvisorFlights
{
    private readonly ConcurrentDictionary<string, byte> running = new(StringComparer.Ordinal);

    /// <summary>Takes the key; <see langword="false" /> when a run holds it.</summary>
    /// <param name="key">Store key and database identity.</param>
    public bool TryEnter(string key) => running.TryAdd(key, 0);

    /// <summary>Releases the key.</summary>
    /// <param name="key">What <see cref="TryEnter" /> took.</param>
    public void Exit(string key) => running.TryRemove(key, out _);

    /// <summary>How many runs are going now, for a test.</summary>
    internal int Count => running.Count;
}

/// <summary>A statement the advisor will plan: what the visitor may see of it and the statement sweep's counts.</summary>
/// <param name="QueryId">Its <c>queryid</c>.</param>
/// <param name="Text">Its text as the statement sweep read it, unmasked: planned, never shown.</param>
/// <param name="Truncated">Whether the read cut it.</param>
/// <param name="Calls">Its calls over the window, or since its counters began when it has no window.</param>
/// <param name="Tables">The judged tables it names.</param>
internal sealed record SelectedStatement(long QueryId, string Text, bool Truncated, double Calls, IReadOnlyList<StudioTable> Tables);

/// <summary>Which statements the advisor plans, and how many it passed over on the way, counted by reason.</summary>
/// <param name="Kept">At most <see cref="IndexAdvisor.MaxStatements" />, the most execution time first.</param>
/// <param name="Skipped">The statements passed over, each reason counted ("4 writes").</param>
internal sealed record WorkloadStatements(IReadOnlyList<SelectedStatement> Kept, IReadOnlyList<string> Skipped);

/// <summary>A statement the advisor planned and may compare: its plan now, parsed, for reading candidates.</summary>
/// <param name="QueryId">Its <c>queryid</c>.</param>
/// <param name="Text">Its text, planned again under each index.</param>
/// <param name="Truncated">Whether the read cut it.</param>
/// <param name="Calls">Its calls.</param>
/// <param name="Before">The planner's total cost of its generic plan now.</param>
/// <param name="Plan">That plan, unmasked: by admission it reads nothing the visitor may not see, and it never leaves the service.</param>
/// <param name="Batch">The generic-plan batch for <paramref name="Text" />.</param>
internal sealed record AdvisedStatement(long QueryId, string Text, bool Truncated, double Calls, double Before, QueryPlan Plan, string Batch);

/// <summary>The indexes one admitted plan offers, after the studio's contributors have been through them.</summary>
/// <param name="Statement">The statement's position in the admitted list.</param>
/// <param name="Calls">Its calls.</param>
/// <param name="Plan">Its plan.</param>
/// <param name="Candidates">The indexes it offers.</param>
internal sealed record CandidateSource(int Statement, double Calls, QueryPlan Plan, IReadOnlyList<HypotheticalIndex> Candidates);

/// <summary>An index to try, and how much it could matter before it is tried.</summary>
/// <param name="Index">The index.</param>
/// <param name="Potential">Calls times the planner's cost of the costliest node that reads its table, summed over the statements that suggested it.</param>
/// <param name="Own">Whether it is the visitor's own index, which is always tried, outside the cap.</param>
internal sealed record AdvisedCandidate(HypotheticalIndex Index, double Potential, bool Own);

/// <summary>What one imagined index did to one statement's plan.</summary>
/// <param name="Statement">The statement's position in the admitted list.</param>
/// <param name="After">The planner's total cost under the index, or <see langword="null" /> when the plan could not be compared.</param>
/// <param name="Used">Whether the plan under the index reads it, judged before any mask.</param>
internal sealed record IndexTrialStatement(int Statement, double? After, bool Used);

/// <summary>What one trial came to: the plans it compared, or the SQLSTATE Postgres refused it with.</summary>
/// <param name="Candidate">The index.</param>
/// <param name="SqlState">The SQLSTATE of a refusal, or <see langword="null" />.</param>
/// <param name="Statements">The statements compared.</param>
internal sealed record IndexTrialResult(AdvisedCandidate Candidate, string? SqlState, IReadOnlyList<IndexTrialStatement> Statements);

/// <summary>The tried indexes, ranked.</summary>
/// <param name="Recommended">Those that help, the most saved first.</param>
/// <param name="NotUsed">Those that do not.</param>
/// <param name="NotTried">Those Postgres refused, as "caption: reason".</param>
internal sealed record IndexRanking(IReadOnlyList<IndexAdviceItem> Recommended, IReadOnlyList<IndexAdviceItem> NotUsed, IReadOnlyList<string> NotTried);

/// <summary>
/// 1.2-INDEX-A: the Statements page's index advisor, the pure half - which statements to plan, how to merge and rank what their plans
/// suggest, how to rank what was tried. Reads no database and asks no gate; <see cref="StatementsService" /> does both and calls
/// these.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> The studio plans the statements with the most execution time that the visitor may see, imagines each
/// index their plans suggest with hypopg - one at a time, in a read-only transaction that resets hypopg last - and plans the
/// statements that read the index's table again. An index is <em>recommended</em> when a plan under it reads it and costs at
/// least <see cref="MinSaving" /> less than it did; its worth is the planner's cost saved times the statement's calls, summed. A
/// cost is the planner's unit, not time, and a generic plan is not the plan a given value gets: <see cref="Caveat" /> says so on
/// the page, always.
/// </para>
/// <para>
/// <b>Bounds.</b> <see cref="MaxStatements" />, <see cref="MaxCandidates" />, <see cref="MaxTrialPlans" />, <see cref="RunBudget" />.
/// A run a bound stopped returns what it has and names the bound.
/// </para>
/// </remarks>
internal static class IndexAdvisor
{
    /// <summary>The most statements one run plans.</summary>
    public const int MaxStatements = 20;

    /// <summary>The most different indexes one run imagines (the visitor's own, if it has one, besides).</summary>
    public const int MaxCandidates = 12;

    /// <summary>The most plans one run makes under imagined indexes.</summary>
    public const int MaxTrialPlans = 150;

    /// <summary>The wall-clock time one run is given, checked between plans and between trials.</summary>
    public static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How much cheaper a plan under an index must be for the index to count as helping that statement: 10%. A judgement, not a
    /// measurement - kept low because the ranking already weights by calls (Dexter asks for 50% a query) - to revisit at the 1.2 UX
    /// review.
    /// </summary>
    public const double MinSaving = 0.10;

    /// <summary>
    /// How much more a longer index must save than a shorter one it extends to be shown beside it rather than folded into it: 1.1
    /// times. A judgement, not a measurement, to revisit at the 1.2 UX review.
    /// </summary>
    public const double PrefixFold = 1.1;

    /// <summary>The query-string name that opens the advisor (D9): <c>?advise=all</c>, or <c>?advise=schema.table</c>.</summary>
    public const string AdviseParameter = "advise";

    /// <summary>The value of <see cref="AdviseParameter" /> that means every table.</summary>
    public const string AdviseAll = "all";

    /// <summary>What the page and the card action's table always say about what the numbers are.</summary>
    public const string Caveat =
        "Planner estimates, not measurements: each statement's generic plan was planned now, with and without one hypothetical index "
        + "at a time, and never run. A cost is the planner's unit, weighted by the calls pg_stat_statements counted over the window - "
        + "not time, and a generic plan is not the plan a given value gets. Every insert and update maintains every index, and an "
        + "index takes disk and cache: those writes are not in this sum. Only the 20 statements with the most execution time that "
        + "you may see and the studio can plan were tried; writes are never planned, and statements another role ran are never "
        + "among them.";

    /// <summary>What a request naming a table no statement the visitor may see names is told.</summary>
    public const string NoStatementNamesTable = "No statement you may see names that table.";

    /// <summary>What the advisor says where another run holds the database.</summary>
    public const string AlreadyRunningForDatabase =
        "Index advice is already running for this database, started from another tab or by another visitor. Try again when it has finished.";

    /// <summary>What the advisor says where this tab already has a run going.</summary>
    public const string AlreadyRunningInTab = "Index advice is already running in this tab.";

    // ---------------------------------------------------------------------------------------------------------------
    // Which statements.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The first <see cref="MaxStatements" /> statements, in the page's own order (execution time over the window, then
    /// <c>queryid</c>), that the drill-in would plan and show to this visitor and that name a table: never a write (planning one takes
    /// the lock its execution would), a utility statement, a cut text, one that is not shown to this visitor, or one the statement
    /// sweep could not tie to a judged table. Each statement passed over is counted by reason, never listed.
    /// </summary>
    /// <param name="facts">The statement sweep's statements: nothing in them is yet shown to anybody.</param>
    /// <param name="rows">The same statements as this visitor may see them (<c>StatementsService.Show</c>), by <c>queryid</c>.</param>
    /// <param name="withheld">The schemas the visitor may not see, or <see langword="null" /> when their gate could not be read.</param>
    /// <param name="table">A <see cref="StudioTable.Key" />: only statements naming it count, and the others are not even passed over.</param>
    public static WorkloadStatements Select(
        IReadOnlyList<StatementFact> facts,
        IReadOnlyDictionary<long, StatementRowView> rows,
        IReadOnlySet<string>? withheld,
        string? table)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(rows);

        List<SelectedStatement> kept = [];
        int writes = 0, utility = 0, locking = 0, shape = 0, cut = 0, notShown = 0, untied = 0, uncalled = 0;

        IEnumerable<StatementFact> ordered = facts
            .OrderByDescending(x => rows.TryGetValue(x.QueryId, out StatementRowView? row) ? row.Window?.ExecMs ?? double.MinValue : double.MinValue)
            .ThenBy(static x => x.QueryId);

        foreach (StatementFact fact in ordered)
        {
            if (kept.Count >= MaxStatements)
            {
                break;
            }

            if (table is not null && !fact.Tables.Any(x => string.Equals(x.Key, table, StringComparison.Ordinal)))
            {
                continue;
            }

            if (!rows.TryGetValue(fact.QueryId, out StatementRowView? row) || fact.Text is not { } text || row.Text is null || withheld is null
                || StatementsService.TextNamesWithheld(text, withheld) is not null)
            {
                notShown++;
                continue;
            }

            switch (HealthStatementQueries.Refusal(text, fact.TextTruncated))
            {
                case StatementPlanRefusal.None:
                    break;
                case StatementPlanRefusal.Truncated:
                    cut++;
                    continue;
                case StatementPlanRefusal.LockingClause:
                    locking++;
                    continue;
                case StatementPlanRefusal.NotASelect:
                    if (IsWrite(text))
                    {
                        writes++;
                    }
                    else
                    {
                        utility++;
                    }

                    continue;
                default:
                    shape++;
                    continue;
            }

            if (fact.Tables.Count == 0)
            {
                untied++;
                continue;
            }

            double calls = row.Window?.Calls ?? row.SinceBegan.Calls;

            if (calls <= 0)
            {
                uncalled++;
                continue;
            }

            kept.Add(new SelectedStatement(fact.QueryId, text, fact.TextTruncated, calls, fact.Tables));
        }

        return new WorkloadStatements(
            kept,
            Counted(
                (writes, "write", "writes"),
                (utility, "utility statement", "utility statements"),
                (locking, "locking read", "locking reads"),
                (shape, "statement the console's guard refuses", "statements the console's guard refuses"),
                (cut, "cut text", "cut texts"),
                (notShown, "statement not shown to you", "statements not shown to you"),
                (untied, "statement the checks could not tie to a table", "statements the checks could not tie to a table"),
                (uncalled, "statement not called in the window", "statements not called in the window")));
    }

    private static bool IsWrite(string text)
    {
        ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();
        int end = 0;

        while (end < trimmed.Length && char.IsAsciiLetter(trimmed[end]))
        {
            end++;
        }

        string word = trimmed[..end].ToString();

        return word.Equals("insert", StringComparison.OrdinalIgnoreCase) || word.Equals("update", StringComparison.OrdinalIgnoreCase)
            || word.Equals("delete", StringComparison.OrdinalIgnoreCase) || word.Equals("merge", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Counts as sentences' parts - "4 writes" - leaving out what is zero.</summary>
    internal static IReadOnlyList<string> Counted(params (int Count, string One, string Many)[] counts) =>
        [.. counts.Where(static x => x.Count > 0).Select(static x => x.Count.ToString("N0", CultureInfo.InvariantCulture) + " " + (x.Count == 1 ? x.One : x.Many))];

    // ---------------------------------------------------------------------------------------------------------------
    // Which indexes.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What identifies an index across statements: its definition, which a builder writes deterministically (Marten's DDL as much as
    /// <see cref="MigrationSqlText.CreateIndex" />), so the same index suggested by two plans - or by a plan and the visitor - is one.
    /// </summary>
    internal static string Identity(HypotheticalIndex index) => index.Definition;

    /// <summary>
    /// The indexes the admitted plans suggest, merged and put in the order they are tried: the same index suggested by several
    /// statements is one, with their potential summed; ordered by that potential, then fewer columns, then key. The visitor's own
    /// indexes are always among them, first, flagged.
    /// </summary>
    /// <param name="sources">What each admitted plan offers.</param>
    /// <param name="own">The visitor's own indexes, already through the studio's contributors.</param>
    public static IReadOnlyList<AdvisedCandidate> Merge(IReadOnlyList<CandidateSource> sources, IReadOnlyList<HypotheticalIndex> own)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(own);

        Dictionary<string, (HypotheticalIndex Index, double Potential, bool Own)> merged = new(StringComparer.Ordinal);

        foreach (CandidateSource source in sources)
        {
            foreach (HypotheticalIndex index in source.Candidates)
            {
                if (index.Schema is not { Length: > 0 } schema)
                {
                    continue;
                }

                double potential = source.Calls * WorkloadIndexCandidates.ScanCost(source.Plan, schema, index.Table);
                string identity = Identity(index);

                merged[identity] = merged.TryGetValue(identity, out var known)
                    ? (known.Index, known.Potential + potential, known.Own)
                    : (index, potential, false);
            }
        }

        foreach (HypotheticalIndex index in own)
        {
            string identity = Identity(index);

            merged[identity] = merged.TryGetValue(identity, out var known) ? (known.Index, known.Potential, true) : (index, 0, true);
        }

        return
        [
            .. merged.Values
                .Select(static x => new AdvisedCandidate(x.Index, x.Potential, x.Own))
                .OrderByDescending(static x => x.Own)
                .ThenByDescending(static x => x.Potential)
                .ThenBy(static x => x.Index.Columns.Count)
                .ThenBy(static x => x.Index.Key, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// The first <see cref="MaxCandidates" /> of <paramref name="candidates" /> that are not the visitor's own, and every one that is.
    /// </summary>
    /// <param name="candidates">Merged and ordered (<see cref="Merge" />).</param>
    /// <param name="notTried">How many were left out by the cap.</param>
    public static IReadOnlyList<AdvisedCandidate> Cap(IReadOnlyList<AdvisedCandidate> candidates, out int notTried)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        List<AdvisedCandidate> capped = [];
        int others = 0;
        notTried = 0;

        foreach (AdvisedCandidate candidate in candidates)
        {
            if (candidate.Own)
            {
                capped.Add(candidate);
            }
            else if (others < MaxCandidates)
            {
                capped.Add(candidate);
                others++;
            }
            else
            {
                notTried++;
            }
        }

        return capped;
    }

    /// <summary>
    /// The trials to make, in order, each with the statements whose plan now reads its table. Taken while the plans they need fit
    /// under <see cref="MaxTrialPlans" />; the first that would not fit ends the list, and the rest are counted.
    /// </summary>
    /// <param name="candidates">Capped and ordered.</param>
    /// <param name="statements">The admitted statements.</param>
    /// <param name="overBound">How many were left out by the plan bound.</param>
    public static IReadOnlyList<HypotheticalIndexTrial> Trials(IReadOnlyList<AdvisedCandidate> candidates, IReadOnlyList<AdvisedStatement> statements, out int overBound)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(statements);

        List<HypotheticalIndexTrial> trials = [];
        int plans = 0;
        overBound = 0;

        for (int c = 0; c < candidates.Count; c++)
        {
            HypotheticalIndex index = candidates[c].Index;

            List<int> reading = [];

            for (int i = 0; i < statements.Count; i++)
            {
                if (index.Schema is { } schema && WorkloadIndexCandidates.Reads(statements[i].Plan, schema, index.Table))
                {
                    reading.Add(i);
                }
            }

            if (reading.Count == 0)
            {
                continue;
            }

            if (plans + reading.Count > MaxTrialPlans)
            {
                overBound = candidates.Count - c;
                break;
            }

            plans += reading.Count;
            trials.Add(new HypotheticalIndexTrial(index, reading));
        }

        return trials;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The visitor's own index.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The columns the admitted <paramref name="plans" /> compare on <paramref name="schema" />.<paramref name="table" />, in the order
    /// they first name them: those a scan of it filters on or tests as an index or recheck condition, and those a join compares
    /// against it. The visitor already sees these names in the drill-in's plans; a list read from <c>pg_attribute</c> would show
    /// columns no visible statement touches. Pure.
    /// </summary>
    /// <param name="plans">The plans of the statements that were admitted.</param>
    /// <param name="schema">The table's schema.</param>
    /// <param name="table">The table.</param>
    public static IReadOnlyList<string> ComparedColumns(IEnumerable<QueryPlan> plans, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(plans);

        List<string> columns = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (QueryPlan plan in plans)
        {
            List<PlanNode> nodes = [.. plan.Nodes];
            List<string> aliases = [.. nodes
                .Where(x => string.Equals(x.Schema, schema, StringComparison.Ordinal) && string.Equals(x.RelationName, table, StringComparison.Ordinal))
                .Select(x => x.Alias ?? table)
                .Distinct(StringComparer.Ordinal)];

            foreach (PlanNode node in nodes)
            {
                bool scan = string.Equals(node.Schema, schema, StringComparison.Ordinal) && string.Equals(node.RelationName, table, StringComparison.Ordinal);

                if (scan)
                {
                    string alias = node.Alias ?? table;

                    foreach (string? expression in new[] { node.Filter, node.IndexCondition, node.RecheckCondition })
                    {
                        Add(expression, alias, prefixedOnly: false);
                    }
                }

                foreach (string? expression in new[] { node.JoinFilter, node.HashCondition, node.MergeCondition })
                {
                    foreach (string alias in aliases)
                    {
                        Add(expression, alias, prefixedOnly: true);
                    }
                }
            }
        }

        return columns;

        void Add(string? expression, string alias, bool prefixedOnly)
        {
            if (expression is null)
            {
                return;
            }

            foreach (string column in HypotheticalIndexCandidates.ColumnsCompared(expression, alias, prefixedOnly))
            {
                if (seen.Add(column))
                {
                    columns.Add(column);
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ranking.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// What the tried indexes came to. Per statement and index: <em>used</em> when the plan under the index reads it; <em>helped</em>
    /// when it is used and costs at most <c>1 - <see cref="MinSaving" /></c> of what it did; its saving is then calls times the cost
    /// saved. Per index: the sum of the savings, its share of the workload (every statement's calls times its cost now), and how many
    /// statements it helped. An index that helps at least one is <em>recommended</em>, the most saved first, then fewer columns, then
    /// key; a longer index on the same table that extends a recommended one and saves less than <see cref="PrefixFold" /> times as much
    /// is folded into it. Pure.
    /// </summary>
    /// <param name="statements">The admitted statements.</param>
    /// <param name="results">The trials that were made.</param>
    /// <param name="tableWrites">Rows inserted plus updated since the counters began, by candidate, when the catalog said.</param>
    public static IndexRanking Rank(IReadOnlyList<AdvisedStatement> statements, IReadOnlyList<IndexTrialResult> results, Func<HypotheticalIndex, long?> tableWrites)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(tableWrites);

        double workload = statements.Sum(static x => x.Calls * x.Before);
        List<IndexAdviceItem> tried = [];
        List<string> refused = [];

        foreach (IndexTrialResult result in results)
        {
            HypotheticalIndex index = result.Candidate.Index;

            if (result.SqlState is { } sqlState)
            {
                refused.Add(index.Caption + ": " + WhatIfResult.Failed(sqlState));
                continue;
            }

            double saved = 0;
            int helped = 0;
            List<IndexAdviceStatement> compared = [];

            foreach (IndexTrialStatement one in result.Statements)
            {
                AdvisedStatement statement = statements[one.Statement];
                bool helps = one.Used && one.After is { } after && after <= statement.Before * (1 - MinSaving);

                if (helps)
                {
                    saved += statement.Calls * (statement.Before - one.After!.Value);
                    helped++;
                }

                compared.Add(new IndexAdviceStatement(statement.QueryId, statement.Calls, statement.Before, one.After, one.Used));
            }

            (string? remedy, string? note) = Remedy(index);

            tried.Add(new IndexAdviceItem
            {
                Index = index,
                TableKey = SchemaKey.For(index.Schema ?? string.Empty, index.Table),
                Remedy = remedy,
                RemedyNote = note,
                TableWrites = tableWrites(index),
                Saved = saved,
                Share = workload > 0 ? saved / workload : 0,
                Helped = helped,
                Statements = compared,
                Own = result.Candidate.Own,
            });
        }

        List<IndexAdviceItem> recommended =
        [
            .. tried
                .Where(static x => x.Helped >= 1)
                .OrderByDescending(static x => x.Saved)
                .ThenBy(static x => x.Index.Columns.Count)
                .ThenBy(static x => x.Index.Key, StringComparer.Ordinal),
        ];

        recommended = Fold(recommended);

        // A folded index is part of its shorter neighbour's note, not a failure to help: it is not listed as unused.
        List<IndexAdviceItem> notUsed = [.. tried.Where(static x => x.Helped < 1)];

        return new IndexRanking(recommended, notUsed, refused);
    }

    /// <summary>
    /// Folds each recommended index that extends another recommended one without saving <see cref="PrefixFold" /> times as much into
    /// the shorter one ("also tried (a, b), no better"); one that does save that much stays beside it and says it "extends" it. The
    /// visitor's own index is neither folded nor folded into. Order is kept.
    /// </summary>
    private static List<IndexAdviceItem> Fold(List<IndexAdviceItem> recommended)
    {
        bool[] folded = new bool[recommended.Count];
        List<string>[] notes = [.. recommended.Select(static _ => new List<string>())];

        for (int i = 0; i < recommended.Count; i++)
        {
            IndexAdviceItem longer = recommended[i];

            if (longer.Own)
            {
                continue;
            }

            // The longest recommended index on the same table, of the same origin, whose columns lead this one's.
            int shorter = -1;

            for (int j = 0; j < recommended.Count; j++)
            {
                IndexAdviceItem candidate = recommended[j];

                if (j != i && !candidate.Own && candidate.Index.Origin == longer.Index.Origin
                    && string.Equals(candidate.TableKey, longer.TableKey, StringComparison.Ordinal)
                    && Leads(candidate.Index.Columns, longer.Index.Columns)
                    && (shorter < 0 || candidate.Index.Columns.Count > recommended[shorter].Index.Columns.Count))
                {
                    shorter = j;
                }
            }

            if (shorter < 0)
            {
                continue;
            }

            if (longer.Saved < recommended[shorter].Saved * PrefixFold)
            {
                folded[i] = true;
                notes[shorter].Add("Also tried (" + string.Join(", ", longer.Index.Columns) + "), no better.");
            }
            else
            {
                notes[i].Add("Extends (" + string.Join(", ", recommended[shorter].Index.Columns) + ").");
            }
        }

        return
        [
            .. recommended
                .Select((item, index) => (Item: item, Index: index))
                .Where(x => !folded[x.Index])
                .Select(x => notes[x.Index].Count == 0 ? x.Item : x.Item with { FoldedNote = string.Join(" ", notes[x.Index]) }),
        ];
    }

    /// <summary>Whether <paramref name="shorter" /> is a leading, strictly shorter prefix of <paramref name="longer" />.</summary>
    private static bool Leads(IReadOnlyList<string> shorter, IReadOnlyList<string> longer) =>
        shorter.Count > 0 && shorter.Count < longer.Count && shorter.Zip(longer, static (a, b) => string.Equals(a, b, StringComparison.Ordinal)).All(static x => x);

    /// <summary>
    /// What to do about an index: a Marten index is declared in <c>StoreOptions</c> (its line), any other table's is created
    /// with <c>CREATE INDEX CONCURRENTLY</c>, and an index on a table named like Marten's that no store declares is left to
    /// the note. Never run.
    /// </summary>
    internal static (string? Remedy, string? Note) Remedy(HypotheticalIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (index.Origin == HypotheticalIndexOrigin.Marten)
        {
            return (index.Line, index.Note);
        }

        if (DatabaseObjectClassifier.IsMartenName(index.Table) || index.Schema is not { } schema)
        {
            return (null, HypotheticalIndexCandidates.MartenTableNote);
        }

        return (MigrationSqlText.CreateIndexConcurrently(schema, index.Table, index.Columns), null);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Words.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A recommendation's headline: "Add this index: saves ~38% of the planned cost of the 20 statements tried, weighted by calls,
    /// across 4 statements." Under one percent, "under 1%".
    /// </summary>
    /// <param name="item">The recommendation.</param>
    /// <param name="statementsTried">How many statements the run compared.</param>
    public static string Headline(IndexAdviceItem item, int statementsTried)
    {
        ArgumentNullException.ThrowIfNull(item);

        string share = item.Share < 0.01 ? "under 1%" : "~" + TableText.Percent(item.Share);

        return "Add this index: saves " + share + " of the planned cost of the " + Statements(statementsTried)
            + " tried, weighted by calls, across " + Statements(item.Helped) + ".";
    }

    /// <summary>"1 statement", "20 statements".</summary>
    internal static string Statements(int count) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " statement" : " statements");

    /// <summary>"Writes that would maintain it: 1,204,331 inserts and updates on public.orders since the counters began".</summary>
    internal static string Writes(IndexAdviceItem item) =>
        item.TableWrites is { } writes
            ? "Writes that would maintain it: " + writes.ToString("N0", CultureInfo.InvariantCulture) + " inserts and updates on " + item.TableKey
              + " since the counters began."
            : string.Empty;
}
