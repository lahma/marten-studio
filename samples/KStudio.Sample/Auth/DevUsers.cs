using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.Cookies;

namespace KStudio.Sample.Auth;

/// <summary>
/// The two hard-coded users the demo signs in: an administrator, and a support engineer who may look at orders only.
/// </summary>
/// <remarks>
/// Passwords equal to user names, in a sample that starts throwaway containers. This exists so the demo can show what
/// <c>support</c> sees that <c>admin</c> does not (the per-topic gate, D35) - not as an example of how to store a credential.
/// </remarks>
internal static class DevUsers
{
    /// <summary>May open the studio.</summary>
    public const string ReadScope = "studio:read";

    /// <summary>May change documents, streams, projections and schema in the Marten half of the studio.</summary>
    public const string WriteScope = "studio:write";

    /// <summary>May read every topic of the cluster (<see cref="Kafka.SampleKafkaPolicies" />).</summary>
    public const string KafkaAllScope = "kafka:all";

    /// <summary>May read the topics whose names start with <c>orders.</c> and no others.</summary>
    public const string KafkaOrdersScope = "kafka:orders";

    private static readonly Dictionary<string, string[]> Users = new(StringComparer.OrdinalIgnoreCase)
    {
        ["admin"] = [ReadScope, WriteScope, KafkaAllScope],
        ["support"] = [ReadScope, KafkaOrdersScope],
    };

    /// <summary>Every user name the demo knows, for the login page's own hint text.</summary>
    public static IEnumerable<KeyValuePair<string, string[]>> All => Users;

    /// <summary>
    /// The principal for <paramref name="userName" /> when <paramref name="password" /> matches the
    /// demo's rule (the password is the user name), or <see langword="null" />.
    /// </summary>
    /// <param name="userName">The user name.</param>
    /// <param name="password">The password.</param>
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
