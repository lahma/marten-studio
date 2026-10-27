using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using PostgresStudio.Services.Query;

namespace MartenStudio.Services.Documents;

/// <summary>One ready-made order the documents toolbar offers.</summary>
/// <param name="Value">The option's value, stable for a test and a bookmark-free control.</param>
/// <param name="Label">What the option says.</param>
/// <param name="SortKey">The column key it sorts by, as <c>?sort=</c> spells it.</param>
/// <param name="Direction">The direction it sorts in.</param>
internal sealed record DocumentOrderPreset(string Value, string Label, string SortKey, SortDirection Direction);

/// <summary>
/// The newest-first orders a collection can be put in from the toolbar - by last modified, by created or by revision -
/// as presets over the list read the studio already has.
/// </summary>
/// <remarks>
/// <para>
/// <b>No Marten call.</b> Marten 9.52's metadata ordering (<c>DocumentQueryOptions.OrderByMetadata</c>, read at
/// v9.52.0 and v9.54.2) is <c>order by mt_last_modified|mt_created_at|mt_version desc nulls last, id</c> in a
/// diagnostics read that runs an exact <c>count(*)</c> for every page and pages by offset. The studio's own list has
/// ordered by those columns from the floor, the same way - <c>desc nulls last</c>, then the id - keyset-paged, with the
/// index verdict beside it (<c>DocumentQueryBuilder</c>). What was missing was a way to ask for it without first adding
/// the column to the grid; these presets are that, and nothing more.
/// </para>
/// <para>
/// <b>Only what the table has, and only what has an order.</b> A preset is offered when the collection's table has the
/// column (<see cref="DocumentDataService.AvailableColumns" />): <c>mt_created_at</c> is opt-in, and
/// <c>DisableInformationalFields()</c> removes <c>mt_last_modified</c>. The version is offered only as a numeric
/// revision: a Guid in the same <c>mt_version</c> column has no meaningful order, which is why Marten refuses it too.
/// </para>
/// </remarks>
internal static class DocumentOrderPresets
{
    /// <summary>The order a collection opens in (<see cref="DocumentDataService.DefaultSort" />, descending).</summary>
    public static DocumentOrderPreset Default { get; } =
        new("id", "Id, highest first", DocumentColumnKeys.Id, SortDirection.Descending);

    /// <summary>Most recently written first.</summary>
    public static DocumentOrderPreset LastModified { get; } = Newest(
        "last-modified", "Last modified, newest first", DocumentMetadataColumn.LastModified);

    /// <summary>Most recently created first.</summary>
    public static DocumentOrderPreset Created { get; } = Newest(
        "created", "Created, newest first", DocumentMetadataColumn.CreatedAt);

    /// <summary>Highest numeric revision first.</summary>
    public static DocumentOrderPreset Revision { get; } = Newest(
        "revision", "Revision, highest first", DocumentMetadataColumn.Revision);

    /// <summary>
    /// The presets for a collection whose grid can show <paramref name="available" />: the default order first, then
    /// each newest-first order whose column the table has.
    /// </summary>
    public static IReadOnlyList<DocumentOrderPreset> For(IReadOnlyList<DocumentColumnHeader> available)
    {
        ArgumentNullException.ThrowIfNull(available);

        List<DocumentOrderPreset> presets = [Default];

        foreach (DocumentOrderPreset preset in NewestFirst)
        {
            if (available.Any(x => string.Equals(x.Key, preset.SortKey, StringComparison.OrdinalIgnoreCase)))
            {
                presets.Add(preset);
            }
        }

        return presets;
    }

    /// <summary>
    /// The preset the list is in now, or <see langword="null" /> when it was sorted some other way - by a column header.
    /// </summary>
    public static DocumentOrderPreset? Current(
        IReadOnlyList<DocumentOrderPreset> presets,
        string? sortKey,
        SortDirection direction)
    {
        ArgumentNullException.ThrowIfNull(presets);

        return presets.FirstOrDefault(x =>
            x.Direction == direction && string.Equals(x.SortKey, sortKey ?? DocumentColumnKeys.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The newest-first presets, in the order the toolbar lists them.</summary>
    private static IReadOnlyList<DocumentOrderPreset> NewestFirst => [LastModified, Created, Revision];

    private static DocumentOrderPreset Newest(string value, string label, DocumentMetadataColumn column) =>
        new(value, label, DocumentColumnKeys.KeyFor(new DocumentColumn.Metadata(column)), SortDirection.Descending);
}
