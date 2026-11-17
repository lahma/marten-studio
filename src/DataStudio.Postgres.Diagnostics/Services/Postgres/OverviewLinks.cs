namespace DataStudio.Postgres.Services.Postgres;

/// <summary>The Postgres Overview's address: the Postgres module's home. In a Marten host that address sends the visitor to the Marten module's own home.</summary>
internal static class OverviewLinks
{
    /// <summary>
    /// The page's address below the mount: the module's own segment, which is its home.
    /// </summary>
    public const string Route = PostgresRoutes.Root;
}
