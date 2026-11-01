namespace PostgresStudio.Services.Postgres;

/// <summary>The Overview's address: the studio's landing page, which is the studio's own root.</summary>
internal static class OverviewLinks
{
    /// <summary>
    /// The page's route under the studio's path: empty. A relative address built from it is the query alone, which resolves
    /// against the studio-rooted <c>&lt;base href&gt;</c> to the front page, with the scope carried.
    /// </summary>
    public const string Route = "";
}
