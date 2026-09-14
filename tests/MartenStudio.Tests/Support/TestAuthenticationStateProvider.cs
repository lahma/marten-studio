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
    private int reads;

    /// <summary>
    /// How many times the principal has been asked for.
    /// </summary>
    /// <remarks>
    /// A host's provider is allowed to be expensive — rebuilding the principal, re-reading a cookie — so
    /// "how many times did this screen ask" is a real property of a sweep over five hundred audit
    /// entries, not an implementation detail. It is the difference between one fetch and one per entry.
    /// </remarks>
    public int Reads => Volatile.Read(ref reads);

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

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        Interlocked.Increment(ref reads);
        return Task.FromResult(state);
    }
}
