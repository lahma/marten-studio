using System.Text;

using MartenStudio.Components;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;

namespace MartenStudio.Services.Database;

/// <summary>The tabs of the object detail page, in the order they are drawn.</summary>
internal enum DatabaseObjectTab
{
    /// <summary>The relation's rows, where it has any this visitor may read.</summary>
    Rows,

    /// <summary>Its columns.</summary>
    Columns,

    /// <summary>Its row key, constraints, indexes and foreign keys both ways.</summary>
    Keys,

    /// <summary>Its triggers.</summary>
    Triggers,

    /// <summary>What it points at and what points at it.</summary>
    Relationships,

    /// <summary>A view's query, or the sentence that says a table has none.</summary>
    Definition,
}

/// <summary>A row link, read back out of a URL.</summary>
/// <param name="Schema">The relation's schema, or <see langword="null" /> when the link names none.</param>
/// <param name="Name">The relation's name, or <see langword="null" /> likewise.</param>
/// <param name="Key">The key columns and their values, in the order the link carries them.</param>
internal sealed record DatabaseRowLink(string? Schema, string? Name, IReadOnlyList<KeyValuePair<string, string>> Key);

/// <summary>
/// Builds the database browser's URLs, and reads them back.
/// </summary>
/// <remarks>
/// <para>
/// Every piece of state the three screens have is in the URL (D9): the scope, the schema, the kind tab, the
/// owner filter, the name filter, the object and its tab, and a row's key. That is what makes a link
/// somebody pastes into a chat reopen the same screen - and it is why an object's name travels as
/// <c>?name=</c> rather than as a path segment: the hand-rolled router has no catch-all segment, and a
/// Postgres identifier may contain <c>/</c>, spaces, quotes and anything else Unicode has.
/// </para>
/// <para>
/// <b>A row's key is one readable parameter per key column</b> - <c>?key.sched_name=QRTZ&amp;key.trigger_name=nightly</c>
/// - rather than one opaque token, so a person can read a pasted link and edit it to the next row by hand.
/// The <c>key.</c> prefix is what keeps a key column called <c>schema</c>, <c>store</c> or <c>name</c> from
/// colliding with the link's own parameters, and it is stripped exactly once, so a column that is itself
/// called <c>key.x</c> travels as <c>key.key.x</c> and comes back as <c>key.x</c>.
/// </para>
/// <para>
/// The page parameters come first and the scope last, which is the order <see cref="EventLinks" /> writes
/// them in: the part a person reads is at the front of the address bar.
/// </para>
/// </remarks>
internal static class DatabaseLinks
{
    /// <summary>The browser: the schemas, the kind tabs and the grids.</summary>
    public const string BrowserRoute = "database";

    /// <summary>One relation's detail.</summary>
    public const string ObjectRoute = "database/object";

    /// <summary>One row's detail.</summary>
    public const string RowRoute = "database/row";

    /// <summary>The query-string key the schema travels under.</summary>
    public const string SchemaParameter = "schema";

    /// <summary>The query-string key an object's name travels under.</summary>
    public const string NameParameter = "name";

    /// <summary>The query-string key the kind tab travels under.</summary>
    public const string KindParameter = "kind";

    /// <summary>The query-string key the owner filter travels under.</summary>
    public const string OwnerParameter = "owner";

    /// <summary>The query-string key the name filter travels under.</summary>
    public const string FilterParameter = "q";

    /// <summary>The query-string key the object detail tab travels under.</summary>
    public const string TabParameter = "tab";

    /// <summary>What every key-column parameter of a row link starts with.</summary>
    public const string KeyParameterPrefix = "key.";

    /// <summary>How many rows the <c>SELECT</c> that "Open in Query" writes into the console asks for.</summary>
    public const int ConsoleRowLimit = 100;

    /// <summary>The browser, on one kind tab, optionally narrowed to one schema, an owner and a name.</summary>
    /// <param name="options">The studio's options.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" /> for none.</param>
    /// <param name="schema">One schema, or <see langword="null" /> for every schema this visitor may see.</param>
    /// <param name="kind">The kind tab, or <see langword="null" /> for the default (tables).</param>
    /// <param name="owner">The owner filter; <see cref="DatabaseOwnerFilter.All" /> is the default and is left out.</param>
    /// <param name="nameFilter">A case-insensitive substring of the name, or <see langword="null" />.</param>
    public static string ToBrowser(
        MartenStudioOptions options,
        StudioScope? scope,
        string? schema = null,
        DatabaseObjectCategory? kind = null,
        DatabaseOwnerFilter owner = DatabaseOwnerFilter.All,
        string? nameFilter = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        LinkBuilder link = new(StudioLink.To(options, BrowserRoute));

        link.Add(SchemaParameter, schema);
        link.Add(KindParameter, kind is { } category ? KindToken(category) : null);
        link.Add(OwnerParameter, owner == DatabaseOwnerFilter.All ? null : OwnerToken(owner));
        link.Add(FilterParameter, string.IsNullOrWhiteSpace(nameFilter) ? null : nameFilter);
        link.AddScope(scope);

        return link.ToString();
    }

    /// <summary>One relation's detail page.</summary>
    /// <param name="options">The studio's options.</param>
    /// <param name="scope">The scope to carry.</param>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="tab">The tab to open, or <see langword="null" /> for the page's own default.</param>
    /// <param name="extra">
    /// Further parameters to carry - the Rows tab's filter, sort, cursor and page size - in order. A blank
    /// value is left out.
    /// </param>
    public static string ToObject(
        MartenStudioOptions options,
        StudioScope? scope,
        string schema,
        string name,
        DatabaseObjectTab? tab = null,
        IReadOnlyList<KeyValuePair<string, string?>>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(name);

        LinkBuilder link = new(StudioLink.To(options, ObjectRoute));

        link.AddAlways(SchemaParameter, schema);
        link.AddAlways(NameParameter, name);
        link.Add(TabParameter, tab is { } value ? TabToken(value) : null);

        foreach (KeyValuePair<string, string?> pair in extra ?? [])
        {
            link.Add(pair.Key, pair.Value);
        }

        link.AddScope(scope);

        return link.ToString();
    }

    /// <summary>One row's detail page, with one readable parameter per key column.</summary>
    /// <param name="options">The studio's options.</param>
    /// <param name="scope">The scope to carry.</param>
    /// <param name="schema">The relation's schema.</param>
    /// <param name="name">The relation's name.</param>
    /// <param name="key">
    /// The key columns and their values, in key order. An empty value is carried as one - it is a value, and
    /// a key column is never null.
    /// </param>
    /// <param name="extra">Further parameters to carry, in order; a blank value is left out.</param>
    public static string ToRow(
        MartenStudioOptions options,
        StudioScope? scope,
        string schema,
        string name,
        IReadOnlyList<KeyValuePair<string, string>> key,
        IReadOnlyList<KeyValuePair<string, string?>>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(key);

        LinkBuilder link = new(StudioLink.To(options, RowRoute));

        link.AddAlways(SchemaParameter, schema);
        link.AddAlways(NameParameter, name);

        foreach (KeyValuePair<string, string> column in key)
        {
            ArgumentException.ThrowIfNullOrEmpty(column.Key, nameof(key));
            link.AddAlways(KeyParameterPrefix + column.Key, column.Value ?? string.Empty);
        }

        foreach (KeyValuePair<string, string?> pair in extra ?? [])
        {
            link.Add(pair.Key, pair.Value);
        }

        link.AddScope(scope);

        return link.ToString();
    }

    /// <summary>A row link's schema, name and key, read back from a URL (absolute or relative).</summary>
    /// <param name="uri">The URL. Only what follows its <c>?</c> is read; with no <c>?</c> it names nothing.</param>
    public static DatabaseRowLink ReadRow(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        string? schema = null;
        string? name = null;

        foreach (KeyValuePair<string, string> pair in Pairs(uri))
        {
            if (schema is null && string.Equals(pair.Key, SchemaParameter, StringComparison.Ordinal))
            {
                schema = pair.Value;
            }
            else if (name is null && string.Equals(pair.Key, NameParameter, StringComparison.Ordinal))
            {
                name = pair.Value;
            }
        }

        return new DatabaseRowLink(schema, name, ReadKey(uri));
    }

    /// <summary>
    /// The <c>key.&lt;column&gt;</c> parameters of a URL, in the order it carries them, the prefix stripped
    /// once. A column named twice keeps its first value: a key names each column once, so a second one is
    /// somebody's edit gone wrong, and the first is what the link was built with.
    /// </summary>
    /// <param name="uri">The URL, absolute or relative.</param>
    public static IReadOnlyList<KeyValuePair<string, string>> ReadKey(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        List<KeyValuePair<string, string>> key = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (KeyValuePair<string, string> pair in Pairs(uri))
        {
            if (!pair.Key.StartsWith(KeyParameterPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string column = pair.Key[KeyParameterPrefix.Length..];
            if (column.Length > 0 && seen.Add(column))
            {
                key.Add(new KeyValuePair<string, string>(column, pair.Value));
            }
        }

        return key;
    }

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

    /// <summary>The owner filter as <c>?owner=</c> spells it.</summary>
    public static string OwnerToken(DatabaseOwnerFilter owner) => owner switch
    {
        DatabaseOwnerFilter.Marten => "marten",
        DatabaseOwnerFilter.Other => "other",
        _ => "all",
    };

    /// <summary>Reads <c>?owner=</c>, defaulting to everything.</summary>
    public static DatabaseOwnerFilter ParseOwner(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "marten" => DatabaseOwnerFilter.Marten,
        "other" => DatabaseOwnerFilter.Other,
        _ => DatabaseOwnerFilter.All,
    };

    /// <summary>The object detail tab as <c>?tab=</c> spells it.</summary>
    public static string TabToken(DatabaseObjectTab tab) => tab switch
    {
        DatabaseObjectTab.Rows => "rows",
        DatabaseObjectTab.Keys => "keys",
        DatabaseObjectTab.Triggers => "triggers",
        DatabaseObjectTab.Relationships => "relationships",
        DatabaseObjectTab.Definition => "definition",
        _ => "columns",
    };

    /// <summary>Reads <c>?tab=</c>, or <see langword="null" /> when it is absent or not a tab.</summary>
    public static DatabaseObjectTab? ParseTab(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "rows" => DatabaseObjectTab.Rows,
        "columns" => DatabaseObjectTab.Columns,
        "keys" => DatabaseObjectTab.Keys,
        "triggers" => DatabaseObjectTab.Triggers,
        "relationships" => DatabaseObjectTab.Relationships,
        "definition" => DatabaseObjectTab.Definition,
        _ => null,
    };

    /// <summary>
    /// Where a Marten object's data is really read, or <see langword="null" /> when that is here.
    /// </summary>
    /// <remarks>
    /// A document table's rows are read in its Documents collection and the event store's in Streams, where
    /// tenancy, soft delete, archiving and the store's serializer apply; Marten's own bookkeeping is
    /// described on the Schema screen and read nowhere. A flat-table projection or an extended schema object
    /// is relational data Marten manages on the host's behalf, and its rows are browsed right here - as is
    /// everything Marten does not own.
    /// </remarks>
    public static string? ToWhereItLives(MartenStudioOptions options, StudioScope? scope, DatabaseObjectOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ownership);

        return ownership.Owner switch
        {
            DatabaseObjectOwner.MartenDocument => ownership.Alias is { Length: > 0 } alias
                ? DocumentLinks.ToCollection(options, scope, alias)
                : DocumentLinks.ToIndex(options, scope),
            DatabaseObjectOwner.MartenEventStore => ToStreams(options, scope),
            DatabaseObjectOwner.MartenInfrastructure => ToSchema(options, scope),
            _ => null,
        };
    }

    /// <summary>The event streams list.</summary>
    public static string ToStreams(MartenStudioOptions options, StudioScope? scope) =>
        EventLinks.To(options, EventLinks.StreamsRoute, scope);

    /// <summary>The event feed.</summary>
    public static string ToFeed(MartenStudioOptions options, StudioScope? scope) =>
        EventLinks.To(options, EventLinks.FeedRoute, scope);

    /// <summary>The projections screen.</summary>
    public static string ToProjections(MartenStudioOptions options, StudioScope? scope) =>
        EventLinks.To(options, "projections", scope);

    /// <summary>One tab of the Schema screen - by default its tables, which is where Marten's own are described.</summary>
    public static string ToSchema(MartenStudioOptions options, StudioScope? scope, string tab = "tables") =>
        EventLinks.To(options, "schema", scope, [new KeyValuePair<string, string?>(TabParameter, tab)]);

    /// <summary>
    /// The SQL console, with a <c>SELECT</c> of this relation written into it - or <see langword="null" />
    /// when its name cannot be quoted safely, in which case there is no statement to offer.
    /// </summary>
    /// <remarks>
    /// This link only fills the editor. The statement runs when the visitor presses Run, through the console's
    /// own gate: <c>RunSql</c>, the write policy, the statement guard and the read-only transaction (D13).
    /// </remarks>
    public static string? ToQueryConsole(MartenStudioOptions options, StudioScope? scope, string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (SelectStatement(schema, name) is not { } statement)
        {
            return null;
        }

        LinkBuilder link = new(StudioLink.To(options, "query"));

        link.AddAlways("mode", "sql");
        link.AddAlways("sql", statement);
        link.AddScope(scope);

        return link.ToString();
    }

    /// <summary>
    /// <c>select * from "schema"."name" limit 100</c> - text for the console's editor and the clipboard, never
    /// a statement the studio runs on its own - or <see langword="null" /> when a name cannot be quoted.
    /// </summary>
    /// <remarks>
    /// The identifiers go through <see cref="SqlIdentifier.Qualify" />, the builder every generated
    /// statement uses (AGENTS.md hard rule 4), and only after <see cref="DatabaseCatalogQueries.IsQuotable" />
    /// said they can: a name the builder refuses is one the browser lists as "cannot be browsed safely", and
    /// there is no statement to write for it.
    /// </remarks>
    public static string? SelectStatement(string schema, string name, int limit = ConsoleRowLimit)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(name)
            || !DatabaseCatalogQueries.IsQuotable(schema) || !DatabaseCatalogQueries.IsQuotable(name))
        {
            return null;
        }

        return "select * from " + SqlIdentifier.Qualify(schema, name) + " limit "
            + limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The name as a person reads it: <c>schema.name</c>, unquoted.</summary>
    public static string QualifiedName(string schema, string name) => schema + "." + name;

    /// <summary>
    /// Every parameter of <paramref name="uri" />'s query string, decoded, in order. <c>+</c> is a space, as
    /// it is for Blazor's own query-parameter reader, so the two never disagree about a hand-typed link.
    /// </summary>
    private static IEnumerable<KeyValuePair<string, string>> Pairs(string uri)
    {
        int start = uri.IndexOf('?', StringComparison.Ordinal);
        if (start < 0)
        {
            yield break;
        }

        int end = uri.IndexOf('#', start);
        string query = end < 0 ? uri[(start + 1)..] : uri[(start + 1)..end];

        foreach (string part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            string key = equals < 0 ? part : part[..equals];
            string value = equals < 0 ? string.Empty : part[(equals + 1)..];

            yield return new KeyValuePair<string, string>(Decode(key), Decode(value));
        }
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    /// <summary>A link's text, built one escaped parameter at a time.</summary>
    private sealed class LinkBuilder
    {
        private readonly StringBuilder builder;
        private bool first = true;

        public LinkBuilder(string path) => builder = new StringBuilder(path);

        /// <summary>Adds a parameter unless its value is null or blank.</summary>
        public void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                AddAlways(key, value);
            }
        }

        /// <summary>Adds a parameter whatever its value is, an empty one included.</summary>
        public void AddAlways(string key, string value)
        {
            builder.Append(first ? '?' : '&')
                .Append(Uri.EscapeDataString(key))
                .Append('=')
                .Append(Uri.EscapeDataString(value));

            first = false;
        }

        /// <summary>Adds <c>store</c>, <c>db</c> and <c>tenant</c>, each only when it is set.</summary>
        public void AddScope(StudioScope? scope)
        {
            if (scope is null)
            {
                return;
            }

            Add(StudioScopeQuery.StoreKeyParameter, scope.StoreKey);
            Add(StudioScopeQuery.DatabaseIdParameter, scope.DatabaseId);
            Add(StudioScopeQuery.TenantIdParameter, scope.TenantId);
        }

        public override string ToString() => builder.ToString();
    }
}
