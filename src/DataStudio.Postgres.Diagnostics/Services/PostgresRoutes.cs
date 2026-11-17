namespace DataStudio.Postgres.Services;

/// <summary>
/// The one segment the Postgres module's pages sit under: <c>{mount}/postgres/health</c>, <c>{mount}/postgres/query</c>. Every Postgres route
/// constant (<see cref="StudioPageRoutes" />, each page's <c>*Links.Route</c>) is built from it, so the address is spelled once; the page
/// components' <c>@page</c> directives carry the same word as text, and the route tests hold the two together.
/// </summary>
/// <remarks>
/// A Marten host serves the same pages under the same segment, over its stores' databases: the Postgres pages are not the Marten module's, and
/// a Marten page links to them by this name. No Postgres page ever names a Marten route (hard rule 16).
/// </remarks>
internal static class PostgresRoutes
{
    /// <summary>The Postgres module's segment: its key, its page assemblies' compile-time root without the slash, and its home route.</summary>
    public const string Root = "postgres";

    /// <summary>
    /// The address of one Postgres page below the mount: <c>Page("query")</c> is <c>postgres/query</c>.
    /// </summary>
    /// <param name="relative">The page's path below the module's segment, with no leading slash.</param>
    public static string Page(string relative) => Root + "/" + relative;
}
