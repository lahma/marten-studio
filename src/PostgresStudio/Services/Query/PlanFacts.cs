using System.Globalization;

namespace PostgresStudio.Services.Query;

/// <summary>What became of one relation a plan names, when its facts were read (0.10-A, issue #28).</summary>
internal enum PlanRelationState
{
    /// <summary>The relation was found, the visitor may see its structure, and its facts were read.</summary>
    Read,

    /// <summary>The relation's schema is not one this visitor may see the structure of (D27), or it is a hidden document type: no facts, shown as <c>‹withheld›</c>.</summary>
    Withheld,

    /// <summary>The plan named no schema (it was run without <c>VERBOSE</c>), or the catalog has no such relation.</summary>
    NotResolved,

    /// <summary>The plan names more relations than a facts read takes (<see cref="PlanFactsTargets.MaxRelations" />), or the read failed.</summary>
    NotRead,
}

/// <summary>Whether a relation's <c>most_common_vals</c> and <c>histogram_bounds</c> - data - were read, and why not.</summary>
internal enum PlanValuesState
{
    /// <summary>Not read: nothing in the plan filters, joins or sorts on a column of the relation.</summary>
    NotNeeded,

    /// <summary>Not read: the values are <c>BrowseDatabase</c>'s, and this visitor or this studio does not have it.</summary>
    NeedsBrowse,

    /// <summary>Not read: the row gate refused the relation (a Marten table, a schema <c>BrowsableSchemas</c> does not admit, a role without <c>SELECT</c>).</summary>
    Refused,

    /// <summary>Read, and audited as a <c>BrowseDatabase</c> read.</summary>
    Read,
}

/// <summary>A relation's index, with what the Schema and database screens show of one.</summary>
/// <param name="Name">The index.</param>
/// <param name="KeyColumns">The key columns in order; a <see langword="null" /> entry is an expression.</param>
/// <param name="IsPrimary">Whether it backs the primary key.</param>
/// <param name="IsUnique">Whether it is unique.</param>
/// <param name="IsValid">Whether it is valid.</param>
/// <param name="IsPartial">Whether it has a predicate.</param>
/// <param name="HasExpressions">Whether any key is an expression.</param>
/// <param name="AccessMethod">Its access method.</param>
/// <param name="SizeBytes">Its size as of the last <c>VACUUM</c>, <c>ANALYZE</c> or build: <c>relpages</c> times the block size.</param>
/// <param name="Scans">Index scans since the counters began.</param>
/// <param name="LastScan">The last scan, from PostgreSQL 16.</param>
internal sealed record PlanIndexFact(
    string Name,
    IReadOnlyList<string?> KeyColumns,
    bool IsPrimary,
    bool IsUnique,
    bool IsValid,
    bool IsPartial,
    bool HasExpressions,
    string AccessMethod,
    long? SizeBytes,
    long? Scans,
    DateTimeOffset? LastScan)
{
    /// <summary>The first key column, or <see langword="null" /> when it is an expression (or there is none).</summary>
    public string? LeadingColumn => KeyColumns.Count > 0 ? KeyColumns[0] : null;
}

/// <summary>An extended statistics object (<c>CREATE STATISTICS</c>) on the relation.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Columns">The columns it covers.</param>
/// <param name="Kinds">Its kinds: <c>d</c> (ndistinct), <c>f</c> (dependencies), <c>m</c> (mcv), <c>e</c> (expressions).</param>
internal sealed record PlanExtendedStatistic(string Name, IReadOnlyList<string> Columns, IReadOnlyList<string> Kinds)
{
    /// <summary>
    /// The schema it lives in (an extended statistics object has one of its own, which need not be its table's), or
    /// <see cref="PlanFacts.Withheld" /> when that schema is not one this visitor may see - then <see cref="Name" /> is withheld too.
    /// </summary>
    public string Schema { get; init; } = string.Empty;
}

/// <summary>The values <c>pg_stats</c> keeps for a column: data, read only through the row gate.</summary>
/// <param name="MostCommonValues">The most common values, as text, bounded.</param>
/// <param name="MostCommonFrequencies">Their frequencies.</param>
/// <param name="HistogramBounds">The histogram's bounds, as text, bounded.</param>
internal sealed record PlanColumnValues(string? MostCommonValues, string? MostCommonFrequencies, string? HistogramBounds);

/// <summary>What <c>pg_stats</c> says of one column a plan filters, joins or sorts on.</summary>
/// <param name="Column">The column.</param>
/// <param name="Type">Its type, as <c>format_type</c> writes it.</param>
/// <param name="HasStatistics">Whether <c>ANALYZE</c> has recorded anything for it.</param>
/// <param name="NullFraction">The share of rows that are null.</param>
/// <param name="Distinct">
/// <c>n_distinct</c> as Postgres keeps it: a positive count, or a negative share of the rows (<c>-1</c> is "every row differs").
/// </param>
/// <param name="Correlation">How closely the column's order follows the table's physical order, -1 to 1.</param>
internal sealed record PlanColumnFact(string Column, string? Type, bool HasStatistics, double? NullFraction, double? Distinct, double? Correlation)
{
    /// <summary>The values, when the row gate let them be read.</summary>
    public PlanColumnValues? Values { get; init; }
}

/// <summary>The partition a relation is, and the table it belongs to.</summary>
/// <param name="ParentSchema">The partitioned table's schema.</param>
/// <param name="ParentName">The partitioned table.</param>
/// <param name="Partitions">How many partitions it has directly.</param>
/// <param name="KeyColumns">Its partition key's columns; <c>(expression)</c> stands for one that is not a column.</param>
internal sealed record PlanPartitionFact(string ParentSchema, string ParentName, int Partitions, IReadOnlyList<string> KeyColumns);

/// <summary>Everything the facts read found out about one relation a plan names.</summary>
/// <param name="Schema">The schema the plan named.</param>
/// <param name="Name">The relation the plan named.</param>
/// <param name="State">What became of it.</param>
/// <remarks>
/// A <see cref="PlanRelationState.Withheld" /> record holds neither name - both are empty - so nothing that serializes, logs or
/// prints it can tell a visitor what they may not see. <see cref="PlanFacts.Find" /> still finds it by the names the plan printed,
/// through the index the read keeps beside the list.
/// </remarks>
internal sealed record PlanRelationFacts(string Schema, string Name, PlanRelationState State)
{
    /// <summary>The relation as a person reads it: <c>public.orders</c>, or <c>‹withheld›</c> for one this visitor may not see.</summary>
    public string Label => State == PlanRelationState.Withheld ? PlanFacts.Withheld : Schema + "." + Name;

    /// <summary>Why there are no facts, for <see cref="PlanRelationState.NotResolved" /> and <see cref="PlanRelationState.NotRead" />.</summary>
    public string? Note { get; init; }

    /// <summary><c>relkind</c>: <c>r</c>, <c>p</c>, <c>m</c>...</summary>
    public string? Kind { get; init; }

    /// <summary><c>reltuples</c>: the planner's row estimate, or <see langword="null" /> where PostgreSQL 14 and later say <c>-1</c> (never analyzed).</summary>
    public double? EstimatedRows { get; init; }

    /// <summary><c>relpages</c>.</summary>
    public long? Pages { get; init; }

    /// <summary><c>relallvisible</c>.</summary>
    public long? AllVisiblePages { get; init; }

    /// <summary>When it was last analyzed by hand.</summary>
    public DateTimeOffset? LastAnalyze { get; init; }

    /// <summary>When autovacuum last analyzed it.</summary>
    public DateTimeOffset? LastAutoAnalyze { get; init; }

    /// <summary>When it was last vacuumed by hand.</summary>
    public DateTimeOffset? LastVacuum { get; init; }

    /// <summary>When autovacuum last vacuumed it.</summary>
    public DateTimeOffset? LastAutoVacuum { get; init; }

    /// <summary>Rows inserted, updated or deleted since it was last analyzed.</summary>
    public long? ModifiedSinceAnalyze { get; init; }

    /// <summary>The statistics collector's dead-tuple estimate.</summary>
    public long? DeadTuples { get; init; }

    /// <summary>The statistics collector's live-tuple estimate.</summary>
    public long? LiveTuples { get; init; }

    /// <summary>Its indexes.</summary>
    public IReadOnlyList<PlanIndexFact> Indexes { get; init; } = [];

    /// <summary>Its extended statistics.</summary>
    public IReadOnlyList<PlanExtendedStatistic> ExtendedStatistics { get; init; } = [];

    /// <summary>The columns the plan filters, joins or sorts on.</summary>
    public IReadOnlyList<PlanColumnFact> Columns { get; init; } = [];

    /// <summary>The partitioned table it belongs to, when it is a partition this visitor may see the parent of.</summary>
    public PlanPartitionFact? Partition { get; init; }

    /// <summary>Whether the most common values and histograms were read.</summary>
    public PlanValuesState Values { get; init; } = PlanValuesState.NotNeeded;

    /// <summary>The row gate's sentence, for <see cref="PlanValuesState.Refused" />.</summary>
    public string? ValuesNote { get; init; }

    /// <summary>Whether neither a manual nor an automatic <c>ANALYZE</c> has ever recorded its statistics.</summary>
    public bool NeverAnalyzed => LastAnalyze is null && LastAutoAnalyze is null;

    /// <summary>The most recent <c>ANALYZE</c>, manual or automatic.</summary>
    public DateTimeOffset? AnalyzedAt =>
        LastAnalyze is { } manual && LastAutoAnalyze is { } automatic ? (manual > automatic ? manual : automatic) : LastAnalyze ?? LastAutoAnalyze;

    /// <summary>The column called <paramref name="column" />, or <see langword="null" />.</summary>
    public PlanColumnFact? FindColumn(string column) =>
        Columns.FirstOrDefault(x => string.Equals(x.Column, column, StringComparison.Ordinal));
}

/// <summary>One planner-related setting beside the plan.</summary>
/// <param name="Name">The setting.</param>
/// <param name="Value">Its value.</param>
/// <param name="Unit">Its unit, when it has one.</param>
/// <param name="Source">Who set it, when known; the plan's own <c>SETTINGS</c> list does not say.</param>
/// <param name="FromPlan">Whether the plan itself printed it (<c>EXPLAIN (SETTINGS)</c>), and so it is the value this plan was made with.</param>
internal sealed record PlanSettingFact(string Name, string Value, string? Unit, string? Source, bool FromPlan)
{
    /// <summary>The value with its unit: <c>4096 kB</c>.</summary>
    public string Text => Unit is { Length: > 0 } unit ? Value + " " + unit : Value;
}

/// <summary>
/// What the Plan page reads from the database beside a plan it ran (0.10-A, issue #28): per relation the planner's own facts, its
/// indexes, its extended statistics and the statistics of the columns the plan uses, and the settings that shaped the plan.
/// </summary>
/// <param name="ReadAt">When, by the studio's clock.</param>
/// <param name="Relations">The relations the plan names, in the plan's order.</param>
/// <param name="Settings">The planner settings: the plan's own first, then the server's.</param>
internal sealed record PlanFacts(DateTimeOffset ReadAt, IReadOnlyList<PlanRelationFacts> Relations, IReadOnlyList<PlanSettingFact> Settings)
{
    /// <summary>What a relation the visitor may not see is called.</summary>
    public const string Withheld = "‹withheld›";

    /// <summary>Why a relation's facts could not be read at all (the gate or the connection), or <see langword="null" />.</summary>
    public string? Failure { get; init; }

    /// <summary>Why the settings were not read, or <see langword="null" />.</summary>
    public string? SettingsNote { get; init; }

    /// <summary>How many relations the plan names beyond <see cref="PlanFactsTargets.MaxRelations" />, which were not read.</summary>
    public int NotRead { get; init; }

    /// <summary>How many scan nodes name a relation without its schema, which cannot be resolved.</summary>
    public int Unqualified { get; init; }

    /// <summary>The relation the plan called <paramref name="schema" />.<paramref name="name" />, or <see langword="null" />.</summary>
    public PlanRelationFacts? Find(string? schema, string? name)
    {
        if (schema is null || name is null)
        {
            return null;
        }

        // A withheld record holds no name, so the read's own index - by the names the plan printed - is how it is found.
        if (Requested.TryGetValue(new PlanRelationRef(schema, name), out int index) && index < Relations.Count)
        {
            return Relations[index];
        }

        return Relations.FirstOrDefault(x => string.Equals(x.Schema, schema, StringComparison.Ordinal) && string.Equals(x.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Each relation the plan named, by the schema and name it printed, to its place in <see cref="Relations" />. Internal, so no
    /// serializer writes the names a withheld record no longer holds.
    /// </summary>
    internal IReadOnlyDictionary<PlanRelationRef, int> Requested { get; init; } = new Dictionary<PlanRelationRef, int>();

    /// <summary>The facts of the relation a node scans, when they were read.</summary>
    public PlanRelationFacts? Of(PlanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Find(node.Schema, node.RelationName) is { State: PlanRelationState.Read } found ? found : null;
    }

    /// <summary>A count in the panel's words.</summary>
    internal static string Count(double value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
