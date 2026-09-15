using JasperFx.MultiTenancy;

using Marten.Schema;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// The Postgres type of a document table's <c>id</c> column, which is what decides the CLR type of every
/// id parameter the studio binds.
/// </summary>
/// <remarks>
/// Deliberately the <em>column</em> type and not <c>IDocumentType.IdType</c>: a strong-typed id, an
/// <c>UseIdentityKey</c> mapping or an F# discriminated union all present a CLR type Marten wraps, while
/// the column underneath is still one of five things. <see cref="DocumentIdColumnTypes.FromUdtName"/>
/// reads it from <c>information_schema.columns</c> (see <see cref="ColumnCatalog"/>);
/// <see cref="DocumentIdColumnTypes.FromClrType"/> is the offline guess used before a connection exists.
/// </remarks>
internal enum DocumentIdColumnType
{
    /// <summary>Not known, or not one of the four Marten supports — bound as an untyped literal.</summary>
    Unknown,

    /// <summary><c>uuid</c> — bound as <see cref="Guid"/>.</summary>
    Uuid,

    /// <summary><c>int4</c> — bound as <see cref="int"/>.</summary>
    Int4,

    /// <summary><c>int8</c> — bound as <see cref="long"/>.</summary>
    Int8,

    /// <summary><c>text</c> — bound as <see cref="string"/>.</summary>
    Text,

    /// <summary><c>varchar</c> — bound as <see cref="string"/>.</summary>
    Varchar,
}

/// <summary>
/// One of Marten's optional metadata columns, named the way <c>DocumentMetadataCollection</c> names it.
/// </summary>
/// <remarks>
/// Every one of these is optional (AGENTS.md hard rule 10): a store configured with
/// <c>DisableInformationalFields()</c> has no <c>mt_last_modified</c> at all, so the enum says which
/// columns <em>could</em> exist and <see cref="DocumentTableInfo.MetadataColumns"/> says which ones do.
/// </remarks>
internal enum DocumentMetadataColumn
{
    /// <summary><c>mt_deleted</c>.</summary>
    IsSoftDeleted,

    /// <summary><c>mt_deleted_at</c>.</summary>
    SoftDeletedAt,

    /// <summary><c>mt_version</c>, the Guid version used for optimistic concurrency.</summary>
    Version,

    /// <summary>The numeric revision, which Marten keeps in the same <c>mt_version</c> column.</summary>
    Revision,

    /// <summary><c>mt_last_modified</c>.</summary>
    LastModified,

    /// <summary><c>mt_created_at</c>.</summary>
    CreatedAt,

    /// <summary><c>tenant_id</c>.</summary>
    TenantId,

    /// <summary><c>mt_dotnet_type</c>.</summary>
    DotNetType,

    /// <summary><c>mt_doc_type</c>, present only for a hierarchy.</summary>
    DocumentType,

    /// <summary><c>causation_id</c>.</summary>
    CausationId,

    /// <summary><c>correlation_id</c>.</summary>
    CorrelationId,

    /// <summary><c>last_modified_by</c>.</summary>
    LastModifiedBy,

    /// <summary><c>headers</c>.</summary>
    Headers,
}

/// <summary>An enabled metadata column: which one it is, and the physical column name Marten gave it.</summary>
/// <param name="Column">Which metadata column this is.</param>
/// <param name="ColumnName">The physical column name, read from <c>MetadataColumn.Name</c>.</param>
internal sealed record DocumentMetadataColumnInfo(DocumentMetadataColumn Column, string ColumnName)
{
    /// <summary>
    /// The type <c>information_schema</c> reported for this column, once the catalog has been consulted.
    /// </summary>
    /// <remarks>
    /// <b>The physical type wins, the same way the id column's does.</b>
    /// <see cref="DocumentTableInfo.MetadataDbType"/> can only state the type Marten's default schema uses,
    /// and for <see cref="DocumentMetadataColumn.Revision"/> that default is <c>bigint</c> — but a
    /// <c>Marten.Metadata.IRevisioned</c> document gets Marten's <c>RevisionColumnInt32</c> and an
    /// <c>integer</c> column instead, and a table somebody migrated by hand can be anything at all.
    /// Binding a parameter of the wrong width against it is how a filter stops using the index on it.
    /// </remarks>
    public NpgsqlDbType? PhysicalDbType { get; init; }

    /// <summary>The parameter type to bind when this column is compared or used as a keyset cursor.</summary>
    public NpgsqlDbType DbType => PhysicalDbType ?? DocumentTableInfo.MetadataDbType(Column);
}

/// <summary>
/// A duplicated field: a real column Marten keeps in step with a JSON property, and therefore the one
/// place where a filter on that property can use an index.
/// </summary>
/// <param name="MemberPath">
/// The dotted member path as Marten reports it (<c>DuplicatedField.MemberName</c>), for example
/// <c>Address.City</c>.
/// </param>
/// <param name="ColumnName">The physical column name.</param>
/// <param name="DbType">
/// The column's Npgsql type, taken straight from <c>DuplicatedField.DbType</c> — Appendix B: it is an
/// <see cref="NpgsqlDbType"/> already, so nothing has to be re-derived from <c>PgType</c>.
/// </param>
/// <param name="PgType">The Postgres type as Marten declares it, for display.</param>
internal sealed record DuplicatedColumnInfo(string MemberPath, string ColumnName, NpgsqlDbType DbType, string PgType)
{
    /// <summary>
    /// Whether this is a metadata column a member is stored in, rather than a column duplicated for it.
    /// </summary>
    /// <remarks>
    /// Both are real columns that a filter can read and an index can serve, which is why they share a
    /// type and a lookup. They differ in what a person can do about them: a duplicated field is the
    /// host's <c>Duplicate(x =&gt; …)</c> and can be added, while <c>mt_version</c> exists because
    /// <c>[Version]</c> named a member and no <c>Duplicate</c> call will ever produce another one —
    /// Marten re-points such a field at the metadata column and marks it search-only. So the index
    /// advisor must not answer "duplicate this field" for one. See <see cref="MartenDuplicatedFields" />.
    /// </remarks>
    public bool IsMetadataAlias { get; init; }

    /// <summary>
    /// The member path with every separator removed and folded to lower case, which is what a search
    /// term's dotted path is matched against.
    /// </summary>
    /// <remarks>
    /// Marten reports a nested duplicated field's <c>MemberName</c> as the member names run together —
    /// <c>Duplicate(x =&gt; x.Address.City)</c> gives <c>AddressCity</c>, column <c>address_city</c>, with
    /// no separator to split on (verified against Marten 9.35). So neither the member name nor the column
    /// name can be compared segment by segment, and both are normalised to the same joined, lower-case
    /// form instead.
    /// </remarks>
    public string NormalizedPath { get; } = Normalize(MemberPath);

    /// <summary>Strips the separators a path may be written with, and folds case.</summary>
    internal static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (c is '.' or '_' or ' ')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }
}

/// <summary>
/// Everything the SQL builders need to know about one document table, and nothing they do not: the
/// plain description an <c>IDocumentType</c> is adapted into, so the builders can be unit-tested without
/// Marten, a store or a database.
/// </summary>
/// <remarks>
/// <para>
/// Two ways in. <see cref="FromDocumentType"/> is the normal one and reads a registered type's mapping —
/// which is the whole reason a Marten UI beats pgAdmin: aliases, duplicated fields, soft delete and
/// tenancy are known because the host configured them. <see cref="FromDiscoveredTable"/> covers the
/// <c>mt_doc_*</c> tables that are in the database but not in <c>StoreOptions</c>; those are read-only
/// always, and everything about them has to come from <c>information_schema</c>.
/// </para>
/// <para>
/// <see cref="IdColumnType"/> deserves its own note. <see cref="FromDocumentType"/> can only guess it from
/// the CLR <c>IdType</c>, and guesses <see cref="DocumentIdColumnType.Unknown"/> for a strong-typed id;
/// the caller refines it with <c>info with { IdColumnType = … }</c> once <see cref="ColumnCatalog"/> has
/// told it what the column really is.
/// </para>
/// </remarks>
internal sealed record DocumentTableInfo
{
    /// <summary>The table alias every generated document query uses. A constant, never interpolated input.</summary>
    public const string SqlAlias = "d";

    /// <summary>The <c>data</c> column, which is <c>jsonb</c> on every Marten document table.</summary>
    public const string DataColumn = "data";

    /// <summary>The <c>id</c> column, which every Marten document table has and which is its primary key.</summary>
    public const string IdColumn = "id";

    /// <summary>The schema the table lives in.</summary>
    public required string Schema { get; init; }

    /// <summary>The table name, normally <c>mt_doc_&lt;alias&gt;</c>.</summary>
    public required string Table { get; init; }

    /// <summary>Marten's document alias, or the alias implied by the table name for a discovered table.</summary>
    public required string Alias { get; init; }

    /// <summary>The CLR type name, used in the <c>StoreOptions</c> lines the index advisor suggests.</summary>
    public string? DocumentTypeName { get; init; }

    /// <summary>The Postgres type of the <c>id</c> column.</summary>
    public DocumentIdColumnType IdColumnType { get; init; } = DocumentIdColumnType.Unknown;

    /// <summary>The metadata columns that are <em>enabled</em>. A disabled column is simply absent here.</summary>
    public IReadOnlyList<DocumentMetadataColumnInfo> MetadataColumns { get; init; } = [];

    /// <summary>The duplicated-field columns, in Marten's order.</summary>
    public IReadOnlyList<DuplicatedColumnInfo> DuplicatedColumns { get; init; } = [];

    /// <summary>
    /// Members a metadata column already stores — <c>Version</c> in <c>mt_version</c>, <c>CreatedAt</c> in
    /// <c>mt_created_at</c> — which a search term may name and nothing may list as a column.
    /// </summary>
    /// <remarks>
    /// Deliberately a second list rather than entries in <see cref="DuplicatedColumns" />. Everything that
    /// describes the table's columns reads that one — the select list, the metadata pane, the column
    /// chooser, the configuration screen — and an entry here in any of those places is the duplicate that
    /// ended a circuit. Only <see cref="FindDuplicated" /> looks here, which is the search path and the
    /// index advisor. See <see cref="MartenDuplicatedFields" />.
    /// </remarks>
    public IReadOnlyList<DuplicatedColumnInfo> MetadataAliases { get; init; } = [];

    /// <summary>Single or conjoined tenancy; conjoined is what puts <c>tenant_id</c> into every predicate.</summary>
    public TenancyStyle TenancyStyle { get; init; } = TenancyStyle.Single;

    /// <summary>Whether the type is soft-deleted, read from <c>Metadata.IsSoftDeleted.Enabled</c> (Appendix B).</summary>
    public bool SoftDeleteEnabled { get; init; }

    /// <summary>Whether Marten knows this type, or the studio found the table without a mapping for it.</summary>
    public bool IsRegistered { get; init; } = true;

    /// <summary>The quoted, schema-qualified table name.</summary>
    public string QualifiedName => SqlIdentifier.Qualify(Schema, Table);

    /// <summary>The physical column name for <paramref name="column"/>, or <see langword="null"/> when it is disabled.</summary>
    public string? MetadataColumnName(DocumentMetadataColumn column)
    {
        foreach (var candidate in MetadataColumns)
        {
            if (candidate.Column == column)
            {
                return candidate.ColumnName;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="column"/> is enabled on this table.</summary>
    public bool HasMetadata(DocumentMetadataColumn column) => MetadataColumnName(column) is not null;

    /// <summary>
    /// The duplicated column for a dotted search path, or <see langword="null"/> when the property is only
    /// in the JSON. Matching is case- and separator-insensitive because a search box is typed by a human,
    /// Marten's member names are Pascal-cased, and a nested path arrives with no separator at all — see
    /// <see cref="DuplicatedColumnInfo.NormalizedPath"/>.
    /// </summary>
    public DuplicatedColumnInfo? FindDuplicated(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Count == 0)
        {
            return null;
        }

        var wanted = DuplicatedColumnInfo.Normalize(string.Concat(path));

        foreach (var candidate in DuplicatedColumns)
        {
            if (Matches(candidate, wanted))
            {
                return candidate;
            }
        }

        // Then the metadata columns a member is stored in, which are columns too and are the one place a
        // filter on such a member should read. Second, not first, so a real duplicated field always wins
        // if a store somehow has both.
        foreach (var candidate in MetadataAliases)
        {
            if (Matches(candidate, wanted))
            {
                return candidate;
            }
        }

        return null;

        static bool Matches(DuplicatedColumnInfo candidate, string wanted) =>
            string.Equals(candidate.NormalizedPath, wanted, StringComparison.Ordinal) ||
            string.Equals(DuplicatedColumnInfo.Normalize(candidate.ColumnName), wanted, StringComparison.Ordinal);
    }

    /// <summary>Adapts a registered Marten document type.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>Metadata.X.Enabled</c> is not on its own whether the column exists</b>, which corrects the
    /// plan's Appendix B. Verified against Marten 9.35 by building the real <c>Marten.Storage.DocumentTable</c>
    /// for four mappings: a document with no configuration at all reports <c>IsSoftDeleted</c>,
    /// <c>SoftDeletedAt</c>, <c>TenantId</c> and <c>DocumentType</c> as <c>Enabled</c>, while its table has
    /// exactly <c>id, data, mt_last_modified, mt_version, mt_dotnet_type</c>. Four of those columns are
    /// <em>structural</em> and appear only when something else is true:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>tenant_id</c> — only when <c>TenancyStyle</c> is conjoined.</description></item>
    /// <item><description><c>mt_doc_type</c> — only when <c>IsHierarchy()</c>.</description></item>
    /// <item><description><c>mt_deleted</c>, <c>mt_deleted_at</c> — only when the delete style is soft.</description></item>
    /// </list>
    /// <para>
    /// For the rest — <c>mt_last_modified</c>, <c>mt_version</c>, <c>mt_dotnet_type</c>, <c>mt_created_at</c>,
    /// <c>causation_id</c>, <c>correlation_id</c>, <c>last_modified_by</c>, <c>headers</c> — <c>Enabled</c>
    /// is exactly right, and <c>Policies.DisableInformationalFields()</c> takes the first three away, leaving
    /// a table of <c>id</c> and <c>data</c> and nothing else.
    /// </para>
    /// <para>
    /// The delete style has no interface-level reading: <c>IDocumentType</c> does not carry <c>DeleteStyle</c>
    /// (Appendix B, U2) and the interface that does is internal. But every <c>IDocumentType</c> Marten hands
    /// back <em>is</em> a <c>Marten.Schema.DocumentMapping</c>, which is public and does carry it, so the cast
    /// is checked at compile time and falls back to <c>Metadata.IsSoftDeleted.Enabled</c> if a future Marten
    /// hands back something else. <see cref="WithPhysicalColumns"/> then settles it against the database.
    /// </para>
    /// </remarks>
    public static DocumentTableInfo FromDocumentType(IDocumentType documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        var metadata = documentType.Metadata;
        var softDeleted = IsSoftDeleted(documentType);
        var conjoined = documentType.TenancyStyle == TenancyStyle.Conjoined;
        var hierarchy = documentType.IsHierarchy();

        List<DocumentMetadataColumnInfo> columns = [];
        Add(columns, DocumentMetadataColumn.IsSoftDeleted, metadata.IsSoftDeleted, softDeleted);
        Add(columns, DocumentMetadataColumn.SoftDeletedAt, metadata.SoftDeletedAt, softDeleted);
        Add(columns, DocumentMetadataColumn.Version, metadata.Version, true);
        Add(columns, DocumentMetadataColumn.Revision, metadata.Revision, true);
        Add(columns, DocumentMetadataColumn.LastModified, metadata.LastModified, true);
        Add(columns, DocumentMetadataColumn.CreatedAt, metadata.CreatedAt, true);
        Add(columns, DocumentMetadataColumn.TenantId, metadata.TenantId, conjoined);
        Add(columns, DocumentMetadataColumn.DotNetType, metadata.DotNetType, true);
        Add(columns, DocumentMetadataColumn.DocumentType, metadata.DocumentType, hierarchy);
        Add(columns, DocumentMetadataColumn.CausationId, metadata.CausationId, true);
        Add(columns, DocumentMetadataColumn.CorrelationId, metadata.CorrelationId, true);
        Add(columns, DocumentMetadataColumn.LastModifiedBy, metadata.LastModifiedBy, true);
        Add(columns, DocumentMetadataColumn.Headers, metadata.Headers, true);

        // Marten keeps the numeric revision in the very same mt_version column as the Guid version, so a
        // mapping with both enabled would name that column twice in a select list.
        DeduplicateByName(columns);

        List<DuplicatedColumnInfo> duplicated = [];

        // Physical, not every entry in DuplicatedFields: a [Version] or [CreatedAt] member gets a
        // search-only duplicated field naming the metadata column it is stored in, and Marten creates no
        // column for one. See MartenDuplicatedFields - taking those for real columns put mt_version in the
        // select list twice and in the metadata pane twice.
        foreach (var field in MartenDuplicatedFields.Physical(documentType))
        {
            duplicated.Add(new DuplicatedColumnInfo(field.MemberName, field.ColumnName, field.DbType, field.PgType));
        }

        // The ones that were dropped, kept where only the search path can see them: the column they name
        // is already in MetadataColumns, so they must never reach a select list - but a filter typed as
        // `Version:<guid>` should still read that column rather than the JSON copy of it, which Marten
        // leaves one write behind.
        List<DuplicatedColumnInfo> aliases = [];

        foreach (var field in MartenDuplicatedFields.SearchAliases(documentType))
        {
            aliases.Add(new DuplicatedColumnInfo(field.MemberName, field.ColumnName, field.DbType, field.PgType)
            {
                IsMetadataAlias = true,
            });
        }

        return new DocumentTableInfo
        {
            Schema = documentType.TableName.Schema,
            Table = documentType.TableName.Name,
            Alias = documentType.Alias,
            DocumentTypeName = documentType.DocumentType.Name,
            IdColumnType = DocumentIdColumnTypes.FromClrType(documentType.IdType),
            MetadataColumns = columns,
            DuplicatedColumns = duplicated,
            MetadataAliases = aliases,
            TenancyStyle = documentType.TenancyStyle,
            SoftDeleteEnabled = softDeleted,
            IsRegistered = true,
        };
    }

    /// <summary>
    /// Reconciles the configured view with the table that is actually there, which is what makes the
    /// generated SQL survive schema drift.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Marten's configuration describes the table it <em>would</em> create. The studio reads a database
    /// somebody else migrated, and the two can disagree: a duplicated field added in code but not yet
    /// applied, a strong-typed id whose column type no CLR type can predict, a store whose own soft-delete
    /// setting was changed after the table was made. A column that is not in
    /// <paramref name="physicalColumns"/> is dropped from the select list, and the id column's real type
    /// wins. An empty read is taken as "the catalog could not see it" and changes nothing.
    /// </para>
    /// <para>
    /// <b>The disagreement runs both ways</b>, which is the half this used to get wrong. A table with a
    /// physical <c>tenant_id</c> that the mapping does not declare was being marked
    /// <see cref="TenancyStyle.Conjoined"/> without ever getting a <c>TenantId</c> entry in
    /// <see cref="MetadataColumns"/> — so the tenant filter knew it had to filter and had no column name to
    /// filter with, and threw with a message about a metadata column being disabled on a table that plainly
    /// has it. <c>mt_deleted</c> and <c>mt_deleted_at</c> had the same hole. Every column the database has
    /// and the configuration lacks is therefore added here under its well-known name.
    /// </para>
    /// </remarks>
    public DocumentTableInfo WithPhysicalColumns(TableColumns physicalColumns)
    {
        ArgumentNullException.ThrowIfNull(physicalColumns);

        if (!physicalColumns.Exists)
        {
            return this;
        }

        List<DocumentMetadataColumnInfo> metadata = [];

        foreach (var column in MetadataColumns)
        {
            if (physicalColumns.Find(column.ColumnName) is { } physical)
            {
                metadata.Add(column with { PhysicalDbType = PhysicalDbTypeOf(physical) });
            }
        }

        List<DuplicatedColumnInfo> duplicated = [];

        foreach (var column in DuplicatedColumns)
        {
            if (physicalColumns.Has(column.ColumnName))
            {
                duplicated.Add(column);
            }
        }

        // The aliases are held to the same reconciliation, and for a sharper reason: a store with
        // DisableInformationalFields() has no mt_version at all, so an alias for it would compile a filter
        // against a column that is not there. The physical type wins over the member's CLR type too - an
        // IRevisioned document's mt_version is integer where the member says bigint.
        List<DuplicatedColumnInfo> aliases = [];

        foreach (var alias in MetadataAliases)
        {
            if (physicalColumns.Find(alias.ColumnName) is { } physical)
            {
                aliases.Add(PhysicalDbTypeOf(physical) is { } dbType ? alias with { DbType = dbType } : alias);
            }
        }

        var softDeleteColumn = MetadataColumnName(DocumentMetadataColumn.IsSoftDeleted) ?? "mt_deleted";
        var softDeletedAtColumn = MetadataColumnName(DocumentMetadataColumn.SoftDeletedAt) ?? "mt_deleted_at";
        var tenantColumn = MetadataColumnName(DocumentMetadataColumn.TenantId) ?? "tenant_id";

        AddPhysicalOnly(metadata, physicalColumns, DocumentMetadataColumn.TenantId, tenantColumn);
        AddPhysicalOnly(metadata, physicalColumns, DocumentMetadataColumn.IsSoftDeleted, softDeleteColumn);
        AddPhysicalOnly(metadata, physicalColumns, DocumentMetadataColumn.SoftDeletedAt, softDeletedAtColumn);

        return this with
        {
            MetadataColumns = metadata,
            DuplicatedColumns = duplicated,
            MetadataAliases = aliases,
            IdColumnType = physicalColumns.Find(IdColumn) is { } id
                ? DocumentIdColumnTypes.FromUdtName(id.UdtName)
                : IdColumnType,
            SoftDeleteEnabled = physicalColumns.Has(softDeleteColumn),
            TenancyStyle = physicalColumns.Has(tenantColumn) ? TenancyStyle.Conjoined : TenancyStyle.Single,
        };
    }

    /// <summary>
    /// Adds the metadata entry for a column the table has and the configuration did not mention, so that
    /// "this table is conjoined-tenanted" and "here is the column to filter on" can never disagree.
    /// </summary>
    private static void AddPhysicalOnly(
        List<DocumentMetadataColumnInfo> metadata,
        TableColumns physicalColumns,
        DocumentMetadataColumn column,
        string columnName)
    {
        if (physicalColumns.Find(columnName) is not { } physical)
        {
            return;
        }

        foreach (var existing in metadata)
        {
            if (existing.Column == column)
            {
                return;
            }
        }

        metadata.Add(new DocumentMetadataColumnInfo(column, columnName)
        {
            PhysicalDbType = PhysicalDbTypeOf(physical),
        });
    }

    /// <summary>
    /// The Npgsql type of a physical column, or <see langword="null"/> when it is a type the studio does
    /// not recognise — a domain, a <c>citext</c>, an enum — in which case the static map is a better guess
    /// than <see cref="NpgsqlDbType.Unknown"/>.
    /// </summary>
    private static NpgsqlDbType? PhysicalDbTypeOf(PostgresColumn column) =>
        PostgresColumn.ToNpgsqlDbType(column.UdtName) is var type && type != NpgsqlDbType.Unknown ? type : null;

    /// <summary>
    /// Describes an <c>mt_doc_*</c> table the studio found in the database but that no <c>StoreOptions</c>
    /// mapping claims. Everything is inferred from the columns, and such a table is read-only always.
    /// </summary>
    public static DocumentTableInfo FromDiscoveredTable(string schema, string table, IEnumerable<PostgresColumn> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(columns);

        List<DocumentMetadataColumnInfo> metadata = [];
        List<DuplicatedColumnInfo> duplicated = [];
        var idColumnType = DocumentIdColumnType.Unknown;

        foreach (var column in columns)
        {
            if (string.Equals(column.Name, IdColumn, StringComparison.Ordinal))
            {
                idColumnType = DocumentIdColumnTypes.FromUdtName(column.UdtName);
                continue;
            }

            if (string.Equals(column.Name, DataColumn, StringComparison.Ordinal))
            {
                continue;
            }

            if (WellKnownMetadataColumns.TryGetValue(column.Name, out var known))
            {
                // The physical type is all there is on a table no mapping describes - and it is also how a
                // discovered mt_version that holds a numeric revision rather than a Guid is bound correctly.
                metadata.Add(new DocumentMetadataColumnInfo(known, column.Name)
                {
                    PhysicalDbType = PhysicalDbTypeOf(column),
                });
                continue;
            }

            // Anything else on an mt_doc_ table is a duplicated field by construction: Marten only adds
            // columns for metadata, duplicated fields and the two above.
            duplicated.Add(new DuplicatedColumnInfo(
                column.Name,
                column.Name,
                PostgresColumn.ToNpgsqlDbType(column.UdtName),
                column.DataType));
        }

        var alias = table.StartsWith("mt_doc_", StringComparison.OrdinalIgnoreCase)
            ? table["mt_doc_".Length..]
            : table;

        return new DocumentTableInfo
        {
            Schema = schema,
            Table = table,
            Alias = alias,
            DocumentTypeName = null,
            IdColumnType = idColumnType,
            MetadataColumns = metadata,
            DuplicatedColumns = duplicated,
            TenancyStyle = metadata.Exists(x => x.Column == DocumentMetadataColumn.TenantId)
                ? TenancyStyle.Conjoined
                : TenancyStyle.Single,
            SoftDeleteEnabled = metadata.Exists(x => x.Column == DocumentMetadataColumn.IsSoftDeleted),
            IsRegistered = false,
        };
    }

    /// <summary>
    /// The parameter type for a metadata column when the database has not been asked, used when one is
    /// filtered on or paged by. <see cref="DocumentMetadataColumnInfo.PhysicalDbType"/> overrides it once
    /// <see cref="WithPhysicalColumns"/> has read the real thing.
    /// </summary>
    /// <remarks>
    /// <see cref="DocumentMetadataColumn.Revision"/> is <c>bigint</c> and not <c>integer</c>: Marten 9.35's
    /// default <c>RevisionColumn</c> declares <c>bigint</c>, and the <c>integer</c> one
    /// (<c>RevisionColumnInt32</c>) is used only for a document implementing <c>IRevisioned</c>. Guessing
    /// <c>integer</c> bound a 4-byte parameter against an 8-byte column on every ordinary revisioned
    /// document.
    /// </remarks>
    internal static NpgsqlDbType MetadataDbType(DocumentMetadataColumn column) => column switch
    {
        DocumentMetadataColumn.IsSoftDeleted => NpgsqlDbType.Boolean,
        DocumentMetadataColumn.SoftDeletedAt => NpgsqlDbType.TimestampTz,
        DocumentMetadataColumn.Version => NpgsqlDbType.Uuid,
        DocumentMetadataColumn.Revision => NpgsqlDbType.Bigint,
        DocumentMetadataColumn.LastModified => NpgsqlDbType.TimestampTz,
        DocumentMetadataColumn.CreatedAt => NpgsqlDbType.TimestampTz,
        DocumentMetadataColumn.Headers => NpgsqlDbType.Jsonb,
        _ => NpgsqlDbType.Varchar,
    };

    /// <summary>The physical names Marten gives its metadata columns, for tables no mapping describes.</summary>
    private static readonly Dictionary<string, DocumentMetadataColumn> WellKnownMetadataColumns =
        new(StringComparer.Ordinal)
        {
            ["mt_deleted"] = DocumentMetadataColumn.IsSoftDeleted,
            ["mt_deleted_at"] = DocumentMetadataColumn.SoftDeletedAt,
            ["mt_version"] = DocumentMetadataColumn.Version,
            ["mt_last_modified"] = DocumentMetadataColumn.LastModified,
            ["mt_created_at"] = DocumentMetadataColumn.CreatedAt,
            ["tenant_id"] = DocumentMetadataColumn.TenantId,
            ["mt_dotnet_type"] = DocumentMetadataColumn.DotNetType,
            ["mt_doc_type"] = DocumentMetadataColumn.DocumentType,
            ["causation_id"] = DocumentMetadataColumn.CausationId,
            ["correlation_id"] = DocumentMetadataColumn.CorrelationId,
            ["last_modified_by"] = DocumentMetadataColumn.LastModifiedBy,
            ["headers"] = DocumentMetadataColumn.Headers,
        };

    /// <summary>
    /// Whether the type is soft-deleted. Read from the concrete mapping's <c>DeleteStyle</c>, because
    /// <c>Metadata.IsSoftDeleted.Enabled</c> is true even on a type that has no <c>mt_deleted</c> column.
    /// </summary>
    private static bool IsSoftDeleted(IDocumentType documentType) => documentType is DocumentMapping mapping
        ? mapping.DeleteStyle == JasperFx.DeleteStyle.SoftDelete
        : documentType.Metadata.IsSoftDeleted.Enabled;

    private static void Add(
        List<DocumentMetadataColumnInfo> columns,
        DocumentMetadataColumn column,
        Marten.Storage.Metadata.MetadataColumn source,
        bool structurallyPresent)
    {
        if (source.Enabled && structurallyPresent)
        {
            columns.Add(new DocumentMetadataColumnInfo(column, source.Name));
        }
    }

    private static void DeduplicateByName(List<DocumentMetadataColumnInfo> columns)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (var i = columns.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(columns[i].ColumnName))
            {
                columns.RemoveAt(i);
            }
        }
    }
}

/// <summary>Maps between Postgres id column types and the CLR types the studio binds for them.</summary>
internal static class DocumentIdColumnTypes
{
    /// <summary>Reads the column type from an <c>information_schema.columns.udt_name</c> value.</summary>
    public static DocumentIdColumnType FromUdtName(string? udtName) => udtName?.ToLowerInvariant() switch
    {
        "uuid" => DocumentIdColumnType.Uuid,
        "int4" or "integer" or "serial" => DocumentIdColumnType.Int4,
        "int8" or "bigint" or "bigserial" => DocumentIdColumnType.Int8,
        "text" => DocumentIdColumnType.Text,
        "varchar" or "character varying" => DocumentIdColumnType.Varchar,
        _ => DocumentIdColumnType.Unknown,
    };

    /// <summary>
    /// The offline guess from <c>IDocumentType.IdType</c>. A strong-typed id, a value object or an F#
    /// discriminated union lands on <see cref="DocumentIdColumnType.Unknown"/>, which is the signal to ask
    /// <see cref="ColumnCatalog"/> what the column actually is.
    /// </summary>
    public static DocumentIdColumnType FromClrType(Type? idType)
    {
        var type = idType is null ? null : Nullable.GetUnderlyingType(idType) ?? idType;

        if (type == typeof(Guid))
        {
            return DocumentIdColumnType.Uuid;
        }

        if (type == typeof(int))
        {
            return DocumentIdColumnType.Int4;
        }

        if (type == typeof(long))
        {
            return DocumentIdColumnType.Int8;
        }

        return type == typeof(string) ? DocumentIdColumnType.Varchar : DocumentIdColumnType.Unknown;
    }

    /// <summary>The Npgsql type an id parameter of this column type is bound as.</summary>
    public static NpgsqlDbType DbType(DocumentIdColumnType type) => type switch
    {
        DocumentIdColumnType.Uuid => NpgsqlDbType.Uuid,
        DocumentIdColumnType.Int4 => NpgsqlDbType.Integer,
        DocumentIdColumnType.Int8 => NpgsqlDbType.Bigint,
        DocumentIdColumnType.Text => NpgsqlDbType.Text,
        DocumentIdColumnType.Varchar => NpgsqlDbType.Varchar,
        // Npgsql sends an Unknown parameter as an untyped literal and lets Postgres infer the type, which
        // is exactly right for a column the studio could not identify (a domain, citext, an enum).
        _ => NpgsqlDbType.Unknown,
    };
}
