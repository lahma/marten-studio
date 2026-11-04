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

    /// <summary>
    /// Whether <c>mt_events</c> has an index leading with <c>type</c> (<c>opts.Events.EnableEventTypeIndex</c>), read from the catalog
    /// under the store policy. Without one a check of a rare type walks the primary key, and a click over many types needs a bound.
    /// Unknown (no table, no connection) is answered <see langword="true" />: the caller's bound is then the gentler one.
    /// </summary>
    /// <param name="scope">The scope; resolved under the store policy before anything else.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<bool> HasTypeIndexAsync(StudioScope scope, CancellationToken cancellationToken);
}
