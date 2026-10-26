using PostgresStudio.Services.Health;
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
    public static PostgresFinding Finding(HealthFinding finding, HealthCheckInfo info, HostGate gate, IStudioRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(runtime);

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
            MeasuredAt = finding.MeasuredAt,
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
