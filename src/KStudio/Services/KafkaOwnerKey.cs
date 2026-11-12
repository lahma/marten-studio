using System.Security.Claims;

using KStudio.Core.Scanning;

using Microsoft.AspNetCore.Components.Authorization;

namespace KStudio.Services;

/// <summary>
/// Who a scan belongs to (D-UI decision 11): the engine counts concurrent scans and the hourly byte allowance per owner, and only KStudio decides
/// who an owner is. A visitor with a name is <c>user:{name}</c>, so two tabs of one user share the slot and the allowance, which is the point of an
/// owner. A principal with no name at all (a host with no authentication) is <c>circuit:{guid}</c>, a Guid made once per circuit. The key is the
/// engine's to keep: it is never logged and never drawn.
/// </summary>
/// <param name="authenticationStateProvider">Who is asking.</param>
internal sealed class KafkaOwnerKey(AuthenticationStateProvider authenticationStateProvider)
{
    private readonly string circuit = "circuit:" + Guid.NewGuid().ToString("N");

    /// <summary>The owner of the scans this circuit starts.</summary>
    public async ValueTask<KafkaScanOwner> OwnerAsync()
    {
        AuthenticationState state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        ClaimsPrincipal user = state.User;

        string? name = user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        }

        return new KafkaScanOwner(string.IsNullOrWhiteSpace(name) ? circuit : "user:" + name);
    }
}
