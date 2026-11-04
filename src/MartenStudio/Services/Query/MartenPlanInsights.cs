using System.Globalization;
using System.Text.RegularExpressions;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;

namespace MartenStudio.Services.Query;

/// <summary>
/// 1.0-P3C (issue #28 phase 3): Marten Studio's plan insights P26 to P28 - what a plan says about a document table that only a
/// studio that knows the store's configuration can say - pure evaluators over the plan, the facts beside it and the tables the
/// store declares (<see cref="MartenPlanTable" />). They read no database and run no Marten LINQ terminal: what the store declares comes
/// from <c>StoreOptions</c> alone (hard rule 14), and a remedy is a <c>StoreOptions</c> line, never DDL and never run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Judged against the declarations, not the database.</b> An index counts when the store declares it; an index somebody made by
/// hand counts for nothing here, because the next schema apply drops it (<c>AutoCreate.CreateOrUpdate</c> is not additive). So the
/// remedy is always a line to declare, and says so.
/// </para>
/// <para>
/// <b>Only a visible document type's table, only schema-qualified.</b> A relation matches a declared table by the schema and name the
/// plan printed (a plan run without <c>VERBOSE</c> names none, and a pasted one may not), and a hidden type has no
/// <see cref="MartenPlanTable" />, so its table is any table: never judged, never named. A relation the facts say is withheld is
/// skipped too. A plan none of this fits is listed as not judged, with no word about the store.
/// </para>
/// </remarks>
internal static partial class MartenPlanInsights
{
    /// <summary>P26.</summary>
    public static readonly PlanInsightInfo JsonFilterWithoutIndex =
        new("P26", "JSON filter on a document table with no usable index", HealthSeverity.Warning, false);

    /// <summary>P27.</summary>
    public static readonly PlanInsightInfo GinNotUsed =
        new("P27", "GIN index on data not used for a containment filter", HealthSeverity.Info, false);

    /// <summary>P28.</summary>
    public static readonly PlanInsightInfo FilteredAfterIndex =
        new("P28", "Tenant or soft-delete predicate filtered after the index", HealthSeverity.Warning, true);

    /// <summary>Every Marten Studio plan insight, by code.</summary>
    public static IReadOnlyList<PlanInsightInfo> All { get; } = [JsonFilterWithoutIndex, GinNotUsed, FilteredAfterIndex];

    /// <summary>The prefix of every Marten document table's name.</summary>
    internal const string DocumentTablePrefix = "mt_doc_";

    /// <summary>Whether <paramref name="plan" /> reads anything that is named like a Marten document table.</summary>
    public static bool ReadsDocumentTables(QueryPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Nodes.Any(static x => x.RelationName?.StartsWith(DocumentTablePrefix, StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// The findings on <paramref name="plan" />, and the insights that could not be judged on it.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="facts">What the Plan page read beside it, or <see langword="null" />.</param>
    /// <param name="tables">The store's visible document tables, or <see langword="null" /> when they could not be learned.</param>
    public static PlanContribution Evaluate(QueryPlan plan, PlanFacts? facts, IReadOnlyList<MartenPlanTable>? tables)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!ReadsDocumentTables(plan))
        {
            return PlanContribution.None;
        }

        List<PlanInsightFinding> findings =
        [
            .. JsonFilters(plan, facts, tables),
            .. GinSkipped(plan, facts, tables),
            .. FilteredAfterIndexes(plan, facts, tables),
        ];

        // A plan none of whose document-table scans is a declared, visible type's is not judged. P28 on a plain plan is already
        // listed with the others that need ANALYZE, so it is not listed twice.
        bool judged = tables is not null && plan.Nodes.Any(x => TableOf(x, facts, tables) is not null);
        IReadOnlyList<PlanInsightInfo> unjudged = judged
            ? []
            : [.. All.Where(x => plan.Analyzed || !x.NeedsAnalyze)];

        return new PlanContribution(findings, unjudged);
    }

    /// <summary>
    /// P26: a sequential scan, or an indexed scan whose filter discarded most of what it read, of a document table, filtering on a
    /// member of the JSON that no index the store declares serves. The remedy is the <c>StoreOptions</c> line that declares one.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> JsonFilters(QueryPlan plan, PlanFacts? facts, IReadOnlyList<MartenPlanTable>? tables)
    {
        List<(PlanNode Node, MartenPlanTable Table, IReadOnlyList<Unserved> Unserved, double Weight)> found = [];

        foreach (PlanNode node in plan.Nodes)
        {
            if (TableOf(node, facts, tables) is not { } table || node.Filter is not { Length: > 0 } filter || !ReadsMuch(plan, node, out double weight))
            {
                continue;
            }

            List<Unserved> unserved = Unserve(table, JsonUses.Read(filter));

            if (unserved.Count > 0)
            {
                found.Add((node, table, unserved, weight));
            }
        }

        return found
            .OrderByDescending(static x => x.Weight)
            .ThenBy(static x => x.Node.Id)
            .Take(PlanInsights.MaxPerInsight)
            .Select(x => Finding(
                JsonFilterWithoutIndex,
                x.Node,
                x.Node.Description + " filters the " + x.Table.TypeName + " documents on " + Subjects(x.Unserved) + " in the JSON"
                + Read(plan, x.Node) + ", and no index or duplicated field the store declares serves it, so every document is read and parsed.",
                RemedyFor(x.Table, x.Unserved)));
    }

    /// <summary>
    /// P27: a sequential scan of a document table whose filter tests containment (<c>data @&gt; …</c>) although the type declares a
    /// GIN index on <c>data</c> (or the facts list one). Text only: why the planner may have skipped it.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> GinSkipped(QueryPlan plan, PlanFacts? facts, IReadOnlyList<MartenPlanTable>? tables)
    {
        return plan.Nodes
            .Where(static x => x.NodeType == "Seq Scan" && x.Filter is { Length: > 0 })
            .Select(x => (Node: x, Table: TableOf(x, facts, tables)))
            .Where(x => x.Table is not null
                && JsonUses.Read(x.Node.Filter!).Any(static use => use.Kind == JsonUseKind.Containment)
                && (x.Table.GinOnData is not null || FactsHaveGin(facts, x.Node)))
            .OrderBy(static x => x.Node.Id)
            .Take(PlanInsights.MaxPerInsight)
            .Select(x => Finding(
                GinNotUsed,
                x.Node,
                x.Node.Description + " tests containment (@>) on the " + x.Table!.TypeName + " documents' JSON, and the type declares a GIN index on data"
                + (x.Table.GinOnData is { } gin ? " (" + gin.Name + ")" : string.Empty) + ", yet Postgres read the whole table"
                + (x.Node.PlanRows is { } rows ? " (expecting " + Rows(rows) + " rows to match)" : string.Empty) + ".",
                "The planner chooses a sequential scan when it expects the containment to match a large share of the table, or when the table is "
                + "small enough that reading it costs less than the index - then nothing is wrong. Otherwise: the index declared as "
                + "GinIndexJsonData() uses the jsonb_path_ops operator class, which serves @> and not the key-existence tests (?, ?|, ?&), "
                + "so a filter that also tests those is not served by it; a containment value passed as a parameter is planned without "
                + "knowing the value (a generic plan, P21); and the statistics may be stale or the index not yet applied (P15, and Schema › "
                + "Apply in this studio). Planning the statement again after ANALYZE on the table shows whether the index is used."));
    }

    /// <summary>
    /// P28: an index scan of a conjoined-tenant or soft-deleted document table whose <i>filter</i> - not its index condition - tests
    /// <c>tenant_id</c> or <c>mt_deleted</c>, and discarded most of what the index found (P10's threshold): the index could not
    /// narrow by the predicate Marten adds to every query.
    /// </summary>
    internal static IEnumerable<PlanInsightFinding> FilteredAfterIndexes(QueryPlan plan, PlanFacts? facts, IReadOnlyList<MartenPlanTable>? tables)
    {
        if (!plan.Analyzed)
        {
            return [];
        }

        List<(PlanNode Node, MartenPlanTable Table, bool Tenant, bool Deleted, double Removed, double Kept)> found = [];

        foreach (PlanNode node in plan.Nodes)
        {
            if (node.NodeType is not ("Index Scan" or "Index Only Scan" or "Bitmap Heap Scan")
                || node is not { RowsRemovedByFilter: not null, ActualRows: not null, ActualLoops: > 0, Filter: { Length: > 0 } filter }
                || TableOf(node, facts, tables) is not { } table)
            {
                continue;
            }

            double removed = node.RowsRemovedByFilter!.Value * node.ActualLoops!.Value;
            double kept = node.ActualRows!.Value * node.ActualLoops!.Value;

            if (removed < PlanInsights.DiscardedMinimum || removed < kept * PlanInsights.DiscardedRatio)
            {
                continue;
            }

            string bare = QuotedText().Replace(filter, "''");
            bool tenant = table.Info.TenancyStyle == JasperFx.MultiTenancy.TenancyStyle.Conjoined && TenantIdWord().IsMatch(bare);
            bool deleted = table.Info.SoftDeleteEnabled && DeletedWord().IsMatch(bare);

            if (tenant || deleted)
            {
                found.Add((node, table, tenant, deleted, removed, kept));
            }
        }

        return found
            .OrderByDescending(static x => x.Removed - x.Kept)
            .ThenBy(static x => x.Node.Id)
            .Take(PlanInsights.MaxPerInsight)
            .Select(x =>
            {
                string what = x.Tenant && x.Deleted ? "tenant_id and mt_deleted" : x.Tenant ? "tenant_id" : "mt_deleted";
                List<string> lines = [];

                if (x.Tenant)
                {
                    lines.Add(TenantLine(x.Table, x.Node));
                }

                if (x.Deleted)
                {
                    lines.Add(DeletedLine(x.Table, x.Node));
                }

                return Finding(
                    FilteredAfterIndex,
                    x.Node,
                    x.Node.Description + " returned " + Rows(x.Kept) + " rows and threw away " + Rows(x.Removed) + " more with its filter, which tests "
                    + what + " - the " + (x.Tenant && x.Deleted ? "predicates" : "predicate") + " Marten adds to every query on the " + x.Table.TypeName
                    + " documents. The index found rows the filter then rejected (the filter's other tests, if it has any, are in that count too).",
                    DeclareNote + " " + string.Join(" ", lines));
            });
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The remedies. Every API named here exists at Marten 9.39.0 and 9.56.0 (MartenRegistry's DocumentMappingExpression<T>:
    // Duplicate, Index, GinIndexJsonData, IndexTenantId, StartIndexesByTenantId; Weasel's IndexDefinition.Predicate).
    // ---------------------------------------------------------------------------------------------------------------

    private const string DeclareNote =
        "Declare it in StoreOptions rather than creating it by hand: Marten's next schema apply (AutoCreate.CreateOrUpdate is not additive) "
        + "drops an index its configuration does not declare.";

    private static string RemedyFor(MartenPlanTable table, IReadOnlyList<Unserved> unserved)
    {
        List<string> lines = [];

        foreach (Unserved one in unserved.Take(3))
        {
            lines.Add(one.Use.Kind switch
            {
                JsonUseKind.Containment =>
                    "For the containment test, a GIN index on data serves @>: " + Line(table, "GinIndexJsonData()") + ".",
                JsonUseKind.KeyExists =>
                    "A key-existence test (?, ?|, ?&) is not served by the GIN index Marten declares, GinIndexJsonData(), whose "
                    + "jsonb_path_ops operator class serves @> only; duplicate the member and compare its column, or test for it by containment.",
                _ => MemberLine(table, one),
            });
        }

        return DeclareNote + " " + string.Join(" ", lines);
    }

    private static string MemberLine(MartenPlanTable table, Unserved one)
    {
        string path = string.Join('.', one.Use.Path);

        if (one.Member is not { } member)
        {
            return "The JSON path " + path + " is not exactly one member of " + table.TypeName
                + " under the store's serializer settings, so the studio cannot write the line for you: duplicate or index the member that is written as "
                + path + ".";
        }

        string call = one.Use.Operator == JsonOperator.Comparison ? "Duplicate(x => x." + member + ")" : "Index(x => x." + member + ")";

        return (one.Use.Operator == JsonOperator.Comparison
            ? "Duplicating " + member + " copies it into a column of its own, which a comparison on it can use: "
            : "A computed index on " + member + " serves this test: ") + Line(table, call) + ".";
    }

    private static string TenantLine(MartenPlanTable table, PlanNode node)
    {
        bool computed = node.IndexName is { } name && table.Indexes.Any(x => x.Computed && string.Equals(x.Name, name, StringComparison.Ordinal));

        return computed
            ? "To lead the type's computed indexes with tenant_id, so the index narrows by tenant: " + Line(table, "StartIndexesByTenantId()") + "."
            : "To index tenant_id itself: " + Line(table, "IndexTenantId()") + ".";
    }

    private static string DeletedLine(MartenPlanTable table, PlanNode node)
    {
        string? member = null;

        if (node.IndexName is { } name && table.Indexes.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal)) is { Computed: true } index)
        {
            JsonUse? first = JsonUses.Read(index.Definition).FirstOrDefault(static x => x.Kind == JsonUseKind.Member);
            member = first is null ? null : table.MemberOf(first.Path);
        }

        return member is null
            ? "To keep deleted documents out of the index, declare it partial: Index(x => x.<the member it is on>, i => i.Predicate = \"mt_deleted = false\") "
              + "on options.Schema.For<" + table.TypeName + ">()."
            : "To keep deleted documents out of the index, declare it partial: "
              + Line(table, "Index(x => x." + member + ", i => i.Predicate = \"mt_deleted = false\")") + ".";
    }

    private static string Line(MartenPlanTable table, string call) =>
        "options.Schema.For<" + table.TypeName + ">()." + call + ";";

    // ---------------------------------------------------------------------------------------------------------------
    // Judging.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>One JSON use of a filter that no declared index serves, and the member it maps to.</summary>
    internal sealed record Unserved(JsonUse Use, string? Member);

    internal static List<Unserved> Unserve(MartenPlanTable table, IReadOnlyList<JsonUse> uses)
    {
        List<Unserved> unserved = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        IReadOnlyList<PostgresIndex> indexes = table.AdvisorIndexes;

        foreach (JsonUse use in uses)
        {
            if (!seen.Add(use.Kind + "\u001f" + string.Join('\u001e', use.Path)))
            {
                continue;
            }

            bool red = use.Kind switch
            {
                JsonUseKind.Containment => IndexAdvisor.Evaluate(table.Info, indexes, new DocumentPredicate.Contains("{}")).Level == IndexVerdictLevel.Red,
                JsonUseKind.KeyExists => !indexes.Any(static x => x.Definition.Contains("using gin", StringComparison.OrdinalIgnoreCase)
                    && x.Definition.Contains("(data", StringComparison.OrdinalIgnoreCase)
                    && !x.Definition.Contains("jsonb_path_ops", StringComparison.OrdinalIgnoreCase)),
                _ => IndexAdvisor.Evaluate(
                        table.Info,
                        indexes,
                        use.Operator == JsonOperator.Pattern
                            ? new DocumentPredicate.FieldLike(use.Path, string.Empty)
                            : new DocumentPredicate.FieldCompare(use.Path, ComparisonOperator.Equal, new SearchValue.Text(string.Empty))).Level
                    == IndexVerdictLevel.Red,
            };

            if (red)
            {
                unserved.Add(new Unserved(use, use.Kind == JsonUseKind.Member ? table.MemberOf(use.Path) : null));
            }
        }

        return unserved;
    }

    /// <summary>
    /// The declared, visible document table <paramref name="node" /> scans - by the schema and name the plan printed - or
    /// <see langword="null" />: not a scan, no schema, not a declared type's table, or a relation the facts say is withheld.
    /// </summary>
    internal static MartenPlanTable? TableOf(PlanNode node, PlanFacts? facts, IReadOnlyList<MartenPlanTable>? tables)
    {
        if (tables is null || node.Schema is not { Length: > 0 } schema || node.RelationName is not { Length: > 0 } name
            || !name.StartsWith(DocumentTablePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        if (facts?.Find(schema, name) is { State: PlanRelationState.Withheld })
        {
            return null;
        }

        return tables.FirstOrDefault(x => string.Equals(x.Schema, schema, StringComparison.Ordinal) && string.Equals(x.Table, name, StringComparison.Ordinal));
    }

    private static bool FactsHaveGin(PlanFacts? facts, PlanNode node) =>
        facts?.Find(node.Schema, node.RelationName) is { State: PlanRelationState.Read } relation
        && relation.Indexes.Any(static x => string.Equals(x.AccessMethod, "gin", StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.LeadingColumn, "data", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a scan of a document table is one to judge, and how much it read and threw away: a sequential scan, or an index scan
    /// that - under <c>ANALYZE</c> - discarded P10's share and number of rows.
    /// </summary>
    private static bool ReadsMuch(QueryPlan plan, PlanNode node, out double weight)
    {
        weight = 0;

        if (node.NodeType == "Seq Scan")
        {
            weight = node.RowsRemovedByFilter is { } removed ? removed * (node.ActualLoops ?? 1) : node.PlanRows ?? 0;
            return true;
        }

        if (plan.Analyzed
            && node.NodeType is "Index Scan" or "Index Only Scan" or "Bitmap Heap Scan"
            && node is { RowsRemovedByFilter: { } filtered, ActualRows: { } kept, ActualLoops: > 0 })
        {
            double removed = filtered * node.ActualLoops!.Value;
            double returned = kept * node.ActualLoops!.Value;
            weight = removed;
            return removed >= PlanInsights.DiscardedMinimum && removed >= returned * PlanInsights.DiscardedRatio;
        }

        return false;
    }

    private static string Read(QueryPlan plan, PlanNode node)
    {
        if (!plan.Analyzed || node is not { ActualRows: { } kept, ActualLoops: > 0 })
        {
            return string.Empty;
        }

        double returned = kept * node.ActualLoops!.Value;
        double removed = (node.RowsRemovedByFilter ?? 0) * node.ActualLoops!.Value;

        return " (it read " + Rows(returned + removed) + " rows to keep " + Rows(returned) + ")";
    }

    private static string Subjects(IReadOnlyList<Unserved> unserved)
    {
        string[] names =
        [
            .. unserved.Take(3).Select(static x => x.Use.Kind switch
            {
                JsonUseKind.Containment => "a containment test (@>)",
                JsonUseKind.KeyExists => "a key-existence test (?)",
                _ => x.Member ?? string.Join('.', x.Use.Path),
            }),
        ];

        return string.Join(", ", names) + (unserved.Count > 3 ? " and " + (unserved.Count - 3).ToString(CultureInfo.InvariantCulture) + " more" : string.Empty);
    }

    private static string Rows(double rows) => Math.Round(rows).ToString("N0", CultureInfo.InvariantCulture);

    private static PlanInsightFinding Finding(PlanInsightInfo insight, PlanNode node, string message, string remedy) =>
        new(insight, node.Id, node.Description, message, remedy);

    /// <summary>The column <c>tenant_id</c>, as a word.</summary>
    [GeneratedRegex("(?<![A-Za-z0-9_])tenant_id(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TenantIdWord();

    /// <summary>The column <c>mt_deleted</c>, as a word.</summary>
    [GeneratedRegex("(?<![A-Za-z0-9_])mt_deleted(?![A-Za-z0-9_])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DeletedWord();

    /// <summary>A string literal.</summary>
    [GeneratedRegex("'(?:[^']|'')*'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedText();
}

/// <summary>What a filter does with the JSON of a document.</summary>
internal enum JsonUseKind
{
    /// <summary>It reads a member: <c>data -&gt;&gt; 'Color'</c>, <c>data -&gt; 'A' -&gt;&gt; 'B'</c> or <c>data #&gt;&gt; '{A,B}'</c>.</summary>
    Member,

    /// <summary>It tests containment: <c>data @&gt; …</c>.</summary>
    Containment,

    /// <summary>It tests that a key exists: <c>data ? 'Color'</c>.</summary>
    KeyExists,
}

/// <summary>What a filter does with the member it reads.</summary>
internal enum JsonOperator
{
    /// <summary>An equality or a range: <c>=</c>, <c>&lt;</c>, <c>&gt;=</c>, <c>= ANY</c>.</summary>
    Comparison,

    /// <summary>A pattern match: <c>LIKE</c>, <c>ILIKE</c>, a regular expression.</summary>
    Pattern,

    /// <summary>Anything else.</summary>
    Other,
}

/// <summary>One use of a document's JSON in a filter.</summary>
/// <param name="Kind">What it does.</param>
/// <param name="Path">The JSON keys of a <see cref="JsonUseKind.Member" />, outermost first; empty otherwise.</param>
/// <param name="Operator">What it does with the member.</param>
internal sealed record JsonUse(JsonUseKind Kind, IReadOnlyList<string> Path, JsonOperator Operator);

/// <summary>
/// Reads the uses of <c>data</c> out of a plan's deparsed condition - a bounded scan with timeouts, returning the JSON keys only. A key
/// that is a placeholder (<c>data -&gt;&gt; $1</c>) names nothing and is left out, as the Statements advisor leaves it.
/// </summary>
internal static partial class JsonUses
{
    /// <summary>The most uses read from one condition.</summary>
    private const int MaxUses = 32;

    /// <summary>The uses in <paramref name="condition" />, in order of appearance.</summary>
    public static IReadOnlyList<JsonUse> Read(string condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        // Postgres parenthesises every operator it deparses; none of the shapes below needs them.
        string flat = condition.Replace('(', ' ').Replace(')', ' ');
        List<(int Index, JsonUse Use)> uses = [];

        foreach (Match match in Chain().Matches(flat))
        {
            List<string> path = [];
            bool placeholder = false;

            foreach (Match step in Step().Matches(match.Groups["chain"].Value))
            {
                string key = step.Groups["key"].Value;

                if (key.StartsWith('$'))
                {
                    placeholder = true;
                    break;
                }

                path.Add(key[1..^1].Replace("''", "'", StringComparison.Ordinal));
            }

            if (!placeholder && path.Count > 0)
            {
                uses.Add((match.Index, new JsonUse(JsonUseKind.Member, path, OperatorAfter(flat[(match.Index + match.Length)..]))));
            }
        }

        foreach (Match match in PathOperator().Matches(flat))
        {
            if (!match.Groups["path"].Success)
            {
                continue;
            }

            string[] path = match.Groups["path"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (path.Length > 0)
            {
                uses.Add((match.Index, new JsonUse(JsonUseKind.Member, path, OperatorAfter(flat[(match.Index + match.Length)..]))));
            }
        }

        foreach (Match match in Containment().Matches(flat))
        {
            uses.Add((match.Index, new JsonUse(JsonUseKind.Containment, [], JsonOperator.Other)));
        }

        foreach (Match match in KeyExists().Matches(flat))
        {
            uses.Add((match.Index, new JsonUse(JsonUseKind.KeyExists, [], JsonOperator.Other)));
        }

        return [.. uses.OrderBy(static x => x.Index).Select(static x => x.Use).Take(MaxUses)];
    }

    private static JsonOperator OperatorAfter(string tail)
    {
        Match match = Following().Match(tail);

        if (!match.Success)
        {
            return JsonOperator.Other;
        }

        return match.Groups["pattern"].Success ? JsonOperator.Pattern : match.Groups["compare"].Success ? JsonOperator.Comparison : JsonOperator.Other;
    }

    /// <summary><c>data -&gt; 'A' -&gt;&gt; 'B'</c>, a key a literal or a placeholder, optionally qualified by an alias.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data(?<chain>(?:\s*->>?\s*(?:'(?:[^']|'')*'|\$[0-9]+)(?:\s*::\s*[a-z]+)?)+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Chain();

    /// <summary>One step of a <see cref="Chain" />.</summary>
    [GeneratedRegex(@"->>?\s*(?<key>'(?:[^']|'')*'|\$[0-9]+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Step();

    /// <summary><c>data #&gt;&gt; '{A,B}'</c>.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data\s*#>>?\s*(?:'\{(?<path>[^}']*)\}'|\$[0-9]+)(?:\s*::\s*[a-z]+)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PathOperator();

    /// <summary><c>data @&gt;</c>.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data\s*@>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Containment();

    /// <summary><c>data ?</c>, <c>data ?|</c> and <c>data ?&amp;</c>.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_$])(?:[A-Za-z_][A-Za-z0-9_]*\.)?data\s*\?[|&]?\s*(?=[A-Za-z'$])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyExists();

    /// <summary>What follows a member: casts, then the operator that tests it.</summary>
    [GeneratedRegex(
        @"^\s*(?:::\s*[A-Za-z_][A-Za-z0-9_]*(?:\s+varying)?\s*)*(?:(?<pattern>!?~~\*?|!?~\*?)|(?<compare>=|<>|!=|<=|>=|<|>))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Following();
}
