using PostgresStudio.Services;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// Checks one document collection (or subclass) against the types this build has.
/// </summary>
internal interface IDocumentShapeCheck
{
    /// <summary>Reads a bounded sample of <paramref name="alias" /> in <paramref name="scope" /> and judges each document.</summary>
    /// <param name="scope">The scope; resolved under the store policy before anything else.</param>
    /// <param name="alias">A visible registered collection or subclass alias.</param>
    /// <param name="force">Run again, once the cached result is older than <see cref="ShapeCheckLimits.RecheckAfter" />.</param>
    /// <param name="cancellationToken">Cancels the caller's wait, never the shared work.</param>
    Task<ShapeCheckResult> CheckAsync(StudioScope scope, string alias, bool force, CancellationToken cancellationToken);
}
