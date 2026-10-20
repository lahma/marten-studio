namespace MartenStudio.Services.Query;

/// <summary>How well the database can answer a filter.</summary>
internal enum IndexVerdictLevel
{
    /// <summary>An index serves this filter.</summary>
    Green,

    /// <summary>An index touches the column but may not be used for this filter.</summary>
    Amber,

    /// <summary>Nothing serves it: every row in the collection is read.</summary>
    Red,
}

/// <summary>A verdict on one filter, with the configuration line that would improve it.</summary>
/// <remarks>
/// Shared by the documents list's index advisor, whose suggestion is a <c>StoreOptions</c> line, and the database
/// browser's row filter, which has no suggestion to make: an index on a table the studio does not manage is the
/// schema owner's to create.
/// </remarks>
/// <param name="Level">Green, amber or red.</param>
/// <param name="Reason">Why, in one sentence a user can act on.</param>
/// <param name="Suggestion">The configuration line that would fix it, or <see langword="null"/>.</param>
internal sealed record IndexVerdict(IndexVerdictLevel Level, string Reason, string? Suggestion);
