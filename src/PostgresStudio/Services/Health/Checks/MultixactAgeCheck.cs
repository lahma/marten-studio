using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>One of the store's tables and its oldest multixact age.</summary>
/// <param name="Table">The declared table.</param>
/// <param name="Age">The oldest <c>mxid_age(relminmxid)</c> over it, its TOAST table and its partitions.</param>
internal sealed record TableMultixactAge(StudioTable Table, long Age);

/// <summary>What T13 reads.</summary>
/// <param name="Database">The database-wide facts: the multixact settings, ages and counter.</param>
/// <param name="Tables">The store's tables that exist and have an age, a hidden document type's never among them.</param>
/// <param name="HealthInterval">The history's cadence, which sizes how long a forecast can be held; a minute when unknown.</param>
internal sealed record MultixactAgeFacts(HealthWraparoundDatabase Database, IReadOnlyList<TableMultixactAge> Tables, TimeSpan? HealthInterval = null);

/// <summary>
/// T13: the database, or one of Marten's tables, is far into Postgres' multixact-id range - or, at the rate it is
/// creating multixacts, soon will be.
/// </summary>
/// <remarks>
/// <para>
/// A multixact is the id Postgres gives a set of transactions that lock one row at once. They are numbered with 32-bit
/// ids like transactions, frozen by vacuum like transactions, and wrap round the same way: autovacuum forces an
/// aggressive vacuum of a table at <c>autovacuum_multixact_freeze_max_age</c> (400 million by default),
/// <c>VACUUM</c> goes into failsafe mode at <c>vacuum_multixact_failsafe_age</c> (PostgreSQL 14+, 1.6 billion), and a
/// margin short of 2^31 Postgres refuses to create new multixacts - which blocks every write that needs a row lock
/// shared with another transaction, though not every write (the documentation's "Routine Vacuuming").
/// </para>
/// <para>
/// <b>Where they come from, and where they do not.</b> A shared row lock - <c>FOR SHARE</c>, <c>FOR KEY SHARE</c>, the
/// lock a foreign-key check takes on the row it references - taken by two transactions at once is a multixact; so
/// concurrent inserts of documents whose type declares a foreign key to one parent row make them. Marten's own event
/// appends do <em>not</em>, measured at 9.39 and 9.50.2 (<c>HorizonServerLiveTests</c>): concurrent Quick appends to one
/// stream serialise on <c>pk_mt_events_stream_and_version</c>, and every loser fails there, before the foreign-key
/// check to <c>mt_streams</c> that would share the stream's row ever runs.
/// </para>
/// <para>
/// <b>The lines.</b> As T12's, from the server's own setting: a Warning <see cref="Wraparound.WarningShare" /> of the
/// way from <c>autovacuum_multixact_freeze_max_age</c> to wraparound, Critical at
/// <see cref="Wraparound.CriticalShare" /> or the failsafe age, whichever comes first. Each sweep samples the next
/// multixact id (<c>datminmxid + mxid_age(datminmxid)</c> - shared memory, no privilege), so the same forecast T12 makes
/// is made here: past the forced line, the time to the refusal line at the rate of the last N minutes.
/// </para>
/// <para>
/// One finding for the database, like T12; only the store's visible tables are named.
/// </para>
/// </remarks>
[HealthCheck("T13", HealthArea.Tables, HealthCostTier.Catalog, "Multixact age")]
internal sealed class MultixactAgeCheck : HealthCheck<MultixactAgeFacts>
{
    /// <summary>How many tables past the line the evidence names, beside the oldest.</summary>
    public const int NamedTables = 5;

    /// <summary>The age past which the check warns, given <c>autovacuum_multixact_freeze_max_age</c>.</summary>
    internal static long WarningAge(long multixactFreezeMaxAge) => Wraparound.WarningAge(multixactFreezeMaxAge);

    /// <summary>
    /// The age past which the finding is Critical: two-thirds of the way to wraparound, or the failsafe age if that comes
    /// first.
    /// </summary>
    internal static long CriticalAge(HealthWraparoundDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        long line = Wraparound.CriticalAge(database.MultixactFreezeMaxAge);
        return Wraparound.EffectiveFailsafe(database.MultixactFailsafeAge, database.MultixactFreezeMaxAge) is { } failsafe
            ? Math.Min(line, failsafe)
            : line;
    }

    /// <inheritdoc />
    public override async Task<MultixactAgeFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IStudioTableSet set = await HealthTables.SetAsync(context).ConfigureAwait(false);
        WraparoundFacts wraparound = await Wraparound.SharedAsync(context).ConfigureAwait(false);

        List<TableMultixactAge> tables = [];
        foreach (HealthWraparoundTable row in wraparound.Tables)
        {
            if (row.MultixactAge is { } age && set.Find(row.Schema, row.Table) is { } table)
            {
                tables.Add(new TableMultixactAge(table, age));
            }
        }

        return new MultixactAgeFacts(wraparound.Database, tables, wraparound.Interval);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(MultixactAgeFacts facts)
    {
        yield return new HealthSamplePart(Wraparound.MultixactCounterSeries, facts.Database.MultixactCounter);
        yield return new HealthSamplePart(Wraparound.MultixactSweptSeries, 1);

        if (OldestAge(facts) > facts.Database.MultixactFreezeMaxAge)
        {
            yield return new HealthSamplePart(Wraparound.MultixactPastForcedSeries, 1);
        }
    }

    /// <summary>The oldest multixact age the facts hold: the store's tables', this database's and the server's oldest.</summary>
    internal static long OldestAge(MultixactAgeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        long tables = facts.Tables.Count == 0 ? 0 : facts.Tables.Max(static x => x.Age);

        return Math.Max(tables, Math.Max(
            Math.Max(facts.Database.DatabaseMultixactAge, facts.Database.OtherOldestMultixactAge ?? 0),
            facts.Database.ServerMultixactAge));
    }

    /// <summary>The forecast the history supports for the multixact counter, or <see langword="null" />.</summary>
    internal static HorizonForecast? Forecast(HealthHistoryView history) =>
        HorizonForecast.From(history.Trend(Wraparound.MultixactCounterSeries));

    /// <inheritdoc />
    public override HealthVerdict Evaluate(MultixactAgeFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        HealthWraparoundDatabase database = facts.Database;

        List<TableMultixactAge> aged = [.. facts.Tables
            .OrderByDescending(static x => x.Age)
            .ThenBy(static x => x.Table.Key, StringComparer.Ordinal)];

        TableMultixactAge? oldest = aged.Count > 0 ? aged[0] : null;
        long tableAge = oldest?.Age ?? 0;
        long databaseAge = Math.Max(database.DatabaseMultixactAge, database.OtherOldestMultixactAge ?? 0);
        long serverAge = database.ServerMultixactAge;
        long age = Math.Max(tableAge, Math.Max(databaseAge, serverAge));

        long forcedLine = database.MultixactFreezeMaxAge;
        long warning = WarningAge(forcedLine);
        long critical = CriticalAge(database);
        long refusal = Wraparound.RefusalAge(database.ServerVersion);

        HorizonForecast? forecast = Forecast(evaluation.History);
        TimeSpan? toForced = forecast?.Until(age, forcedLine);
        TimeSpan? toRefusal = forecast?.Until(age, refusal);
        bool pastForced = age > forcedLine;

        // As T12's: a forecast alone waits for the age to stay past the forced line (follow-up 2).
        TimeSpan pastFor = TableHistory.HeldSince(evaluation.History, Wraparound.MultixactSweptSeries, Wraparound.MultixactPastForcedSeries) is { } since
            ? evaluation.Now - since
            : TimeSpan.Zero;
        TimeSpan persistFor = Wraparound.ForecastPersistFor(facts.HealthInterval ?? TimeSpan.FromMinutes(1));
        bool forecastHeld = pastForced && toRefusal is not null && pastFor >= persistFor;

        if (age <= warning && !forecastHeld)
        {
            string note = QuietNote(oldest, database, forecast, toForced, toRefusal, pastForced, evaluation.Text);

            return HealthVerdict.Quiet(pastForced && toRefusal is { } soon
                ? note + " " + Capitalised(forecast!.AtTheRate) + ", new multixacts would be refused in " + HorizonForecast.Span(soon)
                  + "; it has been past autovacuum_multixact_freeze_max_age for " + TableText.Span(pastFor) + " of samples, and a "
                  + "forecast alone is a finding once it has been for " + Wraparound.PersistText(persistFor) + "."
                : note);
        }

        string whose = tableAge >= Math.Max(databaseAge, serverAge) && oldest is not null
            ? oldest.Table.NoteName
            : databaseAge >= serverAge ? "this database (a table that is " + evaluation.Text.NotJudged + ")" : "another database on this server";

        bool forecastOnly = age <= warning;
        HealthSeverity severity = age > critical || (toRefusal is { } left && left <= HorizonForecast.CriticalWithin)
            ? HealthSeverity.Critical
            : HealthSeverity.Warning;

        List<TableMultixactAge> past = [.. aged.Where(x => x.Age > warning)];

        List<HealthEvidence> evidence = [];

        if (oldest is not null)
        {
            evidence.Add(new HealthEvidence("Oldest table", oldest.Table.DescribeUngated()));
            evidence.Add(new HealthEvidence("Its mxid_age(relminmxid)", Wraparound.Number(oldest.Age)));
        }

        evidence.Add(new HealthEvidence("This database's mxid_age(datminmxid)", Wraparound.Number(database.DatabaseMultixactAge)));

        if (database.ServerMultixactAge > database.DatabaseMultixactAge)
        {
            evidence.Add(new HealthEvidence("Oldest database on this server, mxid_age(datminmxid)", Wraparound.Number(database.ServerMultixactAge)));
        }

        evidence.Add(new HealthEvidence("autovacuum_multixact_freeze_max_age", Wraparound.Number(forcedLine)));
        evidence.Add(new HealthEvidence("vacuum_multixact_failsafe_age", database.MultixactFailsafeAge is { } failsafe
            ? Wraparound.Number(failsafe)
            : "not on this server (PostgreSQL 14+)"));
        evidence.Add(new HealthEvidence("Warning and Critical lines", Wraparound.Number(warning) + " and " + Wraparound.Number(critical)));
        evidence.Add(new HealthEvidence(evaluation.Text.JudgedTables + " past the warning line", Wraparound.Number(past.Count)));

        if (past.Count > 0)
        {
            evidence.Add(new HealthEvidence("The oldest of them", string.Join(", ", past.Take(NamedTables)
                .Select(static x => x.Table.NoteName + " (" + Wraparound.Number(x.Age) + ")"))));
        }

        evidence.Add(new HealthEvidence("Other tables past autovacuum_multixact_freeze_max_age", Wraparound.Number(database.OtherPastForcedMultixact)
            + (database.OtherTemporaryPastForcedMultixact > 0
                ? " (" + Wraparound.Number(database.OtherTemporaryPastForcedMultixact) + " of them temporary)"
                : string.Empty)
            + ", never named: they are " + evaluation.Text.NotJudged));

        if (forecast is not null)
        {
            evidence.Add(new HealthEvidence("Multixacts created", forecast.RateText("multixacts")));

            if (toForced is { } forced && forced > TimeSpan.Zero)
            {
                evidence.Add(new HealthEvidence("Autovacuum forces a freeze in", HorizonForecast.Span(forced) + ", " + forecast.AtTheRate));
            }

            if (toRefusal is { } refused)
            {
                evidence.Add(new HealthEvidence("New multixacts refused in", HorizonForecast.Span(refused) + ", " + forecast.AtTheRate
                    + ", if nothing is frozen first"));
            }
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "T13",
            Severity = severity,
            DocumentTypes = [.. past.SelectMany(static x => x.Table.DocumentTypes).Distinct()],

            // No GatedSchemas: a database-wide wraparound warning must not vanish because the oldest table is one in a
            // schema the browser withholds, so such a table is named generically (StudioTableText.NoteName) throughout.
            Title = forecastOnly
                ? Capitalised(forecast!.AtTheRate) + ", Postgres would stop creating multixacts in " + HorizonForecast.Span(toRefusal!.Value)
                  + ": " + whose + " is past autovacuum_multixact_freeze_max_age and has not been frozen"
                : (oldest is not null && whose == oldest.Table.Key ? whose : Capitalised(whose)) + " is " + Wraparound.Count(age)
                  + " multixacts from its last freeze; Postgres stops creating them at about 2.1 billion",
            WhyItMatters =
                "A multixact is the id Postgres gives a set of transactions that lock one row at the same time, and like a transaction "
                + "id it is 32 bits wide and must be frozen by vacuum before the counter wraps round. Autovacuum forces an aggressive "
                + "vacuum at autovacuum_multixact_freeze_max_age, which this server sets to " + Wraparound.Number(forcedLine)
                + "; a table this far behind is one that vacuum has not managed to freeze - usually because a long transaction that "
                + "is itself a member of a multixact, or a prepared one, keeps the oldest multixact alive (T14 names long transactions; "
                + "replication slots do not hold multixacts back). Past the line Postgres refuses every write that needs a row lock "
                + "shared with another transaction - concurrent foreign-key checks against one row, SELECT ... FOR SHARE - until a "
                + "VACUUM has run, though not writes that need no such lock. A DBA's VACUUM (FREEZE) of these tables, once that "
                + "transaction is gone, is the cure.",
            Evidence = evidence,
            Caveat =
                "Read from pg_class.relminmxid of " + evaluation.Text.JudgedTablesRead + ", their TOAST tables and their partitions, and from "
                + "pg_database.datminmxid of this database and the server's oldest. Tables that are " + evaluation.Text.NotJudged + " are counted and "
                + "never named. Multixact member storage, which can run out before the ids do, is not read: Postgres has no view of it."
                + (forecast is null
                    ? " No forecast yet: a rate needs two samples, an interval apart, in which multixacts were created."
                    : " " + forecast.Caveat("the next multixact id")),
        });
    }

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string QuietNote(
        TableMultixactAge? oldest,
        HealthWraparoundDatabase database,
        HorizonForecast? forecast,
        TimeSpan? toForced,
        TimeSpan? toRefusal,
        bool pastForced,
        StudioText text)
    {
        List<string> parts = [];

        if (oldest is not null)
        {
            parts.Add("The oldest " + text.JudgedTable + ", " + oldest.Table.NoteName + ", is " + Wraparound.Number(oldest.Age) + " multixacts from its last freeze.");
        }

        parts.Add("This database's mxid_age(datminmxid) is " + Wraparound.Number(database.DatabaseMultixactAge) + ".");

        if (forecast is null)
        {
            parts.Add("No multixact rate yet: it needs two samples, an interval apart, in which multixacts were created.");
        }
        else if (pastForced)
        {
            // Fix 3: "more than 90 days" only when the forecast is past its horizon; otherwise the caller says when.
            parts.Add(toRefusal is null
                ? "It is past autovacuum_multixact_freeze_max_age, and " + forecast.AtTheRate + " the refusal line is more than "
                  + ((int) HorizonForecast.Horizon.TotalDays).ToString(CultureInfo.InvariantCulture) + " days away."
                : "It is past autovacuum_multixact_freeze_max_age.");
        }
        else
        {
            parts.Add(Capitalised(forecast.RateText("multixacts")) + "; "
                + (toForced is { } forced
                    ? "autovacuum forces a freeze in " + HorizonForecast.Span(forced) + " - routine, and nothing to act on."
                    : "the forced freeze is more than " + ((int) HorizonForecast.Horizon.TotalDays).ToString(CultureInfo.InvariantCulture) + " days away."));
        }

        return string.Join(" ", parts);
    }
}
