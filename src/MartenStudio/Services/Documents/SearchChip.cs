using MartenStudio.Services.Query;

namespace MartenStudio.Services.Documents;

/// <summary>What a chip in the parse strip is about.</summary>
internal enum SearchChipKind
{
    /// <summary>A term that parsed, with an index verdict of its own.</summary>
    Predicate,

    /// <summary>A term that did not parse.</summary>
    Error,

    /// <summary>The sort key, which is a separate question from any filter.</summary>
    Sort,
}

/// <summary>
/// One chip of the parse strip: the term as the studio understood it, and what the database thinks of it.
/// </summary>
/// <param name="Kind">Predicate, error or sort.</param>
/// <param name="Text">The term, rewritten in the grammar's own words.</param>
/// <param name="Level">Green, amber or red.</param>
/// <param name="Reason">The one-sentence explanation shown on hover and under the strip.</param>
/// <param name="Suggestion">The <c>StoreOptions</c> line that would fix it, or <see langword="null"/>.</param>
internal sealed record SearchChip(
    SearchChipKind Kind,
    string Text,
    IndexVerdictLevel Level,
    string Reason,
    string? Suggestion);
