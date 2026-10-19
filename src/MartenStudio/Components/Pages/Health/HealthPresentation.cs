using System.Globalization;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health;

namespace MartenStudio.Components.Pages.Health;

/// <summary>How the Health page, its cards and the Overview's tile word and colour what the advisor found.</summary>
internal static class HealthPresentation
{
    /// <summary>A severity as the page says it.</summary>
    public static string Label(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Critical => "Critical",
        HealthSeverity.Warning => "Warning",
        _ => "Info",
    };

    /// <summary>The chip class for a severity.</summary>
    public static string ChipClass(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Critical => "ms-health-severity ms-health-severity-critical",
        HealthSeverity.Warning => "ms-health-severity ms-health-severity-warning",
        _ => "ms-health-severity ms-health-severity-info",
    };

    /// <summary>The <c>StatCard</c> colour for the worst severity, or for none.</summary>
    public static string TileColor(HealthSeverity? worst) => worst switch
    {
        HealthSeverity.Critical => "danger",
        HealthSeverity.Warning => "warning",
        HealthSeverity.Info => "info",
        _ => "success",
    };

    /// <summary>An area's heading.</summary>
    public static string AreaTitle(HealthArea area) => area switch
    {
        HealthArea.Daemon => "Daemon and high water",
        HealthArea.Events => "Events",
        HealthArea.Tables => "Tables and indexes",
        HealthArea.Connections => "Connections and sessions",
        HealthArea.Replication => "Replication and WAL",
        HealthArea.Server => "Server",
        _ => "Configuration",
    };

    /// <summary>A status row's outcome, in words.</summary>
    /// <param name="outcome">How the check's last run ended.</param>
    /// <param name="findings">How many of its findings this visitor is shown.</param>
    /// <param name="withheld">
    /// Whether the count leaves findings out (<see cref="HealthCheckStatus.FindingsWithheld" />). Such a row never
    /// reads "nothing found": short of the check's gate it reads "findings not shown to you" whether or not there are
    /// any, and through the gate it says how many are shown and that others are not.
    /// </param>
    public static string OutcomeLabel(HealthCheckOutcome outcome, int findings, bool withheld = false) => outcome switch
    {
        HealthCheckOutcome.Checked when withheld && findings == 0 => "checked, findings not shown to you",
        HealthCheckOutcome.Checked when withheld => "checked, " + findings.ToString(CultureInfo.InvariantCulture) + " shown, others not shown to you",
        HealthCheckOutcome.Checked when findings == 0 => "checked, nothing found",
        HealthCheckOutcome.Checked when findings == 1 => "checked, 1 finding",
        HealthCheckOutcome.Checked => "checked, " + findings.ToString(CultureInfo.InvariantCulture) + " findings",
        HealthCheckOutcome.NotApplicable => "not applicable on this host",
        HealthCheckOutcome.Unavailable => "not checked",
        _ => "could not check",
    };

    /// <summary>A cost tier, in words.</summary>
    public static string TierLabel(HealthCostTier tier) => tier switch
    {
        HealthCostTier.InProcess => "configuration",
        HealthCostTier.Catalog => "catalog",
        HealthCostTier.BoundedData => "deeper",
        _ => "statements",
    };

    /// <summary>
    /// The "studio cost" line: what the studio's own read-only sessions spent in the database recently, and
    /// what that does not include.
    /// </summary>
    public static string Cost(StudioCost cost)
    {
        string seconds = cost.Total.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        string sessions = cost.Sessions == 1 ? "1 read-only session" : cost.Sessions.ToString("N0", CultureInfo.InvariantCulture) + " read-only sessions";

        return "Studio cost: " + seconds + " s of database time in " + sessions + " against this database over the last "
            + ((int) cost.Window.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes, measured around the studio's own "
            + "read-only sessions (health checks, the SQL console, the database browser, schema and relationship reads). "
            + "The documents, events and projections screens' reads are not counted yet.";
    }

    /// <summary>A findings count with its noun.</summary>
    public static string Count(int count, string noun) =>
        count.ToString(CultureInfo.InvariantCulture) + " " + noun + (count == 1 ? string.Empty : "s");

    /// <summary>
    /// "3 findings not shown to you: needs the store policy ...": what the Overview says where it would otherwise say
    /// "nothing to report" - the Health page's notice, in the same words.
    /// </summary>
    /// <param name="count">How many findings this visitor is not shown.</param>
    /// <param name="reason">What it would take to see them, or <see langword="null" />.</param>
    public static string Withheld(int count, string? reason) =>
        Count(count, "finding") + " not shown to you" + (reason is { Length: > 0 } why ? ": " + why : string.Empty) + ".";
}
