namespace DataStudio.Marten.Services;

/// <summary>
/// The one segment the Marten module's own pages sit under: <c>{mount}/marten/documents</c>, <c>{mount}/marten/events/streams</c>. Every Marten route
/// constant (<c>DocumentLinks.Root</c>, <c>EventLinks.*Route</c>, the descriptor's) is built from it, so the address is spelled once; the page
/// components' <c>@page</c> directives carry the same word as text, and the route tests hold the two together.
/// </summary>
/// <remarks>
/// The Postgres pages a Marten host serves (Health, Query, Tables, ...) are not under it: they sit under the Postgres module's own segment, and a Marten
/// page links to them by that name.
/// </remarks>
internal static class MartenRoutes
{
    /// <summary>The Marten module's segment: its key, its page assembly's compile-time root without the slash, and its home route.</summary>
    public const string Root = "marten";

    /// <summary>The address of one Marten page below the mount: <c>Page("projections")</c> is <c>marten/projections</c>.</summary>
    /// <param name="relative">The page's path below the module's segment, with no leading slash.</param>
    public static string Page(string relative) => Root + "/" + relative;
}
