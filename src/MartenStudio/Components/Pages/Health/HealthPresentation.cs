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
    public static string OutcomeLabel(HealthCheckOutcome outcome, int findings) => outcome switch
    {
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
}
