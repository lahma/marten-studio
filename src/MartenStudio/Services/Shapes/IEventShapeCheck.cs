using PostgresStudio.Services;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// Checks one event type against the types this build has, reading events only through Marten's own event reader.
/// </summary>
internal interface IEventShapeCheck
{
    /// <summary>Reads a bounded sample of <paramref name="eventTypeName" /> in <paramref name="scope" /> and judges each event.</summary>
    /// <param name="scope">The scope; resolved under the store policy before anything else.</param>
    /// <param name="eventTypeName">An event type the store registers or its events table holds, as stored.</param>
    /// <param name="force">Run again, once the cached result is older than <see cref="ShapeCheckLimits.RecheckAfter" />.</param>
    /// <param name="cancellationToken">Cancels the caller's wait, never the shared work.</param>
    Task<ShapeCheckResult> CheckAsync(StudioScope scope, string eventTypeName, bool force, CancellationToken cancellationToken);
}
