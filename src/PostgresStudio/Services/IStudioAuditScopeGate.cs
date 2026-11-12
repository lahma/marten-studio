using System.Security.Claims;

namespace PostgresStudio.Services;

/// <summary>
/// Decides which of a module's audit entries a visitor may read (D34). Scoped; one per module kind, registered with
/// <c>TryAddEnumerable</c>. <see cref="StudioActionLog.GetVisibleAsync" /> judges an entry that carries a
/// <see cref="StudioActionLogEntry.ScopeKind" /> only through the gate of that kind - never through the host's store policy, which
/// is written for the host's resources - and hides an entry whose kind has no gate (fail closed).
/// </summary>
internal interface IStudioAuditScopeGate
{
    /// <summary>The module key this gate answers for: the <see cref="StudioActionLogEntry.ScopeKind" /> of the entries it judges.</summary>
    string Kind { get; }

    /// <summary>
    /// Begins one sweep of <see cref="StudioActionLog.GetVisibleAsync" />: the sweep may remember its answers for itself, for this
    /// visitor only, and is dropped when the sweep ends.
    /// </summary>
    /// <param name="user">The visitor, fetched once per sweep.</param>
    IStudioAuditSweep BeginSweep(ClaimsPrincipal user);
}

/// <summary>One sweep of a module's audit entries for one visitor.</summary>
internal interface IStudioAuditSweep
{
    /// <summary>Whether the visitor may read <paramref name="entry" />.</summary>
    /// <param name="entry">An entry of the gate's kind.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    ValueTask<bool> IsVisibleAsync(StudioActionLogEntry entry, CancellationToken cancellationToken);
}
