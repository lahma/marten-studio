namespace MartenStudio.Services.Query;

/// <summary>Which way a list is ordered: a document list, or a relation's rows in the database browser.</summary>
internal enum SortDirection
{
    /// <summary>Ascending, nulls last.</summary>
    Ascending,

    /// <summary>Descending, nulls last.</summary>
    Descending,
}
