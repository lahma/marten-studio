namespace PostgresStudio;

/// <summary>One of the largest tables: its size by the catalog's page counts, its rows, its share and its growth.</summary>
public sealed record PostgresTableStorage
{
    /// <summary>The schema.</summary>
    public string Schema { get; init; } = "";

    /// <summary>The table.</summary>
    public string Name { get; init; } = "";

    /// <summary>Whose design it is.</summary>
    public PostgresTableOwnership Ownership { get; init; }

    /// <summary>For a library's table, which library (<c>Quartz.NET</c>, <c>Wolverine</c>, <c>Marten</c>); otherwise null.</summary>
    public string? Owner { get; init; }

    /// <summary>Heap, indexes and TOAST together, in bytes.</summary>
    public long TotalBytes { get; init; }

    /// <summary>The heap, in bytes.</summary>
    public long TableBytes { get; init; }

    /// <summary>Every index on it, in bytes.</summary>
    public long IndexBytes { get; init; }

    /// <summary>Its TOAST table and that table's index, in bytes.</summary>
    public long ToastBytes { get; init; }

    /// <summary>The planner's row estimate (<c>reltuples</c>); <c>-1</c> before the table was first analysed.</summary>
    public long EstimatedRows { get; init; }

    /// <summary>Its share of the database, 0 to 1.</summary>
    public double ShareOfDatabase { get; init; }

    /// <summary>The share of its updates that were HOT, 0 to 1; null with no updates.</summary>
    public double? HotUpdateRatio { get; init; }

    /// <summary>Its estimated growth a day; null until the statistics have counted for a day.</summary>
    public long? GrowthBytesPerDay { get; init; }
}
