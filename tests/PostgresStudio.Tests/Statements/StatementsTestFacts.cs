using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// What <c>pg_stat_statements</c> would have read, stated outright for the Statements page's tests (W5-Q1): a plain data
/// source's tables, statements with every counter the page shows, and histories of statement sweeps.
/// </summary>
internal static class StatementsTestFacts
{
    /// <summary>When every read here happens.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    /// <summary>PostgreSQL 17 with the extension at its own version.</summary>
    public static StatementExtension Pg17 { get; } = new(170011, "public", "1.11");

    /// <summary>The application's own table.</summary>
    public static readonly StudioTable Orders = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);

    /// <summary>A table in a schema the database browser may withhold from a visitor (D27).</summary>
    public static readonly StudioTable Salaries = new("hr", "salaries", StudioTableKind.Relational, StudioTableOwnership.Other) { GatedSchemas = ["hr"] };

    /// <summary>The judged set of both.</summary>
    public static StatementsTableSet Tables { get; } = new([Orders, Salaries]);

    /// <summary>One kept statement.</summary>
    public static ObservedStatement Statement(
        long queryId,
        string? text,
        long calls,
        double totalMs,
        IReadOnlyList<StudioTable>? tables = null,
        long rows = 0,
        long sharedHit = 0,
        long sharedRead = 0,
        long temp = 0,
        long? storage = null,
        DateTimeOffset? since = null) =>
        new(
            new StatementCounterRow
            {
                QueryId = queryId,
                Calls = calls,
                Rows = rows,
                TotalExecMs = totalMs,
                MeanExecMs = calls > 0 ? totalMs / calls : 0,
                SharedBlocksHit = sharedHit,
                SharedBlocksRead = sharedRead,
                TempBlocksWritten = temp,
                StorageBlocksRead = storage,
                StatsSince = since ?? Now.AddDays(-1),
            },
            text,
            false,
            tables ?? []);

    /// <summary>A read of these statements, whose total is <paramref name="allExecMs" /> (or theirs summed).</summary>
    public static StatementRead Read(double? allExecMs, params ObservedStatement[] statements) =>
        new(Now, Pg17, statements.Length + 10, allExecMs ?? statements.Sum(static x => x.Counters.TotalExecMs), 0, 0, Now.AddDays(-1), statements)
        {
            StorageReads = statements.Any(static x => x.Counters.StorageBlocksRead is not null),
        };

    /// <summary>The outcome of a read that read.</summary>
    public static StatementReadOutcome Outcome(StatementRead read) => new(StatementReadState.Read, read.Extension, read);

    /// <summary>One statement sweep's sample: the database's total and each statement's counters, as T02 writes them.</summary>
    public static HealthSample Sample(TimeSpan ago, double allExecMs, params (long QueryId, double Calls, double ExecMs)[] statements)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal) { [StatementWindows.AllExecSeries] = allExecMs };

        foreach ((long id, double calls, double exec) in statements)
        {
            values[ExpensiveStatementsCheck.CallsSeries + id] = calls;
            values[ExpensiveStatementsCheck.ExecSeries + id] = exec;
            values[ExpensiveStatementsCheck.RowsSeries + id] = calls;
            values[StatementReads.SharedHitSeries + id] = calls * 10;
            values[StatementReads.SharedReadSeries + id] = calls;
            values[StatementReads.TempSeries + id] = 0;
            values[StatementReads.IoReadSeries + id] = 0;
        }

        return new HealthSample(Now - ago, values) { Gates = values.Keys.ToDictionary(static x => x, static _ => HealthGate.Activity, StringComparer.Ordinal) };
    }

    /// <summary>The statement history of these samples, oldest first.</summary>
    public static HealthHistoryView History(params HealthSample[] samples) => new([.. samples.OrderBy(static x => x.At)]);

    /// <summary>The facts of <paramref name="read" /> over <paramref name="history" />.</summary>
    public static StatementFacts Facts(StatementRead read, HealthHistoryView? history = null, StatisticsWindow? statistics = null, IStatementClassifier? classifier = null) =>
        StatementFacts.Build("default", "default", Now, Outcome(read), null, history ?? HealthHistoryView.Empty, statistics, Tables, classifier ?? PostgresStatementClassifier.Instance);
}

/// <summary>A judged set of stated tables.</summary>
/// <param name="Tables">The tables.</param>
internal sealed record StatementsTableSet(IReadOnlyList<StudioTable> Tables) : IStudioTableSet
{
    /// <inheritdoc />
    public string? Coverage => null;

    /// <inheritdoc />
    public StudioTable? Find(string schema, string table) =>
        Tables.FirstOrDefault(x => string.Equals(x.Schema, schema, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Table, table, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IndexOrigin OriginOf(HealthIndexRow index, StudioTable table) => IndexOrigin.Host;
}
