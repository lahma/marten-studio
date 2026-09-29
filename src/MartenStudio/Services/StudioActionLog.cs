using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Services;

/// <summary>
/// What a service calls when a visitor changes something: the process-wide Activity ring, and the
/// application's own logger.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, because the two things the ring cannot know are a circuit's: who did it, and what the studio
/// was pointed at when they did. <see cref="StudioActionLogService" /> is a singleton shared by every
/// circuit in the process, so the services talk to this and it talks to both.
/// </para>
/// <para>
/// Written on failure as well as on success (AGENTS.md hard rule 5). An audit that only records what
/// worked cannot answer the question people actually ask after an incident, which is what someone tried.
/// </para>
/// </remarks>
internal sealed class StudioActionLog
{
    /// <summary>What an entry says about a scope no selector has settled yet.</summary>
    private const string Unknown = "(unknown)";

    /// <summary>What an entry says about a view that is not filtered to one tenant.</summary>
    private const string AllTenants = "(all)";

    private readonly StudioActionLogService store;
    private readonly ILogger<StudioActionLog> logger;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly StudioState state;

    public StudioActionLog(
        StudioActionLogService store,
        ILogger<StudioActionLog> logger,
        AuthenticationStateProvider authenticationStateProvider,
        StudioState state)
    {
        this.store = store;
        this.logger = logger;
        this.authenticationStateProvider = authenticationStateProvider;
        this.state = state;
    }

    /// <summary>
    /// Records one mutating action against the scope the studio is currently about.
    /// </summary>
    /// <param name="action">What was done, named the way the page names it.</param>
    /// <param name="target">What it was done to.</param>
    /// <param name="succeeded">Whether it worked.</param>
    /// <param name="message">The outcome, or the reason it failed.</param>
    /// <param name="capability">The capability it required, when it required one.</param>
    /// <param name="scope">The scope it was aimed at, when it was not the active one.</param>
    public void Record(
        string action,
        string target,
        bool succeeded,
        string? message = null,
        StudioCapability? capability = null,
        StudioScope? scope = null)
    {
        StudioScope? effective = scope ?? state.ActiveScope;
        string storeKey = effective?.StoreKey ?? Unknown;
        string databaseId = effective?.DatabaseId ?? Unknown;
        string? tenantId = effective?.TenantId;
        string user = UserName();

        store.Record(new StudioActionLogEntry(
            Timestamp: DateTimeOffset.UtcNow,
            User: user,
            StoreKey: storeKey,
            DatabaseId: databaseId,
            TenantId: tenantId,
            Action: action,
            Target: target,
            Succeeded: succeeded,
            Message: message,
            Capability: capability?.ToString()));

        if (succeeded)
        {
            logger.ActionPerformed(user, action, target, storeKey, databaseId, tenantId ?? AllTenants, message ?? "done");
        }
        else
        {
            logger.ActionFailed(user, action, target, storeKey, databaseId, tenantId ?? AllTenants, message);
        }
    }

    /// <summary>
    /// Records a capability refusal: the ring entry a person reads, and event 9202 for whoever is
    /// watching the application's log.
    /// </summary>
    public void RecordCapabilityDenied(StudioCapabilityDeniedException denial, string action, string target)
    {
        ArgumentNullException.ThrowIfNull(denial);

        Record(action, target, succeeded: false, denial.Message, denial.Capability);
        logger.CapabilityDenied(UserName(), denial.Capability.ToString(), StudioCapabilityGuard.OptionName(denial.Capability));
    }

    /// <summary>
    /// Records a scope refusal: event 9203, and a ring entry naming the scope that was refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scope is named here and nowhere the visitor can see it. A refusal has to be reconstructible by
    /// whoever administers the process, and it must not tell the person who was refused which stores,
    /// databases and tenants exist.
    /// </para>
    /// <para>
    /// <paramref name="capability" /> is the capability the write policy refused, when that is what refused:
    /// it puts the entry under the same rule as a read of that capability on the Activity screen
    /// (<see cref="GetVisibleAsync" />), so the name somebody was refused is shown only to a visitor who may
    /// make that read.
    /// </para>
    /// </remarks>
    public void RecordScopeDenied(
        StudioScope scope,
        string policyName,
        string action,
        string target,
        StudioCapability? capability = null)
    {
        ArgumentNullException.ThrowIfNull(scope);

        Record(action, target, succeeded: false, "Not authorized for this store, database or tenant.", capability, scope);
        logger.ScopeAuthorizationDenied(UserName(), scope.StoreKey, scope.DatabaseId, scope.TenantId ?? AllTenants, policyName);
    }

    /// <summary>
    /// The entries of the ring this circuit's visitor may read, newest first - what the Activity screen and the
    /// Overview's recent activity both show, so the two cannot disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The store policy, entry by entry</b>, against the store, database and tenant the action was aimed at:
    /// the ring is process-wide and every circuit writes into it, so it is the one place one visitor's actions
    /// can be read by another. An entry whose scope was never settled carries a placeholder store key, which no
    /// policy recognises; that is the safe direction.
    /// </para>
    /// <para>
    /// <b>And a read beyond the store only for somebody who may make it.</b> An entry recorded under
    /// <c>BrowseDatabase</c> or <c>RunSql</c> names what was read - a non-Marten table and the columns a
    /// filter was on, up to two hundred characters of a statement - and the store policy says nothing about
    /// who may learn those: a visitor the write policy refuses the browser would read, here, the tables and
    /// columns an operator browsed. So such an entry is kept only while the capability is on for the process
    /// and the write policy, asked with the capability named against the entry's own scope, passes this
    /// visitor - the question the service asked before the read could run (<see cref="StudioCapabilityGuard.ReadsBeyondTheStore" />).
    /// </para>
    /// <para>
    /// <b>Bounded.</b> <paramref name="take" /> stops the sweep at the last entry a panel draws, and the
    /// principal is fetched once - and only when a policy is configured at all - because a panel refreshed
    /// every interval must not pay five hundred authentication-state reads to draw fifteen rows.
    /// </para>
    /// </remarks>
    /// <param name="authorization">The circuit's policy evaluator.</param>
    /// <param name="capabilities">Which capabilities the process has on.</param>
    /// <param name="take">Stop after this many are kept, or <see langword="null" /> for all of them.</param>
    /// <param name="cancellationToken">Checked between entries, so a page that went away stops asking.</param>
    public async ValueTask<List<StudioActionLogEntry>> GetVisibleAsync(
        StudioAuthorization authorization,
        StudioCapabilityGuard capabilities,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(capabilities);

        IReadOnlyList<StudioActionLogEntry> entries = store.GetLatest();
        int limit = take is { } wanted ? Math.Max(wanted, 0) : int.MaxValue;

        bool storePolicy = !string.IsNullOrWhiteSpace(authorization.PolicyFor(null));
        System.Security.Claims.ClaimsPrincipal? user = null;

        List<StudioActionLogEntry> visible = new(Math.Min(entries.Count, limit));

        foreach (StudioActionLogEntry entry in entries)
        {
            if (visible.Count >= limit)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            StudioScope scope = new(entry.StoreKey, entry.DatabaseId, entry.TenantId);

            if (storePolicy)
            {
                user ??= await PrincipalAsync().ConfigureAwait(false);

                if (!await authorization.IsAuthorizedAsync(user, scope, capability: null, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
            }

            if (ReadBeyondTheStore(entry) is { } capability)
            {
                if (!capabilities.IsEnabled(capability))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(authorization.PolicyFor(capability.ToString())))
                {
                    user ??= await PrincipalAsync().ConfigureAwait(false);

                    if (!await authorization.IsAuthorizedAsync(user, scope, capability.ToString(), cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }
                }
            }

            visible.Add(entry);
        }

        return visible;
    }

    /// <summary>The read beyond the store an entry was recorded under, or <see langword="null" />.</summary>
    private static StudioCapability? ReadBeyondTheStore(StudioActionLogEntry entry)
    {
        foreach (StudioCapability capability in StudioCapabilityGuard.ReadsBeyondTheStore)
        {
            if (string.Equals(entry.Capability, capability.ToString(), StringComparison.Ordinal))
            {
                return capability;
            }
        }

        return null;
    }

    private async Task<System.Security.Claims.ClaimsPrincipal> PrincipalAsync()
    {
        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return state.User;
    }

    /// <inheritdoc cref="StudioActionLogService.GetLatest" />
    public IReadOnlyList<StudioActionLogEntry> GetLatest(int maxCount = StudioActionLogService.MaxEntries) =>
        store.GetLatest(maxCount);

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
