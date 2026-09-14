using System.Net;
using System.Security.Claims;
using System.Text;

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

        app.MapGet("/login", (HttpContext context, string? returnUrl, string? error) =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(LoginPage(returnUrl, error));
        }).AllowAnonymous();

        app.MapPost("/login", async (HttpContext context) =>
        {
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
        }).AllowAnonymous().DisableAntiforgery();

        app.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            context.Response.Redirect("/");
        }).AllowAnonymous().DisableAntiforgery();
    }

    /// <summary>Only a local path is ever redirected to, so the form cannot be used as an open redirect.</summary>
    private static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";

    private static string LoginPage(string? returnUrl, string? error)
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
