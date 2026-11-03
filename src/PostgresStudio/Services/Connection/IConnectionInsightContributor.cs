using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Connection;

/// <summary>
/// What a contributor is handed: the database, the redacted settings the page itself shows, and what the server saw. Never the
/// connection string, and no raw string from one - the settings are already redacted (NP-DESIGN §2).
/// </summary>
/// <param name="Source">The database, resolved with no tenant and passed by the Database gate.</param>
/// <param name="Settings">The redacted settings.</param>
/// <param name="Observed">What the server saw of the studio's connection, or <see langword="null" /> when it was not read.</param>
internal sealed record ConnectionContributorContext(ResolvedSource Source, ConnectionSettings Settings, ConnectionObserved? Observed);

/// <summary>What a contributor adds to the Connection page.</summary>
internal sealed record ConnectionContribution
{
    /// <summary>Sections drawn below the page's own, each with rows and cards.</summary>
    public IReadOnlyList<ConnectionContributedSection> Sections { get; init; } = [];

    /// <summary>
    /// The average row or document size in bytes the studio can vouch for (Marten Studio passes T09's and E04's), for the
    /// <c>BUFFER</c> hint; <see langword="null" /> when it knows none.
    /// </summary>
    public long? AverageRowBytes { get; init; }

    /// <summary>Cards that join "Worth a look" (a card with fixes, a hint of the studio's own).</summary>
    public IReadOnlyList<ConnectionCard> Cards { get; init; } = [];
}

/// <summary>
/// The internal seam a studio fills to add its own knowledge to the Connection page (D32's pattern, as
/// <c>IPlanInsightContributor</c>): PostgresStudio runs every registered contributor and MartenStudio registers its own. A
/// contributor contributes section data and hint evidence, never markup and never a string taken from a connection string.
/// </summary>
/// <remarks>
/// <b>It runs after the Database gate has passed and is asked at most once per read.</b> A contributor that fails is dropped from
/// the page with its type's name in a log line at Debug; it cannot take the page down.
/// </remarks>
internal interface IConnectionInsightContributor
{
    /// <summary>The contribution for the database, or <see langword="null" /> for none.</summary>
    /// <param name="context">The database and the redacted settings.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<ConnectionContribution?> ContributeAsync(ConnectionContributorContext context, CancellationToken cancellationToken);
}
