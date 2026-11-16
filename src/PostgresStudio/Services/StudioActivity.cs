using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;

namespace PostgresStudio.Services;

/// <summary>
/// The neutral way into and out of the process-wide Activity ring: a module records what a visitor did under its own scope, and a page reads
/// back what this circuit's visitor may see. Scoped, because the two things the ring cannot know are a circuit's - who did it, and whether
/// this visitor may read it.
/// </summary>
/// <remarks>
/// <para>
/// It knows no module's scope. Every entry is judged by the <see cref="IStudioAuditScopeGate" /> of its <see cref="StudioAuditScope.Kind" />,
/// and an entry whose kind has no gate is hidden: a ring written before a module was removed must not be read through another module's rules.
/// </para>
/// <para>
/// <b>Bounded.</b> <c>take</c> stops the sweep at the last entry a panel draws, the principal is fetched once per sweep and only when a gate
/// asks for it, and each gate remembers its own answers for the sweep only: the next sweep asks again, as a policy's answer may have
/// changed, and the answers are this visitor's and are never kept where another circuit could read them.
/// </para>
/// </remarks>
internal sealed class StudioActivity
{
    /// <summary>Who an entry is recorded under when nothing has said who the circuit belongs to.</summary>
    private const string Unknown = "(unknown)";

    private readonly StudioActionLogService ring;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly IReadOnlyList<IStudioAuditScopeGate> scopeGates;

    /// <param name="ring">The ring.</param>
    /// <param name="authenticationStateProvider">Who is asking.</param>
    /// <param name="scopeGates">The registered modules' audit gates.</param>
    public StudioActivity(
        StudioActionLogService ring,
        AuthenticationStateProvider authenticationStateProvider,
        IEnumerable<IStudioAuditScopeGate> scopeGates)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(authenticationStateProvider);
        ArgumentNullException.ThrowIfNull(scopeGates);

        this.ring = ring;
        this.authenticationStateProvider = authenticationStateProvider;
        this.scopeGates = [.. scopeGates];
    }

    /// <summary>
    /// Records one action of a module into the ring, under the module's own scope. Written on failure as well as on success (AGENTS.md hard
    /// rule 5). The ring entry only: the module logs its own event.
    /// </summary>
    /// <param name="scope">The module's scope: its kind, the label the Activity page shows and its own value for its gate.</param>
    /// <param name="action">What was done.</param>
    /// <param name="target">What it was done to.</param>
    /// <param name="succeeded">Whether it worked.</param>
    /// <param name="message">The outcome, or the reason it failed.</param>
    /// <param name="capability">The capability it required, when it required one.</param>
    public void Record(
        StudioAuditScope scope,
        string action,
        string target,
        bool succeeded,
        string? message = null,
        string? capability = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        ring.Record(new StudioActionLogEntry(
            Timestamp: DateTimeOffset.UtcNow,
            User: UserName(),
            Scope: scope,
            Action: action,
            Target: target,
            Succeeded: succeeded,
            Message: message,
            Capability: capability));
    }

    /// <summary>
    /// The entries of the ring this circuit's visitor may read, newest first - what the Activity page and the Overview's recent activity both
    /// show, so the two cannot disagree.
    /// </summary>
    /// <param name="take">Stop after this many are kept, or <see langword="null" /> for all of them.</param>
    /// <param name="cancellationToken">Checked between entries, so a page that went away stops asking.</param>
    public async ValueTask<List<StudioActionLogEntry>> GetVisibleAsync(int? take = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StudioActionLogEntry> entries = ring.GetLatest();
        int limit = take is { } wanted ? Math.Max(wanted, 0) : int.MaxValue;

        ClaimsPrincipal? user = null;
        Dictionary<string, IStudioAuditSweep?> sweeps = new(StringComparer.Ordinal);
        List<StudioActionLogEntry> visible = new(Math.Min(entries.Count, limit));

        foreach (StudioActionLogEntry entry in entries)
        {
            if (visible.Count >= limit)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!sweeps.TryGetValue(entry.Scope.Kind, out IStudioAuditSweep? sweep))
            {
                IStudioAuditScopeGate? gate = scopeGates.FirstOrDefault(x => string.Equals(x.Kind, entry.Scope.Kind, StringComparison.Ordinal));
                sweep = gate?.BeginSweep(Principal);
                sweeps[entry.Scope.Kind] = sweep;
            }

            if (sweep is not null && await sweep.IsVisibleAsync(entry, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(entry);
            }
        }

        return visible;

        async ValueTask<ClaimsPrincipal> Principal()
        {
            user ??= await PrincipalAsync().ConfigureAwait(false);
            return user;
        }
    }

    /// <inheritdoc cref="StudioActionLogService.GetLatest" />
    public IReadOnlyList<StudioActionLogEntry> GetLatest(int maxCount = StudioActionLogService.MaxEntries) =>
        ring.GetLatest(maxCount);

    private async Task<ClaimsPrincipal> PrincipalAsync()
    {
        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return state.User;
    }

    /// <summary>
    /// Who the circuit belongs to, or a placeholder when nothing has said.
    /// </summary>
    /// <remarks>
    /// Read from the already-completed task rather than awaited: every caller is a synchronous step of a
    /// service method, a circuit's authentication state is settled long before a visitor can click
    /// anything, and blocking on a task here is the one way this could go wrong. An application that
    /// authenticates nobody records the action under <c>anonymous</c>, which is the truth about it.
    /// </remarks>
    private string UserName()
    {
        Task<AuthenticationState> state = authenticationStateProvider.GetAuthenticationStateAsync();
        if (!state.IsCompletedSuccessfully)
        {
            return Unknown;
        }

        string? name = state.Result.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
    }
}
