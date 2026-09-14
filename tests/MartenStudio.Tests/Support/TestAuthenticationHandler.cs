using System.Security.Claims;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace MartenStudio.Tests.Support;

/// <summary>
/// An authentication handler that authenticates whoever <see cref="UserHeader" /> names and nobody
/// otherwise, so an authorization challenge surfaces as a plain 401 instead of a redirect to a login page
/// that does not exist.
/// </summary>
/// <remarks>
/// The header is how a test says "and now as somebody" without a sign-in flow. A request that does not
/// carry it is anonymous, which is what most of these tests are about.
/// </remarks>
public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "test-scheme";

    /// <summary>Naming a user in this request header authenticates the request as them.</summary>
    public const string UserHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out StringValues user) || string.IsNullOrWhiteSpace(user))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        ClaimsIdentity identity = new([new Claim(ClaimTypes.Name, user.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
