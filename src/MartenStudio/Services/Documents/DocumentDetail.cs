using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Documents;

/// <summary>One physical column of the row, as the metadata pane lists it.</summary>
/// <param name="Name">The column name, exactly as Postgres has it.</param>
/// <param name="Value">The value as text, or <see langword="null"/> for SQL null.</param>
/// <param name="Role">What kind of column it is, so the pane can group them.</param>
internal sealed record PhysicalColumnValue(string Name, string? Value, PhysicalColumnRole Role);

/// <summary>What a physical column is for.</summary>
internal enum PhysicalColumnRole
{
    /// <summary>The primary key.</summary>
    Identity,

    /// <summary>The document itself.</summary>
    Data,

    /// <summary>One of Marten's metadata columns.</summary>
    Metadata,

    /// <summary>A duplicated field, or — on a discovered table — a column nothing explains.</summary>
    Duplicated,
}

/// <summary>Whether a duplicated column and the JSON it shadows still say the same thing.</summary>
internal enum AgreementState
{
    /// <summary>Column and JSON match.</summary>
    Agrees,

    /// <summary>They do not, which means the column was written by something other than Marten.</summary>
    Differs,

    /// <summary>
    /// The studio could not find the JSON property behind the column, so it will not claim either way.
    /// </summary>
    Unknown,
}

/// <summary>
/// One duplicated field, with the column's value beside the JSON's, which is the "explain this document"
/// pane's most useful row (plan §3.4, differentiator 6).
/// </summary>
/// <param name="ColumnName">The physical column.</param>
/// <param name="MemberPath">Marten's member name for it.</param>
/// <param name="JsonPath">The JSON path the studio resolved it to, or <see langword="null"/>.</param>
/// <param name="ColumnValue">What the column holds.</param>
/// <param name="JsonValue">What the document holds.</param>
/// <param name="State">Whether they agree.</param>
internal sealed record DuplicatedFieldAgreement(
    string ColumnName,
    string MemberPath,
    IReadOnlyList<string>? JsonPath,
    string? ColumnValue,
    string? JsonValue,
    AgreementState State);

/// <summary>
/// Everything the document detail page shows about one row: its JSON, every physical column present, the
/// table and function that own it, and its size in both of the ways that matter.
/// </summary>
internal sealed record DocumentDetail
{
    /// <summary>The collection alias asked for.</summary>
    public required string Alias { get; init; }

    /// <summary>The id, as text.</summary>
    public required string Id { get; init; }

    /// <summary>The document, verbatim from <c>data::text</c>.</summary>
    public required string Json { get; init; }

    /// <summary>
    /// <c>octet_length(data::text)</c> — how big the document is as text, which is the number that
    /// matches what the JSON viewer shows.
    /// </summary>
    public long TextBytes { get; init; }

    /// <summary>
    /// <c>pg_column_size(data)</c> — how much room the row's <c>jsonb</c> actually takes, compression
    /// included. Usually much smaller, and the reason both are shown.
    /// </summary>
    public long StoredBytes { get; init; }

    /// <summary>Every physical column of the row, in table order.</summary>
    public IReadOnlyList<PhysicalColumnValue> Columns { get; init; } = [];

    /// <summary>The duplicated fields with their agreement dots.</summary>
    public IReadOnlyList<DuplicatedFieldAgreement> Duplicated { get; init; } = [];

    /// <summary>The schema-qualified table.</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// The function this collection is written through — always <see langword="null" /> on Marten 9.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A constant, and that is the finding rather than a shortcut. Marten 9 generates the write SQL inline
    /// and creates no <c>mt_upsert_&lt;alias&gt;</c> function at all (AGENTS.md D6, verified against a live
    /// 9.35 schema by P7). The studio used to look it up per detail load through
    /// <c>IMartenDatabase.Functions()</c> — which is <c>AllObjects()</c>, which builds Marten's feature
    /// schemas, which applies the HiLo migration: a read path running DDL, on its own connection, with no
    /// command timeout and no cancellation token, to discover something that is never there (P2-fix B4).
    /// </para>
    /// <para>
    /// Nothing sets it, so the pane says "none — written with inline SQL", which is both the truth and the
    /// more useful answer. It stays settable rather than becoming a constant so that a future Marten that
    /// brings the functions back has somewhere to put them without the pane's markup changing twice.
    /// </para>
    /// </remarks>
    public string? UpsertFunction { get; init; }

    /// <summary>What <c>mt_dotnet_type</c> holds, when the column exists.</summary>
    public string? StoredDotNetType { get; init; }

    /// <summary>
    /// The full name of the type this row should hold — on a hierarchy, the subclass its
    /// <c>mt_doc_type</c> names rather than the collection's root.
    /// </summary>
    public string? ExpectedDotNetType { get; init; }

    /// <summary>
    /// Whether the two disagree. A mismatch means the row was written by a renamed or moved type, and
    /// deserializing it may not produce what the mapping expects.
    /// </summary>
    public bool DotNetTypeMismatch { get; init; }

    /// <summary>
    /// Something about the row's type that is worth saying and is not a mismatch — a <c>mt_doc_type</c>
    /// naming a subclass this store no longer registers — or <see langword="null" />.
    /// </summary>
    public string? DotNetTypeWarning { get; init; }

    /// <summary>The CLR type, so the JSON viewer can resolve Marten LINQ paths.</summary>
    public Type? ClrType { get; init; }

    /// <summary>The serializer's casing as a naming policy, or <see langword="null"/> for Marten's default.</summary>
    public System.Text.Json.JsonNamingPolicy? NamingPolicy { get; init; }

    /// <summary>Whether a mapping claims this collection.</summary>
    public bool IsRegistered { get; init; } = true;

    /// <summary>Whether the row is soft-deleted.</summary>
    public bool IsDeleted { get; init; }

    /// <summary>The tenant, on a conjoined collection.</summary>
    public string? TenantId { get; init; }

    /// <summary>The subclass alias from <c>mt_doc_type</c>, on a hierarchy.</summary>
    public string? DocumentTypeAlias { get; init; }

    /// <summary>The id column's Postgres type, which the not-found message names.</summary>
    public DocumentIdColumnType IdColumnType { get; init; }
}

/// <summary>
/// Why a document could not be shown, said precisely enough to act on.
/// </summary>
/// <param name="Message">What happened, phrased for the person who followed the link.</param>
/// <param name="Sql">The shape of the query that was tried, when there was one.</param>
/// <param name="IdWasMalformed">
/// Whether the id itself did not fit the column, which is the case where probing other collections is
/// worth offering.
/// </param>
internal sealed record DocumentNotFound(string Message, string? Sql, bool IdWasMalformed);

/// <summary>The result of asking for one document: the document, or why not.</summary>
/// <param name="Detail">The document, when it was found.</param>
/// <param name="NotFound">Why it was not, otherwise.</param>
internal sealed record DocumentDetailResult(DocumentDetail? Detail, DocumentNotFound? NotFound)
{
    /// <summary>Whether a document came back.</summary>
    public bool Found => Detail is not null;

    /// <summary>A found document.</summary>
    public static DocumentDetailResult Ok(DocumentDetail detail) => new(detail, null);

    /// <summary>A document that is not there, or an id that could never have been.</summary>
    public static DocumentDetailResult Missing(string message, string? sql = null, bool malformed = false) =>
        new(null, new DocumentNotFound(message, sql, malformed));
}

/// <summary>
/// One outbound link from a document, declared by a <c>ForeignKey</c> in <c>StoreOptions</c>.
/// </summary>
/// <param name="ColumnName">The column holding the referenced id.</param>
/// <param name="TargetAlias">The alias of the collection it points at.</param>
/// <param name="TargetTypeName">That collection's .NET type name, for the label.</param>
/// <param name="TargetId">The id, as text, or <see langword="null"/> when the column is null.</param>
/// <param name="Exists">Whether a row with that id is actually there.</param>
/// <param name="Hue">The target collection's colour.</param>
internal sealed record RelatedDocumentLink(
    string ColumnName,
    string TargetAlias,
    string? TargetTypeName,
    string? TargetId,
    bool Exists,
    int Hue);

/// <summary>Everything this document points at.</summary>
/// <param name="Links">The outbound links, in declaration order.</param>
/// <param name="Error">What went wrong reading them, or <see langword="null"/>.</param>
internal sealed record RelatedDocuments(IReadOnlyList<RelatedDocumentLink> Links, string? Error = null)
{
    /// <summary>No declared foreign keys, and nothing to say about it.</summary>
    public static RelatedDocuments None { get; } = new([]);
}

/// <summary>
/// A collection an id could belong to, and whether it does — what the "search other collections for this
/// id" action reports.
/// </summary>
/// <param name="Alias">The collection.</param>
/// <param name="Hue">Its colour.</param>
/// <param name="Found">Whether a row with this id is in it.</param>
internal sealed record DocumentIdProbe(string Alias, int Hue, bool Found);
