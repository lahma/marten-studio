using MartenStudio.Services.Events;

using PostgresStudio.Services;

namespace MartenStudio.Services.Shapes;

/// <summary>Where the aliases only the log holds were taken from.</summary>
internal enum UnregisteredAliasSource
{
    /// <summary>A counted listing in hand (the page's, or one read within the last half minute): every alias the log held then.</summary>
    Counts,

    /// <summary>The planner's statistics on <c>mt_events.type</c>, E05's read: a sample, which can miss a rare alias.</summary>
    Statistics,

    /// <summary>
    /// Neither could be read.
    /// </summary>
    NotRead,

    /// <summary>
    /// A tenant is selected, and no counted listing of that scope's was in hand. The planner's statistics span the whole table, and
    /// Marten stamps <c>mt_events.tenant_id</c> whatever the tenancy style, so with any tenant selected they may name another tenant's
    /// event types: they are never read for a tenant (1.2-UX-FIX-FU3). Load counts is tenant-scoped, and is the way.
    /// </summary>
    NotForATenant,
}

/// <summary>The event types <c>mt_events</c> holds that the store does not register, and where that list came from.</summary>
/// <param name="Names">The aliases, in order.</param>
/// <param name="Source">Where they were taken from.</param>
/// <param name="ListedAt">When the counted listing was read, for <see cref="UnregisteredAliasSource.Counts" />.</param>
internal sealed record UnregisteredAliases(IReadOnlyList<string> Names, UnregisteredAliasSource Source, DateTimeOffset? ListedAt = null);

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
    /// <see cref="CheckAsync(StudioScope, string, bool, CancellationToken)" /> for a name the caller took from a counted listing it holds
    /// (1.2-UX-FIX-FU3). A name the store does not register is confirmed, in order, against that <paramref name="listing" /> - used only
    /// if it was read for this very scope, within five minutes - then the shared listing in hand, then (for no tenant) the planner's
    /// statistics; if none can say, the result is "could not confirm", never "no such event type". The object is passed, never a bare name.
    /// </summary>
    /// <param name="scope">The scope; resolved under the store policy before anything else.</param>
    /// <param name="eventTypeName">An event type the store registers or its events table holds, as stored.</param>
    /// <param name="force">Run again, once the cached result is older than <see cref="ShapeCheckLimits.RecheckAfter" />.</param>
    /// <param name="listing">The counted listing the name came from, or <see langword="null" /> for none.</param>
    /// <param name="cancellationToken">Cancels the caller's wait, never the shared work.</param>
    Task<ShapeCheckResult> CheckAsync(StudioScope scope, string eventTypeName, bool force, EventTypeList? listing, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <c>mt_events</c> has an index leading with <c>type</c> (<c>opts.Events.EnableEventTypeIndex</c>), read from the catalog
    /// under the store policy. Without one a check of a rare type walks the primary key, and a click over many types needs a bound.
    /// Unknown (no table, no connection) is answered <see langword="true" />: the caller's bound is then the gentler one.
    /// </summary>
    /// <param name="scope">The scope; resolved under the store policy before anything else.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<bool> HasTypeIndexAsync(StudioScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// The aliases in <c>mt_events</c> that the store does not register (1.2-UX-FIX), for the names Check shapes asks about besides the
    /// registered ones. Never a <c>group by type</c>: from the counted listing when one is in hand, else from the planner's statistics.
    /// </summary>
    /// <param name="scope">The scope; resolved under the store policy before anything else.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<UnregisteredAliases> ListUnregisteredAsync(StudioScope scope, CancellationToken cancellationToken);
}
