using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace MartenStudio.Services;

/// <summary>
/// Holds <see cref="MartenStudioOptions.StoreAuthorizationPolicy" /> and
/// <see cref="MartenStudioOptions.WriteAuthorizationPolicy" /> against one
/// <see cref="MartenStoreResource" />.
/// </summary>
/// <remarks>
/// <para>
/// The visitor comes from <see cref="AuthenticationStateProvider" /> and not from an
/// <c>HttpContext</c>: a rendered studio is a circuit, and its request is long gone by the time a page
/// asks for anything. The endpoint policy (<see cref="MartenStudioOptions.AuthorizationPolicy" />) has
/// already decided who gets in; this decides which store, database and tenant, and it is asked on
/// <em>every</em> data call rather than only at the selector (plan D5).
/// </para>
/// <para>
/// With no store policy configured nothing is asked and everything passes, which is what keeps an
/// application that never set the option exactly as it was.
/// </para>
/// </remarks>
internal sealed class StudioAuthorization
{
    private readonly IOptions<MartenStudioOptions> options;
    private readonly IAuthorizationService authorizationService;
    private readonly AuthenticationStateProvider authenticationStateProvider;

    public StudioAuthorization(
        IOptions<MartenStudioOptions> options,
        IAuthorizationService authorizationService,
        AuthenticationStateProvider authenticationStateProvider)
    {
        this.options = options;
        this.authorizationService = authorizationService;
        this.authenticationStateProvider = authenticationStateProvider;
    }

    /// <summary>
    /// Whether a per-store policy is configured at all. A component reads it to know whether it has
    /// anything to wait for before it renders.
    /// </summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(options.Value.StoreAuthorizationPolicy);

    /// <summary>
    /// Whether the visitor may exercise <paramref name="capability" /> on <paramref name="scope" />.
    /// </summary>
    /// <param name="scope">The store, database and tenant being reached.</param>
    /// <param name="capability">
    /// The capability name for a mutating call, or <see langword="null" /> for a read. A non-null value
    /// selects <see cref="MartenStudioOptions.WriteAuthorizationPolicy" />, falling back to the store
    /// policy when the host configured only one.
    /// </param>
    /// <param name="cancellationToken">Cancels the evaluation.</param>
    public async ValueTask<bool> IsAuthorizedAsync(
        StudioScope scope,
        string? capability = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        string? policyName = PolicyFor(capability);
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return await IsAuthorizedAsync(state.User, scope, capability, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same question for a caller that already holds the principal.
    /// </summary>
    public async ValueTask<bool> IsAuthorizedAsync(
        ClaimsPrincipal user,
        StudioScope scope,
        string? capability = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(scope);

        string? policyName = PolicyFor(capability);
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        AuthorizationResult result = await authorizationService
            .AuthorizeAsync(user, scope.ToResource(capability), policyName)
            .ConfigureAwait(false);

        return result.Succeeded;
    }

    /// <summary>
    /// The scopes of <paramref name="scopes" /> the visitor may see, in the order they arrived.
    /// </summary>
    /// <remarks>
    /// Listings are filtered rather than annotated, because an entry in a picker is one the visitor can
    /// select - and the count of tenants in a process is itself something a tenant should not learn.
    /// </remarks>
    public async ValueTask<List<T>> FilterAsync<T>(
        IReadOnlyList<T> scopes,
        Func<T, StudioScope> scopeOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(scopeOf);

        if (!IsEnabled)
        {
            return [.. scopes];
        }

        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);

        List<T> allowed = new(scopes.Count);
        foreach (T candidate in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsAuthorizedAsync(state.User, scopeOf(candidate), capability: null, cancellationToken).ConfigureAwait(false))
            {
                allowed.Add(candidate);
            }
        }

        return allowed;
    }

    /// <summary>
    /// Who the circuit belongs to, or <c>anonymous</c> when nothing has said. Used by the audit log.
    /// </summary>
    public async ValueTask<string> UserNameAsync()
    {
        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        string? name = state.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? "anonymous" : name;
    }

    /// <summary>
    /// Which policy answers for this call: the write policy for a capability, the store policy otherwise,
    /// and the store policy for a write when the host configured only one.
    /// </summary>
    internal string? PolicyFor(string? capability)
    {
        MartenStudioOptions value = options.Value;
        if (capability is null)
        {
            return value.StoreAuthorizationPolicy;
        }

        return string.IsNullOrWhiteSpace(value.WriteAuthorizationPolicy)
            ? value.StoreAuthorizationPolicy
            : value.WriteAuthorizationPolicy;
    }
}
