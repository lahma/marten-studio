using JasperFx.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>Stated facts for the event checks' evaluators: the rows their reads return, built by hand.</summary>
internal static class EventHealthFacts
{
    /// <summary>The time every evaluation happens at.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The window's totals: <paramref name="events" /> events from #1, appended over ten minutes up to <see cref="Now" />.</summary>
    public static TrafficProfileRow Totals(
        long events,
        long tombstones = 0,
        long? median = 400,
        long? p95 = 2_000,
        long? max = 4_000,
        long tenants = 1) =>
        new(HealthEventQueries.TotalRow, null, null, events, events * 500, median, p95, max,
            events == 0 ? null : 1, events == 0 ? null : events, Now.AddMinutes(-10), Now, tombstones, 0, tenants);

    /// <summary>One stream's row.</summary>
    public static TrafficProfileRow StreamOf(string id, long events, string tenant = "*DEFAULT*", long archived = 0) =>
        new(HealthEventQueries.StreamRow, id, tenant, events, events * 500, null, null, 4_000, 1, events, Now.AddMinutes(-9), Now.AddMinutes(-1), null, archived, null);

    /// <summary>One event type's row.</summary>
    public static TrafficProfileRow TypeOf(string name, long events, long p95) =>
        new(HealthEventQueries.TypeRow, name, null, events, events * p95, null, p95, p95 * 2, 1, events, null, null, 0, 0, null);

    /// <summary>One tenant's row.</summary>
    public static TrafficProfileRow TenantOf(string tenant, long events, long tombstones = 0) =>
        new(HealthEventQueries.TenantRow, null, tenant, events, events * 500, null, null, 4_000, 1, events, null, null, tombstones, 0, null);

    /// <summary>A profile over those rows.</summary>
    public static TrafficProfile Profile(
        TrafficProfileRow totals,
        IEnumerable<TrafficProfileRow>? streams = null,
        IEnumerable<TrafficProfileRow>? types = null,
        IEnumerable<TrafficProfileRow>? tenants = null,
        EventAppendMode mode = EventAppendMode.Rich,
        bool conjoined = false) =>
        new(totals, [.. types ?? []], [.. streams ?? []], [.. tenants ?? []], "tombstone", mode, conjoined);

    /// <summary>An evaluation at <see cref="Now" />.</summary>
    public static HealthEvaluation At(HealthHistoryView? history = null) => HealthEvaluation.At(Now, history: history);

    /// <summary>
    /// A table's statistics with one sample per column: each column's most common values (value, share) and its
    /// histogram's bounds.
    /// </summary>
    public static TableStatistics Statistics(
        string table,
        double rows,
        params (string Column, (string Value, double Share)[] Common, string[] Histogram)[] columns)
    {
        Dictionary<string, IReadOnlyList<ColumnSample>> samples = new(StringComparer.Ordinal);

        foreach ((string column, (string Value, double Share)[] common, string[] histogram) in columns)
        {
            samples[column] =
            [
                new ColumnSample(
                    column,
                    rows,
                    0,
                    [.. common.Select(static x => new KeyValuePair<string, double>(x.Value, x.Share))],
                    histogram),
            ];
        }

        return new TableStatistics(table, true, true, rows, 1, 1, Now.AddHours(-1), samples);
    }

    /// <summary>A history whose samples hold <paramref name="series" />'s values at the given times.</summary>
    public static HealthHistoryView History(string series, params (DateTimeOffset At, double Value)[] points) =>
        new([.. points.Select(x => new HealthSample(x.At, new Dictionary<string, double>(StringComparer.Ordinal) { [series] = x.Value }))]);

    /// <summary>A history of two series sampled together, as one sweep samples every part of a check at once.</summary>
    public static HealthHistoryView History(string first, string second, params (DateTimeOffset At, double First, double Second)[] points) =>
        new([
            .. points.Select(x => new HealthSample(
                x.At,
                new Dictionary<string, double>(StringComparer.Ordinal) { [first] = x.First, [second] = x.Second })),
        ]);
}
