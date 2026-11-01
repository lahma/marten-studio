namespace PostgresStudio.Services.Database;

/// <summary>The declarations of every registered source, or why they could not all be read.</summary>
/// <remarks>
/// Read through the studio's <c>IDatabaseDeclarationsSource</c>: MartenStudio's reads every registered
/// store's options; a source with no declarations of its own answers an empty classifier.
/// </remarks>
/// <param name="Classifier">The classifier over every source, when every source could be read.</param>
/// <param name="StoreSchemas">The resolved source's own schemas.</param>
/// <param name="Failure">
/// Why the classification is unavailable - fail closed - in words any visitor of the resolved store may read:
/// the resolved store is named, because the visitor's own scope names it, and another store is "a registered
/// Marten store". <c>DatabaseAccess.DescribeFailureAsync</c> names that one to a visitor its store
/// policy passes.
/// </param>
internal sealed record DatabaseDeclarations(
    DatabaseObjectClassifier? Classifier,
    IReadOnlyList<string> StoreSchemas,
    string? Failure)
{
    /// <summary>Whether every store's declarations were read.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(Classifier))]
    public bool Succeeded => Classifier is not null && Failure is null;

    /// <summary>
    /// The registration key of the other store <see cref="Failure" /> is about, when it is not the resolved
    /// store - shown only to a visitor that store's store policy passes (AGENTS.md D27).
    /// </summary>
    public string? FailedStoreKey { get; init; }

    /// <summary>
    /// <see cref="Failure" /> with <see cref="FailedStoreKey" /> named and the reason it gave, for a visitor
    /// that store's store policy passes; <see langword="null" /> when <see cref="Failure" /> names no other store.
    /// </summary>
    public string? NamedFailure { get; init; }
}
