using MartenStudio.Services.Health.Checks;

using Npgsql;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The aliases <c>mt_events</c> holds, from the planner's statistics on its <c>type</c> column (1.2-UX-FIX): the read E05 makes
/// (<see cref="HealthEventQueries.EventStatistics" />), run for the shape check's list of event types. It reads <c>pg_class</c> and
/// <c>pg_stats</c> and touches no event table, so it is no <c>group by type</c> over the log; what it costs is what E05's sweep pays.
/// </summary>
/// <remarks>
/// A statistic is a sample: a rare alias can be missing from it, and one written since the last <c>ANALYZE</c> is. The caller says so.
/// </remarks>
internal static class EventAliasStatistics
{
    /// <summary>
    /// E05's statement as a command, its parameters copied (a <see cref="HealthQuery" /> may be handed out twice).
    /// </summary>
    /// <param name="eventSchema">The store's event schema.</param>
    /// <param name="timeout">The studio's <c>QueryTimeout</c>.</param>
    public static NpgsqlCommand Command(string eventSchema, TimeSpan timeout)
    {
        HealthQuery query = HealthEventQueries.EventStatistics(eventSchema);
        NpgsqlCommand command = new(query.Sql) { CommandTimeout = (int) Math.Max(1, Math.Ceiling(timeout.TotalSeconds)) };

        foreach (NpgsqlParameter parameter in query.Parameters)
        {
            command.Parameters.Add(parameter.Clone());
        }

        return command;
    }

    /// <summary>
    /// The aliases the statistics name that <paramref name="isKnown" /> does not, in order, or <see langword="null" /> when the
    /// statistics cannot say: the role may not select from <c>mt_events</c>, or nothing has analysed it since its events arrived.
    /// </summary>
    /// <param name="rows">The rows of <see cref="Command" />.</param>
    /// <param name="isKnown">Whether Marten can read an event by this alias (<see cref="EventHealth.KnownAliases" />).</param>
    public static IReadOnlyList<string>? Unregistered(IReadOnlyList<EventStatisticsRow> rows, Func<string, bool> isKnown)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(isKnown);

        TableStatistics events = EventStatistics.From(rows).Events;

        if (!events.Readable || !events.HasColumn("type"))
        {
            return null;
        }

        return [.. events.ValuesOf("type").Where(x => x.Length > 0 && !isKnown(x)).Order(StringComparer.Ordinal)];
    }
}
