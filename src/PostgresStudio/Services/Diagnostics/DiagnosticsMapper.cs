using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Diagnostics;

/// <summary>
/// The studio's internal health model as the public read model says it (API design §1.4's mapping table): every internal
/// enum member to its public value, a check to a <see cref="PostgresCheck" />, a finding to a <see cref="PostgresFinding" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exhaustive, on purpose.</b> Each enum is mapped member by member and an unmapped one throws, so an internal member added
/// without a public value fails the mapper's theory over every member rather than reaching a host as a number nobody
/// documented. The public enums' values are explicit and frozen; a new public value is an addition, never a renumbering.
/// </para>
/// <para>
/// <b>What is never carried.</b> PostgreSQL's own words for a failure (<see cref="HealthCheckProblem.PostgresText" />) and a
/// connection error's (<see cref="HealthCheckProblem.Detail" />) stay home: they can echo a bound value, and the server log
/// has them (events 9222 and 9206). The studio's own sentence and the SQLSTATE are carried. "Show SQL" statements are not part
/// of the read model at all.
/// </para>
/// </remarks>
internal static class DiagnosticsMapper
{
    /// <summary>The most statements a statements section lists, in the API's snapshot and the studio's download alike.</summary>
    public const int MaxStatements = 100;

    /// <summary>A severity.</summary>
    public static PostgresSeverity Severity(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Critical => PostgresSeverity.Critical,
        HealthSeverity.Warning => PostgresSeverity.Warning,
        HealthSeverity.Info => PostgresSeverity.Info,
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "A severity with no public value."),
    };

    /// <summary>An area.</summary>
    public static PostgresFindingCategory Category(HealthArea area) => area switch
    {
        HealthArea.Daemon => PostgresFindingCategory.Daemon,
        HealthArea.Events => PostgresFindingCategory.Events,
        HealthArea.Tables => PostgresFindingCategory.Tables,
        HealthArea.Connections => PostgresFindingCategory.Connections,
        HealthArea.Replication => PostgresFindingCategory.Replication,
        HealthArea.Server => PostgresFindingCategory.Server,
        HealthArea.Configuration => PostgresFindingCategory.Configuration,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "An area with no public category."),
    };

    /// <summary>A remedy's kind.</summary>
    public static PostgresActionKind ActionKind(HealthRemedyKind kind) => kind switch
    {
        HealthRemedyKind.Configuration => PostgresActionKind.ApplicationConfiguration,
        HealthRemedyKind.Migration => PostgresActionKind.Migration,
        HealthRemedyKind.ServerSetting => PostgresActionKind.ServerSetting,
        HealthRemedyKind.ManagedPlatform => PostgresActionKind.ManagedPlatform,
        HealthRemedyKind.Repair => PostgresActionKind.Repair,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A remedy kind with no public action kind."),
    };

    /// <summary>
    /// How a check's run ended. An expected state that ran out of time - a statement's timeout, a lock wait, the check's or the
    /// sweep's budget - is <see cref="PostgresReadOutcome.TimedOut" />, as the health export already splits them.
    /// </summary>
    public static PostgresReadOutcome Outcome(HealthCheckOutcome outcome, HealthUnavailableReason? reason = null) => outcome switch
    {
        HealthCheckOutcome.Checked => PostgresReadOutcome.Read,
        HealthCheckOutcome.NotApplicable => PostgresReadOutcome.NotApplicable,
        HealthCheckOutcome.Unavailable => reason is HealthUnavailableReason.TimedOut or HealthUnavailableReason.SweepBudget
            ? PostgresReadOutcome.TimedOut
            : PostgresReadOutcome.Unavailable,
        HealthCheckOutcome.Failed => PostgresReadOutcome.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "An outcome with no public value."),
    };

    /// <summary>Whose table it is.</summary>
    public static PostgresTableOwnership Ownership(StudioTableOwnership ownership) => ownership switch
    {
        StudioTableOwnership.Owned => PostgresTableOwnership.Application,
        StudioTableOwnership.Library => PostgresTableOwnership.Library,
        StudioTableOwnership.Other => PostgresTableOwnership.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(ownership), ownership, "An ownership with no public value."),
    };

    /// <summary>A check in the catalogue: what it is, with no outcome.</summary>
    /// <param name="info">The check's metadata.</param>
    public static PostgresCheck Check(HealthCheckInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new PostgresCheck
        {
            Code = info.Code,
            Category = Category(info.Area),
            Title = info.Title,

            // A copy: the registry's list is every snapshot's, and a host may hold on to a result as long as it likes.
            Variants = [.. info.Variants],
            RequiresActivity = info.Gate == HealthGate.Activity,
            DocsUrl = info.DocsUrl,
        };
    }

    /// <summary>
    /// A check's last run, as the host gate shows it: <see cref="PostgresReadOutcome.Withheld" /> - with nothing more - for an
    /// Activity-grade check the request did not include, whether or not it found anything.
    /// </summary>
    /// <param name="result">The check's part of a sweep.</param>
    /// <param name="gate">The host gate.</param>
    /// <param name="included">How many of its findings the snapshot includes.</param>
    public static PostgresCheck Check(HealthCheckResult result, HostGate gate, int included)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(gate);

        PostgresCheck check = Check(result.Check);

        if (!gate.Passes(result.Check.Gate))
        {
            return check with { Outcome = PostgresReadOutcome.Withheld };
        }

        // The studio's own sentence: a problem's reason, or - for a check that did not run - its note ("Needs Marten 9.47+").
        string? reason = result.Problem?.Reason ?? (result.Outcome == HealthCheckOutcome.Checked ? null : result.Note);

        return check with
        {
            Outcome = Outcome(result.Outcome, result.Problem?.UnavailableReason),
            Reason = reason,
            SqlState = result.Problem?.SqlState,
            Findings = included,
        };
    }

    /// <summary>
    /// A finding, through the host gate - which the caller has asked already (<see cref="HostGate.Shows" />): its evidence
    /// value by value, its statement texts through the host's screen, and its tenant only past the activity gate.
    /// </summary>
    /// <param name="finding">The finding, as the sweep recorded it.</param>
    /// <param name="info">Its check.</param>
    /// <param name="gate">The host gate.</param>
    /// <param name="runtime">The studio's runtime, for what a remedy needs that this host lacks.</param>
    /// <param name="sourceParameter">The studio's source parameter, for the finding's links.</param>
    /// <param name="scope">The database the finding is about, for the finding's links.</param>
    public static PostgresFinding Finding(
        HealthFinding finding,
        HealthCheckInfo info,
        HostGate gate,
        IStudioRuntime runtime,
        string sourceParameter,
        StudioScope scope)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(scope);

        StudioFeatureRequirement? feature = finding.Remedy?.Requires;

        return new PostgresFinding
        {
            Code = finding.Code,
            Variant = finding.Variant,
            Subject = finding.Subject,
            TenantId = gate.IncludeActivity ? finding.TenantId : null,
            Severity = Severity(finding.Severity),
            Category = Category(info.Area),
            Title = finding.Title,
            Detail = finding.WhyItMatters,
            Caveat = finding.Caveat,
            Action = finding.Remedy?.Code,
            ActionExplanation = finding.Remedy?.Explanation,
            ActionKind = finding.Remedy is { } remedy ? ActionKind(remedy.Kind) : null,
            ActionRequires = feature is not null && !runtime.IsAvailable(feature) ? feature.Needs : null,
            Evidence = [.. finding.Evidence.Select(gate.Evidence)],
            DocsUrl = info.DocsUrl,
            StudioPath = HealthLinks.Relative(sourceParameter, HealthLinks.Route, scope) + "#" + HealthReportFinding.AnchorFor(finding),
            StudioLinks = finding.Link is { } link
                ? [new PostgresStudioLink { Label = link.Label, Path = HealthLinks.Relative(sourceParameter, link.Route, scope, link.Query) }]
                : [],
            MeasuredAt = finding.MeasuredAt,
        };
    }

    /// <summary>
    /// A finding as one visitor's Health page shows it, for the studio's snapshot download (API design §2.4): the report's own
    /// evidence, which the Health service has already gated value by value for that visitor and whose statement texts it has
    /// already masked with that visitor's screen - never the sweep's.
    /// </summary>
    /// <param name="shown">The finding as the report shows it.</param>
    public static PostgresFinding Finding(HealthReportFinding shown)
    {
        ArgumentNullException.ThrowIfNull(shown);

        HealthFinding finding = shown.Finding;

        return new PostgresFinding
        {
            Code = finding.Code,
            Variant = finding.Variant,
            Subject = finding.Subject,
            TenantId = finding.TenantId,
            Severity = Severity(finding.Severity),
            Category = Category(finding.Area),
            Title = finding.Title,
            Detail = finding.WhyItMatters,
            Caveat = finding.Caveat,
            Action = finding.Remedy?.Code,
            ActionExplanation = finding.Remedy?.Explanation,
            ActionKind = finding.Remedy is { } remedy ? ActionKind(remedy.Kind) : null,
            ActionRequires = shown.RemedyAvailable ? null : finding.Remedy?.Requires?.Needs,
            Evidence = [.. shown.Evidence.Select(static x => new KeyValuePair<string, string>(x.Label, x.Value))],
            DocsUrl = shown.DocsUrl,
            MeasuredAt = finding.MeasuredAt,
        };
    }

    /// <summary>
    /// A check's row as one visitor's Health page shows it, for the studio's snapshot download:
    /// <see cref="PostgresReadOutcome.Withheld" /> with the page's own sentence for a row past the visitor's gate, otherwise
    /// its outcome, the page's words for a problem, and how many findings the visitor is shown.
    /// </summary>
    /// <param name="status">The row.</param>
    /// <param name="info">The check, from the registry, for its variants and gate; <see langword="null" /> when unknown.</param>
    public static PostgresCheck Check(HealthCheckStatus status, HealthCheckInfo? info)
    {
        ArgumentNullException.ThrowIfNull(status);

        PostgresCheck check = new()
        {
            Code = status.Code,
            Category = Category(status.Area),
            Title = status.Title,
            Variants = info is null ? [] : [.. info.Variants],
            RequiresActivity = info?.Gate == HealthGate.Activity,
            DocsUrl = status.DocsUrl,
        };

        if (status.BelowGate is { } gate)
        {
            return check with { Outcome = PostgresReadOutcome.Withheld, Reason = "withheld - " + gate };
        }

        return check with
        {
            Outcome = Outcome(status.Outcome),
            Reason = status.Problem ?? (status.Outcome == HealthCheckOutcome.Checked ? null : status.Note),
            SqlState = status.SqlState,
            Findings = status.FindingCount,
        };
    }

    /// <summary>One row of connection counts, as the public type has it.</summary>
    /// <param name="counts">The row.</param>
    public static PostgresConnectionCounts Counts(SessionApplicationCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);

        return new PostgresConnectionCounts
        {
            ApplicationName = counts.Application,
            Total = Count(counts.Total),
            Active = Count(counts.Active),
            Idle = Count(counts.Idle),
            IdleInTransaction = Count(counts.IdleInTransaction),
            Waiting = Count(counts.Waiting),
            StateUnknown = Count(counts.StateUnknown),
            OldestTransaction = counts.OldestTransaction,
        };
    }

    /// <summary>A count as the public type's <see cref="int" />, clamped.</summary>
    internal static int Count(long value) => (int) Math.Clamp(value, 0, int.MaxValue);

    /// <summary>A share held to 0 to 1; one the catalog does not give, or one that is not a number, is null.</summary>
    internal static double? Ratio(double? value) => value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : null;

    /// <summary>One curated setting, as the Server page lists it.</summary>
    /// <param name="row">The row.</param>
    public static PostgresSetting Setting(ServerSettingRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new PostgresSetting
        {
            Name = row.Name,
            Group = row.Group,
            Value = row.Setting?.Value,
            Unit = row.Setting?.Unit,
            Source = row.Setting?.Source,
            PendingRestart = row.Setting?.PendingRestart == true,
        };
    }

    /// <summary>One extension chip, as the Server page shows it.</summary>
    /// <param name="chip">The chip.</param>
    public static PostgresExtension Extension(ServerExtensionStatus chip)
    {
        ArgumentNullException.ThrowIfNull(chip);

        return new PostgresExtension
        {
            Name = chip.Name,
            InstalledVersion = chip.InstalledVersion,
            AvailableVersion = chip.AvailableVersion,
            Usable = chip.Usable,
            Note = chip.Note,
        };
    }

    /// <summary>
    /// The order a snapshot lists its findings in: worst first, then by code and key - what a host sorting them itself would
    /// get, whatever order the checks ran in.
    /// </summary>
    public static int Compare(PostgresFinding? a, PostgresFinding? b)
    {
        if (ReferenceEquals(a, b))
        {
            return 0;
        }

        if (a is null)
        {
            return -1;
        }

        if (b is null)
        {
            return 1;
        }

        int bySeverity = a.Severity.CompareTo(b.Severity);
        if (bySeverity != 0)
        {
            return bySeverity;
        }

        int byCode = string.CompareOrdinal(a.Code, b.Code);
        return byCode != 0 ? byCode : string.CompareOrdinal(a.Key, b.Key);
    }
}
