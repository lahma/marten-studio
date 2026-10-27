using System.Data;
using System.Globalization;

using MartenStudio.Internal.Sql;

using Npgsql;

using PostgresStudio.Services;

namespace MartenStudio.Services.Events;

/// <summary>
/// The dead letters grouped by cause, and "Skip this group" (M3).
/// </summary>
/// <remarks>
/// <para>
/// <b>The grouping is a read</b>, the list's in shape: the studio's own SQL over <c>mt_doc_deadletterevent</c>
/// (<see cref="DeadLetterGroupQueries" />), the body's own <c>TenantId</c> as the tenant axis, the soft-delete filter where
/// the table has one, nothing created when the table is not there (hard rule 14). It reads columns of the newest
/// <see cref="DeadLetterGroupLimits.MaxLettersGrouped" /> letters and groups them here (<see cref="DeadLetterGrouping" />):
/// measured, that costs the database a fifth of fingerprinting in SQL. It is shared across circuits for one refresh
/// interval (<see cref="DeadLetterGroupCache" />), and what a visitor is shown of it - which tenants are named - is decided
/// after the cache, per visitor. A group's own letters - the list's filter, the dialog's count, the skip - are found again
/// in SQL, by the same rule (<see cref="DeadLetterGroupQueries.AppendGroupPredicates" />).
/// </para>
/// <para>
/// <b>The bulk skip is single Skip's path with a longer array.</b> Marten's <c>MarkEventsAsSkipped</c> takes an array of
/// sequences at both ends (<c>IMartenDatabase</c> at <c>V9.39.0</c> and <c>v9.54.2</c>), and from 9.51 so does its tenant
/// overload. So the bulk skip makes the same checks in the same order - capability and write policy
/// (<see cref="MutateAsync" />), the per-tenant numbering (<c>RequireSkippableNumbering</c>), the <c>is_skipped</c> column,
/// the tenant proof of every sequence (<c>ProveSequencesInScopeAsync</c>) - and ends in the same call
/// (<c>CommitSkipAsync</c>). What it adds is in front: the type must be visible, because it reads the dead letters; the
/// group's events are read again here rather than taken from the page; and the typed count must still be the count.
/// </para>
/// </remarks>
internal sealed partial class EventDataService
{
    /// <summary>The action name for skipping a dead-letter group's events.</summary>
    internal const string SkipGroupAction = "Skip dead-letter group";

    /// <summary>What a bulk skip is refused with when the group has no event left to skip.</summary>
    internal const string NothingToSkipMessage =
        "This group has no event left to skip in this scope: every event of it is skipped already, or no longer in mt_events.";

    /// <inheritdoc />
    public async Task<DeadLetterGroupList> ListDeadLetterGroupsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            // A hidden DeadLetterEvent is grouped by nothing, as it is listed by nothing.
            if (!DeadLettersVisible(scope))
            {
                return DeadLetterGroupList.Hidden;
            }

            DeadLetterGroupList read = await groups
                .GetAsync(resolved, token => ReadGroupsAsync(resolved, token), cancellationToken)
                .ConfigureAwait(false);

            return read.Groups.Count == 0
                ? read
                : await NameVisibleTenantsAsync(resolved, read, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return DeadLetterGroupList.Failed(Describe(scope, exception, "group the dead letters"));
        }
    }

    /// <inheritdoc />
    public async Task<DeadLetterGroupSkipPlan> DescribeDeadLetterGroupSkipAsync(
        StudioScope scope,
        DeadLetterGroupKey group,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(group);

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            // The refusals the skip itself would make, in its order, as a sentence for the dialog - so a dialog never
            // offers what the service refuses. The service asks them all again (hard rule 5).
            if (!DeadLettersVisible(scope))
            {
                return DeadLetterGroupSkipPlan.Refused(DeadLetterVisibility.HiddenMessage);
            }

            if (SkipNumberingRefusal(resolved) is { } numbering)
            {
                return DeadLetterGroupSkipPlan.Refused(numbering);
            }

            await using NpgsqlConnection connection = await OpenAsync(resolved, cancellationToken).ConfigureAwait(false);
            EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

            if (!table.HasIsSkipped)
            {
                return DeadLetterGroupSkipPlan.Refused(NoSkipColumnMessage);
            }

            string? tenant = resolved.Store.Options.Events.UseTenantPartitionedEvents ? resolved.TenantId : null;

            if (await DescribeDeadLettersAsync(resolved, connection, cancellationToken).ConfigureAwait(false) is not { } deadLetters)
            {
                return new DeadLetterGroupSkipPlan(0, 0, 0, tenant);
            }

            await using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupEventCounts(
                deadLetters,
                KeysFor(resolved),
                new DeadLetterEventSource(table, resolved.Store.Options.Events.UseTenantPartitionedEvents),
                resolved.TenantId,
                group);
            command.Connection = connection;
            command.CommandTimeout = CommandTimeoutSeconds;

            long notSkipped = 0;
            long skipped = 0;

            await using (NpgsqlDataReader reader = await command
                .ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken).ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    notSkipped = reader.GetInt64(0);
                    skipped = reader.GetInt64(1);
                }
            }

            return new DeadLetterGroupSkipPlan(
                Math.Min(notSkipped, DeadLetterGroupLimits.MaxEventsPerSkip),
                notSkipped,
                skipped,
                tenant);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            // A refused scope is Describe's "Not authorized" sentence, which says nothing about what is behind it (D5).
            return DeadLetterGroupSkipPlan.Refused(Describe(scope, exception, "count a dead-letter group's events").Display);
        }
    }

    /// <inheritdoc />
    public async Task<long> SkipDeadLetterGroupAsync(
        StudioScope scope,
        DeadLetterGroupKey group,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(group);

        long? confirmed = ConfirmedCount(confirmation);
        long marked = 0;

        await MutateAsync(
            scope,
            StudioCapability.ManageDeadLetters,
            SkipGroupAction,
            GroupTarget(group, confirmed),
            async (resolved, token) =>
            {
                // First: the group is read out of the dead letters, so a hidden type is refused - audited as refused -
                // before anything of it is read.
                RequireDeadLettersVisible(resolved);

                // Then single Skip's own checks, in its order (SkipEventAsync): the numbering, before anything is read...
                RequireSkippableNumbering(resolved);

                long[] sequences;
                bool more;

                await using (NpgsqlConnection connection = await OpenAsync(resolved, token).ConfigureAwait(false))
                {
                    // ...the column...
                    EventTableInfo table = await DescribeTablesAsync(resolved, connection, token).ConfigureAwait(false);
                    RequireSkipColumn(table);

                    // The group's events not skipped yet, read here and now - never a list the page sends - lowest first,
                    // one past the cap so "there are more" needs no count.
                    long[] read = await DescribeDeadLettersAsync(resolved, connection, token).ConfigureAwait(false) is { } deadLetters
                        ? await ReadGroupSequencesAsync(resolved, connection, deadLetters, table, group, token).ConfigureAwait(false)
                        : [];

                    more = read.Length > DeadLetterGroupLimits.MaxEventsPerSkip;
                    sequences = more ? read[..DeadLetterGroupLimits.MaxEventsPerSkip] : read;

                    if (sequences.Length == 0)
                    {
                        throw new InvalidOperationException(NothingToSkipMessage);
                    }

                    // What the visitor typed is the number the dialog said; a group that grew or shrank since is refused,
                    // not skipped differently from what was confirmed.
                    if (confirmed != sequences.Length)
                    {
                        throw new InvalidOperationException(ConfirmationMismatch(sequences.Length));
                    }

                    // ...and the tenant proof, of every sequence, before any is handed over (hard rule 15).
                    await ProveSequencesInScopeAsync(resolved, connection, table, sequences, token).ConfigureAwait(false);
                }

                // ...and the same Marten call, past every check, on a token of the operation's own.
                await CommitSkipAsync(resolved, sequences).ConfigureAwait(false);
                marked = sequences.Length;

                string outcome = Count(sequences.Length) + " marked as skipped";

                if (resolved.Store.Options.Events.UseTenantPartitionedEvents)
                {
                    outcome += " in tenant " + resolved.TenantId;
                }

                return more
                    ? outcome + "; more of the group's events are not skipped yet - one action skips at most "
                        + DeadLetterGroupLimits.MaxEventsPerSkip.ToString("N0", CultureInfo.InvariantCulture)
                    : outcome;
            },
            cancellationToken).ConfigureAwait(false);

        return marked;
    }

    /// <summary>What a bulk skip whose typed count is not the count it would skip now is refused with.</summary>
    internal static string ConfirmationMismatch(long count) =>
        "The typed confirmation is not the number of events this action would skip now (" + Count(count)
        + "): the group changed after the dialog opened. Open it again.";

    /// <summary>
    /// The audit target of a bulk skip: the group's key and the count the visitor confirmed - so one entry names both,
    /// whether the skip succeeds, fails or is refused (hard rule 5). The typed text itself is never echoed; only a count
    /// that parses is.
    /// </summary>
    internal static string GroupTarget(DeadLetterGroupKey group, long? confirmed) =>
        group.Describe() + "; " + (confirmed is { } count ? Count(count) + " confirmed" : "no count confirmed");

    /// <summary>The typed confirmation as a count, or <see langword="null" /> when it is not a whole number.</summary>
    internal static long? ConfirmedCount(string? confirmation) =>
        long.TryParse(confirmation?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long count) ? count : null;

    private static string Count(long count) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " event" : " events");

    /// <summary>The refusal <see cref="RequireSkippableNumbering" /> would throw, as a sentence, or <see langword="null" />.</summary>
    private static string? SkipNumberingRefusal(ResolvedScope resolved)
    {
        try
        {
            RequireSkippableNumbering(resolved);
            return null;
        }
        catch (InvalidOperationException refusal)
        {
            return refusal.Message;
        }
    }

    /// <summary>The events table a dead-letter statement looks a letter's event up in, or <see langword="null" /> when there is none.</summary>
    private async Task<DeadLetterEventSource?> DescribeEventSourceAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        EventTableInfo table = await DescribeTablesAsync(resolved, connection, cancellationToken).ConfigureAwait(false);

        return table.EventColumns.Count == 0
            ? null
            : new DeadLetterEventSource(table, resolved.Store.Options.Events.UseTenantPartitionedEvents);
    }

    /// <summary>The JSON keys of <c>DeadLetterEvent</c> under this store's naming policy.</summary>
    private static DeadLetterJsonKeys KeysFor(ResolvedScope resolved) =>
        DeadLetterJsonKeys.For(MartenStudio.Services.Documents.DocumentDataService.NamingPolicyFor(resolved.Store));

    /// <summary>The read and the grouping, run once per interval for everyone (the cache's factory).</summary>
    private async Task<DeadLetterGroupList> ReadGroupsAsync(ResolvedScope resolved, CancellationToken token)
    {
        await using NpgsqlConnection connection = await OpenAsync(resolved, token).ConfigureAwait(false);

        DateTimeOffset readAt = DateTimeOffset.UtcNow;

        if (await DescribeDeadLettersAsync(resolved, connection, token).ConfigureAwait(false) is not { } table)
        {
            // No table: the daemon never recorded one, and the studio never creates it.
            return DeadLetterGroupList.Empty with { ReadAt = readAt };
        }

        DeadLetterEventSource? events = await DescribeEventSourceAsync(resolved, connection, token).ConfigureAwait(false);

        // Columns, never the body, newest first, one past the bound so "there were more" needs no count; grouped here
        // rather than in SQL, which measured five times the database's time for the same answer (DeadLetterGroupQueries).
        await using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupRows(
            table,
            KeysFor(resolved),
            events,
            resolved.TenantId,
            DeadLetterGroupLimits.MaxLettersGrouped + 1);
        command.Connection = connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        List<DeadLetterGroupRow> rows = [];

        await using (NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleResult, token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                rows.Add(new DeadLetterGroupRow(
                    Text(reader, 0),
                    Text(reader, 1),
                    Text(reader, 2),
                    Text(reader, 3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    Text(reader, 5),
                    Text(reader, 6),
                    reader.IsDBNull(7) ? null : reader.GetBoolean(7),
                    Text(reader, 8)));
            }
        }

        return DeadLetterGrouping.Group(
            rows,
            DeadLetterGroupLimits.MaxLettersGrouped,
            DeadLetterGroupLimits.MaxGroups,
            readAt,
            recordsSkips: events?.Table.HasIsSkipped == true);

        static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    /// <summary>
    /// The groups as this visitor may see them: every group's tenants counted, and named only where this visitor's store
    /// policy passes that tenant (D29) - asked once per tenant, with the principal fetched once, about the store's
    /// registered key and the database's own identity.
    /// </summary>
    private async Task<DeadLetterGroupList> NameVisibleTenantsAsync(
        ResolvedScope resolved,
        DeadLetterGroupList read,
        CancellationToken cancellationToken)
    {
        string[] names = [.. read.Groups.SelectMany(static x => x.TenantNames).Distinct(StringComparer.Ordinal)];

        if (names.Length == 0)
        {
            return read;
        }

        string storeKey = resolved.Registration.Key;
        string identity = resolved.Database.Id.Identity;

        HashSet<string> visible = new(
            await authorization
                .FilterAsync(names, tenant => new StudioScope(storeKey, identity, tenant), take: null, cancellationToken)
                .ConfigureAwait(false),
            StringComparer.Ordinal);

        if (visible.Count == names.Length)
        {
            return read;
        }

        return read with
        {
            Groups = [.. read.Groups.Select(group => group with { TenantNames = [.. group.TenantNames.Where(visible.Contains)] })],
        };
    }

    /// <summary>One group's events not skipped yet, in this scope, lowest first, one past the cap.</summary>
    private async Task<long[]> ReadGroupSequencesAsync(
        ResolvedScope resolved,
        NpgsqlConnection connection,
        DocumentTableInfo deadLetters,
        EventTableInfo table,
        DeadLetterGroupKey group,
        CancellationToken token)
    {
        await using NpgsqlCommand command = DeadLetterGroupQueries.BuildGroupSequencesToSkip(
            deadLetters,
            KeysFor(resolved),
            new DeadLetterEventSource(table, resolved.Store.Options.Events.UseTenantPartitionedEvents),
            resolved.TenantId,
            group,
            DeadLetterGroupLimits.MaxEventsPerSkip + 1);
        command.Connection = connection;
        command.CommandTimeout = CommandTimeoutSeconds;

        List<long> sequences = [];

        await using (NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(CommandBehavior.SingleResult, token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                sequences.Add(reader.GetInt64(0));
            }
        }

        return [.. sequences];
    }
}
