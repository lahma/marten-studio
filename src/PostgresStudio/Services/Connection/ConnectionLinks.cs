using PostgresStudio.Components;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Connection;

/// <summary>Database › Connection's address as a visitor's link.</summary>
internal static class ConnectionLinks
{
    /// <summary>The page's route under the studio's path.</summary>
    public const string Route = "database/connection";

    /// <summary>
    /// The page, with the scope carried (D9) under the studio's own source parameter, and optionally a fragment: a link to a row
    /// or a card on the page itself. The fragment is part of the link because the studio's <c>&lt;base href&gt;</c> is its
    /// root, so a bare <c>#row</c> would leave the page.
    /// </summary>
    /// <param name="settings">The studio's settings.</param>
    /// <param name="descriptor">The studio's descriptor: its source parameter.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="fragment">An element id on the page, or <see langword="null" />.</param>
    public static string To(IStudioSettings settings, StudioDescriptor descriptor, StudioScope? scope, string? fragment = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(descriptor);

        string link = StudioScopeQuery.Append(StudioLink.To(settings, Route), scope, descriptor.SourceQueryParameter);

        return fragment is { Length: > 0 } ? link + "#" + Uri.EscapeDataString(fragment) : link;
    }
}
