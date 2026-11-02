using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Components.Pages.Health;

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

    /// <summary>
    /// ": the host's reason" after the "Accepted" label of a check's row, or nothing for a reason the host did not give.
    /// </summary>
    /// <param name="reason">The host's reason, as configured.</param>
    public static string AcceptedReasonText(string? reason) => string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason;

    /// <summary>A cost tier, in words.</summary>
    public static string TierLabel(HealthCostTier tier) => tier switch
    {
        HealthCostTier.InProcess => "configuration",
        HealthCostTier.Catalog => "catalog",
        HealthCostTier.BoundedData => "deeper",
        _ => "statements",
    };

    /// <summary>
    /// The "studio cost" line: what the studio's own connections to the database took recently, and what that does not
    /// include - the same meter as the Statements page's "the studio itself" line (W5-Q1).
    /// </summary>
    /// <param name="cost">The meter's total for the database.</param>
    /// <param name="text">The studio's words, for what its connections cover.</param>
    public static string Cost(StudioCost cost, StudioText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string seconds = cost.Total.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture);
        string connections = cost.Connections == 1 ? "1 connection" : cost.Connections.ToString("N0", CultureInfo.InvariantCulture) + " connections";

        return "Studio cost: " + seconds + " s of database time over " + connections + " to this database in the last "
            + ((int) cost.Window.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " minutes, measured from open to close of "
            + "every connection the studio opens to it (" + text.StudioCostCovers + ").";
    }

    /// <summary>
    /// Issue #44: the studio's share of the database's pool, after its cost: the cap, the property that sets it, and how many it
    /// holds now. Issue #49: when the data source's own <c>Max Pool Size</c> held the cap below the property, it says so.
    /// </summary>
    /// <param name="share">The cap in force, the property's value, the data source's pool size where it was the bound, and how many it holds now.</param>
    /// <param name="settings">The studio's settings, for the property's name as the host spells it.</param>
    public static string Share(StudioConnectionShare share, IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(settings);

        string option = settings.OptionName("MaxStudioConnections");
        string reason = share.PoolSize is { } poolSize
            ? option + " is " + share.Configured.ToString(CultureInfo.InvariantCulture) + "; the data source's Max Pool Size is "
                + poolSize.ToString(CultureInfo.InvariantCulture)
            : option;

        return "It holds at most " + share.Max.ToString(CultureInfo.InvariantCulture) + " of the pool's connections at a time ("
            + reason + "), " + share.InUse.ToString(CultureInfo.InvariantCulture) + " now.";
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
