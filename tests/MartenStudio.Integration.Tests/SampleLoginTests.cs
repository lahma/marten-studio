using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

using MartenStudio.Sample;
using MartenStudio.Sample.Auth;

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// The sample host's sign-in form. No Marten and no Docker: the form is mapped on its own, because what
/// is being tested is the form.
/// </summary>
/// <remarks>
/// A demo's login page is still a login page. It was an open redirect and it accepted cross-site posts,
/// and both are the kind of thing somebody copies out of a sample into something that matters.
/// </remarks>
public class SampleLoginTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static WebApplication CreateApp()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.LoginPath = "/login");
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery();

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapLogin();
        return app;
    }

    /// <summary>The request token out of the rendered form, and the cookies the page set.</summary>
    private static async Task<(string Token, string Cookies)> SignInPageAsync(HttpClient client)
    {
        using HttpResponseMessage page = await client.GetAsync(new Uri("/login", UriKind.Relative), Token);
        page.StatusCode.Should().Be(HttpStatusCode.OK);

        string html = await page.Content.ReadAsStringAsync(Token);
        Match match = Regex.Match(
            html,
            $"name=\"{LoginEndpoints.AntiforgeryFieldName}\" value=\"(?<token>[^\"]+)\"",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        match.Success.Should().BeTrue("the plain HTML form has to carry a request token for the POST to be verifiable");

        IEnumerable<string> setCookies = page.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? values : [];
        string cookies = string.Join("; ", setCookies.Select(static x => x.Split(';')[0]));
        cookies.Should().NotBeEmpty("GetAndStoreTokens sets the companion cookie on this response");

        return (WebUtility.HtmlDecode(match.Groups["token"].Value), cookies);
    }

    private static FormUrlEncodedContent SignInForm(string token, string user, string returnUrl) =>
        new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LoginEndpoints.AntiforgeryFieldName] = token,
            ["username"] = user,
            ["password"] = user,
            ["returnUrl"] = returnUrl,
        });

    // -------------------------------------------------------------------------------------------
    // Open redirect
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>/\evil.example</c> is a protocol-relative URL exactly as <c>//evil.example</c> is: browsers
    /// treat the backslash as a second slash, which is why ASP.NET Core's own <c>IsLocalUrl</c> rejects
    /// both. Checking only for <c>//</c> leaves the form usable as the redirect in a phishing link.
    /// </summary>
    [Theory]
    [InlineData("/marten", "/marten")]
    [InlineData("/marten/activity?store=default", "/marten/activity?store=default")]
    [InlineData("/", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/\\\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void Only_a_local_path_is_ever_redirected_to(string? returnUrl, string expected)
    {
        LoginEndpoints.SafeReturnUrl(returnUrl).Should().Be(expected);
    }

    [Fact]
    public async Task A_protocol_relative_return_url_does_not_leave_the_site()
    {
        await using WebApplication app = CreateApp();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            (string token, string cookies) = await SignInPageAsync(client);

            using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/login", UriKind.Relative))
            {
                Content = SignInForm(token, "admin", "/\\evil.example"),
            };
            request.Headers.Add("Cookie", cookies);

            using HttpResponseMessage response = await client.SendAsync(request, Token);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().Be("/");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    // -------------------------------------------------------------------------------------------
    // Antiforgery
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sign_in_without_a_token_is_refused()
    {
        await using WebApplication app = CreateApp();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = "admin",
                ["password"] = "admin",
                ["returnUrl"] = "/marten",
            });

            using HttpResponseMessage response = await client.PostAsync(new Uri("/login", UriKind.Relative), form, Token);

            response.StatusCode.Should().Be(
                HttpStatusCode.BadRequest,
                "a sign-in form with no token check is a cross-site login");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_sign_out_without_a_token_is_refused()
    {
        await using WebApplication app = CreateApp();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.PostAsync(
                new Uri("/logout", UriKind.Relative),
                new StringContent(string.Empty, MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded")),
                Token);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_sign_in_with_the_forms_own_token_works()
    {
        await using WebApplication app = CreateApp();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            (string token, string cookies) = await SignInPageAsync(client);

            using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/login", UriKind.Relative))
            {
                Content = SignInForm(token, "admin", "/marten"),
            };
            request.Headers.Add("Cookie", cookies);

            using HttpResponseMessage response = await client.SendAsync(request, Token);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().Be("/marten");
            response.Headers.GetValues("Set-Cookie").Should().Contain(static x => x.StartsWith(".AspNetCore.Cookies", StringComparison.Ordinal));
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    [Fact]
    public async Task A_wrong_password_goes_back_to_the_form_rather_than_signing_anybody_in()
    {
        await using WebApplication app = CreateApp();
        await app.StartAsync(Token);
        try
        {
            using HttpClient client = app.GetTestClient();
            (string token, string cookies) = await SignInPageAsync(client);

            using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/login", UriKind.Relative))
            {
                Content = SignInForm(token, "admin", "/marten"),
            };
            request.Headers.Add("Cookie", cookies);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LoginEndpoints.AntiforgeryFieldName] = token,
                ["username"] = "admin",
                ["password"] = "not-the-password",
                ["returnUrl"] = "/marten",
            });

            using HttpResponseMessage response = await client.SendAsync(request, Token);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect);
            response.Headers.Location!.OriginalString.Should().StartWith("/login?error=1");
        }
        finally
        {
            await app.StopAsync(Token);
        }
    }

    // -------------------------------------------------------------------------------------------
    // The throwaway Postgres switch
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>TESTCONTAINERS_REUSE_ENABLE</c> does not exist in Testcontainers for .NET 4.x — it reads like
    /// the obvious switch and does nothing at all, which is worse than no switch. The sample reads
    /// <c>MARTENSTUDIO_PG_REUSE</c> instead; <see langword="null" /> here is "the variable is not set".
    /// </summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData(" false ", false)]
    [InlineData("0", false)]
    [InlineData("off", false)]
    public void Container_reuse_is_on_unless_the_environment_says_otherwise(string? configured, bool expected)
    {
        EphemeralPostgres.IsReuseEnabled(configured).Should().Be(expected);
    }
}
