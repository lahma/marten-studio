using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

namespace MartenStudio.Services.Documents;

/// <summary>
/// Turns a column into the short key that travels in <c>?cols=</c> and in local storage, and back again —
/// back again only against a table, which is what keeps a key from a stale bookmark from becoming SQL.
/// </summary>
/// <remarks>
/// The keys are deliberately readable (<c>id</c>, <c>meta:LastModified</c>, <c>dup:email</c>,
/// <c>json:Address.City</c>) because they end up in a URL somebody pastes into a chat, and an opaque
/// token would make that URL unreadable and unfixable. Readability costs nothing here: every key is
/// resolved against the collection's real columns before it is used, so an unknown one is dropped rather
/// than trusted.
/// </remarks>
internal static class DocumentColumnKeys
{
    /// <summary>The key for the id column.</summary>
    public const string Id = "id";

    private const string MetadataPrefix = "meta:";
    private const string DuplicatedPrefix = "dup:";
    private const string JsonPrefix = "json:";

    /// <summary>The key for a column.</summary>
    public static string KeyFor(DocumentColumn column) => column switch
    {
        DocumentColumn.Id => Id,
        DocumentColumn.Metadata metadata => MetadataPrefix + metadata.Column,
        DocumentColumn.Duplicated duplicated => DuplicatedPrefix + duplicated.ColumnName,
        DocumentColumn.JsonPath path => JsonPrefix + string.Join('.', path.Path),
        _ => Id,
    };

    /// <summary>
    /// The column a key names, or <see langword="null"/> when this table has no such column. A metadata
    /// column the store disabled and a duplicated column that was never migrated both land here.
    /// </summary>
    public static DocumentColumn? Resolve(DocumentTableInfo table, string? key)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var trimmed = key.Trim();

        if (string.Equals(trimmed, Id, StringComparison.OrdinalIgnoreCase))
        {
            return DocumentColumn.ById;
        }

        if (trimmed.StartsWith(MetadataPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[MetadataPrefix.Length..];

            return Enum.TryParse(name, ignoreCase: true, out DocumentMetadataColumn column) && table.HasMetadata(column)
                ? new DocumentColumn.Metadata(column)
                : null;
        }

        if (trimmed.StartsWith(DuplicatedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[DuplicatedPrefix.Length..];

            foreach (var candidate in table.DuplicatedColumns)
            {
                if (string.Equals(candidate.ColumnName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new DocumentColumn.Duplicated(candidate.ColumnName);
                }
            }

            return null;
        }

        if (trimmed.StartsWith(JsonPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var path = trimmed[JsonPrefix.Length..].Split('.', StringSplitOptions.RemoveEmptyEntries);

            return path.Length == 0 ? null : new DocumentColumn.JsonPath(path);
        }

        return null;
    }

    /// <summary>The header text for a column.</summary>
    public static string LabelFor(DocumentColumn column) => column switch
    {
        DocumentColumn.Id => "id",
        DocumentColumn.Metadata metadata => MetadataLabel(metadata.Column),
        DocumentColumn.Duplicated duplicated => duplicated.ColumnName,
        DocumentColumn.JsonPath path => path.Label,
        _ => "?",
    };

    /// <summary>What kind of column this is, which decides how its cells are drawn.</summary>
    public static DocumentColumnKind KindFor(DocumentColumn column) => column switch
    {
        DocumentColumn.Id => DocumentColumnKind.Id,
        DocumentColumn.Metadata => DocumentColumnKind.Metadata,
        DocumentColumn.Duplicated => DocumentColumnKind.Duplicated,
        _ => DocumentColumnKind.Json,
    };

    /// <summary>A header for a column.</summary>
    public static DocumentColumnHeader HeaderFor(DocumentColumn column) =>
        new(column, KeyFor(column), LabelFor(column), KindFor(column));

    /// <summary>
    /// The human name of a metadata column — the physical column name, because that is what a person
    /// reading a Marten table in psql will recognise.
    /// </summary>
    private static string MetadataLabel(DocumentMetadataColumn column) => column switch
    {
        DocumentMetadataColumn.IsSoftDeleted => "deleted",
        DocumentMetadataColumn.SoftDeletedAt => "deleted at",
        DocumentMetadataColumn.Version => "version",
        DocumentMetadataColumn.Revision => "revision",
        DocumentMetadataColumn.LastModified => "last modified",
        DocumentMetadataColumn.CreatedAt => "created at",
        DocumentMetadataColumn.TenantId => "tenant",
        DocumentMetadataColumn.DotNetType => ".NET type",
        DocumentMetadataColumn.DocumentType => "doc type",
        DocumentMetadataColumn.CausationId => "causation",
        DocumentMetadataColumn.CorrelationId => "correlation",
        DocumentMetadataColumn.LastModifiedBy => "modified by",
        DocumentMetadataColumn.Headers => "headers",
        _ => column.ToString(),
    };
}
