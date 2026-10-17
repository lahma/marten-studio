using System.Globalization;
using System.Text;

namespace MartenStudio.Services.Health.Export;

/// <summary>How bad an export run's result is, before the caller's <c>failureStatus</c> names it.</summary>
internal enum HealthExportLevel
{
    /// <summary>Nothing worse than an Info finding, and everything the run set out to check was checked.</summary>
    Healthy,

    /// <summary>A Warning finding, or something the run could not check.</summary>
    Degraded,

    /// <summary>A Critical finding.</summary>
    Critical,
}

/// <summary>
/// What one export run found, as the health endpoint may say it: codes and counts, never a name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built from codes, and from nothing a finding wrote.</b> <see cref="From" /> reads, of each check's
/// result, only the check's own code (<see cref="HealthCheckInfo.Code" />, an attribute constant the registry
/// holds to a letter and two digits), its <see cref="HealthCheckOutcome" /> - with, for a check not checked, the
/// <see cref="HealthUnavailableReason" /> the sweeper classified it as, an enum and never a sentence - and the
/// <see cref="HealthFinding.Severity" /> of each finding. A title, a subject, a tenant, a piece of evidence,
/// a statement, Postgres' words, a store key and a database identity are never read here, so there is no
/// path by which one reaches the endpoint - whatever a check puts in its findings. That is the whole
/// authorization model of the export: there is no visitor to ask, so nothing is said that would need one.
/// </para>
/// <para>
/// <b>Hidden document types are already gone.</b> The sweeper drops a finding about a type
/// <c>MartenStudioOptions.IsDocumentTypeVisible</c> hides before the sweep is returned, so such a finding is
/// not counted here either - not by code, not by number.
/// </para>
/// </remarks>
internal sealed record HealthExportSummary
{
    /// <summary>When the run began, by the studio's clock.</summary>
    public required DateTimeOffset MeasuredAt { get; init; }

    /// <summary>The codes with a Critical finding in any database, in ordinal order.</summary>
    public IReadOnlyList<string> Critical { get; init; } = [];

    /// <summary>The codes with a Warning finding in any database.</summary>
    public IReadOnlyList<string> Warning { get; init; } = [];

    /// <summary>The codes with an Info finding in any database.</summary>
    public IReadOnlyList<string> Info { get; init; } = [];

    /// <summary>How many Critical findings, across every database.</summary>
    public int CriticalFindings { get; init; }

    /// <summary>How many Warning findings.</summary>
    public int WarningFindings { get; init; }

    /// <summary>How many Info findings.</summary>
    public int InfoFindings { get; init; }

    /// <summary>The codes of checks that failed somewhere - a "could not check" row on the Health page.</summary>
    public IReadOnlyList<string> Failed { get; init; } = [];

    /// <summary>
    /// The codes of checks that could not run somewhere for a reason that is the host's choice - a missing
    /// privilege, no extension, an old Postgres. A value: it changes no status.
    /// </summary>
    public IReadOnlyList<string> Unavailable { get; init; } = [];

    /// <summary>
    /// The codes of checks that ran out of time somewhere: a statement that hit <c>statement_timeout</c>, a lock
    /// wait, the check's own budget, or a sweep whose budget ran out before the check's turn. Degraded: a database
    /// that cannot answer a catalog read in time is worth somebody's look, whatever the page calls it.
    /// </summary>
    public IReadOnlyList<string> TimedOut { get; init; } = [];

    /// <summary>How many databases were checked.</summary>
    public int Databases { get; init; }

    /// <summary>Databases the run did not check because it had already checked as many as one run may.</summary>
    public int DatabasesSkipped { get; init; }

    /// <summary>
    /// Databases a sharded tenancy holds under an id its <c>TenantIdStyle</c> would correct, so that looking it up
    /// would miss the tenancy's cache and read its master table; the run leaves them out (<see cref="HeldDatabases" />).
    /// Counted with <see cref="DatabasesSkipped" /> in the data, and said apart in the description.
    /// </summary>
    public int DatabasesUnnamed { get; init; }

    /// <summary>Databases the run did not finish checking because it ran out of time.</summary>
    public int DatabasesTimedOut { get; init; }

    /// <summary>Stores that would not build, or whose databases could not be listed.</summary>
    public int StoresNotReached { get; init; }

    /// <summary>
    /// Stores whose tenancy creates or migrates databases on first enumeration and has not loaded any in this
    /// process yet, so the run did not enumerate them. A value: it changes no status.
    /// </summary>
    public int StoresNotEnumerated { get; init; }

    /// <summary>
    /// Why the run could not check anything at all, as a fixed sentence of the studio's own, or
    /// <see langword="null" />.
    /// </summary>
    public string? Problem { get; init; }

    /// <summary>How bad it is.</summary>
    public HealthExportLevel Level =>
        CriticalFindings > 0
            ? HealthExportLevel.Critical
            : WarningFindings > 0 || Failed.Count > 0 || TimedOut.Count > 0 || StoresNotReached > 0 || DatabasesTimedOut > 0 || Problem is not null
                ? HealthExportLevel.Degraded
                : HealthExportLevel.Healthy;

    /// <summary>The summary of a run that swept <paramref name="sweeps" />.</summary>
    /// <param name="measuredAt">When the run began.</param>
    /// <param name="sweeps">One regular sweep per database checked.</param>
    /// <param name="databasesSkipped">Databases left out because the run had checked as many as it may.</param>
    /// <param name="databasesTimedOut">Databases left out because the run ran out of time.</param>
    /// <param name="storesNotReached">Stores that would not build, or whose databases could not be listed.</param>
    /// <param name="storesNotEnumerated">Stores whose tenancy holds no database it can name without building one.</param>
    /// <param name="databasesUnnamed">Held databases left out because naming them would read the master table.</param>
    public static HealthExportSummary From(
        DateTimeOffset measuredAt,
        IReadOnlyCollection<HealthSweep> sweeps,
        int databasesSkipped = 0,
        int databasesTimedOut = 0,
        int storesNotReached = 0,
        int storesNotEnumerated = 0,
        int databasesUnnamed = 0)
    {
        ArgumentNullException.ThrowIfNull(sweeps);

        SortedSet<string> critical = new(StringComparer.Ordinal);
        SortedSet<string> warning = new(StringComparer.Ordinal);
        SortedSet<string> info = new(StringComparer.Ordinal);
        SortedSet<string> failed = new(StringComparer.Ordinal);
        SortedSet<string> unavailable = new(StringComparer.Ordinal);
        SortedSet<string> timedOut = new(StringComparer.Ordinal);
        int criticalFindings = 0;
        int warningFindings = 0;
        int infoFindings = 0;

        foreach (HealthSweep sweep in sweeps)
        {
            foreach (HealthCheckResult result in sweep.Results)
            {
                // The check's own code - never anything one of its findings wrote.
                string code = result.Check.Code;

                if (result.Outcome == HealthCheckOutcome.Failed)
                {
                    failed.Add(code);
                }
                else if (result.Outcome == HealthCheckOutcome.Unavailable)
                {
                    // The sweeper's own classification, never its sentence: a reason it did not mark is the host's.
                    (result.Problem?.UnavailableReason is HealthUnavailableReason.TimedOut or HealthUnavailableReason.SweepBudget
                        ? timedOut
                        : unavailable).Add(code);
                }

                foreach (HealthFinding finding in result.Findings)
                {
                    if (finding.Severity == HealthSeverity.Critical)
                    {
                        critical.Add(code);
                        criticalFindings++;
                    }
                    else if (finding.Severity == HealthSeverity.Warning)
                    {
                        warning.Add(code);
                        warningFindings++;
                    }
                    else
                    {
                        info.Add(code);
                        infoFindings++;
                    }
                }
            }
        }

        return new HealthExportSummary
        {
            MeasuredAt = measuredAt,
            Critical = [.. critical],
            Warning = [.. warning],
            Info = [.. info],
            CriticalFindings = criticalFindings,
            WarningFindings = warningFindings,
            InfoFindings = infoFindings,
            Failed = [.. failed],
            Unavailable = [.. unavailable],
            TimedOut = [.. timedOut],
            Databases = sweeps.Count,
            DatabasesSkipped = databasesSkipped,
            DatabasesTimedOut = databasesTimedOut,
            StoresNotReached = storesNotReached,
            StoresNotEnumerated = storesNotEnumerated,
            DatabasesUnnamed = databasesUnnamed,
        };
    }

    /// <summary>A host that added the health check and not the studio itself.</summary>
    public static HealthExportSummary NotRegistered(DateTimeOffset measuredAt) => new()
    {
        MeasuredAt = measuredAt,
        Problem = "Marten Studio's services are not registered: call services.AddMartenStudio() as well as AddHealthChecks().AddMartenStudio()",
    };

    /// <summary>A host whose container holds no Marten store the studio would list.</summary>
    public static HealthExportSummary NoStores(DateTimeOffset measuredAt) => new()
    {
        MeasuredAt = measuredAt,
        Problem = "Marten Studio found no Marten store to check",
    };

    /// <summary>A run the application's stopping cut short: nothing it found is reported half-way.</summary>
    public static HealthExportSummary Stopping(DateTimeOffset measuredAt) => new()
    {
        MeasuredAt = measuredAt,
        Problem = "The application is stopping, so Marten Studio did not finish checking",
    };

    /// <summary>A run that failed in the studio itself. The exception's type is named; its words never are.</summary>
    /// <param name="measuredAt">When the run began.</param>
    /// <param name="exceptionType">The exception's type name, which carries no data.</param>
    public static HealthExportSummary Broken(DateTimeOffset measuredAt, string exceptionType) => new()
    {
        MeasuredAt = measuredAt,
        Problem = "Marten Studio could not check: its health export failed with " + exceptionType + ", logged as event 9270",
    };

    /// <summary>
    /// One short paragraph for the report's description: what was found, how much was checked, and how long
    /// the result is reused for. Codes and counts only, like the data.
    /// </summary>
    /// <param name="interval">How long the result is served before the next run.</param>
    public string Describe(TimeSpan interval)
    {
        StringBuilder text = new();

        if (Problem is not null)
        {
            text.Append(Problem);
        }
        else
        {
            List<string> found = [];

            if (CriticalFindings > 0)
            {
                found.Add(Count(CriticalFindings, "critical", "critical") + " (" + string.Join(", ", Critical) + ")");
            }

            if (WarningFindings > 0)
            {
                found.Add(Count(WarningFindings, "warning", "warnings") + " (" + string.Join(", ", Warning) + ")");
            }

            if (InfoFindings > 0)
            {
                found.Add(Count(InfoFindings, "info", "info") + " (" + string.Join(", ", Info) + ")");
            }

            text.Append(found.Count == 0 ? "No findings" : string.Join(", ", found));
            text.Append(" on ").Append(Count(Databases, "database", "databases"));

            if (Failed.Count > 0)
            {
                text.Append("; ").Append(Count(Failed.Count, "check", "checks"))
                    .Append(" could not run (").Append(string.Join(", ", Failed)).Append(')');
            }

            if (TimedOut.Count > 0)
            {
                text.Append("; ").Append(Count(TimedOut.Count, "check", "checks"))
                    .Append(" ran out of time (").Append(string.Join(", ", TimedOut)).Append(')');
            }

            if (StoresNotReached > 0)
            {
                text.Append("; ").Append(Count(StoresNotReached, "store", "stores")).Append(" could not be reached");
            }

            if (DatabasesTimedOut > 0)
            {
                text.Append("; ").Append(Count(DatabasesTimedOut, "database", "databases"))
                    .Append(" not checked: the run ran out of time");
            }

            if (DatabasesSkipped > 0)
            {
                text.Append("; ").Append(Count(DatabasesSkipped, "database", "databases"))
                    .Append(" skipped: past the most one run checks");
            }

            if (DatabasesUnnamed > 0)
            {
                text.Append("; ").Append(Count(DatabasesUnnamed, "held database", "held databases"))
                    .Append(" skipped: naming them would read the master table");
            }

            if (StoresNotEnumerated > 0)
            {
                text.Append("; ").Append(Count(StoresNotEnumerated, "store", "stores"))
                    .Append(" not enumerated: it holds no database it can name without building one");
            }
        }

        // What the runner does with it (HealthExportRunner.GetAsync): the next run starts once it is an interval
        // old, and it is never served once it is two.
        text.Append(". Refreshed after ")
            .Append(Seconds(interval))
            .Append(" s, served for at most ")
            .Append(Seconds(interval * 2))
            .Append(" s.");

        return text.ToString();
    }

    private static string Seconds(TimeSpan span) => ((long) span.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The report's data: every key always present, every value a list of codes, a count or the time the run
    /// began - so a consumer reads one shape whatever was found.
    /// </summary>
    public IReadOnlyDictionary<string, object> ToData() => new Dictionary<string, object>(StringComparer.Ordinal)
    {
        ["critical"] = Critical.ToArray(),
        ["warning"] = Warning.ToArray(),
        ["info"] = Info.ToArray(),
        ["failed"] = Failed.ToArray(),
        ["unavailable"] = Unavailable.ToArray(),
        ["timedOut"] = TimedOut.ToArray(),
        ["databases"] = Databases,
        ["databasesSkipped"] = DatabasesSkipped + DatabasesUnnamed,
        ["databasesTimedOut"] = DatabasesTimedOut,
        ["storesNotReached"] = StoresNotReached,
        ["storesNotEnumerated"] = StoresNotEnumerated,
        ["measuredAt"] = MeasuredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
    };

    private static string Count(int count, string one, string many) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
}
