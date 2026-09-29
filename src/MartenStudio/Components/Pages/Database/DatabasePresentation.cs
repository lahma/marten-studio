using System.Globalization;

using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;

namespace MartenStudio.Components.Pages.Database;

/// <summary>
/// How the database browser says things: kinds, owners, estimates, sizes and the default order. One place,
/// so the grids, the rail and the object header never spell the same fact two ways.
/// </summary>
internal static class DatabasePresentation
{
    /// <summary>What a kind is called in a sentence or a badge.</summary>
    public static string KindLabel(DatabaseObjectKind kind) => kind switch
    {
        DatabaseObjectKind.PartitionedTable => "partitioned table",
        DatabaseObjectKind.View => "view",
        DatabaseObjectKind.MaterializedView => "materialized view",
        DatabaseObjectKind.ForeignTable => "foreign table",
        DatabaseObjectKind.Function => "function",
        DatabaseObjectKind.Procedure => "procedure",
        DatabaseObjectKind.Aggregate => "aggregate",
        DatabaseObjectKind.WindowFunction => "window function",
        DatabaseObjectKind.Trigger => "trigger",
        DatabaseObjectKind.Sequence => "sequence",
        DatabaseObjectKind.EnumType => "enum",
        DatabaseObjectKind.DomainType => "domain",
        DatabaseObjectKind.RangeType => "range",
        DatabaseObjectKind.CompositeType => "composite",
        _ => "table",
    };

    /// <summary>
    /// What a policy's refusal of a link adds under the service's own sentence: that the store's own schemas'
    /// structure is still shown - so a visitor the policy refuses knows why some tables are listed at all - or
    /// <see langword="null" /> for any other refusal.
    /// </summary>
    /// <remarks>
    /// Only for the two policies. Either of them refusing <c>BrowseDatabase</c> leaves the store's own structure
    /// in view (D27), which a visitor who has just been told their account may not browse the database cannot be
    /// expected to guess; a capability or <c>ReadOnly</c> refusal is the whole studio's, and already says what
    /// the host would change. It says nothing about the object asked for, so it is as neutral as the panel.
    /// </remarks>
    public static string? StructureStillShown(DatabaseRefusal refusal) =>
        refusal is DatabaseRefusal.WritePolicy or DatabaseRefusal.StorePolicy
            ? "The structure of this store's own schemas - their tables, columns, keys and indexes - is still shown " +
              "in the database browser, as on the Schema screen; rows, definitions and other schemas are not."
            : null;

    /// <summary>A routine's kind, short enough for a narrow column.</summary>
    public static string RoutineKindShort(DatabaseObjectKind kind) => kind switch
    {
        DatabaseObjectKind.Procedure => "proc",
        DatabaseObjectKind.Aggregate => "agg",
        DatabaseObjectKind.WindowFunction => "window",
        _ => "fn",
    };

    /// <summary>A kind tab's label.</summary>
    public static string CategoryLabel(DatabaseObjectCategory category) => category switch
    {
        DatabaseObjectCategory.Views => "Views",
        DatabaseObjectCategory.Functions => "Functions",
        DatabaseObjectCategory.Triggers => "Triggers",
        DatabaseObjectCategory.Sequences => "Sequences",
        DatabaseObjectCategory.Types => "Types",
        _ => "Tables",
    };

    /// <summary>A count of one kind, as a noun phrase: "14 tables", "1 view".</summary>
    public static string CountOf(DatabaseObjectCategory category, int count)
    {
        string noun = category switch
        {
            DatabaseObjectCategory.Views => count == 1 ? "view" : "views",
            DatabaseObjectCategory.Functions => count == 1 ? "function" : "functions",
            DatabaseObjectCategory.Triggers => count == 1 ? "trigger" : "triggers",
            DatabaseObjectCategory.Sequences => count == 1 ? "sequence" : "sequences",
            DatabaseObjectCategory.Types => count == 1 ? "type" : "types",
            _ => count == 1 ? "table" : "tables",
        };

        return Number(count) + " " + noun;
    }

    /// <summary>Tables and views together, as object detail's rail counts them: "14 tables", "11 tables, 3 views".</summary>
    public static string RelationsOf(int tables, int views) =>
        views == 0
            ? CountOf(DatabaseObjectCategory.Tables, tables)
            : CountOf(DatabaseObjectCategory.Tables, tables) + ", " + CountOf(DatabaseObjectCategory.Views, views);

    /// <summary>Every kind tab, in the order they are drawn.</summary>
    public static IReadOnlyList<DatabaseObjectCategory> Categories { get; } =
    [
        DatabaseObjectCategory.Tables,
        DatabaseObjectCategory.Views,
        DatabaseObjectCategory.Functions,
        DatabaseObjectCategory.Triggers,
        DatabaseObjectCategory.Sequences,
        DatabaseObjectCategory.Types,
    ];

    /// <summary>
    /// A relation's row estimate (D8): <c>~1,204</c>, "not analyzed" when Postgres has none for a table,
    /// and a dash for a view or a foreign table, which have no rows of their own to estimate.
    /// </summary>
    public static string Rows(DatabaseRelationSummary relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        if (relation.EstimatedRows is { } rows)
        {
            return "~" + Number(rows);
        }

        return HasNoOwnRows(relation.Kind) ? "—" : "not analyzed";
    }

    /// <summary>What the estimate is, for its tooltip.</summary>
    public static string RowsTitle(DatabaseRelationSummary relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        if (relation.EstimatedRows is not null)
        {
            return "Estimated from pg_class.reltuples, not counted (D8)";
        }

        return relation.Kind switch
        {
            DatabaseObjectKind.View => "A view keeps no rows of its own; each read runs its query",
            DatabaseObjectKind.ForeignTable => "A foreign table's rows live on the remote server",
            _ => "Postgres has never analyzed this table, so there is no estimate. ANALYZE gives it one.",
        };
    }

    /// <summary>A relation's size, or a dash where there is nothing stored locally.</summary>
    public static string Size(DatabaseRelationSummary relation)
    {
        ArgumentNullException.ThrowIfNull(relation);

        return relation.SizeBytes <= 0 && HasNoOwnRows(relation.Kind) ? "—" : SchemaFormat.Bytes(relation.SizeBytes);
    }

    /// <summary>A number, grouped, culture-invariant.</summary>
    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>What an owner is called in the owner column when there is no link to make of it.</summary>
    public static string OwnerLabel(DatabaseObjectOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        return ownership.Owner switch
        {
            DatabaseObjectOwner.MartenDocument => ownership.Alias ?? "document table",
            DatabaseObjectOwner.MartenEventStore => "event store",
            DatabaseObjectOwner.MartenProjectionOrExtended => "Marten-managed",
            DatabaseObjectOwner.MartenInfrastructure => "Marten internal",
            _ => ownership.RecognisedAs ?? "other",
        };
    }

    /// <summary>What an owner means, for the badge's tooltip.</summary>
    public static string OwnerTitle(DatabaseObjectOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);

        return ownership.Owner switch
        {
            DatabaseObjectOwner.MartenDocument =>
                "A Marten document table. Its rows are browsed in Documents, where tenancy, soft delete and the serializer apply.",
            DatabaseObjectOwner.MartenEventStore =>
                "Part of Marten's event store. Its rows are browsed in Streams, the Feed and Projections.",
            DatabaseObjectOwner.MartenProjectionOrExtended =>
                "Relational data Marten manages for this application - a flat-table projection or an extended schema object. Its rows are browsable here.",
            DatabaseObjectOwner.MartenInfrastructure =>
                "Marten's own bookkeeping. It is described on the Schema screen and never browsed raw.",
            _ when ownership.RecognisedAs is { } hint =>
                "Recognised by name as " + hint + ". A guess from the name, not something the database says.",
            _ => "Not Marten's: this application's own, or another library's.",
        };
    }

    /// <summary>
    /// The browser's default order: what Marten does not own first - that is what somebody opens this
    /// screen to find - then by schema and name.
    /// </summary>
    public static IOrderedEnumerable<T> DefaultOrder<T>(IEnumerable<T> items)
        where T : DatabaseObjectSummary =>
        items
            .OrderBy(static x => x.Ownership.IsMarten)
            .ThenBy(static x => x.Schema, StringComparer.Ordinal)
            .ThenBy(static x => x.Name, StringComparer.Ordinal);

    /// <summary>The value of <c>aria-sort</c> for a header, or <see langword="null" /> when it is not the sorted one.</summary>
    public static string? AriaSort(bool sorted, bool descending) =>
        sorted ? (descending ? "descending" : "ascending") : null;

    /// <summary>Whether a kind keeps no rows of its own, so an estimate and a size mean nothing for it.</summary>
    private static bool HasNoOwnRows(DatabaseObjectKind kind) =>
        kind is DatabaseObjectKind.View or DatabaseObjectKind.ForeignTable;
}
