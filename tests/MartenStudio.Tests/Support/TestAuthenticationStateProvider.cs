using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;

namespace MartenStudio.Tests.Support;

/// <summary>
/// The circuit's visitor, as a test names them.
/// </summary>
/// <remarks>
/// The studio reads the principal from <see cref="AuthenticationStateProvider" /> rather than from an
/// <c>HttpContext</c>, because a rendered studio is a circuit and its request is long gone. This is the
/// seam that makes that testable.
/// </remarks>
public sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
{
    private AuthenticationState state = new(new ClaimsPrincipal(new ClaimsIdentity()));

    /// <summary>Signs in as <paramref name="name" /> with the given claims.</summary>
    public void SignIn(string name, params (string Type, string Value)[] claims)
    {
        List<Claim> all = [new(ClaimTypes.Name, name)];
        foreach ((string type, string value) in claims)
        {
            all.Add(new Claim(type, value));
        }

        state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(all, "test")));
        NotifyAuthenticationStateChanged(Task.FromResult(state));
    }

    /// <summary>Signs out, which is what an application that authenticates nobody looks like.</summary>
    public void SignOut()
    {
        state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));
        NotifyAuthenticationStateChanged(Task.FromResult(state));
    }

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(state);
}
