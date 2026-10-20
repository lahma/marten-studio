using System.Text;

using MartenStudio.Components;
using MartenStudio.Services.Settings;

namespace MartenStudio.Services.Health;

/// <summary>Where the Health page lives, and how to link to it - and to one finding on it, or from a finding to a screen.</summary>
internal static class HealthLinks
{
    /// <summary>The Health page, relative to the studio root.</summary>
    public const string Route = "health";

    /// <summary>A link to the Health page, carrying the scope.</summary>
    /// <param name="settings">The studio's settings, for its root.</param>
    /// <param name="sourceParameter">The studio's source parameter (<see cref="StudioDescriptor.SourceQueryParameter" />).</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    public static string ToPage(IStudioSettings settings, string sourceParameter, StudioScope? scope) =>
        To(settings, sourceParameter, Route, scope);

    /// <summary>A link to one finding's card on the Health page.</summary>
    /// <param name="settings">The studio's settings, for its root.</param>
    /// <param name="sourceParameter">The studio's source parameter.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="finding">The finding.</param>
    public static string ToFinding(IStudioSettings settings, string sourceParameter, StudioScope? scope, HealthReportFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return ToPage(settings, sourceParameter, scope) + "#" + finding.Anchor;
    }

    /// <summary>
    /// A link to <paramref name="route" />, relative to the studio-rooted <c>&lt;base href&gt;</c>, with the page's own
    /// <paramref name="parameters" /> first and then the scope (D9) - the same shape every studio link has, so a finding's
    /// link to a screen (<see cref="HealthLink" />) and the screen's own links read alike.
    /// </summary>
    /// <param name="settings">The studio's settings, for its root.</param>
    /// <param name="sourceParameter">The studio's source parameter.</param>
    /// <param name="route">The page, relative to the studio root.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="parameters">The page's own parameters; an entry whose value is blank is left out.</param>
    public static string To(
        IStudioSettings settings,
        string sourceParameter,
        string route,
        StudioScope? scope,
        params IReadOnlyList<KeyValuePair<string, string?>> parameters)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceParameter);

        StringBuilder builder = new(StudioLink.To(settings, route));
        bool first = true;

        foreach (KeyValuePair<string, string?> parameter in parameters)
        {
            Append(builder, ref first, parameter.Key, parameter.Value);
        }

        if (scope is not null)
        {
            Append(builder, ref first, sourceParameter, scope.StoreKey);
            Append(builder, ref first, StudioScopeQuery.DatabaseIdParameter, scope.DatabaseId);
            Append(builder, ref first, StudioScopeQuery.TenantIdParameter, scope.TenantId);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, ref bool first, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(first ? '?' : '&')
            .Append(Uri.EscapeDataString(key))
            .Append('=')
            .Append(Uri.EscapeDataString(value));

        first = false;
    }
}
