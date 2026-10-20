using PostgresStudio.Components.Pages;
using PostgresStudio.Components.Pages.Query;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// PostgresStudio's <see cref="StudioDescriptor" />: its one page assembly, its sidebar, its nouns and its authorization
/// resource.
/// </summary>
/// <remarks>
/// <para>
/// One instance per process, immutable. The source parameter is <c>store</c>, as MartenStudio's is, so the shared code
/// that reads and writes a scope needs no second spelling; what a visitor reads is the label, "Database".
/// </para>
/// <para>
/// <b>Its pages are this assembly's alone.</b> The root component is <c>PostgresStudioApp</c>, whose assembly is the
/// discovery root, and no other assembly is named here - so a PostgresStudio mapping never serves a Marten page, even
/// in a process whose MartenStudio assembly is loaded.
/// </para>
/// </remarks>
internal static class PostgresStudioDescriptor
{
    /// <summary>PostgresStudio's descriptor key, and its registration marker's.</summary>
    public const string Key = "postgres";

    /// <summary>The descriptor every PostgresStudio service and component reads.</summary>
    public static StudioDescriptor Instance { get; } = Create();

    /// <summary>What <c>AddPostgresStudio()</c> claims, so a second studio in the same container is refused.</summary>
    public static StudioRegistrationMarker Marker { get; } = new(Key, "Postgres Studio", "AddPostgresStudio()");

    private static StudioDescriptor Create() => new()
    {
        Key = Key,
        ProductName = "Postgres Studio",
        PageAssemblies =
        [
            new StudioPageAssembly(typeof(StudioHome).Assembly, PostgresStudioOptions.DefaultPath),
        ],

        // The shared checks, which read nothing but Postgres, run against every kind of source this studio has.
        HealthCheckAssemblies =
        [
            new StudioHealthCheckAssembly(typeof(StudioHome).Assembly),
        ],
        Navigation = Navigation(),
        OverviewComponent = typeof(PostgresOverview),
        QueryComponent = typeof(PostgresQuery),
        SourceQueryParameter = "store",
        SourceNoun = "data source",
        SourceLabel = "Database",
        ExposedContent = "the structure of every database this application registered, and whatever its capabilities read,",
        CreateResource = static (scope, capability) => new PostgresStudioResource(scope.StoreKey, scope.DatabaseId, capability),
        Surface = "Postgres Studio",
        Exposure = "Postgres Studio is mapped with no authorization. Its pages show the structure of every database this "
            + "application registered and, with its capabilities on, can read their rows, run SQL against them and list other "
            + "sessions' statements, so Postgres Studio refuses to start rather than serve that by accident. Say which you meant:",
        Remedies = """
              - app.MapPostgresStudio().RequireAuthorization() authorizes its pages and its Blazor circuit;
              - services.AddPostgresStudio(options => options.AuthorizationPolicy = "...") authorizes those and the static assets with them;
              - app.MapPostgresStudio().AllowAnonymous() serves it to anyone, deliberately.
            """,
    };

    /// <summary>The sidebar: Overview and Query, the database section, Health and Activity.</summary>
    private static IReadOnlyList<StudioNavSection> Navigation() =>
    [
        new("Data",
        [
            new("Overview", "", "grid"),
            new("Query", "query", "query", Title: "The SQL console"),
        ]),
        new("Database",
        [
            new("Tables", "database", "table", Title: "Tables, views, functions, triggers, sequences and types"),
            new("Sessions", SessionLinks.Route, "feed", Title: "Sessions, locks and prepared transactions"),
            new("Relationships", "relationships", "graph"),
        ]),
        new("Operations",
        [
            new("Health", "health", "pulse", StudioNavBadgeKeys.Health),
        ]),
        new("System",
        [
            new("Activity", "activity", "list"),
        ]),
    ];
}
