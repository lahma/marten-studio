using System.Text;

using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Services.Health;

/// <summary>Where the Health page lives, and how to link to it - and to one finding on it, or from a finding to a screen.</summary>
internal static class HealthLinks
{
    /// <summary>The Health page's address below the mount.</summary>
    public const string Route = PostgresRoutes.Root + "/health";

    /// <summary>A link to the Health page, carrying the scope.</summary>
    /// <param name="sourceParameter">The studio's source parameter (<see cref="DiagnosticsDescriptor.SourceQueryParameter" />).</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    public static string ToPage(string sourceParameter, StudioScope? scope) =>
        To(sourceParameter, Route, scope);

    /// <summary>A link to one finding's card on the Health page.</summary>
    /// <param name="sourceParameter">The studio's source parameter.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="finding">The finding.</param>
    public static string ToFinding(string sourceParameter, StudioScope? scope, HealthReportFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return ToPage(sourceParameter, scope) + "#" + finding.Anchor;
    }

    /// <summary>
    /// A link to <paramref name="route" />, an address below the mount and so relative to the studio-rooted <c>&lt;base href&gt;</c>, with the page's own
    /// <paramref name="parameters" /> first and then the scope (D9) - the same shape every studio link has, so a finding's
    /// link to a screen (<see cref="HealthLink" />) and the screen's own links read alike.
    /// </summary>
    /// <param name="sourceParameter">The studio's source parameter.</param>
    /// <param name="route">The page's address below the mount.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="parameters">The page's own parameters; an entry whose value is blank is left out.</param>
    public static string To(
        string sourceParameter,
        string route,
        StudioScope? scope,
        params IReadOnlyList<KeyValuePair<string, string?>> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceParameter);

        StringBuilder builder = new(route);
        AppendQuery(builder, sourceParameter, scope, parameters);
        return builder.ToString();
    }

    /// <summary>
    /// The same link as <see cref="To" />, as the diagnostics API hands it to a host (<see cref="PostgresPageLink" />): relative to the studio's
    /// mount path with no leading slash, and so starting with <c>postgres/</c>, whichever studio is mounted.
    /// </summary>
    /// <param name="sourceParameter">The studio's source parameter.</param>
    /// <param name="route">The page's address below the mount, with no leading slash.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="parameters">The page's own parameters; an entry whose value is blank is left out.</param>
    public static string Relative(
        string sourceParameter,
        string route,
        StudioScope? scope,
        params IReadOnlyList<KeyValuePair<string, string?>> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceParameter);
        ArgumentNullException.ThrowIfNull(route);

        StringBuilder builder = new(route);
        AppendQuery(builder, sourceParameter, scope, parameters);
        return builder.ToString();
    }

    private static void AppendQuery(
        StringBuilder builder,
        string sourceParameter,
        StudioScope? scope,
        IReadOnlyList<KeyValuePair<string, string?>> parameters)
    {
        bool first = true;

        foreach (KeyValuePair<string, string?> parameter in parameters)
        {
            Append(builder, ref first, parameter.Key, parameter.Value);
        }

        if (scope is not null)
        {
            Append(builder, ref first, sourceParameter, scope.StoreKey);
            Append(builder, ref first, StudioPageRoutes.DatabaseIdParameter, scope.DatabaseId);
            Append(builder, ref first, StudioPageRoutes.TenantIdParameter, scope.TenantId);
        }
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
