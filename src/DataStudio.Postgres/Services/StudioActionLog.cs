using DataStudio.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Services;

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

    private readonly StudioActivity activity;
    private readonly ILogger<StudioActionLog> logger;
    private readonly AuthenticationStateProvider authenticationStateProvider;
    private readonly StudioState state;
    private readonly StudioDescriptor descriptor;
    private readonly StudioText text;

    /// <param name="activity">The ring's neutral way in and out.</param>
    /// <param name="logger">Where events 9200-9203 go.</param>
    /// <param name="authenticationStateProvider">Who is asking.</param>
    /// <param name="state">The circuit's active scope.</param>
    /// <param name="descriptor">The studio's descriptor, whose key is the database module's: the kind its entries are recorded under.</param>
    /// <param name="settings">The studio's settings, whose words the events are logged in (<see cref="StudioText.Of" />).</param>
    public StudioActionLog(
        StudioActivity activity,
        ILogger<StudioActionLog> logger,
        AuthenticationStateProvider authenticationStateProvider,
        StudioState state,
        StudioDescriptor descriptor,
        IStudioSettings? settings = null)
    {
        this.activity = activity;
        this.logger = logger;
        this.authenticationStateProvider = authenticationStateProvider;
        this.state = state;
        this.descriptor = descriptor;
        text = StudioText.Of(settings);
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

        activity.Record(
            new DatabaseAuditScope(storeKey, databaseId, tenantId).ToAuditScope(descriptor.Key),
            action,
            target,
            succeeded,
            message,
            capability?.ToString());

        if (succeeded)
        {
            logger.ActionPerformed(text.Product, user, action, target, storeKey, databaseId, tenantId ?? AllTenants, message ?? "done");
        }
        else
        {
            logger.ActionFailed(text.Product, user, action, target, storeKey, databaseId, tenantId ?? AllTenants, message);
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
        logger.CapabilityDenied(text.Product, UserName(), denial.Capability.ToString(), denial.CapabilityOption);
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
    /// <paramref name="capability" /> is the capability the refused read needed - the one the write policy
    /// refused, or, for a database-browser read the store policy refused first, <c>BrowseDatabase</c>: it puts
    /// the entry under the same rule as a read of that capability on the Activity screen
    /// (<see cref="DatabaseAuditGate" />), so the name somebody was refused is shown only to a visitor who may
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

        Record(action, target, succeeded: false, text.ScopeDeniedEntry, capability, scope);
        logger.ScopeAuthorizationDenied(text.Product, UserName(), scope.StoreKey, scope.DatabaseId, scope.TenantId ?? AllTenants, policyName);
    }

    /// <inheritdoc cref="StudioActionLogService.GetLatest" />
    public IReadOnlyList<StudioActionLogEntry> GetLatest(int maxCount = StudioActionLogService.MaxEntries) =>
        activity.GetLatest(maxCount);

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
