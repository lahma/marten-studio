using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>A document the statement checks' evaluator tests name: a duplicated column, a JSON member, a nested one.</summary>
public sealed class StatementDoc
{
    public Guid Id { get; set; }

    public string Color { get; set; } = string.Empty;

    public int Number { get; set; }

    public StatementInner Inner { get; set; } = new();
}

/// <summary>A nested member of <see cref="StatementDoc" />.</summary>
public sealed class StatementInner
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Facts for the statement checks (T02, T22, T23), stated outright: what <c>pg_stat_statements</c> would have read,
/// with no Postgres.
/// </summary>
internal static class StatementBuilders
{
    /// <summary>When every evaluation here happens.</summary>
    public static DateTimeOffset Now => TableCheckBuilders.Now;

    /// <summary>PostgreSQL 17 with the extension at its own version: everything, the entry's own stats_since included.</summary>
    public static StatementExtension Pg17 { get; } = new(170006, "public", "1.11");

    /// <summary>PostgreSQL 15: JIT columns, no per-entry stats_since, no generic plans.</summary>
    public static StatementExtension Pg15 { get; } = new(150010, "public", "1.10");

    /// <summary>PostgreSQL 14: no JIT columns.</summary>
    public static StatementExtension Pg14 { get; } = new(140015, "public", "1.9");

    /// <summary>The <see cref="StatementDoc" /> table, with <c>Color</c> duplicated.</summary>
    public static MartenTable Doc { get; } =
        TableCheckBuilders.Document<StatementDoc>(static o => o.Schema.For<StatementDoc>().Duplicate(x => x.Color));

    /// <summary>The event store's streams table.</summary>
    public static MartenTable Streams { get; } = TableCheckBuilders.EventTable("mt_streams");

    /// <summary>A select on <see cref="Doc" />, as pg_stat_statements records it: the member's name is a placeholder.</summary>
    public static string SelectOnDoc => "select d.id, d.data from " + Doc.Schema + "." + Doc.Table + " as d where d.data ->> $2 = $1";

    /// <summary>One statement as the shared read keeps it.</summary>
    public static ObservedStatement Statement(
        long queryId,
        string text,
        IReadOnlyList<MartenTable> tables,
        long calls,
        double totalMs,
        long? rows = null,
        double? jitMs = null,
        TimeSpan? age = null,
        long tempBlocks = 0,
        double walBytes = 0) =>
        new(
            new StatementCounterRow
            {
                QueryId = queryId,
                Calls = calls,
                Rows = rows ?? calls,
                TotalExecMs = totalMs,
                MeanExecMs = calls > 0 ? totalMs / calls : 0,
                TempBlocksWritten = tempBlocks,
                WalBytes = walBytes,
                WalRecords = walBytes > 0 ? calls : 0,
                JitMs = jitMs,
                StatsSince = Now - (age ?? TimeSpan.FromHours(1)),
            },
            text,
            false,
            tables);

    /// <summary>A read of these statements on PostgreSQL 17.</summary>
    public static StatementRead Read(params ObservedStatement[] statements) => Read(Pg17, statements);

    /// <summary>A read of these statements on <paramref name="extension" />.</summary>
    public static StatementRead Read(StatementExtension extension, params ObservedStatement[] statements) =>
        new(
            Now,
            extension,
            statements.Length,
            statements.Sum(static x => x.Counters.TotalExecMs),
            statements.Sum(static x => x.Counters.WalBytes),
            extension.HasToplevelAndInfo ? 0 : null,
            extension.HasToplevelAndInfo ? Now.AddDays(-1) : null,
            statements);

    /// <summary>A history whose last sample is now: what the sweeper hands an evaluator after appending this sweep's.</summary>
    public static HealthHistoryView History(params (TimeSpan Ago, (string Series, double Value)[] Values)[] samples) =>
        TableCheckBuilders.History(samples);
}
