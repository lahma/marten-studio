using System.Globalization;
using System.Reflection;

using JasperFx.Core;
using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten.Events;

using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Health.Checks;

/// <summary>Which of the event store's tables exist in this database, and their shape.</summary>
/// <param name="Schema">The event store's schema.</param>
/// <param name="Relations">What the catalog listed, by table name.</param>
internal sealed record EventStoreRelations(string Schema, IReadOnlyDictionary<string, EventStoreRelation> Relations)
{
    /// <summary><c>mt_events</c>, or <see langword="null" />.</summary>
    public EventStoreRelation? Events => Relations.GetValueOrDefault(HealthEventQueries.EventsTable);

    /// <summary><c>mt_streams</c>, or <see langword="null" />.</summary>
    public EventStoreRelation? Streams => Relations.GetValueOrDefault(HealthEventQueries.StreamsTable);

    /// <summary><c>mt_event_progression</c>, or <see langword="null" />.</summary>
    public EventStoreRelation? Progression => Relations.GetValueOrDefault(HealthEventQueries.ProgressionTable);

    /// <summary><c>mt_high_water_skips</c>, or <see langword="null" />.</summary>
    public EventStoreRelation? Skips => Relations.GetValueOrDefault(HealthEventQueries.HighWaterSkipsTable);

    /// <summary><c>mt_dcb_tag_version</c>, or <see langword="null" />.</summary>
    public EventStoreRelation? DcbTagVersion => Relations.GetValueOrDefault(HealthEventQueries.DcbTagVersionTable);
}

/// <summary>One column's statistics in one relation, as the planner's last <c>ANALYZE</c> left them.</summary>
/// <param name="Column">The column.</param>
/// <param name="Rows">The rows these statistics describe: the relation's estimate, or every partition's for inherited ones.</param>
/// <param name="NullFraction">The share of rows where the column is null.</param>
/// <param name="Common">The most common values, each with the share of rows that hold it.</param>
/// <param name="Histogram">The histogram's bounds over every other value: equal-population buckets, lowest first.</param>
internal sealed record ColumnSample(
    string Column,
    double Rows,
    double NullFraction,
    IReadOnlyList<KeyValuePair<string, double>> Common,
    IReadOnlyList<string> Histogram)
{
    /// <summary>The share of rows the histogram describes: neither null nor one of the common values.</summary>
    public double HistogramShare => Math.Clamp(1 - NullFraction - Common.Sum(static x => x.Value), 0, 1);
}

/// <summary>What <c>pg_stats</c> and <c>pg_class</c> say about one of the event store's tables.</summary>
/// <param name="Table">The table.</param>
/// <param name="Exists">Whether it exists.</param>
/// <param name="Readable">Whether the reading role may select from it, without which <c>pg_stats</c> shows nothing.</param>
/// <param name="EstimatedRows">Its rows, as the planner estimates them over every partition; <see langword="null" /> before the first analyse.</param>
/// <param name="Leaves">How many plain tables it is made of: 1, or its partitions.</param>
/// <param name="LeavesSampled">How many of them carried statistics this read used.</param>
/// <param name="AnalyzedAt">The latest analyse among the relations read, or <see langword="null" /> for never.</param>
/// <param name="Samples">Per column, the statistics of each relation that has them.</param>
internal sealed record TableStatistics(
    string Table,
    bool Exists,
    bool Readable,
    double? EstimatedRows,
    int Leaves,
    int LeavesSampled,
    DateTimeOffset? AnalyzedAt,
    IReadOnlyDictionary<string, IReadOnlyList<ColumnSample>> Samples)
{
    /// <summary>A table that is not there.</summary>
    public static TableStatistics Missing(string table) =>
        new(table, false, false, null, 0, 0, null, new Dictionary<string, IReadOnlyList<ColumnSample>>(StringComparer.Ordinal));

    /// <summary>Whether Postgres holds any statistics for <paramref name="column" />.</summary>
    public bool HasColumn(string column) => Samples.TryGetValue(column, out IReadOnlyList<ColumnSample>? samples) && samples.Count > 0;

    /// <summary>Every value the statistics name for <paramref name="column" />: the common ones and the histogram's bounds.</summary>
    public IReadOnlySet<string> ValuesOf(string column)
    {
        HashSet<string> values = new(StringComparer.Ordinal);

        foreach (ColumnSample sample in SamplesOf(column))
        {
            foreach (KeyValuePair<string, double> common in sample.Common)
            {
                values.Add(common.Key);
            }

            foreach (string bound in sample.Histogram)
            {
                values.Add(bound);
            }
        }

        return values;
    }

    /// <summary>The estimated number of rows whose <paramref name="column" /> is null.</summary>
    public double EstimatedNulls(string column) => SamplesOf(column).Sum(static x => x.Rows * x.NullFraction);

    /// <summary>
    /// The estimated number of rows whose <paramref name="column" /> is one of the common values
    /// <paramref name="match" /> accepts - right for a column whose values are all common, as a boolean's are.
    /// </summary>
    public double EstimatedCommon(string column, Func<string, bool> match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return SamplesOf(column).Sum(sample => sample.Rows * sample.Common.Where(x => match(x.Key)).Sum(static x => x.Value));
    }

    /// <summary>The rows <paramref name="column" />'s statistics describe, summed over the relations read.</summary>
    public double SampledRows(string column) => SamplesOf(column).Sum(static x => x.Rows);

    /// <summary>
    /// The estimated number of rows whose numeric <paramref name="column" /> is above <paramref name="threshold" />:
    /// the common values counted exactly, the histogram's buckets by their share, interpolated within the bucket
    /// the threshold falls in.
    /// </summary>
    public double EstimatedAbove(string column, long threshold)
    {
        double total = 0;

        foreach (ColumnSample sample in SamplesOf(column))
        {
            double share = sample.Common
                .Where(x => TryNumber(x.Key, out double value) && value > threshold)
                .Sum(static x => x.Value);

            share += sample.HistogramShare * HistogramFractionAbove(sample.Histogram, threshold);
            total += sample.Rows * share;
        }

        return total;
    }

    /// <summary>The largest value of numeric <paramref name="column" /> the statistics saw, or <see langword="null" />.</summary>
    public double? LargestSeen(string column)
    {
        double? largest = null;

        foreach (ColumnSample sample in SamplesOf(column))
        {
            foreach (string value in sample.Common.Select(static x => x.Key).Concat(sample.Histogram))
            {
                if (TryNumber(value, out double number) && (largest is null || number > largest))
                {
                    largest = number;
                }
            }
        }

        return largest;
    }

    /// <summary>
    /// The share of a histogram's population above <paramref name="threshold" />: each of its
    /// <c>bounds - 1</c> buckets holds the same share, and the bucket the threshold falls in counts by how far into
    /// it the threshold is.
    /// </summary>
    internal static double HistogramFractionAbove(IReadOnlyList<string> bounds, long threshold)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        List<double> values = [];
        foreach (string bound in bounds)
        {
            if (TryNumber(bound, out double value))
            {
                values.Add(value);
            }
        }

        if (values.Count < 2)
        {
            return 0;
        }

        int buckets = values.Count - 1;
        double above = 0;

        for (int i = 0; i < buckets; i++)
        {
            double low = values[i];
            double high = values[i + 1];

            if (low >= threshold)
            {
                above += 1;
            }
            else if (high > threshold)
            {
                above += high > low ? (high - threshold) / (high - low) : 0;
            }
        }

        return above / buckets;
    }

    private IReadOnlyList<ColumnSample> SamplesOf(string column) =>
        Samples.TryGetValue(column, out IReadOnlyList<ColumnSample>? samples) ? samples : [];

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

/// <summary>What the statistics say about <c>mt_events</c> and <c>mt_streams</c> together.</summary>
/// <param name="Events"><c>mt_events</c>.</param>
/// <param name="Streams"><c>mt_streams</c>.</param>
internal sealed record EventStatistics(TableStatistics Events, TableStatistics Streams)
{
    /// <summary>
    /// Folds <see cref="HealthEventQueries.EventStatistics" />'s rows into one description per table: a partitioned
    /// table's inherited statistics when somebody analysed the parent (they cover every partition at once),
    /// otherwise every partition's own.
    /// </summary>
    public static EventStatistics From(IReadOnlyList<EventStatisticsRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return new EventStatistics(
            Fold(HealthEventQueries.EventsTable, [.. rows.Where(static x => x.Root == HealthEventQueries.EventsTable)]),
            Fold(HealthEventQueries.StreamsTable, [.. rows.Where(static x => x.Root == HealthEventQueries.StreamsTable)]));
    }

    private static TableStatistics Fold(string table, IReadOnlyList<EventStatisticsRow> rows)
    {
        if (rows.Count == 0)
        {
            return TableStatistics.Missing(table);
        }

        EventStatisticsRow first = rows[0];
        bool readable = rows.Any(static x => x.IsRoot && x.Readable);
        int leaves = first.Leaves;

        // reltuples is -1 (PostgreSQL 14+) until the first analyse, and the leaves' sum covers every partition
        // whether or not its statistics were read.
        double? estimated = first.LeafRows > 0 ? first.LeafRows : null;

        Dictionary<string, List<ColumnSample>> byColumn = new(StringComparer.Ordinal);
        HashSet<string> inheritedColumns = new(StringComparer.Ordinal);

        foreach (EventStatisticsRow row in rows.Where(static x => x.IsRoot && x.Inherited && x.Column is not null))
        {
            inheritedColumns.Add(row.Column!);
            Add(byColumn, row, estimated ?? Math.Max(row.EstimatedRows, 0));
        }

        HashSet<long> sampled = [];
        foreach (EventStatisticsRow row in rows.Where(static x => !x.Inherited && x.Column is not null && x.RelationKind == "r"))
        {
            if (inheritedColumns.Contains(row.Column!))
            {
                continue;
            }

            sampled.Add(row.Relation);
            Add(byColumn, row, Math.Max(row.EstimatedRows, 0));
        }

        int leavesSampled = sampled.Count;

        DateTimeOffset? analyzed = rows.Max(static x => x.AnalyzedAt);

        return new TableStatistics(
            table,
            Exists: true,
            readable,
            estimated,
            leaves,
            inheritedColumns.Count > 0 ? leaves : leavesSampled,
            analyzed,
            byColumn.ToDictionary(static x => x.Key, static x => (IReadOnlyList<ColumnSample>) x.Value, StringComparer.Ordinal));
    }

    private static void Add(Dictionary<string, List<ColumnSample>> byColumn, EventStatisticsRow row, double rows)
    {
        if (!byColumn.TryGetValue(row.Column!, out List<ColumnSample>? list))
        {
            list = [];
            byColumn[row.Column!] = list;
        }

        List<KeyValuePair<string, double>> common = [];
        for (int i = 0; i < row.CommonValues.Count && i < row.CommonFrequencies.Count; i++)
        {
            common.Add(new KeyValuePair<string, double>(row.CommonValues[i], row.CommonFrequencies[i]));
        }

        list.Add(new ColumnSample(row.Column!, rows, row.NullFraction, common, row.Histogram));
    }
}

/// <summary>The recent-traffic profile, folded: the window's totals, its types, its busiest streams and tenants.</summary>
/// <param name="Totals">The window as a whole.</param>
/// <param name="Types">Its event types, busiest first.</param>
/// <param name="Streams">Its busiest streams, tombstones left out.</param>
/// <param name="Tenants">Its busiest tenants - a conjoined store only.</param>
/// <param name="TombstoneType">What Marten calls a tombstone in this store.</param>
/// <param name="AppendMode">The store's append mode, which decides whether a failed save leaves a tombstone or a gap.</param>
/// <param name="Conjoined">Whether the event store is conjoined-tenanted, so a stream belongs to one tenant.</param>
internal sealed record TrafficProfile(
    TrafficProfileRow Totals,
    IReadOnlyList<TrafficProfileRow> Types,
    IReadOnlyList<TrafficProfileRow> Streams,
    IReadOnlyList<TrafficProfileRow> Tenants,
    string TombstoneType,
    EventAppendMode AppendMode,
    bool Conjoined)
{
    /// <summary>The window's size in events.</summary>
    public long Events => Totals.Events;

    /// <summary>How long the window took to append, or <see langword="null" /> when it is empty.</summary>
    public TimeSpan? Span => Totals is { FirstAt: { } first, LastAt: { } last } ? last - first : null;

    /// <summary>Folds the profile's rows.</summary>
    public static TrafficProfile From(
        IReadOnlyList<TrafficProfileRow> rows,
        string tombstoneType,
        EventAppendMode appendMode,
        bool conjoined)
    {
        ArgumentNullException.ThrowIfNull(rows);

        TrafficProfileRow totals = rows.FirstOrDefault(static x => x.Kind == HealthEventQueries.TotalRow)
            ?? new TrafficProfileRow(HealthEventQueries.TotalRow, null, null, 0, 0, null, null, null, null, null, null, null, 0, 0, 0);

        return new TrafficProfile(
            totals,
            [.. rows.Where(static x => x.Kind == HealthEventQueries.TypeRow)],
            [.. rows.Where(static x => x.Kind == HealthEventQueries.StreamRow)],
            [.. rows.Where(static x => x.Kind == HealthEventQueries.TenantRow)],
            tombstoneType,
            appendMode,
            conjoined);
    }

    /// <summary>The share <paramref name="tenantId" /> took of the window, or <see langword="null" /> when it is not among the busiest.</summary>
    public double? TenantShare(string? tenantId)
    {
        if (tenantId is null || Events <= 0)
        {
            return null;
        }

        TrafficProfileRow? row = Tenants.FirstOrDefault(x => string.Equals(x.TenantId, tenantId, StringComparison.Ordinal));
        return row is null ? null : (double) row.Events / Events;
    }

    /// <summary>"100,000 events (#9,900,001 to #10,000,000), appended over 3 h".</summary>
    public string DescribeWindow()
    {
        if (Events <= 0 || Totals.FirstSequence is not { } first || Totals.LastSequence is not { } last)
        {
            return "The window is empty: mt_events has no events.";
        }

        string text = EventHealth.Count(Events) + " event" + (Events == 1 ? string.Empty : "s")
            + " (#" + EventHealth.Count(first) + " to #" + EventHealth.Count(last) + ")";

        return Span is { } span ? text + ", appended over " + EventHealth.Duration(span) : text;
    }
}

/// <summary>What the event checks share: their reads, the store's facts and the words they print.</summary>
internal static class EventHealth
{
    /// <summary>The memo key of <see cref="ShapeAsync" />.</summary>
    internal const string ShapeKey = "events.shape";

    /// <summary>The memo key of <see cref="StatisticsAsync" />.</summary>
    internal const string StatisticsKey = "events.statistics";

    /// <summary>The memo key of <see cref="TrafficAsync" />.</summary>
    internal const string TrafficKey = "events.traffic";

    /// <summary>
    /// Marten's private alias cache on <c>EventGraph</c> - every alias it can read an event by, the upcasters'
    /// old names included, which <c>AllKnownEventTypes()</c> does not list (verified at <c>V9.39.0</c> and
    /// <c>V9.50.2</c>: <c>private readonly Cache&lt;string, EventMapping?&gt; _byEventName</c>, filled by
    /// <c>Upcast</c>). Read only through <c>Cache.TryFind</c>, which fills nothing; absent, E05 says it could not
    /// tell an upcaster's alias from an unknown one.
    /// </summary>
    private static readonly FieldInfo? AliasCacheField =
        typeof(EventGraph).GetField("_byEventName", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Which event-store tables exist and their columns - one catalog statement per sweep, however many checks ask.
    /// </summary>
    public static Task<EventStoreRelations> ShapeAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string schema = context.StoreOptions.Events.DatabaseSchemaName;

        cancellationToken.ThrowIfCancellationRequested();

        return context.Shared(
            ShapeKey,
            async token =>
            {
                var query = HealthEventQueries.EventStoreShape(schema);
                IReadOnlyList<EventStoreRelation> relations = await context.Sql
                    .QueryAsync(query, HealthEventQueries.ReadRelation, token)
                    .ConfigureAwait(false);

                return new EventStoreRelations(schema, relations.ToDictionary(static x => x.Name, StringComparer.Ordinal));
            });
    }

    /// <summary>The planner's statistics of <c>mt_events</c> and <c>mt_streams</c> - one statement per sweep.</summary>
    public static Task<EventStatistics> StatisticsAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        string schema = context.StoreOptions.Events.DatabaseSchemaName;

        cancellationToken.ThrowIfCancellationRequested();

        return context.Shared(
            StatisticsKey,
            async token =>
            {
                var query = HealthEventQueries.EventStatistics(schema);
                IReadOnlyList<EventStatisticsRow> rows = await context.Sql
                    .QueryAsync(query, HealthEventQueries.ReadStatistics, token)
                    .ConfigureAwait(false);

                return EventStatistics.From(rows);
            });
    }

    /// <summary>
    /// The recent-traffic profile - one bounded statement per deeper sweep, shared by E02, E03 and E04.
    /// </summary>
    /// <exception cref="HealthCheckUnavailableException">
    /// There is no <c>mt_events</c> yet, or the store partitions its events by tenant.
    /// </exception>
    public static async Task<TrafficProfile> TrafficAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        RefuseTenantPartitioning(context);

        EventStoreRelations shape = await ShapeAsync(context, cancellationToken).ConfigureAwait(false);
        EventStoreRelation events = RequireEvents(shape);

        string tombstone = TombstoneType(context.StoreOptions.Events);
        EventAppendMode mode = context.StoreOptions.Events.AppendMode;
        bool conjoined = context.StoreOptions.Events.TenancyStyle == TenancyStyle.Conjoined;

        TrafficProfileShape query = new(
            shape.Schema,
            tombstone,
            HasBinaryData: events.Has("bdata"),
            HasIsArchived: events.Has("is_archived"),
            ByTenant: conjoined);

        return await context.Shared(
                TrafficKey,
                async token =>
                {
                    var statement = HealthEventQueries.TrafficProfile(query);
                    IReadOnlyList<TrafficProfileRow> rows = await context.Sql
                        .QueryAsync(statement, HealthEventQueries.ReadTraffic, token)
                        .ConfigureAwait(false);

                    return TrafficProfile.From(rows, tombstone, mode, conjoined);
                })
            .ConfigureAwait(false);
    }

    /// <summary><c>mt_events</c>, or the expected state that it is not there yet.</summary>
    /// <exception cref="HealthCheckUnavailableException">It does not exist in this database.</exception>
    public static EventStoreRelation RequireEvents(EventStoreRelations shape)
    {
        ArgumentNullException.ThrowIfNull(shape);

        return shape.Events ?? throw new HealthCheckUnavailableException(
            "The event store's tables do not exist in this database yet, so there are no events to look at.");
    }

    /// <summary>
    /// Refuses a store that partitions its events by tenant: every tenant numbers its events from one there, so a
    /// store-wide window by sequence means nothing and would reach every partition.
    /// </summary>
    /// <exception cref="HealthCheckUnavailableException">The store has <c>UseTenantPartitionedEvents</c> on.</exception>
    public static void RefuseTenantPartitioning(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.StoreOptions.Events.UseTenantPartitionedEvents)
        {
            throw new HealthCheckUnavailableException(
                "This store partitions its events by tenant (Events.UseTenantPartitionedEvents), so every tenant numbers its "
                + "events from one and the store has no single recent window to read; the traffic checks do not cover that "
                + "shape yet.");
        }
    }

    /// <summary>
    /// The <c>type</c> Marten stamps on a tombstone in this store: <c>JasperFx.Events.Tombstone</c>'s alias under the
    /// store's naming style (<c>tombstone</c> by default), or the name a mapping of it already carries.
    /// </summary>
    /// <remarks>
    /// Verified at <c>V9.39.0</c> and <c>V9.50.2</c> (<c>EventGraph.TryCreateTombstoneBatch</c>): a tombstone is an
    /// <c>Event&lt;Tombstone&gt;</c> whose <c>EventTypeName</c> is <c>EventMappingFor&lt;Tombstone&gt;()</c>'s, in the
    /// stream <c>Tombstone.StreamId</c> - a <see cref="Guid" /> drawn afresh in every process - or
    /// <c>Tombstone.StreamKey</c> (<c>mt_tombstone</c>) on a string-keyed store. So the type, not the stream, is what
    /// identifies one across processes. Computed rather than asked of <c>EventMappingFor</c>, which would register a
    /// mapping on the host's store as a side effect.
    /// </remarks>
    public static string TombstoneType(IReadOnlyEventStoreOptions events)
    {
        ArgumentNullException.ThrowIfNull(events);

        IEventType? mapped = events.AllKnownEventTypes().FirstOrDefault(static x => x.EventType == typeof(Tombstone));
        return mapped?.EventTypeName ?? typeof(Tombstone).GetEventTypeName(events.EventNamingStyle);
    }

    /// <summary>
    /// A predicate for whether Marten can read an event by the alias stored with it: a registered mapping, or an
    /// upcaster's old name. <see langword="null" /> when the upcasters' aliases cannot be read on this Marten.
    /// </summary>
    public static Func<string, bool>? KnownAliases(IReadOnlyEventStoreOptions events)
    {
        ArgumentNullException.ThrowIfNull(events);

        HashSet<string> registered = new(events.AllKnownEventTypes().Select(static x => x.EventTypeName), StringComparer.Ordinal);

        Cache<string, EventMapping?>? cache = null;
        try
        {
            if (events is EventGraph graph && AliasCacheField?.GetValue(graph) is Cache<string, EventMapping?> found)
            {
                cache = found;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            cache = null;
        }

        if (cache is null)
        {
            return null;
        }

        return alias => registered.Contains(alias) || (cache.TryFind(alias, out EventMapping? mapping) && mapping is not null);
    }

    /// <summary>
    /// The type an assembly-qualified name names, looked up only among the assemblies this process has already
    /// loaded - <see langword="null" /> when none has it. Never loads one: these names come from the database, and
    /// a health read must not change what the host has loaded.
    /// </summary>
    public static Type? ResolveLoaded(string assemblyQualifiedName, IReadOnlyDictionary<string, Assembly> loaded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyQualifiedName);
        ArgumentNullException.ThrowIfNull(loaded);

        try
        {
            return Type.GetType(
                assemblyQualifiedName,
                name => name.Name is { } simple && loaded.TryGetValue(simple, out Assembly? assembly) ? assembly : null,
                typeResolver: null,
                throwOnError: false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The assemblies this process has loaded, by simple name.</summary>
    public static IReadOnlyDictionary<string, Assembly> LoadedAssemblies()
    {
        Dictionary<string, Assembly> loaded = new(StringComparer.OrdinalIgnoreCase);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name is { } name)
            {
                loaded.TryAdd(name, assembly);
            }
        }

        return loaded;
    }

    /// <summary>A count, grouped: "100,000".</summary>
    public static string Count(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A share: "12.5%".</summary>
    public static string Percent(double share) => (share * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>A size: "64 KB", "1.2 MB".</summary>
    public static string Bytes(double bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / (1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString("0", CultureInfo.InvariantCulture) + " bytes",
    };

    /// <summary>A span: "45 s", "12 min", "3.5 h", "2.1 days".</summary>
    public static string Duration(TimeSpan span) => span.TotalSeconds switch
    {
        < 90 => Math.Max(0, span.TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + " s",
        < 90 * 60 => span.TotalMinutes.ToString("0", CultureInfo.InvariantCulture) + " min",
        < 36 * 3600 => span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " h",
        _ => span.TotalDays.ToString("0.#", CultureInfo.InvariantCulture) + " days",
    };

    /// <summary>A time, as the evidence prints it: ISO 8601, UTC, to the second.</summary>
    public static string Time(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>The caveat line every statistics-based finding carries: when Postgres last sampled the table.</summary>
    public static string StatisticsCaveat(TableStatistics table)
    {
        ArgumentNullException.ThrowIfNull(table);

        string when = table.AnalyzedAt is { } at ? "last analysed at " + Time(at) : "never analysed";
        string partitions = table.Leaves > 1
            ? " Read from " + Count(table.LeavesSampled) + " of its " + Count(table.Leaves) + " partitions' statistics."
            : string.Empty;

        return "Estimated from PostgreSQL's planner statistics for " + table.Table + " (" + when + "), with no scan: "
            + "a sample, not a count, and only as fresh as the last ANALYZE." + partitions;
    }
}
