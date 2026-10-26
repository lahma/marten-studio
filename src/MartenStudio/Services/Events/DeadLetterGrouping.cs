namespace MartenStudio.Services.Events;

/// <summary>One letter as the grouped view reads it: what its key is made of, and its event's type and skip.</summary>
/// <param name="ProjectionName">The letter's <c>ProjectionName</c>.</param>
/// <param name="ShardName">The letter's <c>ShardName</c>.</param>
/// <param name="ExceptionType">The letter's <c>ExceptionType</c>.</param>
/// <param name="TenantId">The letter's own <c>TenantId</c> - the failing event's tenant.</param>
/// <param name="Timestamp">The letter's <c>Timestamp</c>.</param>
/// <param name="Message">The first characters of the letter's <c>ExceptionMessage</c> - what the fingerprint reads.</param>
/// <param name="EventType">The type of the letter's event, as this scope sees it; <see langword="null" /> when it does not.</param>
/// <param name="EventSkipped">Whether that event is skipped; <see langword="null" /> where the store records no skips or there is no event.</param>
/// <param name="EventKey">That event's identity, or <see langword="null" /> when there is no event in scope.</param>
internal sealed record DeadLetterGroupRow(
    string? ProjectionName,
    string? ShardName,
    string? ExceptionType,
    string? TenantId,
    DateTimeOffset? Timestamp,
    string? Message,
    string? EventType,
    bool? EventSkipped,
    string? EventKey);

/// <summary>
/// The grouped view's grouping (M3): letters into groups by <see cref="DeadLetterGroupKey" />, with each group's counts,
/// first and last seen, tenants and sample - a pure function of the rows, so it is tested without a database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here and not in SQL</b>: see <c>DeadLetterGroupQueries</c> - fingerprinting ten thousand messages cost the
/// database about five times what reading them and fingerprinting them here costs.
/// </para>
/// <para>
/// Groups are listed by letters, then by the newest letter, then by their key ordinally, so a read that has not changed
/// lists its groups in the same order. An event counts once in a group however many of its letters name it, and a letter
/// whose event the scope cannot see counts as a letter and not as an event. A group's tenants are counted in full and
/// named, ordinally, up to <see cref="DeadLetterGroupLimits.MaxTenantNames" />; which of those a visitor is shown is the
/// service's decision, made per visitor after the shared read.
/// </para>
/// </remarks>
internal static class DeadLetterGrouping
{
    /// <summary>The groups of <paramref name="rows" />, newest letters first, at most <paramref name="maxLetters" /> of them.</summary>
    /// <param name="rows">The letters, newest first - the read asks for one more than <paramref name="maxLetters" />.</param>
    /// <param name="maxLetters">The most letters grouped; one more means the read was cut.</param>
    /// <param name="maxGroups">The most groups listed.</param>
    /// <param name="readAt">When the rows were read.</param>
    /// <param name="recordsSkips">Whether the store records skipped events.</param>
    public static DeadLetterGroupList Group(
        IEnumerable<DeadLetterGroupRow> rows,
        int maxLetters,
        int maxGroups,
        DateTimeOffset readAt,
        bool recordsSkips)
    {
        ArgumentNullException.ThrowIfNull(rows);

        Dictionary<DeadLetterGroupKey, Accumulator> groups = [];
        long letters = 0;
        bool truncated = false;

        foreach (DeadLetterGroupRow row in rows)
        {
            if (letters >= maxLetters)
            {
                truncated = true;
                break;
            }

            letters++;

            var key = new DeadLetterGroupKey(
                row.ProjectionName,
                row.ShardName,
                row.EventType,
                row.ExceptionType,
                DeadLetterFingerprint.Of(row.Message));

            if (!groups.TryGetValue(key, out Accumulator? group))
            {
                group = new Accumulator(row.Message is null ? null : DeadLetterFingerprint.Truncate(row.Message, DeadLetterGroupLimits.SampleLength));
                groups.Add(key, group);
            }

            group.Add(row);
        }

        List<DeadLetterGroup> listed =
        [
            .. groups
                .Select(pair => pair.Value.ToGroup(pair.Key, recordsSkips))
                .OrderByDescending(static x => x.Letters)
                .ThenByDescending(static x => x.LastSeen ?? DateTimeOffset.MinValue)
                .ThenBy(static x => x.Key.ProjectionName, StringComparer.Ordinal)
                .ThenBy(static x => x.Key.ShardName, StringComparer.Ordinal)
                .ThenBy(static x => x.Key.EventType, StringComparer.Ordinal)
                .ThenBy(static x => x.Key.ExceptionType, StringComparer.Ordinal)
                .ThenBy(static x => x.Key.Fingerprint, StringComparer.Ordinal)
                .Take(Math.Max(0, maxGroups)),
        ];

        return new DeadLetterGroupList(listed, groups.Count, letters, readAt)
        {
            RecordsSkips = recordsSkips,
            LettersTruncated = truncated,
        };
    }

    private sealed class Accumulator(string? sample)
    {
        private readonly HashSet<string> events = new(StringComparer.Ordinal);
        private readonly HashSet<string> skipped = new(StringComparer.Ordinal);
        private readonly SortedSet<string> tenants = new(StringComparer.Ordinal);
        private long letters;
        private DateTimeOffset? first;
        private DateTimeOffset? last;

        public void Add(DeadLetterGroupRow row)
        {
            letters++;

            if (row.Timestamp is { } at)
            {
                first = first is { } earliest && earliest <= at ? earliest : at;
                last = last is { } latest && latest >= at ? latest : at;
            }

            if (row.EventKey is { } key)
            {
                events.Add(key);

                if (row.EventSkipped == true)
                {
                    skipped.Add(key);
                }
            }

            if (row.TenantId is { } tenant)
            {
                tenants.Add(tenant);
            }
        }

        public DeadLetterGroup ToGroup(DeadLetterGroupKey key, bool recordsSkips) =>
            new(
                key,
                letters,
                events.Count,
                recordsSkips ? skipped.Count : null,
                first,
                last,
                tenants.Count,
                [.. tenants.Take(DeadLetterGroupLimits.MaxTenantNames)],
                sample);
    }
}
