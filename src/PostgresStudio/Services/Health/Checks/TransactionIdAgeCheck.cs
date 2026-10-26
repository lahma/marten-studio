using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T12 reads: every declared table's oldest transaction-id age, and the database's.</summary>
/// <param name="Tables">The declared tables that exist.</param>
/// <param name="Wraparound">
/// The database-wide read - <c>age(datfrozenxid)</c> here and on the server, the counter, the other tables counted - or
/// <see langword="null" /> for facts that predate it (a unit test of the per-table half alone).
/// </param>
internal sealed record TransactionIdAgeFacts(IReadOnlyList<TableFacts> Tables, WraparoundFacts? Wraparound = null);

/// <summary>
/// T12: the database, or one of Marten's tables in it, is far into Postgres' transaction-id range and needs freezing -
/// or, at the rate it is spending transaction ids, soon will be.
/// </summary>
/// <remarks>
/// <para>
/// Postgres numbers transactions with 32-bit ids, so every row's id must be "frozen" by vacuum before the counter
/// wraps; <c>age(relfrozenxid)</c> is how far a table is from its last complete freeze, and the database's
/// <c>age(datfrozenxid)</c> - the oldest of every table in it, the store's or not - is what wraparound protection acts on.
/// Autovacuum forces an anti-wraparound vacuum at <c>autovacuum_freeze_max_age</c> (200 million by default), and a
/// margin short of 2^31 Postgres stops accepting writes to every database on the server.
/// </para>
/// <para>
/// <b>The lines are the server's own, not fixed numbers</b> (H4-fix, kept): a Warning <see cref="WarningShare" /> of the
/// way from <c>autovacuum_freeze_max_age</c> to wraparound, Critical <see cref="CriticalShare" /> - about 1.0 and 1.5
/// billion at the default, and much later on a server whose DBA raised the setting.
/// </para>
/// <para>
/// <b>The whole database counts.</b> The age judged is the oldest of the store's tables, this database's
/// <c>datfrozenxid</c> and the server's oldest database - so a never-frozen legacy table, a Quartz job store or a
/// temporary table holds the finding up as much as a Marten table does. Only the store's own, visible tables are
/// named; every other table is counted, never named (D27), and a temporary table is called out because autovacuum
/// cannot freeze one: only the session that created it can.
/// </para>
/// <para>
/// <b>And a forecast.</b> Each sweep samples <c>pg_snapshot_xmax(pg_current_snapshot())</c> - the next transaction id,
/// read without assigning one - so two samples give the rate the database spends ids at, and
/// <see cref="HorizonForecast" /> the time to the forced-vacuum line and to the write-refusal line at that rate, worded
/// "at the rate of the last N minutes". Past the forced line, a refusal within 90 days at that rate is a finding even
/// below the Warning age - autovacuum has been asked to freeze and has not finished - and within seven days a Critical
/// one. Before the forced line, the time to it is a status note: a forced freeze is routine.
/// </para>
/// </remarks>
[HealthCheck("T12", HealthArea.Tables, HealthCostTier.Catalog, "Transaction-id age")]
internal sealed class TransactionIdAgeCheck : HealthCheck<TransactionIdAgeFacts>
{
    /// <summary>Where the transaction-id counter wraps: <c>2^31</c>, about 2.1 billion.</summary>
    public const long WraparoundAge = Wraparound.WraparoundAge;

    /// <summary>How far from <c>autovacuum_freeze_max_age</c> to wraparound the oldest table must be for a Warning.</summary>
    public const double WarningShare = Wraparound.WarningShare;

    /// <summary>How far from <c>autovacuum_freeze_max_age</c> to wraparound the oldest table must be for a Critical.</summary>
    public const double CriticalShare = Wraparound.CriticalShare;

    /// <summary>How many tables past the line the evidence names, beside the oldest.</summary>
    public const int NamedTables = 5;

    /// <summary>The age past which the check warns, given the server's <c>autovacuum_freeze_max_age</c>.</summary>
    internal static long WarningAge(long freezeMaxAge) => Wraparound.WarningAge(freezeMaxAge);

    /// <summary>The age past which the finding is Critical, given the server's <c>autovacuum_freeze_max_age</c>.</summary>
    internal static long CriticalAge(long freezeMaxAge) => Wraparound.CriticalAge(freezeMaxAge);

    /// <inheritdoc />
    public override async Task<TransactionIdAgeFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<TableFacts> tables = await HealthTables.TablesAsync(context).ConfigureAwait(false);
        WraparoundFacts wraparound = await Wraparound.SharedAsync(context).ConfigureAwait(false);

        return new TransactionIdAgeFacts(tables, wraparound);
    }

    /// <inheritdoc />
    public override IEnumerable<HealthSamplePart> Sample(TransactionIdAgeFacts facts)
    {
        if (facts.Wraparound is { } wraparound)
        {
            HealthWraparoundDatabase database = wraparound.Database;

            yield return new HealthSamplePart(Wraparound.XidCounterSeries, database.XidCounter);
            yield return new HealthSamplePart(Wraparound.XidSweptSeries, 1);

            if (OldestAge(facts) > database.FreezeMaxAge)
            {
                yield return new HealthSamplePart(Wraparound.XidPastForcedSeries, 1);
            }
        }
    }

    /// <summary>The oldest age the facts hold: the store's tables', this database's and the server's oldest database's.</summary>
    internal static long OldestAge(TransactionIdAgeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        long tables = facts.Tables.Max(static x => x.Row.OldestXidAge) ?? 0;
        HealthWraparoundDatabase? database = facts.Wraparound?.Database;

        return Math.Max(tables, Math.Max(
            Math.Max(database?.DatabaseXidAge ?? 0, database?.OtherOldestXidAge ?? 0),
            database?.ServerXidAge ?? 0));
    }

    /// <summary>The forecast the history supports for the transaction-id counter, or <see langword="null" />.</summary>
    internal static HorizonForecast? Forecast(HealthHistoryView history) =>
        HorizonForecast.From(history.Trend(Wraparound.XidCounterSeries));

    /// <inheritdoc />
    public override HealthVerdict Evaluate(TransactionIdAgeFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<TableFacts> aged = [.. facts.Tables
            .Where(static x => x.Row.OldestXidAge is not null)
            .OrderByDescending(static x => x.Row.OldestXidAge)
            .ThenBy(static x => x.Key, StringComparer.Ordinal)];

        HealthWraparoundDatabase? database = facts.Wraparound?.Database;

        if (aged.Count == 0 && database is null)
        {
            return HealthVerdict.Quiet(facts.Tables.Count == 0 ? evaluation.Text.NoJudgedTable : null);
        }

        TableFacts? oldest = aged.Count > 0 ? aged[0] : null;
        long tableAge = oldest?.Row.OldestXidAge ?? 0;
        long freezeMaxAge = database?.FreezeMaxAge ?? oldest!.Row.FreezeMaxAge;
        long databaseAge = Math.Max(database?.DatabaseXidAge ?? 0, database?.OtherOldestXidAge ?? 0);
        long serverAge = database?.ServerXidAge ?? 0;
        long age = Math.Max(tableAge, Math.Max(databaseAge, serverAge));

        long warning = WarningAge(freezeMaxAge);
        long critical = CriticalAge(freezeMaxAge);
        long refusal = Wraparound.RefusalAge(database?.ServerVersion ?? oldest?.Row.ServerVersion ?? 170_000);

        HorizonForecast? forecast = database is null ? null : Forecast(evaluation.History);
        TimeSpan? toForced = forecast?.Until(age, freezeMaxAge);
        TimeSpan? toRefusal = forecast?.Until(age, refusal);
        bool pastForced = age > freezeMaxAge;

        // A forecast alone speaks only once the age has stayed past the forced line for ForecastPersistFor of samples: a
        // routine anti-wraparound vacuum, or template0 reaching the line, crosses it and comes back (follow-up 2).
        TimeSpan pastFor = TableHistory.HeldSince(evaluation.History, Wraparound.XidSweptSeries, Wraparound.XidPastForcedSeries) is { } since
            ? evaluation.Now - since
            : TimeSpan.Zero;
        TimeSpan persistFor = Wraparound.ForecastPersistFor(facts.Wraparound?.Interval ?? TimeSpan.FromMinutes(1));
        bool forecastHeld = pastForced && toRefusal is not null && pastFor >= persistFor;

        if (age <= warning && !forecastHeld)
        {
            string note = QuietNote(oldest, tableAge, database, freezeMaxAge, forecast, toForced, toRefusal, pastForced, evaluation.Text);

            return HealthVerdict.Quiet(pastForced && toRefusal is { } soon
                ? note + " " + Capitalised(forecast!.AtTheRate) + ", writes would be refused in " + HorizonForecast.Span(soon)
                  + "; it has been past autovacuum_freeze_max_age for " + TableText.Span(pastFor) + " of samples, and a forecast "
                  + "alone is a finding once it has been for " + Wraparound.PersistText(persistFor)
                  + " - a routine anti-wraparound vacuum ends sooner."
                : note);
        }

        Origin origin = tableAge >= Math.Max(databaseAge, serverAge) && oldest is not null
            ? Origin.StoreTable
            : databaseAge >= serverAge ? Origin.Database : Origin.Server;

        bool forecastOnly = age <= warning;
        HealthSeverity severity = age > critical || (toRefusal is { } left && left <= HorizonForecast.CriticalWithin)
            ? HealthSeverity.Critical
            : HealthSeverity.Warning;

        List<TableFacts> past = [.. aged.Where(x => x.Row.OldestXidAge > warning)];

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "T12",
            Severity = severity,
            DocumentTypes = [.. past.SelectMany(static x => x.Table.DocumentTypes).Distinct()],

            // No GatedSchemas: a database-wide wraparound warning must not vanish because the oldest table is one in a
            // schema the browser withholds, so such a table is named generically (StudioTableText.NoteName) throughout.
            Title = forecastOnly
                ? Capitalised(forecast!.AtTheRate) + ", Postgres would stop accepting writes in " + HorizonForecast.Span(toRefusal!.Value)
                  + ": " + Whose(origin, oldest, evaluation.Text) + " is past autovacuum_freeze_max_age and has not been frozen"
                : (origin == Origin.StoreTable && !oldest!.Table.OutsideOwnSchemas ? oldest.Key : Capitalised(Whose(origin, oldest, evaluation.Text))) + " is " + Wraparound.Count(age)
                  + " transactions from its last freeze; Postgres stops accepting writes at about 2.1 billion",
            WhyItMatters = WhyItMatters(origin, freezeMaxAge, database, evaluation.Text),
            Evidence = Evidence(oldest, tableAge, database, freezeMaxAge, warning, critical, past, forecast, toForced, toRefusal, evaluation.Text),
            Caveat = Caveat(database, forecast, evaluation.Text),
        });
    }

    /// <summary>Which age the finding is about.</summary>
    private enum Origin
    {
        StoreTable,
        Database,
        Server,
    }

    private static string Whose(Origin origin, TableFacts? oldest, StudioText text) => origin switch
    {
        Origin.StoreTable => oldest!.Table.NoteName,
        Origin.Database => "this database (a table that is " + text.NotJudged + ")",
        _ => "another database on this server",
    };

    private static string Capitalised(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string WhyItMatters(Origin origin, long freezeMaxAge, HealthWraparoundDatabase? database, StudioText text)
    {
        string why =
            "Postgres numbers transactions with 32-bit ids, so vacuum must freeze every row before the counter wraps round. "
            + "Autovacuum forces that long before now - at autovacuum_freeze_max_age, which this server sets to "
            + freezeMaxAge.ToString("N0", CultureInfo.InvariantCulture) + " - so a table this far behind is one autovacuum has "
            + "not managed to freeze: usually because a transaction, a replication slot or a prepared transaction holds the "
            + "oldest transaction id back (T14 names it), or because vacuum keeps being cancelled. At about 2.1 billion Postgres refuses every "
            + "write to the database until a VACUUM has run: a DBA's VACUUM (FREEZE) of these tables now, once whatever holds "
            + "the horizon back is gone, is the cure.";

        if (origin == Origin.Database)
        {
            why += text.UnjudgedOldest;
        }
        else if (origin == Origin.Server)
        {
            why += " The oldest database on this server is another one; Postgres' write refusal is the server's, so when that database "
                + "reaches the line every database on the server, this one included, stops accepting writes.";
        }

        if (database is { OtherTemporaryPastForcedXid: > 0 } || database is { OtherOldestIsTemporary: true } && origin == Origin.Database)
        {
            why += " A temporary table can be vacuumed only by the session that created it - autovacuum cannot freeze one - so a "
                + "long-lived session that keeps a temporary table holds the database's age up until it ends.";
        }

        return why;
    }

    private static List<HealthEvidence> Evidence(
        TableFacts? oldest,
        long tableAge,
        HealthWraparoundDatabase? database,
        long freezeMaxAge,
        long warning,
        long critical,
        List<TableFacts> past,
        HorizonForecast? forecast,
        TimeSpan? toForced,
        TimeSpan? toRefusal,
        StudioText text)
    {
        List<HealthEvidence> evidence = [];

        if (oldest is not null)
        {
            evidence.Add(new HealthEvidence("Oldest table", oldest.Table.DescribeUngated()));
            evidence.Add(new HealthEvidence("Its age(relfrozenxid)", tableAge.ToString("N0", CultureInfo.InvariantCulture)));
        }

        if (database is not null)
        {
            evidence.Add(new HealthEvidence("This database's age(datfrozenxid)", Wraparound.Number(database.DatabaseXidAge)));

            if (database.ServerXidAge > database.DatabaseXidAge)
            {
                evidence.Add(new HealthEvidence("Oldest database on this server, age(datfrozenxid)", Wraparound.Number(database.ServerXidAge)));
            }
        }

        evidence.Add(new HealthEvidence("autovacuum_freeze_max_age", freezeMaxAge.ToString("N0", CultureInfo.InvariantCulture)));
        evidence.Add(new HealthEvidence("Warning and Critical lines", warning.ToString("N0", CultureInfo.InvariantCulture) + " and "
            + critical.ToString("N0", CultureInfo.InvariantCulture)));
        evidence.Add(new HealthEvidence(text.JudgedTables + " past the warning line", past.Count.ToString("N0", CultureInfo.InvariantCulture)));

        if (past.Count > 0)
        {
            evidence.Add(new HealthEvidence("The oldest of them", string.Join(", ", past.Take(NamedTables).Select(static x => x.Table.NoteName
                + " (" + x.Row.OldestXidAge!.Value.ToString("N0", CultureInfo.InvariantCulture) + ")"))));
        }

        if (database is not null)
        {
            evidence.Add(new HealthEvidence("Other tables past autovacuum_freeze_max_age", Wraparound.Number(database.OtherPastForcedXid)
                + (database.OtherTemporaryPastForcedXid > 0 ? " (" + Wraparound.Number(database.OtherTemporaryPastForcedXid) + " of them temporary)" : string.Empty)
                + ", never named: they are " + text.NotJudged));

            if (database.SystemPastForcedXid > 0)
            {
                evidence.Add(new HealthEvidence("System catalogs past autovacuum_freeze_max_age", Wraparound.Number(database.SystemPastForcedXid)));
            }
        }

        if (oldest is not null)
        {
            evidence.Add(new HealthEvidence("Last autovacuum of the oldest", TableText.When(oldest.Row.LastAutovacuum)));
        }

        if (forecast is not null)
        {
            evidence.Add(new HealthEvidence("Transaction ids spent", forecast.RateText("transaction ids")));

            if (toForced is { } forced && forced > TimeSpan.Zero)
            {
                evidence.Add(new HealthEvidence("Autovacuum forces a freeze in", HorizonForecast.Span(forced) + ", " + forecast.AtTheRate));
            }

            if (toRefusal is { } refusal)
            {
                evidence.Add(new HealthEvidence("Writes refused in", HorizonForecast.Span(refusal) + ", " + forecast.AtTheRate
                    + ", if nothing is frozen first"));
            }
        }

        return evidence;
    }

    private static string Caveat(HealthWraparoundDatabase? database, HorizonForecast? forecast, StudioText text)
    {
        string caveat =
            "Read from pg_class.relfrozenxid of " + text.JudgedTablesRead + ", their TOAST tables and their partitions"
            + (database is null ? string.Empty : ", and from pg_database.datfrozenxid of this database and the server's oldest")
            + ". The lines sit " + TableText.Percent(WarningShare) + " and " + TableText.Percent(CriticalShare) + " of the way from "
            + "autovacuum_freeze_max_age to wraparound. Tables that are " + text.NotJudged + " are counted and never named.";

        if (forecast is not null)
        {
            caveat += " " + forecast.Caveat("pg_snapshot_xmax(pg_current_snapshot())");
        }
        else if (database is not null)
        {
            caveat += " No forecast yet: a rate needs two samples, an interval apart, in which transaction ids were spent.";
        }

        return caveat;
    }

    private static string QuietNote(
        TableFacts? oldest,
        long tableAge,
        HealthWraparoundDatabase? database,
        long freezeMaxAge,
        HorizonForecast? forecast,
        TimeSpan? toForced,
        TimeSpan? toRefusal,
        bool pastForced,
        StudioText text)
    {
        List<string> parts = [];

        if (oldest is not null)
        {
            parts.Add("The oldest " + text.JudgedTable + ", " + oldest.Table.NoteName + ", is " + tableAge.ToString("N0", CultureInfo.InvariantCulture)
                + " transactions from its last freeze.");
        }

        if (database is not null)
        {
            parts.Add("This database's age(datfrozenxid) is " + Wraparound.Number(database.DatabaseXidAge)
                + (database.ServerXidAge > database.DatabaseXidAge
                    ? ", and the server's oldest database's " + Wraparound.Number(database.ServerXidAge)
                    : string.Empty)
                + ".");

            if (forecast is null)
            {
                parts.Add("A forecast needs two samples, an interval apart, in which transaction ids were spent.");
            }
            else if (pastForced)
            {
                // Fix 3: "more than 90 days" only when the forecast is past its horizon; otherwise the caller says when.
                parts.Add(toRefusal is null
                    ? "It is past autovacuum_freeze_max_age (" + Wraparound.Number(freezeMaxAge) + "), and " + forecast.AtTheRate
                      + " the write-refusal line is more than " + ((int) HorizonForecast.Horizon.TotalDays).ToString(CultureInfo.InvariantCulture)
                      + " days away."
                    : "It is past autovacuum_freeze_max_age (" + Wraparound.Number(freezeMaxAge) + ").");
            }
            else if (toForced is { } forced)
            {
                parts.Add(Capitalised(forecast.AtTheRate) + " (" + forecast.RateText("transaction ids") + "), autovacuum forces a freeze in "
                    + HorizonForecast.Span(forced) + " - routine, and nothing to act on.");
            }
            else
            {
                parts.Add(Capitalised(forecast.AtTheRate) + ", the forced freeze is more than "
                    + ((int) HorizonForecast.Horizon.TotalDays).ToString(CultureInfo.InvariantCulture) + " days away.");
            }
        }

        return string.Join(" ", parts);
    }
}
