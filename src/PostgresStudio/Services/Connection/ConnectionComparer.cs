using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Connection;

/// <summary>
/// Sets a pasted connection string's settings beside the ones in effect (1.1, PASTE). Pure: both sides are redacted records already,
/// and the advice is the Connection page's own hints, evaluated for each side by <see cref="ConnectionPageBuilder" /> against the
/// <i>same</i> observed facts and platform - the ones of the connection in effect - so the only thing that differs is the settings.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why only the hints.</b> The advisor's findings are the sweep's, about the data source the host registered; they cannot be run
/// on settings that are not one. The page says so beside the advice.
/// </para>
/// <para>
/// <b>What "differs" means.</b> The value in effect, whichever way it got there: a setting the pasted string spells out at Npgsql's
/// default is not a difference. A password is never compared (the data source's own cannot be seen), and <c>Options</c> by the
/// names it sets, because a pasted string's option values are never shown.
/// </para>
/// </remarks>
internal static class ConnectionComparer
{
    private const string IdPrefix = "conn-cmp-";

    private static readonly SessionDetailsAccess NoActivity = new(false, false, null, DatabaseRefusal.CapabilityOff, null);

    /// <summary>Compares <paramref name="pasted" /> with <paramref name="current" />.</summary>
    /// <param name="current">The connection in effect: its settings, what the server saw and the platform.</param>
    /// <param name="pasted">A pasted string that was read (<see cref="PastedConnectionOutcome.Read" />).</param>
    /// <param name="now">When.</param>
    public static ConnectionCompareView Compare(ConnectionFacts current, PastedConnection pasted, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(pasted);

        if (pasted.Settings is not { } settings)
        {
            throw new ArgumentException("Only a pasted string that was read can be compared.", nameof(pasted));
        }

        // The pasted settings are judged against what the server told the studio about the connection in effect.
        ConnectionFacts pastedFacts = current with { Settings = settings, Sessions = null, Siblings = null, SiblingsNotObserved = null };

        ConnectionPageView before = ConnectionPageBuilder.Build(Inputs(current, now));
        ConnectionPageView after = ConnectionPageBuilder.Build(Inputs(pastedFacts, now));

        Dictionary<string, ConnectionSettingRow> inEffect = [];

        if (current.Settings.Readable)
        {
            foreach (ConnectionSettingView view in before.Settings)
            {
                inEffect[view.Row.Keyword] = view.Row;
            }
        }

        List<ConnectionCompareRow> rows =
        [
            .. after.Settings.Select(view =>
            {
                ConnectionSettingRow? was = inEffect.GetValueOrDefault(view.Row.Keyword);

                return new ConnectionCompareRow(
                    view.Row.Keyword,
                    view.Row.Category,
                    IdPrefix + "row-" + ConnectionPageBuilder.Slug(view.Row.Keyword),
                    was,
                    view.Row,
                    was is null || Differs(was, view.Row, current.Settings, settings),
                    view.AdviceCard is { Counts: true } card ? IdPrefix + card.Id : null);
            }),
        ];

        // The same advice is the same id saying the same thing: a hint that was a note about a default and is now a warning about a
        // pooler has one id and two headings, and the second is news.
        HashSet<string> had = [.. before.Cards.Concat(before.Notes).Select(Key)];
        HashSet<string> has = [.. after.Cards.Concat(after.Notes).Select(Key)];

        return new ConnectionCompareView
        {
            Outcome = ConnectionCompareOutcome.Compared,
            CurrentReadable = current.Settings.Readable,
            NpgsqlVersion = settings.NpgsqlVersion,
            RemovedSecrets = pasted.RemovedSecrets,
            Rows = rows,
            Cards = [.. after.Cards.Select(card => Cover(card, had))],
            Notes = [.. after.Notes.Where(static x => !x.AllSettingsOnly && x.Kind == ConnectionCardKind.Note).Where(x => !had.Contains(Key(x))).Select(card => Cover(card, had))],
            Resolved = [.. before.Cards.Where(x => !has.Contains(Key(x))).Select(static x => x.Heading)],
        };
    }

    private static string Key(ConnectionCard card) => card.Id + "|" + card.Heading;

    private static ConnectionPageInputs Inputs(ConnectionFacts facts, DateTimeOffset now) => new()
    {
        Facts = facts,
        Activity = NoActivity,
        MeasuredAt = now,
    };

    private static ConnectionCompareCard Cover(ConnectionCard card, HashSet<string> had) =>
        new(card with { Id = IdPrefix + card.Id }, had.Contains(Key(card)) ? ConnectionCompareChange.Unchanged : ConnectionCompareChange.New);

    private static bool Differs(ConnectionSettingRow was, ConnectionSettingRow now, ConnectionSettings current, ConnectionSettings pasted)
    {
        // The data source's own password cannot be seen, so there is nothing to compare a pasted one with.
        if (was.Origin == ConnectionSettingOrigin.NotVisible || now.Origin == ConnectionSettingOrigin.NotVisible)
        {
            return false;
        }

        if (now.Exposure == NpgsqlSettingExposure.Options)
        {
            return !(current.Options?.Names ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(pasted.Options?.Names ?? []);
        }

        return !string.Equals(was.Value, now.Value, StringComparison.Ordinal);
    }
}
