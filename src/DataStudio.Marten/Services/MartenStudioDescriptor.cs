using System.Collections.Frozen;

using DataStudio.Marten.Services.Documents;
using DataStudio.Marten.Services.Events;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Live;
using DataStudio.Postgres.Services.Maintenance;
using DataStudio.Postgres.Services.Query;
using DataStudio.Postgres.Services.Replication;
using DataStudio.Postgres.Services.Server;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Postgres.Services.Sources;
using DataStudio.Postgres.Services.Statements;
using DataStudio.Postgres.Services.Storage;
using DataStudio.Services.Settings;

namespace DataStudio.Marten.Services;

/// <summary>
/// MartenStudio's <see cref="StudioDescriptor" />: its pages, its sidebar, its nouns and its authorization resource.
/// </summary>
/// <remarks>
/// One instance per process, immutable: everything on it is a constant of the package. Every string here is the one
/// the shared code used to say itself, moved, so that registering the descriptor changed nothing a host or a visitor
/// reads.
/// </remarks>
internal static class MartenStudioDescriptor
{
    /// <summary>MartenStudio's descriptor key, and its registration marker's.</summary>
    public const string Key = MartenRoutes.Root;

    /// <summary>The query-string parameter a store key travels under.</summary>
    public const string SourceQueryParameter = "store";

    /// <summary>The Stores page's address below the mount (1.2).</summary>
    public const string StoresRoute = MartenRoutes.Root + "/stores";

    /// <summary>The Tenants page's address below the mount (1.2).</summary>
    public const string TenantsRoute = MartenRoutes.Root + "/tenants";

    /// <summary>The dead-letter entry's badge key.</summary>
    public const string DeadLettersBadge = "dead-letters";

    /// <summary>The projections entry's badge key.</summary>
    public const string ProjectionsBadge = "projections";

    /// <summary>The source kinds MartenStudio's own health checks run against: a Marten store, and nothing else.</summary>
    internal static IReadOnlySet<StudioSourceKind> MartenStoreOnly { get; } =
        FrozenSet.Create(StudioSourceKind.MartenStore);

    /// <summary>The descriptor every MartenStudio service and component reads.</summary>
    public static StudioDescriptor Instance { get; } = Create();

    /// <summary>The compile-time root every Marten page's <c>@page</c> starts with, and the first segment of its address.</summary>
    internal const string PageRoot = "/" + MartenRoutes.Root;

    /// <summary>What <c>AddMarten()</c> claims, so a second database module in the same container is refused.</summary>
    public static StudioRegistrationMarker Marker { get; } = new(Key, "the Marten module", "AddMarten()");

    private static StudioDescriptor Create() => new()
    {
        Key = Key,
        ProductName = "Marten",
        PageAssemblies =
        [
            new StudioPageAssembly(typeof(Components.Pages.Overview).Assembly, PageRoot),
            new StudioPageAssembly(typeof(DataStudio.Postgres.Components.Pages.StudioHome).Assembly, "/" + PostgresRoutes.Root),
        ],
        // The shared checks, the diagnostics engine's and PostgresStudio's own (the studio-posture ones), run against any source;
        // MartenStudio's own read the store and only ever run against one. Every source MartenStudio has is a Marten store, so
        // here every set runs against all of them.
        HealthCheckAssemblies =
        [
            new StudioHealthCheckAssembly(typeof(HealthCheckRegistry).Assembly),
            new StudioHealthCheckAssembly(typeof(StudioDescriptor).Assembly),
            new StudioHealthCheckAssembly(typeof(Components.Pages.Overview).Assembly, MartenStoreOnly),
        ],
        Navigation = Navigation,
        Summary = "Documents, event streams, projections and the databases under them.",
        QueryComponent = typeof(Components.Pages.Query.Query),
        SourceQueryParameter = SourceQueryParameter,
        RetiredCheckCodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["K10"] = "1.1" },
        SourceNoun = "Marten store",
        SourceLabel = "Store",
        ExposedContent = "every document, event stream and schema in this process",
        ExtraShortcuts = MartenStudioShortcuts.Groups,
        CreateResource = static (scope, capability) => scope.ToResource(capability),
    };

    /// <summary>The sidebar, as it has been since 0.3.0's database section.</summary>
    private static IReadOnlyList<StudioNavSection> Navigation(IStudioSettings studio) =>
    [
        new("Data",
        [
            new("Overview", MartenRoutes.Root, "grid") { Exact = true },

            // 1.2 (SCOPE-1): every store the visitor may see, drawn only while there are two or more - the header's store picker's own
            // test. A single-store host never sees it; the address still works and shows the one store.
            new("Stores", StoresRoute, "grid", Title: "Every store you may see: whether it builds, its tenancy, its databases and their health")
            {
                ShownWhen = DatabaseRailConditions.SeveralSources,
            },

            // 1.2 (SCOPE-2): the tenants the visitor may address, drawn where the header draws a tenant picker: a store with anything tenanted
            // per row (TenantDiscovery.IsTenantScopeRelevant, which the scope state already holds as ShowTenantSelector).
            new("Tenants", TenantsRoute, "list", Title: "The tenants you may address, where each lives and how big it is")
            {
                ShownWhen = DatabaseRailConditions.TenantSelector,
            },
            new("Documents", DocumentLinks.Root, "doc"),

            // Query stays whatever RunSql says (issue #37): besides the SQL console it hosts Mode A, a typed where clause that
            // needs no capability, and LINQ mode, which has its own (RunLinqQueries).
            new("Query", PostgresRoutes.Page("query"), "query"),

            // X3 (issue #28): PostgresStudio's Plan page, which the studio re-roots under /marten like every shared page. Running a
            // plan is the console's gate; pasting one reads nothing and is every visitor's (1.0-P3A), so the entry is always drawn.
            new("Plan", PlanLinks.Route, "plan", Title: "A statement's plan, drawn every way, with what to fix"),
        ]),

        // What else lives in the store's database, beside what Marten declares (D27). Relationships moved
        // here from Data with its route unchanged: foreign keys are a database fact, and the diagram draws
        // plain tables as well as document types. The Tables entry's active state covers
        // /marten/database/* through NavClass's prefix match, so object detail keeps it lit.
        new("Database",
        [
            new("Tables", StudioPageRoutes.DatabaseBrowser, "table", Title: "Tables, views, functions, triggers, sequences and types"),

            // 1.1-NP3: PostgresStudio's Connection page, re-rooted under /marten like every shared page: how the store connects.
            new("Connection", DataStudio.Postgres.Services.Connection.ConnectionLinks.Route, "plug", Title: "How the store connects: its Npgsql settings, where each came from, what the server saw and what to change"),
            new("Relationships", DatabaseLinks.RelationshipsRoute, "graph"),
        ]),
        new("Events",
        [
            new("Streams", EventLinks.StreamsRoute, "stream"),
            new("Feed", EventLinks.FeedRoute, "feed"),
            new("Event types", EventLinks.EventTypesRoute, "tag"),
            new("Event flow", EventLinks.EventFlowRoute, "flow", Title: "Which projections read each event type, and what they write"),

            // Absent while the host hides DeadLetterEvent (DeadLetterVisibility, failing closed): a hidden document type
            // is absent (D27), as the Overview's tile is, and the page it would link to says why if reached by address.
            new("Dead letters", EventLinks.DeadLettersRoute, "alert", DeadLettersBadge)
            {
                IsShown = shell => DeadLetterVisibility.IsVisible(studio.IsDocumentTypeVisible, out _),
            },
        ]),
        new("Operations",
        [
            // Health is first in its section: it is where a person starts when something is wrong, and its
            // badge reads the last sweep a Health page or the Overview ran - it never starts one.
            new("Health", HealthLinks.Route, "pulse", StudioNavBadgeKeys.Health),

            // W5-Q1: the top statements by total time, after Health and before Maintenance (W5-index §4).
            new("Statements", StatementLinks.Route, "statements", Title: "The top statements by total time, over the window the statement checks watched"),
            new("Maintenance", MaintenanceLinks.Route, "maintenance", Title: "Autovacuum's triggers, running maintenance, the freeze forecast and the horizon"),

            // W5-CAP: the database's size, growth and largest tables, after Maintenance.
            new("Storage", StorageLinks.Route, "storage", Title: "Database size, growth and the largest tables"),

            // W5-R2: the shared Replication page, after Storage (W5-index §4).
            new("Replication", ReplicationLinks.Route, "replication", Title: "Slots, standbys, the archiver, WAL, checkpoints and I/O"),
            new("Projections", EventLinks.ProjectionsRoute, "projection", ProjectionsBadge),

            // Schema deliberately carries no badge. Finding drift costs a CreateMigrationAsync, which is
            // a schema-building call that hard rule 14 keeps off every navigation path - so drift stays
            // behind the button on the schema screen that says what it may create.
            new("Schema", MartenRoutes.Page("schema"), "schema"),
        ]),
        new("System",
        [
            // W5-S2: the shared Server page, first in System (W5-index §4).
            new("Server", ServerLinks.Route, "server", Title: "Settings, extensions, the roles the studio reads as, and the security posture"),
            new("Configuration", MartenRoutes.Page("config"), "cog"),
        ]),
    ];
}
