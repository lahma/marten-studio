using Marten.Linq.Members;
using Marten.Schema;

namespace MartenStudio.Internal;

/// <summary>
/// Which of a mapping's <c>DuplicatedFields</c> are real columns on the document table.
/// </summary>
/// <remarks>
/// <para>
/// Not every entry in <c>IDocumentType.DuplicatedFields</c> describes a column. Marten registers a
/// <em>search-only</em> duplicated field for every metadata column whose member a Marten attribute named
/// — <c>[Version]</c>, <c>[CreatedAt]</c>, <c>[LastModified]</c>, <c>[TenantId]</c>,
/// <c>[CorrelationId]</c>, <c>[CausationId]</c>, <c>[LastModifiedBy]</c> — so that a LINQ comparison
/// against that member reads the metadata column instead of the JSON. It is
/// <c>MetadataColumn.RegisterForLinqSearching</c> that does it, it names the metadata column
/// (<c>mt_version</c>, <c>mt_created_at</c>, <c>tenant_id</c> …), and it marks the field
/// <c>OnlyForSearching</c>. Marten's own <c>DocumentTable</c> builds its columns from
/// <c>DuplicatedFields.Where(x =&gt; !x.OnlyForSearching)</c>, so no column is created for one.
/// </para>
/// <para>
/// The studio has to filter the same way, because the column such a field names is already a metadata
/// column: taking it for a duplicated field named <c>mt_version</c> twice in a single-document select
/// list, listed it twice in the document's metadata pane, and killed the Blazor circuit on the duplicate
/// <c>@key</c> — a 500 on the detail view of every document whose type has a <c>[Version]</c> property
/// (issue #1).
/// </para>
/// <para>
/// It is worth knowing <em>when</em> these appear, because it is why the failure never showed up in a
/// unit test: <c>DocumentSchema</c> is what registers them and <c>DocumentMapping</c> holds it behind a
/// <c>Lazy&lt;T&gt;</c>, so a mapping that has never had its schema built reports none of them and one
/// out of a store that has served a single query reports them all.
/// </para>
/// </remarks>
internal static class MartenDuplicatedFields
{
    /// <summary>
    /// The duplicated fields that are columns on <paramref name="documentType" />'s table, in the order
    /// Marten holds them.
    /// </summary>
    /// <param name="documentType">The mapping to read.</param>
    /// <exception cref="ArgumentNullException"><paramref name="documentType" /> is null.</exception>
    public static IEnumerable<DuplicatedField> Physical(IDocumentType documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        foreach (DuplicatedField field in documentType.DuplicatedFields)
        {
            if (!field.OnlyForSearching)
            {
                yield return field;
            }
        }
    }
}
