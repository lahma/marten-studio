using DataStudio.Postgres.Components.Pages;
using DataStudio.Postgres.Components.Pages.Query;
using DataStudio.Postgres.Services.Connection;
using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Maintenance;
using DataStudio.Postgres.Services.Query;
using DataStudio.Postgres.Services.Replication;
using DataStudio.Postgres.Services.Server;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Postgres.Services.Statements;
using DataStudio.Postgres.Services.Storage;
using DataStudio.Services.Settings;

namespace DataStudio.Postgres.Services.Postgres;

/// <summary>
/// The Postgres module's <see cref="StudioDescriptor" />: its one page assembly, its sidebar, its nouns and its authorization
/// resource.
/// </summary>
/// <remarks>
/// <para>
/// One instance per process, immutable. The source parameter is <c>store</c>, as MartenStudio's is, so the shared code
/// that reads and writes a scope needs no second spelling; what a visitor reads is the label, "Database".
/// </para>
/// <para>
/// <b>Its pages are this assembly's alone.</b> No other assembly is named here - so a mapping with the Postgres module never serves a
/// Marten page, even in a process whose DataStudio.Marten assembly is loaded.
/// </para>
/// </remarks>
internal static class PostgresStudioDescriptor
{
    /// <summary>PostgresStudio's descriptor key, and its registration marker's.</summary>
    public const string Key = PostgresRoutes.Root;

    /// <summary>The compile-time root every Postgres page's <c>@page</c> starts with, and the first segment of its address.</summary>
    internal const string PageRoot = "/" + PostgresRoutes.Root;

    /// <summary>The descriptor every Postgres module service and component reads.</summary>
    public static StudioDescriptor Instance { get; } = Create();

    /// <summary>What <c>AddPostgres()</c> claims, so a second database module in the same container is refused.</summary>
    public static StudioRegistrationMarker Marker { get; } = new(Key, "the Postgres module", "AddPostgres()");

    private static StudioDescriptor Create() => new()
    {
        Key = Key,
        ProductName = "Postgres Studio",
        PageAssemblies =
        [
            new StudioPageAssembly(typeof(StudioHome).Assembly, PageRoot),
        ],

        // The shared checks, which read nothing but Postgres, run against every kind of source this studio has: the engine's
        // (the diagnostics assembly) and the ones that judge this studio's own exposure (this assembly).
        HealthCheckAssemblies =
        [
            new StudioHealthCheckAssembly(typeof(PostgresDiagnosticsDescriptor).Assembly),
            new StudioHealthCheckAssembly(typeof(StudioHome).Assembly),
        ],
        Navigation = Navigation,
        Summary = "Health, queries, tables and sessions of the application's data sources.",
        QueryComponent = typeof(PostgresQuery),
        SourceQueryParameter = "store",
        SourceNoun = "data source",
        SourceLabel = "Database",
        ExposedContent = "the structure of every database this application registered, and whatever its capabilities read,",
        CreateResource = static (scope, capability) => new PostgresResource(scope.StoreKey, scope.DatabaseId, capability),
    };

    /// <summary>The sidebar: Overview and Query, the database section, Health, Replication and Server. Activity is the shell's own.</summary>
    private static IReadOnlyList<StudioNavSection> Navigation(IStudioSettings studio) =>
    [
        new("Data",
        [
            new("Overview", PostgresRoutes.Root, "grid") { Exact = true },

            // Query is the SQL console: with RunSql off (or ReadOnly on) it opens a sentence for the developer who configures the
            // host, so it is not drawn then (issue #37). A visitor who reaches it by address still reads the notice.
            new("Query", PostgresRoutes.Page("query"), "query", Title: "The SQL console")
            {
                IsShown = _ => StudioNavConditions.RunSqlEnabled(studio),
            },

            // X3 (issue #28): a statement's plan drawn every way, and what to fix. Running one is the console's gate; pasting one reads
            // nothing and is every visitor's (1.0-P3A), so the entry is always drawn.
            new("Plan", PlanLinks.Route, "plan", Title: "A statement's plan, drawn every way, with what to fix"),
        ]),
        new("Database",
        [
            new("Tables", StudioPageRoutes.DatabaseBrowser, "table", Title: "Tables, views, functions, triggers, sequences and types"),
            new("Sessions", SessionLinks.Route, "feed", Title: "Sessions, locks and prepared transactions"),

            // 1.1-NP3: how the application connects - its Npgsql settings and where each came from, what the server saw, and what to change.
            new("Connection", ConnectionLinks.Route, "plug", Title: "How the application connects: its Npgsql settings, where each came from, what the server saw and what to change"),
            new("Relationships", DatabaseLinks.RelationshipsRoute, "graph"),
        ]),
        new("Operations",
        [
            new("Health", HealthLinks.Route, "pulse", StudioNavBadgeKeys.Health),

            // W5-Q1: the top statements by total time, after Health and before Maintenance (W5-index §4).
            new("Statements", StatementLinks.Route, "statements", Title: "The top statements by total time, over the window the statement checks watched"),
            new("Maintenance", MaintenanceLinks.Route, "maintenance", Title: "Autovacuum's triggers, running maintenance, the freeze forecast and the horizon"),

            // W5-CAP: the database's size, growth and largest tables, after Maintenance.
            new("Storage", StorageLinks.Route, "storage", Title: "Database size, growth and the largest tables"),

            // W5-R2: after Storage (W5-index §4).
            new("Replication", ReplicationLinks.Route, "replication", Title: "Slots, standbys, the archiver, WAL, checkpoints and I/O"),
        ]),
        new("System",
        [
            // W5-S2: the shared Server page, first in System (W5-index §4).
            new("Server", ServerLinks.Route, "server", Title: "Settings, extensions, the roles the studio reads as, and the security posture"),
        ]),
    ];
}
