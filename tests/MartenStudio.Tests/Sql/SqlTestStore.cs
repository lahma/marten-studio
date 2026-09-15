using System.Reflection;

using Marten;
using Marten.Schema;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// Builds real Marten mappings without a database.
/// </summary>
/// <remarks>
/// <c>DocumentStore.For</c> never opens a connection, and <c>FindOrResolveDocumentType</c> only compiles
/// the mapping, so the SQL builders can be tested against what Marten <em>actually</em> produces —
/// column names, duplicated fields, the metadata a policy turned off — rather than against a hand-written
/// fake that would agree with the tests and disagree with Marten. Same trick, same unreachable host, as
/// <c>MartenApiSurfaceTest</c>.
/// </remarks>
internal static class SqlTestStore
{
    /// <summary>Never connected to. The host does not resolve, which is the point.</summary>
    public const string Unreachable =
        "Host=marten-studio-sql-tests.invalid;Port=5432;Database=none;Username=none;Password=none";

    /// <summary>The schema every test mapping lives in.</summary>
    public const string Schema = "studio_sql";

    /// <summary>Resolves one document type's mapping from a store configured the given way.</summary>
    public static IDocumentType DocumentType<T>(Action<StoreOptions>? configure = null)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(Unreachable);
            options.DatabaseSchemaName = Schema;
            configure?.Invoke(options);
        });

        return store.Options.FindOrResolveDocumentType(typeof(T));
    }

    /// <summary>
    /// Registers the search-only duplicated field Marten registers for a metadata column whose member an
    /// attribute named — <c>[Version]</c>, <c>[CreatedAt]</c>, <c>[TenantId]</c> and the rest.
    /// </summary>
    /// <remarks>
    /// This is <c>MetadataColumn.RegisterForLinqSearching</c>'s body, which is <c>internal</c> and is
    /// called from the <c>DocumentSchema</c> constructor — held behind a <c>Lazy&lt;T&gt;</c> on
    /// <c>DocumentMapping</c>, so a mapping resolved out of a store that has never built its schema has
    /// none of these and the same mapping out of a store that has served one query has them all. That
    /// lazy is precisely why issue #1 shipped, and it is why these tests register the field themselves
    /// rather than waiting for it. <c>MartenApiSurfaceTest</c> pins the shape against Marten, and
    /// <c>DocumentDetailVersionedLiveTests</c> proves Marten really does it, against a live store.
    /// </remarks>
    /// <param name="documentType">The mapping to register on.</param>
    /// <param name="columnName">The metadata column the member is stored in, for example <c>mt_version</c>.</param>
    /// <param name="memberName">The member the attribute was on.</param>
    public static IDocumentType WithLinqSearchAliasFor(
        this IDocumentType documentType,
        string columnName,
        string memberName)
    {
        var mapping = (DocumentMapping) documentType;
        MemberInfo member = mapping.DocumentType.GetProperty(memberName)
            ?? (MemberInfo) mapping.DocumentType.GetField(memberName)!;

        mapping.DuplicateField([member], columnName: columnName).OnlyForSearching = true;

        return documentType;
    }
}

/// <summary>A document with everything on: duplicated fields, soft delete, conjoined tenancy, metadata.</summary>
internal class SqlTestCustomer
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int OrderCount { get; set; }

    public SqlTestAddress Address { get; set; } = new();
}

/// <summary>A nested value, so a dotted path has something to point at.</summary>
internal class SqlTestAddress
{
    public string City { get; set; } = string.Empty;
}

/// <summary>A document with nothing on but the two columns every Marten table has.</summary>
internal class SqlTestNote
{
    public Guid Id { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>A document with a string id, for the id-typing tests.</summary>
internal class SqlTestTicket
{
    public string Id { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;
}

/// <summary>A subclass, so one mapping can be a hierarchy.</summary>
internal class SqlTestVipCustomer : SqlTestCustomer;

/// <summary>
/// A document whose version is a member of its own, which is what <c>[Version]</c> means and what makes
/// Marten register a search-only duplicated field named <c>mt_version</c>.
/// </summary>
internal class SqlTestVersionedNote
{
    public Guid Id { get; set; }

    [Version]
    public Guid Version { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>A document with a long id.</summary>
internal class SqlTestLedgerEntry
{
    public long Id { get; set; }

    public decimal Amount { get; set; }
}
