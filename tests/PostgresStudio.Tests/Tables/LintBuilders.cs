using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Tables;

/// <summary>
/// Facts for W5-B1's evaluators, stated outright over plain Postgres tables - no Marten anywhere: every threshold and every
/// sentence is judged against numbers a test chose.
/// </summary>
internal static class LintBuilders
{
    /// <summary>The moment every evaluation here happens at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A host's own relational table.</summary>
    public static StudioTable Host(string table, string schema = "app") =>
        new(schema, table, StudioTableKind.Relational, StudioTableOwnership.Owned);

    /// <summary>A recognised library's table: exempt from the index-design rules only.</summary>
    public static StudioTable LibraryTable(string table, string schema = "quartz") =>
        new(schema, table, StudioTableKind.Relational, StudioTableOwnership.Library) { Library = StudioTableLibraries.LibraryOf(table) };

    /// <summary>A catalog row for <paramref name="table" /> of <paramref name="heapBytes" />, analysed and quiet.</summary>
    public static TableFacts Table(StudioTable table, long heapBytes = 0, Func<HealthTableRow, HealthTableRow>? shape = null)
    {
        HealthTableRow row = new()
        {
            Schema = table.Schema,
            Table = table.Table,
            EstimatedRows = 0,
            HeapBytes = heapBytes,
            LiveRows = 0,
            DeadRows = 0,
            Inserts = 0,
            Updates = 0,
            Deletes = 0,
            HotUpdates = 0,
            SequentialScans = 0,
            SequentialRowsRead = 0,
            IndexScans = 0,
        };

        return new TableFacts(table, shape is null ? row : shape(row));
    }

    /// <summary>A valid b-tree on <paramref name="keys" /> (attribute numbers), named <paramref name="name" />.</summary>
    public static HealthIndexRow IndexRow(StudioTable table, string name, params short[] keys) => new()
    {
        Schema = table.Schema,
        Table = table.Table,
        Name = name,
        KeyAttributes = keys,
        KeyColumnCount = keys.Length,
        Columns = [.. keys.Select(static x => "c" + x)],
        KeyWidths = [.. keys.Select(static _ => (short) 4)],
        KeyTypes = [.. keys.Select(static _ => "integer")],
        KeyAverageWidths = [.. keys.Select(static _ => -1)],
        Bytes = 8192,
        Scans = 1,
    };

    /// <summary>The index as the sweep's index read hands it to a check.</summary>
    public static IndexFacts Index(StudioTable table, HealthIndexRow row, IndexOrigin origin = IndexOrigin.Host) => new(table, row, origin);

    /// <summary>A statistics window of <paramref name="length" /> ending <see cref="Now" />.</summary>
    public static StatisticsWindow Window(TimeSpan length, bool looksReset = false, StatisticsResetReason reason = StatisticsResetReason.None, string? signal = null) => new()
    {
        MeasuredAt = Now,
        Since = Now - length,
        Basis = StatisticsBasis.ServerReset,
        Reason = looksReset && reason == StatisticsResetReason.None ? StatisticsResetReason.Crash : reason,
        LooksReset = looksReset,
        Signal = signal,
        SharedView = "pg_stat_checkpointer",
    };

    /// <summary>An evaluation in Postgres Studio's words, with its migration remedies, over <paramref name="window" />.</summary>
    public static HealthEvaluation Evaluation(StatisticsWindow? window = null, HealthHistoryView? history = null) =>
        HealthEvaluation.At(Now, history: history, text: PostgresStudioText.Instance) with
        {
            TableRemedies = PostgresHealthTableRemedies.Instance,
            Statistics = window,
        };

    /// <summary>An evaluation over thirty days of statistics, which every usage guard passes.</summary>
    public static HealthEvaluation Covered(HealthHistoryView? history = null) => Evaluation(Window(TimeSpan.FromDays(30)), history);

    /// <summary>A history of samples, oldest first, each at so long before <see cref="Now" />.</summary>
    public static HealthHistoryView History(params (TimeSpan Ago, (string Series, double Value)[] Values)[] samples) =>
        new([.. samples.Select(static x => new HealthSample(Now - x.Ago, x.Values.ToDictionary(static v => v.Series, static v => v.Value, StringComparer.Ordinal)))]);

    /// <summary>Every sentence of a finding, for a test that holds the wording as a whole.</summary>
    public static string AllText(HealthFinding finding) =>
        string.Join("\n", [finding.Title, finding.WhyItMatters, finding.Caveat ?? string.Empty, .. finding.Evidence.Select(static x => x.Label + ": " + x.Value)]);
}
