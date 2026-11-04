using PostgresStudio.Services.Database;

namespace PostgresStudio.Services;

/// <summary>
/// The addresses of the studio's pages and the query-string names a link to one carries, for what the diagnostics engine
/// says and a UI draws: a finding's link, <c>GetStudioPath</c>, the remedies that name a page.
/// </summary>
/// <remarks>
/// <para>
/// <b>One spelling.</b> The UI's own link classes (<c>DatabaseLinks</c>, <c>PlanLinks</c>) build the visitor's links around
/// these, and the engine builds the host's mount-relative strings from the same constants, so the two cannot drift. A route
/// is relative to wherever a UI is mounted and meaningless where none is.
/// </para>
/// </remarks>
internal static class StudioPageRoutes
{
    /// <summary>The Plan page's route under the studio's path.</summary>
    public const string Plan = "plan";

    /// <summary>Database › Connection's route under the studio's path (1.1).</summary>
    public const string Connection = "database/connection";

    /// <summary>The database browser: the schemas, the kind tabs and the grids.</summary>
    public const string DatabaseBrowser = "database";

    /// <summary>The query-string key the browser's schema travels under.</summary>
    public const string DatabaseSchemaParameter = "schema";

    /// <summary>The query-string key the browser's kind tab travels under.</summary>
    public const string DatabaseKindParameter = "kind";

    /// <summary>The query-string key the browser's name filter travels under.</summary>
    public const string DatabaseFilterParameter = "q";

    /// <summary>The query-string key the scope's database travels under.</summary>
    public const string DatabaseIdParameter = "db";

    /// <summary>The query-string key the scope's tenant travels under.</summary>
    public const string TenantIdParameter = "tenant";

    /// <summary>
    /// <paramref name="route" /> as a link carries it: relative to the studio-rooted document base, where an empty href
    /// resolves to the base URI (RFC 3986 section 5.4), which is the studio's own front page.
    /// </summary>
    /// <param name="route">The page's route.</param>
    public static string BaseRelative(string route) => route;

    /// <summary>The kind tab as <c>?kind=</c> spells it.</summary>
    public static string KindToken(DatabaseObjectCategory category) => category switch
    {
        DatabaseObjectCategory.Views => "views",
        DatabaseObjectCategory.Functions => "functions",
        DatabaseObjectCategory.Triggers => "triggers",
        DatabaseObjectCategory.Sequences => "sequences",
        DatabaseObjectCategory.Types => "types",
        _ => "tables",
    };

    /// <summary>Reads <c>?kind=</c>, defaulting to tables.</summary>
    public static DatabaseObjectCategory ParseKind(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "views" => DatabaseObjectCategory.Views,
        "functions" => DatabaseObjectCategory.Functions,
        "triggers" => DatabaseObjectCategory.Triggers,
        "sequences" => DatabaseObjectCategory.Sequences,
        "types" => DatabaseObjectCategory.Types,
        _ => DatabaseObjectCategory.Tables,
    };

    /// <summary>The name as a person reads it: <c>schema.name</c>, unquoted.</summary>
    public static string QualifiedName(string schema, string name) => schema + "." + name;
}
