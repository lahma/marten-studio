using System.Net;
using System.Security.Claims;
using System.Text;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MartenStudio.Sample.Auth;

/// <summary>
/// A plain HTML login form, because the demo needs the three users to be reachable and nothing more.
/// </summary>
internal static class LoginEndpoints
{
    /// <summary>Maps <c>GET /login</c>, <c>POST /login</c> and <c>POST /logout</c>.</summary>
    public static void MapLogin(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery, string? returnUrl, string? error) =>
        {
            // GetAndStoreTokens both mints the request token and sets the companion cookie on this
            // response, which is what makes the hidden field below verifiable on the POST.
            AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(LoginPage(returnUrl, error, tokens));
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await IsRequestValidAsync(context, antiforgery))
            {
                return;
            }

            IFormCollection form = await context.Request.ReadFormAsync();
            string? userName = form["username"];
            string? password = form["password"];
            string? returnUrl = form["returnUrl"];

            ClaimsPrincipal? principal = DevUsers.Authenticate(userName, password);
            if (principal is null)
            {
                context.Response.Redirect($"/login?error=1&returnUrl={WebUtility.UrlEncode(SafeReturnUrl(returnUrl))}");
                return;
            }

            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            context.Response.Redirect(SafeReturnUrl(returnUrl));
        }).AllowAnonymous();

        app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await IsRequestValidAsync(context, antiforgery))
            {
                return;
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/");
        }).AllowAnonymous();
    }

    /// <summary>The hidden field name ASP.NET Core's antiforgery defaults expect.</summary>
    internal const string AntiforgeryFieldName = "__RequestVerificationToken";

    /// <summary>
    /// Validates the antiforgery token, answering <c>400</c> when it does not hold.
    /// </summary>
    /// <remarks>
    /// Explicitly rather than by metadata. <c>UseAntiforgery()</c>'s middleware only validates endpoints
    /// that carry <c>IAntiforgeryMetadata</c>, which a minimal-API handler that reads the form itself does
    /// not get; <c>DisableAntiforgery()</c> was therefore not disabling a check that was happening, it was
    /// stating that none was - and a sign-in form with no token check is a cross-site login, where an
    /// attacker signs a visitor into an account the attacker controls and then reads whatever the visitor
    /// does in it.
    /// </remarks>
    private static async Task<bool> IsRequestValidAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("The form could not be verified. Reload the sign-in page and try again.");
            return false;
        }
    }

    /// <summary>
    /// Only a local path is ever redirected to, so the form cannot be used as an open redirect.
    /// </summary>
    /// <remarks>
    /// Both <c>//evil.example</c> and <c>/\evil.example</c> are protocol-relative URLs: browsers treat a
    /// backslash after the leading slash the same as a second slash, which is why ASP.NET Core's own
    /// <c>IsLocalUrl</c> rejects both. Checking only for <c>//</c> leaves the backslash form open, and an
    /// open redirect on a sign-in page is the one that matters - it is the link a phishing mail sends.
    /// </remarks>
    internal static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl.Length < 1 || returnUrl[0] != '/')
        {
            return "/";
        }

        return returnUrl.Length > 1 && (returnUrl[1] == '/' || returnUrl[1] == '\\') ? "/" : returnUrl;
    }

    private static string LoginPage(string? returnUrl, string? error, AntiforgeryTokenSet tokens)
    {
        StringBuilder users = new();
        foreach ((string name, string[] scopes) in DevUsers.All)
        {
            users.Append("<li><code>").Append(WebUtility.HtmlEncode(name)).Append(" / ").Append(WebUtility.HtmlEncode(name))
                .Append("</code> &mdash; ").Append(WebUtility.HtmlEncode(string.Join(", ", scopes))).Append("</li>");
        }

        string errorBanner = error is null
            ? string.Empty
            : "<p style=\"color:#dc2626\">That user name and password did not match. In this demo the password is the user name.</p>";

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Sign in - Marten Studio sample</title>
              <style>
                body { font-family: system-ui, sans-serif; margin: 3rem auto; max-width: 34rem; line-height: 1.5; }
                label { display: block; margin-top: 1rem; font-weight: 600; }
                input { font: inherit; padding: .4rem .6rem; width: 100%; box-sizing: border-box; }
                button { font: inherit; margin-top: 1.25rem; padding: .5rem 1rem; }
                ul { padding-left: 1.2rem; }
              </style>
            </head>
            <body>
              <h1>Marten Studio sample</h1>
              {{errorBanner}}
              <form method="post" action="/login">
                <input type="hidden" name="{{AntiforgeryFieldName}}" value="{{WebUtility.HtmlEncode(tokens.RequestToken)}}" />
                <input type="hidden" name="returnUrl" value="{{WebUtility.HtmlEncode(SafeReturnUrl(returnUrl))}}" />
                <label for="username">User name</label>
                <input id="username" name="username" autocomplete="username" autofocus />
                <label for="password">Password</label>
                <input id="password" name="password" type="password" autocomplete="current-password" />
                <button type="submit">Sign in</button>
              </form>
              <h2>Demo users</h2>
              <ul>{{users}}</ul>
              <p>The password is the user name. This is a demo host with a throwaway database.</p>
            </body>
            </html>
            """;
    }
}
