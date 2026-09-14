using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.Cookies;

namespace MartenStudio.Sample.Auth;

/// <summary>
/// The three hard-coded users the demo signs in, one per level of the authorization contract.
/// </summary>
/// <remarks>
/// Passwords equal to user names, in a sample that starts a throwaway database. This exists so the demo
/// can show what a <c>viewer</c> sees that an <c>admin</c> does not, which is the whole point of the
/// capability and policy design - not as an example of how to store a credential.
/// </remarks>
internal static class DevUsers
{
    /// <summary>May open the studio.</summary>
    public const string ReadScope = "studio:read";

    /// <summary>May change documents, streams, projections and schema.</summary>
    public const string WriteScope = "studio:write";

    /// <summary>May do the irreversible things.</summary>
    public const string DestroyScope = "studio:destroy";

    private static readonly Dictionary<string, string[]> Users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = [ReadScope, WriteScope, DestroyScope],
        ["ops"] = [ReadScope, WriteScope],
        ["viewer"] = [ReadScope]
    };

    /// <summary>Every user name the demo knows, for the login page's own hint text.</summary>
    public static IEnumerable<KeyValuePair<string, string[]>> All => Users;

    /// <summary>
    /// The principal for <paramref name="userName" /> when <paramref name="password" /> matches the
    /// demo's rule (the password is the user name), or <see langword="null" />.
    /// </summary>
    public static ClaimsPrincipal? Authenticate(string? userName, string? password)
    {
        if (string.IsNullOrWhiteSpace(userName)
            || !Users.TryGetValue(userName, out string[]? scopes)
            || !string.Equals(userName, password, StringComparison.Ordinal))
        {
            return null;
        }

        List<Claim> claims = [new(ClaimTypes.Name, userName)];
        foreach (string scope in scopes)
        {
            claims.Add(new Claim(SamplePolicies.ScopeClaim, scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }
}
