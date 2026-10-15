using MartenStudio.Services.Events;

namespace MartenStudio.Services.Health;

/// <summary>Where the Health page lives, and how to link to it - and to one finding on it.</summary>
internal static class HealthLinks
{
    /// <summary>The Health page, relative to the studio root.</summary>
    public const string Route = "health";

    /// <summary>A link to the Health page, carrying the scope.</summary>
    public static string ToPage(MartenStudioOptions options, StudioScope? scope) =>
        EventLinks.To(options, Route, scope);

    /// <summary>A link to one finding's card on the Health page.</summary>
    public static string ToFinding(MartenStudioOptions options, StudioScope? scope, HealthReportFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return ToPage(options, scope) + "#" + finding.Anchor;
    }
}
