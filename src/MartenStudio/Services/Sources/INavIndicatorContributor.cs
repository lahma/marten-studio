using MartenStudio.Services.Settings;

namespace MartenStudio.Services.Sources;

/// <summary>
/// The sidebar badges a studio draws beyond the shared health badge - MartenStudio's dead-letter count and its
/// projections dot - keyed by the <see cref="StudioNavItem.BadgeKey" /> its descriptor's entries carry.
/// </summary>
/// <remarks>
/// <para>
/// The shared indicator service keeps the health badge, which reads the sweep cache and never sweeps, and asks each
/// registered contributor for the rest. A studio with no such badges registers none.
/// </para>
/// <para>
/// <b>What an implementation must and must not do.</b> It is asked for the circuit's active scope, after the scope policy
/// passed for it, from inside the shared snapshot cache's factory (D10: one read per scope per interval, however many
/// circuits ask), so it must read only through the same data services the badge's page reads - which resolve the scope
/// and apply the policies again - and never through a database connection of its own. It must never throw: a badge it
/// could not read is a key it does not answer, because a decoration must never take navigation down. And it must start
/// nothing: no daemon lookup that builds one, no storage-ensuring call (AGENTS.md hard rules 11 and 14) - the sidebar
/// is on every route.
/// </para>
/// </remarks>
internal interface INavIndicatorContributor
{
    /// <summary>The badges this contributor draws for <paramref name="scope" />, by badge key; a key it could not read is absent.</summary>
    /// <param name="scope">The circuit's active scope, which the scope policy has passed.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyDictionary<string, StudioNavBadge>> ReadAsync(StudioScope scope, CancellationToken cancellationToken = default);
}
