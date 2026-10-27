using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace MartenStudio.Services.Events;

/// <summary>The bounds of the grouped dead-letter view and of a bulk skip (M3).</summary>
internal static class DeadLetterGroupLimits
{
    /// <summary>
    /// The most events one "Skip this group" marks skipped. A larger group takes more than one action, each one audited,
    /// and the page says so before the first.
    /// </summary>
    public const int MaxEventsPerSkip = 1_000;

    /// <summary>The most groups the view lists; the rest are counted, not listed.</summary>
    public const int MaxGroups = 200;

    /// <summary>
    /// The most letters one read groups, newest first. A database with more is grouped by its newest, and the view says
    /// so: about 350 ms of reading and fingerprinting at this bound, measured (M3), once per interval across circuits.
    /// </summary>
    public const int MaxLettersGrouped = 20_000;

    /// <summary>The most tenant names a group carries out of the database; the rest are counted.</summary>
    public const int MaxTenantNames = 50;

    /// <summary>How much of a sample message is read, in characters.</summary>
    public const int SampleLength = 500;
}

/// <summary>The grouped view's sentences, in one place so a test can hold the page to them.</summary>
internal static class DeadLetterGroupText
{
    /// <summary>What the grouped view says it is.</summary>
    public const string Intro =
        "Dead letters grouped by cause: the projection and shard, the type of the event, the exception type and the "
        + "message with its numbers, ids, quoted strings, timestamps and hex runs set aside. Open a group to see its "
        + "letters in the list, with every action a letter has there.";

    /// <summary>What a group whose events are all skipped, or none of which this scope can see, says on its button.</summary>
    public const string NothingLeft =
        "Every event of this group that this scope can see is skipped already, or no longer in mt_events.";

    /// <summary>
    /// What "Skip this group" says on a store that numbers events per tenant when the scope names no tenant and the group's
    /// letters do not name exactly one this visitor may see.
    /// </summary>
    public static string SkipNeedsOneTenant(long tenants) =>
        "This store numbers events per tenant (UseTenantPartitionedEvents), so a skip is one tenant's, and this group's "
        + "letters name " + Tenants(tenants) + ": choose a tenant in the header to skip the group's events in that tenant.";

    /// <summary>
    /// What the grouped view says when there were more letters than one read groups - and the list opened on a group, whose
    /// letters are found among the same newest ones (SEC-FIX).
    /// </summary>
    public static readonly string LettersCut =
        "There are more than " + DeadLetterGroupLimits.MaxLettersGrouped.ToString("N0", CultureInfo.InvariantCulture)
        + " dead letters here: the groups are those of the newest "
        + DeadLetterGroupLimits.MaxLettersGrouped.ToString("N0", CultureInfo.InvariantCulture)
        + ", their counts are counts of those, and a group opened in the list shows its letters among them. Skipping a group "
        + "reads all of its letters.";

    /// <summary>What a group larger than one action says.</summary>
    public static string CapNote(long notSkipped) =>
        "One action skips at most " + DeadLetterGroupLimits.MaxEventsPerSkip.ToString("N0", CultureInfo.InvariantCulture)
        + " events, the lowest sequence numbers first; this group has "
        + notSkipped.ToString("N0", CultureInfo.InvariantCulture) + " not skipped yet.";

    private static string Tenants(long count) =>
        count == 0 ? "no tenant" : count.ToString("N0", CultureInfo.InvariantCulture) + (count == 1 ? " tenant" : " tenants");
}

/// <summary>
/// What makes dead letters one group: the projection and shard that failed, the type of the event they failed on, the
/// exception type and the message's fingerprint (<see cref="DeadLetterFingerprint" />).
/// </summary>
/// <remarks>
/// <para>
/// Each part is nullable, because each is read from a document or a join that may not have it: a letter Marten wrote for
/// an event it could not deserialize, a letter whose event is no longer in <c>mt_events</c> (no event type), a host's own
/// letter with no message. A missing part is a value of the key, matched with <c>is not distinct from</c>.
/// </para>
/// <para>
/// <b>The event type is not on the letter.</b> <c>DeadLetterEvent</c> records the event's sequence and tenant, not its
/// type (JasperFx 2.74.0 and 2.95.1), so the type is <c>mt_events.type</c> of that event, read in the scope's tenant:
/// another tenant's event never lends its type to a letter, and a letter whose event the scope cannot see has none.
/// </para>
/// </remarks>
/// <param name="ProjectionName">The letter's <c>ProjectionName</c>.</param>
/// <param name="ShardName">The letter's <c>ShardName</c> - Marten's shard key, such as <c>All</c>.</param>
/// <param name="EventType">The failing event's type alias, or <see langword="null" /> when the event is not there.</param>
/// <param name="ExceptionType">The letter's <c>ExceptionType</c>.</param>
/// <param name="Fingerprint">The fingerprint of the letter's <c>ExceptionMessage</c>.</param>
internal sealed record DeadLetterGroupKey(
    string? ProjectionName,
    string? ShardName,
    string? EventType,
    string? ExceptionType,
    string? Fingerprint)
{
    /// <summary>The longest part a <c>?group=</c> value may carry. Longer is not a key this page wrote.</summary>
    public const int MaxPartLength = 4_000;

    /// <summary>The key as one opaque <c>?group=</c> value (D9: a pasted link reopens the same group).</summary>
    /// <remarks>
    /// Opaque for <c>DocumentLinks.Encode</c>'s reason: every part is arbitrary text, a fingerprint has spaces, quotes and
    /// braces in it, and a missing part differs from an empty one. Base64url of length-prefixed parts carries all of it.
    /// </remarks>
    public string Encode()
    {
        var builder = new StringBuilder();

        foreach (string? part in Parts())
        {
            if (part is null)
            {
                builder.Append('n');
            }
            else
            {
                builder.Append('v').Append(part.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(part);
            }
        }

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    /// <summary>Reads a <c>?group=</c> value, or <see langword="null" /> when it is not one this page wrote.</summary>
    /// <param name="value">The query-string value - which a circuit can make anything.</param>
    public static DeadLetterGroupKey? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 5 * (MaxPartLength + 16) * 2)
        {
            return null;
        }

        try
        {
            string text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value));
            string?[] parts = new string?[5];
            int index = 0;

            for (int part = 0; part < parts.Length; part++)
            {
                if (index >= text.Length)
                {
                    return null;
                }

                char marker = text[index++];

                if (marker == 'n')
                {
                    parts[part] = null;
                    continue;
                }

                int colon = marker == 'v' ? text.IndexOf(':', index) : -1;

                if (colon <= index
                    || !int.TryParse(text.AsSpan(index, colon - index), NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                    || length > MaxPartLength
                    || colon + 1 + length > text.Length)
                {
                    return null;
                }

                parts[part] = text.Substring(colon + 1, length);
                index = colon + 1 + length;
            }

            return index == text.Length ? new DeadLetterGroupKey(parts[0], parts[1], parts[2], parts[3], parts[4]) : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The key as one line, for the audit entry and the list's filter chip: <c>projection:shard, event type, exception
    /// type, "fingerprint"</c>, a missing part named as missing.
    /// </summary>
    public string Describe() =>
        (ProjectionName ?? "(no projection)") + ":" + (ShardName ?? "(no shard)")
        + ", event " + (EventType ?? "(not in mt_events)")
        + ", " + (ExceptionType ?? "(no exception type)")
        + ", " + (Fingerprint is null ? "(no message)" : "\"" + Fingerprint + "\"");

    private IEnumerable<string?> Parts()
    {
        yield return ProjectionName;
        yield return ShardName;
        yield return EventType;
        yield return ExceptionType;
        yield return Fingerprint;
    }
}

/// <summary>One group of the grouped view.</summary>
/// <param name="Key">What the group's letters share.</param>
/// <param name="Letters">How many dead letters it holds.</param>
/// <param name="Events">
/// How many distinct events they name that the scope can see in <c>mt_events</c> - a letter whose event is gone, or
/// another tenant's, counts as a letter and not as an event.
/// </param>
/// <param name="EventsSkipped">How many of those events are already skipped; <see langword="null" /> when the store has no <c>is_skipped</c>.</param>
/// <param name="FirstSeen">The oldest letter's <c>Timestamp</c>.</param>
/// <param name="LastSeen">The newest letter's <c>Timestamp</c>.</param>
/// <param name="TenantCount">How many tenants its letters name.</param>
/// <param name="TenantNames">
/// The names of those tenants this visitor's store policy passes (D29: a tenant's name needs that tenant's store policy),
/// in order; never more than <see cref="DeadLetterGroupLimits.MaxTenantNames" />.
/// </param>
/// <param name="SampleMessage">One letter's message, cut at <see cref="DeadLetterGroupLimits.SampleLength" /> characters.</param>
internal sealed record DeadLetterGroup(
    DeadLetterGroupKey Key,
    long Letters,
    long Events,
    long? EventsSkipped,
    DateTimeOffset? FirstSeen,
    DateTimeOffset? LastSeen,
    long TenantCount,
    IReadOnlyList<string> TenantNames,
    string? SampleMessage)
{
    /// <summary>The events not yet skipped, when the store records skips.</summary>
    public long? EventsNotSkipped => EventsSkipped is { } skipped ? Math.Max(0, Events - skipped) : null;

    /// <summary>How many tenants are counted and not named to this visitor.</summary>
    public long TenantsNotNamed => Math.Max(0, TenantCount - TenantNames.Count);
}

/// <summary>The grouped view's read: the groups, largest first, and what was left out.</summary>
/// <param name="Groups">At most <see cref="DeadLetterGroupLimits.MaxGroups" /> groups, by letters, then newest.</param>
/// <param name="TotalGroups">How many groups the letters read fall into.</param>
/// <param name="TotalLetters">How many letters were grouped.</param>
/// <param name="ReadAt">When the database was read - the view may be up to one refresh interval old (D10).</param>
/// <param name="Error">Why it could not be read, when it could not.</param>
internal sealed record DeadLetterGroupList(
    IReadOnlyList<DeadLetterGroup> Groups,
    long TotalGroups,
    long TotalLetters,
    DateTimeOffset? ReadAt,
    EventDataError? Error = null)
{
    /// <summary>Nothing to group: no table, or no letters.</summary>
    public static DeadLetterGroupList Empty { get; } = new([], 0, 0, null);

    /// <summary>The host hides <c>DeadLetterEvent</c>, so nothing was read.</summary>
    public static DeadLetterGroupList Hidden { get; } = new([], 0, 0, null) { IsHidden = true };

    /// <summary>A read that failed.</summary>
    public static DeadLetterGroupList Failed(EventDataError error) => new([], 0, 0, null, error);

    /// <summary>Whether the host hides <c>DeadLetterEvent</c>.</summary>
    public bool IsHidden { get; init; }

    /// <summary>Whether there are more groups than are listed.</summary>
    public bool IsTruncated => TotalGroups > Groups.Count;

    /// <summary>
    /// Whether there were more letters than one read groups (<see cref="DeadLetterGroupLimits.MaxLettersGrouped" />), so the
    /// groups are the newest letters' and their counts are lower bounds.
    /// </summary>
    public bool LettersTruncated { get; init; }

    /// <summary>Whether the store records skipped events (<c>mt_events.is_skipped</c>), so "Skip this group" can do anything.</summary>
    public bool RecordsSkips { get; init; }
}

/// <summary>What "Skip this group" would do, asked when its dialog opens.</summary>
/// <param name="ToSkip">How many events this action marks skipped: the group's events not yet skipped, at most <see cref="DeadLetterGroupLimits.MaxEventsPerSkip" />.</param>
/// <param name="NotYetSkipped">How many of the group's events are not skipped yet, in all.</param>
/// <param name="AlreadySkipped">How many are skipped already.</param>
/// <param name="TenantId">The tenant whose events are skipped, when the store numbers events per tenant.</param>
/// <param name="Refusal">Why it would be refused, when it would - the sentence the service refuses with.</param>
internal sealed record DeadLetterGroupSkipPlan(
    long ToSkip,
    long NotYetSkipped,
    long AlreadySkipped,
    string? TenantId,
    string? Refusal = null)
{
    /// <summary>A plan that would be refused, and why.</summary>
    public static DeadLetterGroupSkipPlan Refused(string refusal) => new(0, 0, 0, null, refusal);

    /// <summary>Whether the group has more events to skip than one action takes.</summary>
    public bool OverCap => NotYetSkipped > ToSkip;

    /// <summary>The typed confirmation: the number of events this action skips.</summary>
    public string ConfirmationPhrase => ToSkip.ToString(CultureInfo.InvariantCulture);
}
