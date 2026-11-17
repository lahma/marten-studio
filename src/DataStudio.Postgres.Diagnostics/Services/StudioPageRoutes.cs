using DataStudio.Postgres.Services.Database;

namespace DataStudio.Postgres.Services;

/// <summary>
/// The addresses of the Postgres module's pages (the whole address below the mount, <c>postgres/plan</c>) and the query-string names a link to one carries, for what the diagnostics engine
/// says and a UI draws: a finding's link, <c>GetStudioPath</c>, the remedies that name a page.
/// </summary>
/// <remarks>
/// <para>
/// <b>One spelling.</b> The UI's own link classes (<c>DatabaseLinks</c>, <c>PlanLinks</c>) build the visitor's links around
/// these, and the engine builds the host's mount-relative strings from the same constants, so the two cannot drift. A route
/// is relative to wherever a UI is mounted and meaningless where none is; it starts with <see cref="PostgresRoutes.Root" />.
/// </para>
/// </remarks>
internal static class StudioPageRoutes
{
    /// <summary>The Plan page's address below the mount.</summary>
    public const string Plan = PostgresRoutes.Root + "/plan";

    /// <summary>Database › Connection's address below the mount (1.1).</summary>
    public const string Connection = PostgresRoutes.Root + "/database/connection";

    /// <summary>The database browser: the schemas, the kind tabs and the grids.</summary>
    public const string DatabaseBrowser = PostgresRoutes.Root + "/database";

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
