using System.Reflection;

using Marten.Schema;

using PostgresStudio.Services.Query;

using Weasel.Postgresql.Tables;

namespace MartenStudio.Internal.Sql;

/// <summary>
/// X2: the index Marten would create for the index advisor's <c>StoreOptions</c> line - Marten's own DDL, for hypopg to imagine.
/// The other of the two places a Postgres identifier becomes SQL text in MartenStudio (hard rule 4): here, it is Marten that
/// writes it, through the very classes its migration would.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly the one Marten would create.</b> <c>Index(x =&gt; x.Member)</c> registers a <see cref="ComputedIndex" /> over the
/// member path, and a metadata index (<c>IndexLastModified()</c>, <c>SoftDeletedWithIndex()</c>) a <see cref="DocumentIndex" /> on
/// the column; each one's <c>ToDDL</c> is the statement Marten's migration writes. Building one here registers nothing: the
/// constructor is all that is called, never <c>DocumentMapping.AddIndex</c> or <c>Indexes.Add</c>, so the store's configuration is
/// what it was, and no connection is touched. <see cref="ComputedIndex" /> reads the member's locator from the mapping's own
/// queryable members - <c>(data -&gt;&gt; 'Color')</c>, a <c>CAST</c> for a number, the column itself for a duplicated member - and
/// leads with <c>tenant_id</c> where the mapping says Marten starts its indexes with it: the expression Marten's LINQ statement
/// compares, so the planner can match it.
/// </para>
/// <para>
/// <b>What has no hypothetical form.</b> <c>Duplicate(x =&gt; x.Member)</c> adds a column, and hypopg imagines indexes, never
/// columns: the what-if for it is the computed index <c>Index(x =&gt; x.Member)</c> would create on the same member, which serves
/// the same filter in Marten's own statement, and the note says so. A GIN index (<c>GinIndexJsonData()</c>) is not one hypopg
/// imagines (it imagines btree, BRIN, hash and bloom), and <c>MultiTenanted()</c> and <c>SoftDeleted()</c> add columns: none of
/// them is offered.
/// </para>
/// <para>
/// Read at <c>V9.39.0</c> and <c>v9.54.2</c>: <c>ComputedIndex(DocumentMapping, MemberInfo[])</c>,
/// <c>DocumentIndex(DocumentMapping, params string[])</c> and Weasel's <c>IndexDefinition.ToDDL(Table)</c> are the same at both.
/// </para>
/// </remarks>
internal static class MartenIndexDdl
{
    /// <summary>What a what-if for a <c>Duplicate</c> line says.</summary>
    internal const string DuplicateNote =
        "Duplicate(...) adds a column, and hypopg imagines indexes, never columns: this is the computed index Index(...) on the same "
        + "member would create, which serves the same filter in Marten's own statement.";

    /// <summary>What every Marten what-if says about acting on it.</summary>
    internal const string DeclareNote =
        "Declare it in StoreOptions rather than creating it by hand: Marten's next schema apply drops an index its configuration "
        + "does not declare.";

    /// <summary>
    /// The computed index Marten creates for <c>Index(x =&gt; x.Member)</c> over <paramref name="members" />, as Marten writes it -
    /// or <see langword="null" /> when the mapping is not one Marten builds that index for, or Marten cannot.
    /// </summary>
    /// <param name="mapping">The document type, as <c>IDocumentType</c> - a <see cref="DocumentMapping" /> on every Marten 9.</param>
    /// <param name="members">The member path, outermost first.</param>
    public static string? Computed(IDocumentType mapping, IReadOnlyList<MemberInfo> members)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(members);

        if (mapping is not DocumentMapping document || members.Count == 0)
        {
            return null;
        }

        try
        {
            return new ComputedIndex(document, [.. members]).ToDDL(new Table(document.TableName));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            // A member Marten's queryable tree does not know: Marten could not build this index either.
            return null;
        }
    }

    /// <summary>
    /// The index Marten creates on one metadata column - <c>IndexLastModified()</c>'s on <c>mt_last_modified</c>,
    /// <c>SoftDeletedWithIndex()</c>'s on <c>mt_deleted_at</c> where <c>mt_deleted</c> - as Marten writes it, or
    /// <see langword="null" />.
    /// </summary>
    /// <param name="mapping">The document type.</param>
    /// <param name="column">The metadata column.</param>
    /// <param name="predicate">Its partial-index predicate, as Marten sets it, or <see langword="null" />.</param>
    public static string? OnColumn(IDocumentType mapping, string column, string? predicate = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);

        return mapping is DocumentMapping document
            ? new DocumentIndex(document, column) { Predicate = predicate }.ToDDL(new Table(document.TableName))
            : null;
    }

    /// <summary>
    /// A hypothetical index of Marten's, for the "What if?" panel: Marten's definition, the advisor's line it stands for, and the
    /// key the server finds it again by.
    /// </summary>
    /// <param name="key">The advisor's subject, unique within one piece of advice.</param>
    /// <param name="definition">Marten's DDL.</param>
    /// <param name="line">The advisor's <c>StoreOptions</c> line.</param>
    /// <param name="table">The document table.</param>
    /// <param name="duplicate">Whether the line is a <c>Duplicate</c>, which hypopg can only stand in for.</param>
    public static HypotheticalIndex Index(string key, string definition, string line, DocumentTableInfo table, bool duplicate)
    {
        ArgumentNullException.ThrowIfNull(table);

        return new HypotheticalIndex
        {
            Key = "marten:" + key,
            Definition = definition,
            Caption = "The index Marten would create for " + line,
            Origin = HypotheticalIndexOrigin.Marten,
            Schema = table.Schema,
            Table = table.Table,
            Note = duplicate ? DuplicateNote + " " + DeclareNote : DeclareNote,
        };
    }
}
