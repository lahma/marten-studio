using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.Cookies;

namespace PostgresStudio.Sample.Auth;

/// <summary>
/// The three hard-coded users the demo signs in, one per level of the authorization contract.
/// </summary>
/// <remarks>
/// Passwords equal to user names, in a sample that starts a throwaway database. This exists so the demo can show what
/// a <c>viewer</c> sees that an <c>admin</c> does not - capability by capability, since Postgres Studio asks its write
/// policy about each one by name - which is the whole point of the capability and policy design, not an example of
/// how to store a credential.
/// </remarks>
internal static class DevUsers
{
    /// <summary>May open the studio.</summary>
    public const string ReadScope = "studio:read";

    /// <summary>May read rows and definitions in the database browser (<c>BrowseDatabase</c>).</summary>
    public const string BrowseScope = "studio:browse";

    /// <summary>May see other sessions and their statements (<c>ViewDatabaseActivity</c>).</summary>
    public const string ActivityScope = "studio:activity";

    /// <summary>May run SQL in the console (<c>RunSql</c>).</summary>
    public const string SqlScope = "studio:sql";

    private static readonly Dictionary<string, string[]> Users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = [ReadScope, BrowseScope, ActivityScope, SqlScope],
        ["ops"] = [ReadScope, BrowseScope, ActivityScope],
        ["viewer"] = [ReadScope]
    };

    /// <summary>Every user name the demo knows, for the login page's own hint text.</summary>
    public static IEnumerable<KeyValuePair<string, string[]>> All => Users;

    /// <summary>
    /// The principal for <paramref name="userName" /> when <paramref name="password" /> matches the demo's rule (the
    /// password is the user name), or <see langword="null" />.
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
