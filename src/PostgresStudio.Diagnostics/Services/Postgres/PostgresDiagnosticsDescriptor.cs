using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// The <see cref="DiagnosticsDescriptor" /> of a host that registered <c>AddPostgresDiagnostics()</c> and no studio: the
/// diagnostics assembly's checks, run against every kind of source it has, and the names a source carries.
/// </summary>
/// <remarks>
/// <see cref="Key" /> and the source parameter are the studio's, so a host's code that reads a path or a code cannot tell the
/// two registrations apart; a studio's own descriptor adds its pages and its other checks to the same values.
/// </remarks>
internal static class PostgresDiagnosticsDescriptor
{
    /// <summary>The descriptor key: Postgres Studio's.</summary>
    public const string Key = "postgres";

    /// <summary>The descriptor every engine service reads in a host with no studio.</summary>
    public static DiagnosticsDescriptor Instance { get; } = new()
    {
        Key = Key,
        ProductName = "Postgres Studio",

        // The shared checks, which read nothing but Postgres, run against every kind of source.
        HealthCheckAssemblies = [new StudioHealthCheckAssembly(typeof(PostgresDiagnosticsDescriptor).Assembly)],
        SourceQueryParameter = "store",
        SourceNoun = "data source",
        SourceLabel = "Database",
    };
}
