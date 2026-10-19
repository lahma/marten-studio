using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Health;

/// <summary>A document the tables-and-indexes evaluator tests name in their remedies.</summary>
public sealed class HealthOrderDoc
{
    public Guid Id { get; set; }

    public string Status { get; set; } = string.Empty;
}

/// <summary>A document with an <c>int</c> id, which Marten draws from HiLo.</summary>
public sealed class HealthIntDoc
{
    public int Id { get; set; }
}

/// <summary>A document with a <c>long</c> id, which Marten draws from HiLo too - but never near its end.</summary>
public sealed class HealthLongDoc
{
    public long Id { get; set; }
}

/// <summary>A strong-typed id wrapping an <c>int</c>: Weasel's identification calls <c>NextInt()</c> for it.</summary>
public readonly record struct HealthIntId(int Value);

/// <summary>A document whose id is <see cref="HealthIntId" />.</summary>
public sealed class HealthStrongIdDoc
{
    public HealthIntId Id { get; set; }
}

/// <summary>A document with a string id the application assigns.</summary>
public sealed class HealthStringDoc
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// Facts for the tables-and-indexes evaluators, stated outright - the <c>IndexAdvice.Join</c> precedent: every
/// threshold and every sentence is judged here against numbers a test chose, with no Postgres.
/// </summary>
internal static class TableCheckBuilders
{
    /// <summary>The moment every evaluation here happens at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The document table of <typeparamref name="T" />, from a real mapping that never connects.</summary>
    public static MartenTable Document<T>(Action<StoreOptions>? configure = null)
    {
        var documentType = SqlTestStore.DocumentType<T>(configure);
        return new MartenTable(SqlTestStore.Schema, documentType.TableName.Name, MartenTableKind.Document, OutsideStoreSchemas: false, documentType);
    }

    /// <summary>One of the event store's tables.</summary>
    public static MartenTable EventTable(string table = "mt_events") => new("studio_sql_events", table, MartenTableKind.EventStore, OutsideStoreSchemas: false);

    /// <summary>A catalog row for <paramref name="table" />: analysed, empty, autovacuum on.</summary>
    public static HealthTableRow Row(MartenTable table) => new()
    {
        Schema = table.Schema,
        Table = table.Table,
        EstimatedRows = 0,
        LiveRows = 0,
        DeadRows = 0,
        SequentialScans = 0,
        SequentialRowsRead = 0,
        IndexScans = 0,
        Inserts = 0,
        Updates = 0,
        Deletes = 0,
        HotUpdates = 0,
        ModifiedSinceAnalyze = 0,
        LastAutovacuum = Now.AddHours(-1),
        LastAutoanalyze = Now.AddHours(-1),
        OldestXidAge = 1_000,
        StatsReset = Now.AddDays(-30),
    };

    /// <summary>The table's facts, with <paramref name="shape" /> applied to its row.</summary>
    public static TableFacts Table(MartenTable table, Func<HealthTableRow, HealthTableRow>? shape = null) =>
        new(table, shape is null ? Row(table) : shape(Row(table)));

    /// <summary>A valid b-tree index on <paramref name="table" /> over <paramref name="columns" />.</summary>
    public static HealthIndexRow IndexRow(MartenTable table, string name, params string[] columns) => new()
    {
        Schema = table.Schema,
        Table = table.Table,
        Name = name,
        AccessMethod = "btree",
        Definition = "CREATE INDEX " + name + " ON " + table.Schema + "." + table.Table + " USING btree (" + string.Join(", ", columns) + ")",
        Columns = columns,
        KeyColumnCount = columns.Length,
        OperatorClasses = [.. columns.Select(static _ => 3126u)],
        Collations = [.. columns.Select(static _ => 100u)],
        Options = [.. columns.Select(static _ => (short) 0)],
        Bytes = 8 * 1024 * 1024,
        Scans = 10,
        ReferencedColumns = [.. columns.Where(static x => !x.StartsWith('(')).Distinct()],
    };

    /// <summary>An index's facts.</summary>
    public static IndexFacts Index(MartenTable table, HealthIndexRow row, IndexOrigin origin = IndexOrigin.Declared) =>
        new(table, row, origin, null);

    /// <summary>A history of samples, each so long before <see cref="Now" />, oldest first.</summary>
    public static HealthHistoryView History(params (TimeSpan Ago, (string Series, double Value)[] Values)[] samples) =>
        new([.. samples
            .OrderByDescending(static x => x.Ago)
            .Select(static x => new HealthSample(
                Now - x.Ago,
                x.Values.ToDictionary(static v => v.Series, static v => v.Value, StringComparer.Ordinal)))]);

    /// <summary>An evaluation at <see cref="Now" />.</summary>
    public static HealthEvaluation Evaluation(HealthHistoryView? history = null, MartenRuntime? runtime = null) =>
        HealthEvaluation.At(Now, runtime, history);

    /// <summary>A host on the Marten floor, where FillFactor and StorageParameter do not exist.</summary>
    public static MartenRuntime Floor { get; } = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
    {
        [RuntimePackage.Marten] = "9.39.0",
        [RuntimePackage.JasperFx] = "2.74.0",
        [RuntimePackage.JasperFxEvents] = "2.74.0",
    });

    /// <summary>A host on the latest-tested Marten.</summary>
    public static MartenRuntime Latest { get; } = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
    {
        [RuntimePackage.Marten] = "9.50.2",
        [RuntimePackage.JasperFx] = "2.87.1",
        [RuntimePackage.JasperFxEvents] = "2.87.1",
    });

    /// <summary>Every piece of text a finding shows, for the wording rules.</summary>
    public static string AllText(HealthFinding finding) =>
        string.Join(
            " ",
            [
                finding.Title,
                finding.WhyItMatters,
                finding.Caveat ?? string.Empty,
                finding.Remedy?.Code ?? string.Empty,
                finding.Remedy?.Explanation ?? string.Empty,
                .. finding.Evidence.Select(static x => x.Label + " " + x.Value),
            ]);
}
