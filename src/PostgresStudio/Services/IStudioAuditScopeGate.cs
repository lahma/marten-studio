using System.Security.Claims;

namespace PostgresStudio.Services;

/// <summary>
/// Decides which of a module's audit entries a visitor may read (D34). Scoped; one per module kind, registered with
/// <c>TryAddEnumerable</c>. <see cref="StudioActivity.GetVisibleAsync" /> judges an entry only through the gate of its
/// <see cref="StudioAuditScope.Kind" /> - the database's own entries included - and hides an entry whose kind has no gate (fail closed).
/// </summary>
internal interface IStudioAuditScopeGate
{
    /// <summary>The module key this gate answers for: the <see cref="StudioAuditScope.Kind" /> of the entries it judges.</summary>
    string Kind { get; }

    /// <summary>
    /// Begins one sweep of <see cref="StudioActivity.GetVisibleAsync" />: the sweep may remember its answers for itself, for this
    /// visitor only, and is dropped when the sweep ends.
    /// </summary>
    /// <param name="principal">
    /// The visitor, fetched on the first call and then remembered by the sweep that owns it, so that a gate which never needs one never
    /// causes a fetch, and two gates in one sweep share one.
    /// </param>
    IStudioAuditSweep BeginSweep(Func<ValueTask<ClaimsPrincipal>> principal);
}

/// <summary>One sweep of a module's audit entries for one visitor.</summary>
internal interface IStudioAuditSweep
{
    /// <summary>Whether the visitor may read <paramref name="entry" />.</summary>
    /// <param name="entry">An entry of the gate's kind.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    ValueTask<bool> IsVisibleAsync(StudioActionLogEntry entry, CancellationToken cancellationToken);
}
