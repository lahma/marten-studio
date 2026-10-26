using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Components.Pages.Maintenance;

/// <summary>How the Maintenance page words and draws its numbers - pure, so a component test reads the same strings.</summary>
internal static class MaintenancePresentation
{
    /// <summary>
    /// How full a decision's bar is, in percent: the counter against twice its trigger - the line T07 and T08 call behind
    /// past - so the trigger itself sits at the middle and a full bar is a table autovacuum is not keeping up with.
    /// </summary>
    public static double Fill(AutovacuumUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        double scale = Math.Max(unit.Trigger, 1) * 2;
        return Math.Clamp(unit.Counter / scale * 100, 0, 100);
    }

    /// <summary>"12,000 of 1,050": a counter against its trigger, rounded as Postgres rounds it.</summary>
    public static string Against(AutovacuumUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return TableText.Count(unit.Counter) + " of " + TableText.Count(Autovacuum.Rounded(unit.Trigger));
    }

    /// <summary>The bar's class: past twice the trigger, past it, or below.</summary>
    public static string BarClass(AutovacuumUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        return unit.Counter > unit.Trigger * 2 ? "ms-maint-bar ms-maint-bar-behind"
            : unit.Due ? "ms-maint-bar ms-maint-bar-due"
            : "ms-maint-bar";
    }

    /// <summary>The bar's accessible description.</summary>
    public static string BarLabel(string what, AutovacuumUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);

        return what + ": " + TableText.Count(unit.Counter) + " against a trigger of " + TableText.Count(Autovacuum.Rounded(unit.Trigger))
            + (unit.Counter > unit.Trigger * 2 ? ", past twice the trigger" : unit.Due ? ", past the trigger" : string.Empty);
    }

    /// <summary>The trigger's own parts, for the bar's title: "50 + 0.2 × ~10,000 rows".</summary>
    public static string TriggerParts(AutovacuumUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return Autovacuum.Number(unit.Threshold) + " + " + Autovacuum.Number(unit.ScaleFactor) + " × " + TableText.Rows((long) unit.Tuples) + " rows";
    }

    /// <summary>A moment, or "never".</summary>
    public static string When(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } value ? SessionText.Duration(now - value) + " ago" : "never";

    /// <summary>Milliseconds as a person reads them: "3.2 s", "4 min", "1 h 5 min".</summary>
    public static string Milliseconds(double? value) => value is { } ms
        ? ms < 60_000
            ? (ms / 1000).ToString(ms < 10_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s"
            : SessionText.Duration(TimeSpan.FromMilliseconds(ms))
        : "-";

    /// <summary>A progress phase's blocks: "1,200 of 10,345 blocks (12%)".</summary>
    public static string Blocks(long? done, long? total) =>
        done is { } d && total is { } t && t > 0
            ? TableText.Count(d) + " of " + TableText.Count(t) + " blocks (" + TableText.Percent((double) d / t) + ")"
            : "-";

    /// <summary>A forecast's time, or why there is none.</summary>
    public static string Until(TimeSpan? span, string? rate) =>
        span is { } value ? HorizonForecast.Span(value) : rate is null ? "no rate yet - two samples are needed" : "beyond 90 days at this rate";

    /// <summary>A size.</summary>
    public static string Bytes(long bytes) => TableText.Bytes(bytes);

    /// <summary>A percentage Postgres reports already multiplied by 100.</summary>
    public static string Percent(double percent) => TableText.Percent(percent / 100);

    /// <summary>A whole number, grouped.</summary>
    public static string Count(double value) => TableText.Count(value);
}
