using MartenStudio.Internal.Sql;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The two document tables the builder tests are written against: one with every feature Marten offers,
/// and one with nothing at all.
/// </summary>
/// <remarks>
/// The bare one matters more than it looks. A store configured with
/// <c>Policies.DisableInformationalFields()</c> has a document table of <c>id</c> and <c>data</c> and
/// nothing else — no <c>mt_last_modified</c>, no <c>mt_version</c>, no <c>mt_dotnet_type</c> — and a
/// select list that assumed any of them would fail on every page render against that store.
/// </remarks>
internal static class SqlTestTables
{
    /// <summary>Duplicated fields (one nested), soft delete, conjoined tenancy, every metadata column.</summary>
    public static DocumentTableInfo FullyFeatured() =>
        DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestCustomer>(options =>
            options.Schema.For<SqlTestCustomer>()
                .Duplicate(x => x.Email)
                .Duplicate(x => x.Address.City)
                .Duplicate(x => x.OrderCount)
                .SoftDeleted()
                .MultiTenanted()
                .Metadata(m =>
                {
                    m.CausationId.Enabled = true;
                    m.CorrelationId.Enabled = true;
                    m.Headers.Enabled = true;
                    m.LastModifiedBy.Enabled = true;
                    m.CreatedAt.Enabled = true;
                })));

    /// <summary>A table of <c>id</c> and <c>data</c>, and nothing else.</summary>
    public static DocumentTableInfo MetadataLess() =>
        DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestNote>(options =>
            options.Policies.DisableInformationalFields()));

    /// <summary>A string-keyed collection, for the id-typing tests.</summary>
    public static DocumentTableInfo StringKeyed() =>
        DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestTicket>());

    /// <summary>
    /// A type whose <c>[Version]</c> member Marten has registered a search alias for, plus one real
    /// duplicated field beside it, so the two are told apart rather than lumped together.
    /// </summary>
    public static DocumentTableInfo Versioned() =>
        DocumentTableInfo.FromDocumentType(SqlTestStore
            .DocumentType<SqlTestVersionedNote>(options =>
                options.Schema.For<SqlTestVersionedNote>().Duplicate(x => x.Text))
            .WithLinqSearchAliasFor("mt_version", nameof(SqlTestVersionedNote.Version)));

    /// <summary>
    /// A hierarchy, so <c>mt_doc_type</c> exists and the subclass verdict has something to judge.
    /// </summary>
    public static DocumentTableInfo Hierarchy() =>
        DocumentTableInfo.FromDocumentType(SqlTestStore.DocumentType<SqlTestCustomer>(options =>
            options.Schema.For<SqlTestCustomer>().AddSubClass<SqlTestVipCustomer>()));
}
