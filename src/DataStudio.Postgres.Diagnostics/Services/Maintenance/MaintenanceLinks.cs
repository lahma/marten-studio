namespace DataStudio.Postgres.Services.Maintenance;

/// <summary>Operations › Maintenance's address (W5-V1), for the pages that point at it - Storage's largest tables (W5-CAP).</summary>
internal static class MaintenanceLinks
{
    /// <summary>The page's address below the mount.</summary>
    public const string Route = PostgresRoutes.Root + "/maintenance";
}
