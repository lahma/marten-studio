using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using Marten.Schema;

using MartenStudio.Internal.Sql;

using Weasel.Postgresql.Tables;

namespace MartenStudio.Services.Query;

/// <summary>One index a document type declares, as the plan insights read it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Definition">Marten's own <c>CREATE INDEX</c> statement for it - the text <c>pg_indexes</c> would hold once applied.</param>
/// <param name="Computed">Whether it is a computed index over members of the JSON (<c>Index(x =&gt; x.Member)</c>), rather than over a column.</param>
internal sealed record MartenDeclaredIndex(string Name, string Definition, bool Computed);

/// <summary>
/// 1.0-P3C: a document table the store declares, as the plan insights P26 to P28 judge a plan against - the type's mapping read from
/// <c>StoreOptions</c> alone, so nothing here touches a connection (hard rule 14).
/// </summary>
/// <remarks>
/// A hidden document type has no instance: <see cref="MartenPlanTables.Read" /> never builds one, so the type's table is any table
/// to the insights - never matched, never named.
/// </remarks>
internal sealed class MartenPlanTable
{
    /// <summary>The table's schema.</summary>
    public required string Schema { get; init; }

    /// <summary>The table.</summary>
    public required string Table { get; init; }

    /// <summary>The document type's name, as a <c>StoreOptions</c> line writes it.</summary>
    public required string TypeName { get; init; }

    /// <summary>The document type.</summary>
    public required Type DocumentType { get; init; }

    /// <summary>The table's columns as the mapping declares them: duplicated fields, tenancy, soft delete.</summary>
    public required DocumentTableInfo Info { get; init; }

    /// <summary>The indexes the type declares.</summary>
    public required IReadOnlyList<MartenDeclaredIndex> Indexes { get; init; }

    /// <summary>
    /// 1.2-INDEX-A: the type's mapping, which Marten's own index classes are built from (<see cref="MartenIndexDdl" />); set by
    /// <see cref="From" />.
    /// </summary>
    public IDocumentType? Mapping { get; init; }

    /// <summary>How the store's serializer writes a member's name into the JSON; <see langword="null" /> writes the member's own.</summary>
    public JsonNamingPolicy? Naming { get; init; }

    /// <summary>The declared indexes, as the Query page's index advisor reads them.</summary>
    public IReadOnlyList<PostgresIndex> AdvisorIndexes => [.. Indexes.Select(static x => new PostgresIndex(x.Name, x.Definition))];

    /// <summary>The declared GIN index over <c>data</c> as a whole, when there is one.</summary>
    public MartenDeclaredIndex? GinOnData => Indexes.FirstOrDefault(static x =>
        x.Definition.Contains("using gin", StringComparison.OrdinalIgnoreCase)
        && x.Definition.Contains("(data", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The member a JSON path names, written as a <c>StoreOptions</c> lambda body (<c>Address.City</c>) - or <see langword="null" />
    /// when it is not exactly one member of the document type.
    /// </summary>
    /// <param name="jsonPath">The keys, outermost first.</param>
    public string? MemberOf(IReadOnlyList<string> jsonPath) => MartenJsonMembers.Resolve(DocumentType, jsonPath, Naming);

    /// <summary>Describes one document type from its mapping.</summary>
    /// <param name="mapping">The mapping.</param>
    /// <param name="naming">The store's serializer casing as a naming policy.</param>
    public static MartenPlanTable From(IDocumentType mapping, JsonNamingPolicy? naming)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        Table table = new(mapping.TableName);
        List<MartenDeclaredIndex> indexes = [];

        foreach (IndexDefinition index in mapping.Indexes)
        {
            string definition;

            try
            {
                definition = index.ToDDL(table);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                definition = index.Name;
            }

            indexes.Add(new MartenDeclaredIndex(index.Name, definition, index is ComputedIndex));
        }

        return new MartenPlanTable
        {
            Schema = string.IsNullOrWhiteSpace(mapping.TableName.Schema) ? mapping.DatabaseSchemaName : mapping.TableName.Schema,
            Table = mapping.TableName.Name,
            TypeName = mapping.DocumentType.Name,
            DocumentType = mapping.DocumentType,
            Info = DocumentTableInfo.FromDocumentType(mapping),
            Mapping = mapping,
            Indexes = indexes,
            Naming = naming,
        };
    }
}

/// <summary>The document tables of the store in scope, read from its configuration.</summary>
internal static class MartenPlanTables
{
    /// <summary>
    /// Every visible document type's table. <c>AllKnownDocumentTypes()</c> reads the store's configuration and touches no connection.
    /// </summary>
    /// <param name="store">The store in scope.</param>
    /// <param name="isVisible"><c>MartenStudioOptions.IsDocumentTypeVisible</c>.</param>
    public static IReadOnlyList<MartenPlanTable> Read(Marten.IDocumentStore store, Func<Type, bool>? isVisible)
    {
        ArgumentNullException.ThrowIfNull(store);

        JsonNamingPolicy? naming = Documents.DocumentDataService.NamingPolicyFor(store);
        List<MartenPlanTable> tables = [];

        foreach (IDocumentType documentType in store.Options.AllKnownDocumentTypes())
        {
            if (isVisible is not null && !isVisible(documentType.DocumentType))
            {
                continue;
            }

            tables.Add(MartenPlanTable.From(documentType, naming));
        }

        return tables;
    }
}

/// <summary>
/// 1.0-P3C: the member of a document type a JSON key names, through the serializer's casing and the attributes that rename a member -
/// the half of "which <c>Duplicate</c> line" a plan's <c>data -&gt;&gt; 'color'</c> cannot say by itself.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one member must match each key: two members that write the same key (<c>Name</c> and <c>name</c> under the default
/// casing), none, or a path through a collection or a type with no members all answer <see langword="null" />, and the insight then
/// names the JSON path and says it could not map it. A guess here would put a line in the host's <c>StoreOptions</c> that Marten
/// rejects or that indexes another member.
/// </para>
/// <para>
/// Read: <c>[JsonPropertyName]</c> and, by name, Newtonsoft's <c>[JsonProperty]</c> (Marten can serialize with either; the reference
/// is by name so this assembly has none); <c>[JsonIgnore]</c> removes a member. Public instance properties and fields, inherited
/// ones included.
/// </para>
/// </remarks>
internal static class MartenJsonMembers
{
    /// <summary>The most keys a path is followed through.</summary>
    private const int MaxDepth = 8;

    /// <summary>The member path (<c>Address.City</c>) <paramref name="jsonPath" /> names on <paramref name="root" />, or <see langword="null" />.</summary>
    /// <param name="root">The document type.</param>
    /// <param name="jsonPath">The keys, outermost first.</param>
    /// <param name="naming">The serializer's naming policy, or <see langword="null" /> to write a member's own name.</param>
    public static string? Resolve(Type root, IReadOnlyList<string> jsonPath, JsonNamingPolicy? naming) =>
        ResolveMembers(root, jsonPath, naming) is { } members ? string.Join('.', members.Select(static x => x.Name)) : null;

    /// <summary>
    /// 1.2-INDEX-A: the members (outermost first) <paramref name="jsonPath" /> names on <paramref name="root" />, or <see langword="null" />
    /// - the walk <see cref="Resolve" /> makes, as <see cref="MemberInfo" />s, for Marten's own index constructor.
    /// </summary>
    /// <param name="root">The document type.</param>
    /// <param name="jsonPath">The keys, outermost first.</param>
    /// <param name="naming">The serializer's naming policy, or <see langword="null" /> to write a member's own name.</param>
    public static IReadOnlyList<MemberInfo>? ResolveMembers(Type root, IReadOnlyList<string> jsonPath, JsonNamingPolicy? naming)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(jsonPath);

        if (jsonPath.Count is 0 or > MaxDepth)
        {
            return null;
        }

        Type current = root;
        List<MemberInfo> members = [];

        foreach (string key in jsonPath)
        {
            current = Unwrap(current);

            if (current.IsPrimitive || current == typeof(string) || current.IsArray || current == typeof(object)
                || typeof(System.Collections.IEnumerable).IsAssignableFrom(current))
            {
                return null;
            }

            List<(MemberInfo Member, Type Type)> matches = [];

            foreach (MemberInfo member in Members(current))
            {
                if (JsonNameOf(member, naming) is { } written && string.Equals(written, key, StringComparison.Ordinal))
                {
                    matches.Add((member, member is PropertyInfo property ? property.PropertyType : ((FieldInfo) member).FieldType));
                }
            }

            if (matches.Count != 1)
            {
                return null;
            }

            members.Add(matches[0].Member);
            current = matches[0].Type;
        }

        return members;
    }

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static IEnumerable<MemberInfo> Members(Type type)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.GetMethod is not null)
            {
                yield return property;
            }
        }

        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            yield return field;
        }
    }

    /// <summary>The key a member is written under, or <see langword="null" /> when the serializer leaves it out.</summary>
    private static string? JsonNameOf(MemberInfo member, JsonNamingPolicy? naming)
    {
        if (member.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: JsonIgnoreCondition.Always })
        {
            return null;
        }

        if (member.GetCustomAttribute<JsonPropertyNameAttribute>() is { } named)
        {
            return named.Name;
        }

        foreach (object attribute in member.GetCustomAttributes(inherit: true))
        {
            Type type = attribute.GetType();

            switch (type.FullName)
            {
                case "Newtonsoft.Json.JsonIgnoreAttribute":
                    return null;
                case "Newtonsoft.Json.JsonPropertyAttribute"
                    when type.GetProperty("PropertyName")?.GetValue(attribute) is string { Length: > 0 } renamed:
                    return renamed;
            }
        }

        return naming?.ConvertName(member.Name) ?? member.Name;
    }
}
