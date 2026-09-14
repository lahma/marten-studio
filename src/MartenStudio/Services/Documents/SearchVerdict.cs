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

/// <summary>
/// The whole verdict on a search: what parsed, what the indexes can serve, and the configuration line
/// that would turn a red into a green (plan §3.4, differentiator 2).
/// </summary>
/// <remarks>
/// This is the thing a generic SQL browser cannot produce. It exists because the studio knows the store's
/// <c>DuplicatedFields</c> and declared <c>Indexes</c> <em>and</em> what <c>pg_indexes</c> says, so it can
/// tell "indexed" from "reads every row's column" from "reads every document's JSON" — before the query
/// runs, which is the only moment the answer is useful.
/// </remarks>
internal sealed record SearchVerdict
{
    /// <summary>Nothing typed: no chips, no verdict, and nothing to warn about.</summary>
    public static SearchVerdict Empty { get; } = new();

    /// <summary>The worst of the chips, which is what the strip's colour is.</summary>
    public IndexVerdictLevel Level { get; init; } = IndexVerdictLevel.Green;

    /// <summary>
    /// The worst of the <em>filter</em> chips: the sort key's verdict is excluded.
    /// </summary>
    /// <remarks>
    /// This, not <see cref="Level" />, is what withholds a read. Marten creates no index on
    /// <c>mt_last_modified</c>, so the default sort of nearly every collection is red on its own — and a
    /// studio that refused to open a collection until somebody pressed "Run anyway" would be a studio
    /// nobody opens. The sort's verdict is still shown, because "this sorts the whole collection" is worth
    /// knowing; it just is not a reason to refuse.
    /// </remarks>
    public IndexVerdictLevel FilterLevel { get; init; } = IndexVerdictLevel.Green;

    /// <summary>The chips, in the order the terms were typed, with the sort chip last.</summary>
    public IReadOnlyList<SearchChip> Chips { get; init; } = [];

    /// <summary>Everything the parser could not make sense of, with positions.</summary>
    public IReadOnlyList<SearchGrammarError> Errors { get; init; } = [];

    /// <summary>The predicates that parsed, which are what the query builder is given.</summary>
    public IReadOnlyList<DocumentPredicate> Predicates { get; init; } = [];

    /// <summary>The first suggestion among the chips, which is what the red banner offers.</summary>
    public string? Suggestion { get; init; }

    /// <summary>Whether anything failed to parse.</summary>
    public bool HasErrors => Errors.Count > 0;

    /// <summary>Whether this search reads the whole collection.</summary>
    public bool IsRed => Level == IndexVerdictLevel.Red;

    /// <summary>Whether the <em>filter</em> reads the whole collection, which is what withholds a read.</summary>
    public bool FilterIsRed => FilterLevel == IndexVerdictLevel.Red;
}
