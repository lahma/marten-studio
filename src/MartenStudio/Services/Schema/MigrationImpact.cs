using System.Globalization;
using System.Text.RegularExpressions;

using MartenStudio.Internal.Sql;

using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Schema;

/// <summary>A table's estimated rows and size, from the catalog's statistics.</summary>
/// <param name="EstimatedRows"><c>reltuples</c>, summed over the leaf partitions; <c>-1</c> before the first analyze.</param>
/// <param name="HeapBytes">The table's pages times the block size.</param>
/// <param name="TotalBytes">Heap, TOAST and indexes, from page counts times the block size.</param>
/// <param name="IsPartitioned">Whether the figures include partitions.</param>
internal sealed record TableFootprint(long EstimatedRows, long HeapBytes, long TotalBytes, bool IsPartitioned)
{
    /// <summary>
    /// Whether the statistics say anything about the row count: <c>reltuples</c> is <c>-1</c> before the first analyze, and below
    /// PostgreSQL 14 it is <c>0</c> there - a table with pages and no tuples has not been analyzed.
    /// </summary>
    public bool RowsKnown => EstimatedRows > 0 || (EstimatedRows == 0 && HeapBytes == 0);

    /// <summary>The row count as the Schema screen prints one: <c>~</c> and a number, or that it was never analyzed.</summary>
    public string RowsText => RowsKnown ? SchemaFormat.Estimate(EstimatedRows) + " rows" : "rows unknown (not analyzed yet)";

    /// <summary>The size as the Schema screen prints one.</summary>
    public string SizeText =>
        SchemaFormat.EstimatedBytes(TotalBytes) + " with its indexes and TOAST (" + SchemaFormat.EstimatedBytes(HeapBytes) + " of heap)";
}

/// <summary>How many sessions hold, or wait for, a lock on a table in one state.</summary>
/// <param name="State"><c>pg_stat_activity.state</c>, or <c>prepared transaction</c> or <c>state not visible</c>.</param>
/// <param name="Granted">Whether the lock is held rather than waited for.</param>
/// <param name="Sessions">How many sessions.</param>
internal sealed record SessionStateCount(string State, bool Granted, int Sessions);

/// <summary>
/// The sessions that have a lock on a table now: counts and states, and nothing that says who or what.
/// </summary>
/// <param name="Counts">One entry per state and whether the lock is held.</param>
internal sealed record TableSessions(IReadOnlyList<SessionStateCount> Counts)
{
    /// <summary>How many sessions hold a lock on the table.</summary>
    public int Holding => Counts.Where(static x => x.Granted).Sum(static x => x.Sessions);

    /// <summary>How many sessions wait for one.</summary>
    public int Waiting => Counts.Where(static x => !x.Granted).Sum(static x => x.Sessions);

    /// <summary>The counts in a sentence: <c>1 session holds a lock (idle in transaction)</c>, or that there is none.</summary>
    public string Summary
    {
        get
        {
            List<string> parts = [];

            AddPart(parts, Counts.Where(static x => x.Granted).ToList(), "hold a lock", "holds a lock");
            AddPart(parts, Counts.Where(static x => !x.Granted).ToList(), "wait for one", "waits for one");

            return parts.Count == 0 ? "no sessions hold or wait for a lock on it now" : string.Join("; ", parts);
        }
    }

    private static void AddPart(List<string> parts, List<SessionStateCount> counts, string plural, string singular)
    {
        int total = counts.Sum(static x => x.Sessions);

        if (total == 0)
        {
            return;
        }

        string states = string.Join(", ", counts.Select(static x => x.Sessions.ToString(CultureInfo.InvariantCulture) + " " + x.State));

        parts.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"{total} {(total == 1 ? "session" : "sessions")} {(total == 1 ? singular : plural)} ({states})"));
    }
}

/// <summary>One table the migration touches, what it does to it, and what the catalog says about it.</summary>
/// <param name="Display">The table as <c>schema.name</c>.</param>
/// <param name="Statements">The statements that lock it, in script order.</param>
/// <param name="Footprint">Its estimated rows and size, or <see langword="null" /> when the catalog has no such table.</param>
/// <param name="Sessions">The sessions locking it now, or <see langword="null" /> when they are not shown to this visitor.</param>
internal sealed record TableImpact(
    string Display,
    IReadOnlyList<TableLockStatement> Statements,
    TableFootprint? Footprint,
    TableSessions? Sessions)
{
    /// <summary>
    /// Whether this is a function dropped with <c>CASCADE</c> rather than a table: it has no rows, size or sessions of its own.
    /// </summary>
    public bool IsFunction => Statements.All(static x => x.IsFunction);

    /// <summary>
    /// For a function, which tables lose what: the tables whose declared indexes use it where the configuration says so, otherwise a
    /// sentence that every table with an index or default built on it does.
    /// </summary>
    public string? Note { get; init; }

    /// <summary>The strongest lock any of its statements takes.</summary>
    public TableLockMode StrongestLock => Statements.Max(static x => x.Lock);

    /// <summary>Whether any of its statements rewrites it (or might).</summary>
    public TableRewrite Rewrite => Statements.Max(static x => x.Rewrite);
}

/// <summary>
/// What a previewed migration will lock and rewrite, table by table, with the table's estimated rows and size and - for a visitor
/// who passes the Activity gate - the sessions on it now. Notes, never a refusal.
/// </summary>
/// <param name="Tables">The tables, in the order the script first touches them.</param>
/// <param name="Advice">What to do about it.</param>
internal sealed record MigrationImpact(IReadOnlyList<TableImpact> Tables, IReadOnlyList<string> Advice)
{
    /// <summary>Nothing to say: the script locks no table that already exists.</summary>
    public static MigrationImpact None { get; } = new([], []);

    /// <summary>Why the rows and sizes are missing, when the catalog read failed; otherwise <see langword="null" />.</summary>
    public string? Notice { get; init; }

    /// <summary>
    /// Why the sessions are not shown - the visitor does not pass the Activity gate, or the read failed - or
    /// <see langword="null" /> when they are.
    /// </summary>
    public string? SessionsNotice { get; init; }

    /// <summary>Whether there is anything to draw.</summary>
    public bool HasContent => Tables.Count > 0;

    /// <summary>
    /// One line over the per-table list (1.2-UX-FIX), built from the same notes: how many tables the migration takes
    /// <c>ACCESS EXCLUSIVE</c> on, how big they are by the catalog's estimates, and how many of them it rewrites - or
    /// <see langword="null" /> when no table is locked that hard. A line of notes, like everything here: the apply, its confirmation and
    /// its authorization do not read it.
    /// </summary>
    public string? LockSummary
    {
        get
        {
            TableImpact[] exclusive = [.. Tables.Where(static x => !x.IsFunction && x.StrongestLock == TableLockMode.AccessExclusive)];

            if (exclusive.Length == 0)
            {
                return null;
            }

            CultureInfo culture = CultureInfo.InvariantCulture;
            TableFootprint[] known = [.. exclusive.Select(static x => x.Footprint).OfType<TableFootprint>().Where(static x => x.RowsKnown)];
            long rows = known.Sum(static x => x.EstimatedRows);
            long bytes = exclusive.Select(static x => x.Footprint).OfType<TableFootprint>().Sum(static x => x.TotalBytes);
            int rewritten = exclusive.Count(static x => x.Rewrite == TableRewrite.Yes);
            int unsure = exclusive.Count(static x => x.Rewrite is TableRewrite.Maybe or TableRewrite.Unknown);

            string size = known.Length == 0 && bytes == 0
                ? "sizes not in the catalog yet"
                : $"about {rows.ToString("N0", culture)} rows, {SchemaFormat.Bytes(bytes)} with indexes and TOAST";

            string rewrites = (rewritten, unsure) switch
            {
                (0, 0) => "rewrites none",
                (_, 0) => $"rewrites {rewritten.ToString(culture)}",
                (0, _) => $"may rewrite {unsure.ToString(culture)}",
                _ => $"rewrites {rewritten.ToString(culture)}, may rewrite {unsure.ToString(culture)}",
            };

            return $"Takes ACCESS EXCLUSIVE on {exclusive.Length.ToString(culture)} {(exclusive.Length == 1 ? "table" : "tables")} ({size}); {rewrites}.";
        }
    }

    /// <summary>
    /// The distinct qualified relations the statements name, in order of first appearance. A statement that names its relation
    /// without a schema is not looked up: Weasel qualifies every name, and an unqualified one would resolve through a search
    /// path this read does not know.
    /// </summary>
    public static IReadOnlyList<(string Schema, string Name)> RelationsOf(IReadOnlyList<TableLockStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);

        List<(string Schema, string Name)> relations = [];

        foreach (TableLockStatement statement in statements)
        {
            if (!statement.IsFunction && statement.Schema is { } schema && !relations.Contains((schema, statement.Name)))
            {
                relations.Add((schema, statement.Name));
            }
        }

        return relations;
    }

    /// <summary>Joins the statements to what the catalog said about the relations they name.</summary>
    /// <param name="statements">The script's lock statements.</param>
    /// <param name="footprints">The catalog read, asked about <see cref="RelationsOf" /> of the same statements.</param>
    /// <param name="sessions">The sessions read, or <see langword="null" /> when it was not made.</param>
    /// <param name="declaredIndexes">
    /// The indexes the configuration declares, to name the tables a function dropped with <c>CASCADE</c> takes an index from; or
    /// <see langword="null" /> when they could not be read.
    /// </param>
    public static MigrationImpact Assemble(
        IReadOnlyList<TableLockStatement> statements,
        IReadOnlyList<RelationFootprintRow> footprints,
        IReadOnlyList<TableSessionRow>? sessions,
        IReadOnlyList<PostgresStudio.Services.Schema.DeclaredIndex>? declaredIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(footprints);

        if (statements.Count == 0)
        {
            return None;
        }

        IReadOnlyList<(string Schema, string Name)> asked = RelationsOf(statements);
        Dictionary<(string, string), RelationFootprintRow> byRelation = [];

        foreach (RelationFootprintRow row in footprints)
        {
            if (row.Ordinal >= 0 && row.Ordinal < asked.Count)
            {
                byRelation[asked[row.Ordinal]] = row;
            }
        }

        // Grouped by the table a statement is about - an index's by its table's, which only the catalog can say.
        List<(string Key, RelationFootprintRow? Row, string Display, List<TableLockStatement> Statements)> groups = [];

        foreach (TableLockStatement statement in statements)
        {
            RelationFootprintRow? row = statement.Schema is { } schema && byRelation.TryGetValue((schema, statement.Name), out RelationFootprintRow? found)
                ? found
                : null;

            string key = row is null ? "\u001F" + (statement.IsFunction ? "function " : string.Empty) + statement.Display : row.Schema + "\u001F" + row.Table;
            string display = row is null ? (statement.IsFunction ? "function " : string.Empty) + statement.Display : row.Schema + "." + row.Table;

            int at = groups.FindIndex(x => string.Equals(x.Key, key, StringComparison.Ordinal));
            if (at < 0)
            {
                groups.Add((key, row, display, [statement]));
            }
            else
            {
                groups[at].Statements.Add(statement);
            }
        }

        List<TableImpact> tables = [];

        foreach ((string _, RelationFootprintRow? row, string display, List<TableLockStatement> grouped) in groups)
        {
            TableFootprint? footprint = row is null
                ? null
                : new TableFootprint(row.EstimatedRows, row.HeapBytes, row.TotalBytes, row.IsPartitioned);

            TableSessions? onTable = sessions is null || row is null
                ? null
                : new TableSessions([.. sessions.Where(x => x.TableOid == row.TableOid).Select(static x => new SessionStateCount(x.State, x.Granted, x.Sessions))]);

            tables.Add(new TableImpact(display, grouped, footprint, onTable)
            {
                Note = grouped.All(static x => x.IsFunction) ? FunctionNote(grouped[0].Name, declaredIndexes) : null,
            });
        }

        return new MigrationImpact(tables, MigrationRisk.Advice(statements));
    }

    /// <summary>What a function dropped with <c>CASCADE</c> takes with it, and where the configuration says so, from which tables.</summary>
    internal static string FunctionNote(string function, IReadOnlyList<PostgresStudio.Services.Schema.DeclaredIndex>? declaredIndexes)
    {
        const string General = "Tables whose indexes or defaults use it.";

        if (declaredIndexes is null)
        {
            return General;
        }

        Regex uses = new(@"\b" + Regex.Escape(function) + @"\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        string[] names =
        [
            .. declaredIndexes
                .Where(x => uses.IsMatch(x.Definition))
                .Select(static x => x.Schema + "." + x.Table + " (" + x.Name + ")")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static x => x, StringComparer.Ordinal),
        ];

        return names.Length == 0
            ? General
            : "Declared indexes that use it: " + string.Join(", ", names) + ". " + General.Replace("Tables", "Any other table", StringComparison.Ordinal);
    }
}
