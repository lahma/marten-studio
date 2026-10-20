using MartenStudio.Components;
using MartenStudio.Services.Events;
using MartenStudio.Services.Settings;

namespace MartenStudio.Services;

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
    public const string Key = "marten";

    /// <summary>The query-string parameter a store key travels under.</summary>
    public const string SourceQueryParameter = "store";

    /// <summary>The dead-letter entry's badge key.</summary>
    public const string DeadLettersBadge = "dead-letters";

    /// <summary>The projections entry's badge key.</summary>
    public const string ProjectionsBadge = "projections";

    /// <summary>The descriptor every MartenStudio service and component reads.</summary>
    public static StudioDescriptor Instance { get; } = Create();

    /// <summary>What <c>AddMartenStudio()</c> claims, so a second studio in the same container is refused.</summary>
    public static StudioRegistrationMarker Marker { get; } = new(Key, "Marten Studio", "AddMartenStudio()");

    private static StudioDescriptor Create() => new()
    {
        Key = Key,
        ProductName = "Marten Studio",
        PageAssemblies = [new StudioPageAssembly(typeof(MartenStudioApp).Assembly, MartenStudioOptions.DefaultPath)],
        HealthCheckAssemblies = [typeof(MartenStudioApp).Assembly],
        Navigation = Navigation(),
        OverviewComponent = typeof(Components.Pages.Overview),
        QueryComponent = typeof(Components.Pages.Query.Query),
        SourceQueryParameter = SourceQueryParameter,
        SourceNoun = "Marten store",
        SourceLabel = "Store",
        ExposedContent = "every document, event stream and schema in this process",
        CreateResource = static (scope, capability) => scope.ToResource(capability),
        Surface = "Marten Studio",
        Exposure = "Marten Studio is mapped with no authorization. Its pages can read and edit every document in every Marten "
            + "store in this process, archive event streams, control the projection daemon, apply schema changes and run SQL, "
            + "so Marten Studio refuses to start rather than serve that by accident. Say which you meant:",
        Remedies = """
              - app.MapMartenStudio().RequireAuthorization() authorizes its pages and its Blazor circuit;
              - services.AddMartenStudio(options => options.AuthorizationPolicy = "...") authorizes those and the static assets with them;
              - app.MapMartenStudio().AllowAnonymous() serves it to anyone, deliberately.
            """,
    };

    /// <summary>The sidebar, as it has been since 0.3.0's database section.</summary>
    private static IReadOnlyList<StudioNavSection> Navigation() =>
    [
        new("Data",
        [
            new("Overview", "", "grid"),
            new("Documents", "documents", "doc"),
            new("Query", "query", "query"),
        ]),

        // What else lives in the store's database, beside what Marten declares (D27). Relationships moved
        // here from Data with its route unchanged: foreign keys are a database fact, and the diagram draws
        // plain tables as well as document types. The Tables entry's active state covers
        // /marten/database/* through NavClass's prefix match, so object detail keeps it lit.
        new("Database",
        [
            new("Tables", "database", "table", Title: "Tables, views, functions, triggers, sequences and types"),
            new("Relationships", "relationships", "graph"),
        ]),
        new("Events",
        [
            new("Streams", "events/streams", "stream"),
            new("Feed", "events/feed", "feed"),
            new("Event types", "events/types", "tag"),

            // Absent while the host hides DeadLetterEvent (DeadLetterVisibility, failing closed): a hidden document type
            // is absent (D27), as the Overview's tile is, and the page it would link to says why if reached by address.
            new("Dead letters", "events/dead-letters", "alert", DeadLettersBadge)
            {
                IsShown = static settings => DeadLetterVisibility.IsVisible(settings.IsDocumentTypeVisible, out _),
            },
        ]),
        new("Operations",
        [
            // Health is first in its section: it is where a person starts when something is wrong, and its
            // badge reads the last sweep a Health page or the Overview ran - it never starts one.
            new("Health", "health", "pulse", StudioNavBadgeKeys.Health),
            new("Projections", "projections", "projection", ProjectionsBadge),

            // Schema deliberately carries no badge. Finding drift costs a CreateMigrationAsync, which is
            // a schema-building call that hard rule 14 keeps off every navigation path - so drift stays
            // behind the button on the schema screen that says what it may create.
            new("Schema", "schema", "schema"),
        ]),
        new("System",
        [
            new("Configuration", "config", "cog"),
            new("Activity", "activity", "list"),
        ]),
    ];
}
