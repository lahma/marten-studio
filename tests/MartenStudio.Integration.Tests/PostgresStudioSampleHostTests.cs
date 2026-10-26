using System.Net;
using System.Text.RegularExpressions;

using MartenStudio.Integration.Tests.Browser;
using MartenStudio.Sample.Auth;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// The Postgres Studio sample, started exactly as <c>dotnet run</c> starts it - nothing configured - on real Kestrel:
/// it starts a throwaway Postgres of its own, seeds the admin database and the three tenant databases, and serves
/// <c>/postgres</c> to a signed-in user with every database in the picker.
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero-config, on purpose.</b> The Marten sample's host tests hand it a database of the suite's container, so as not
/// to pay a container per host. This one pays for one, because what it proves is D18 itself: no connection string, a
/// banner, a fresh container (<c>MARTENSTUDIO_PG_REUSE=false</c> is set for the child), seeding on a server with no
/// Marten anywhere, and a studio that answers. Testcontainers' reaper removes the container when the child is killed.
/// </para>
/// <para>
/// <b>What the picker says is read from the prerendered page.</b> The scope selector renders its options during the
/// static render, so the default source, the tenants source and - with <c>?store=tenants</c> - the three tenant
/// databases are in the HTML a plain GET returns, listed through the sample's own <c>IPostgresDatabaseSource</c>.
/// </para>
/// <para>
/// It lives here until <c>tests/PostgresStudio.Integration.Tests</c> exists (PS-5), which is its natural home: this
/// suite builds the sample through a project reference that references none of its assembly.
/// </para>
/// </remarks>
public class PostgresStudioSampleHostTests
{
    // ASP.NET Core's default field name, which both samples' sign-in forms use; the Marten sample's constant names it.
    private static readonly Regex RequestToken = new(
        "name=\"" + LoginEndpoints.AntiforgeryFieldName + "\" value=\"(?<token>[^\"]+)\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task With_nothing_configured_it_starts_its_own_postgres_and_serves_the_studio_with_every_database_in_the_picker()
    {
        await using SampleHost host = await SampleHost.StartAsync(SampleApp.Postgres, connectionString: null, cancellationToken: Token);

        host.StudioPath.Should().Be("/postgres");
        host.Log.Should().Contain("throwaway PostgreSQL container was started", "D18's banner says what the sample did");
        host.Log.Should().Contain("no - a fresh container", "this sample does not reuse a container unless asked");

        using (HttpClient anonymous = Client(host))
        {
            using HttpResponseMessage refused = await anonymous.GetAsync(new Uri("/postgres", UriKind.Relative), Token);
            refused.StatusCode.Should().Be(HttpStatusCode.Redirect, "the mapping requires the PostgresStudio policy");
            refused.Headers.Location!.OriginalString.Should().Contain("/login");
        }

        using HttpClient admin = await SignInAsync(host, "admin");

        using (HttpResponseMessage page = await admin.GetAsync(new Uri("/postgres", UriKind.Relative), Token))
        {
            page.StatusCode.Should().Be(HttpStatusCode.OK, host.LogTail());

            string html = await page.Content.ReadAsStringAsync(Token);
            html.Should().Contain("<title>Postgres Studio</title>");
            html.Should().Contain("<base href=\"/postgres/\"");
            html.Should().Contain("<option value=\"default\"", "the admin database is the application's unkeyed NpgsqlDataSource");
            html.Should().Contain("<option value=\"tenants\"", "the tenant databases are a second source, AddDatabaseSource(\"tenants\")");
        }

        using (HttpResponseMessage tenants = await admin.GetAsync(new Uri("/postgres?store=tenants", UriKind.Relative), Token))
        {
            tenants.StatusCode.Should().Be(HttpStatusCode.OK, host.LogTail());

            string html = await tenants.Content.ReadAsStringAsync(Token);
            foreach (string tenant in (string[]) ["tenant_a", "tenant_b", "tenant_c"])
            {
                html.Should().Contain("<option value=\"" + tenant + "\"", "the host lists {0} from its admin database's directory", tenant);
            }
        }

        using (HttpResponseMessage script = await admin.GetAsync(new Uri("/postgres/_framework/blazor.web.js", UriKind.Relative), Token))
        {
            script.StatusCode.Should().Be(HttpStatusCode.OK, "D22: the sample sets no RequiresAspNetWebAssets and still serves the script");
            (await script.Content.ReadAsByteArrayAsync(Token)).Should().NotBeEmpty();
        }

        using (HttpResponseMessage negotiate = await admin.PostAsync(
            new Uri("/postgres/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), content: null, Token))
        {
            negotiate.StatusCode.Should().Be(HttpStatusCode.OK, "the circuit can be negotiated, so the studio becomes interactive");
        }
    }

    private static HttpClient Client(SampleHost host)
    {
        HttpClientHandler handler = new() { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>Signs in through the sample's own form, as the Marten sample's host tests do.</summary>
    private static async Task<HttpClient> SignInAsync(SampleHost host, string user)
    {
        HttpClient client = Client(host);

        string login = await client.GetStringAsync(new Uri("/login", UriKind.Relative), Token);
        Match token = RequestToken.Match(login);
        token.Success.Should().BeTrue("the sign-in form carries a request token");

        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LoginEndpoints.AntiforgeryFieldName] = WebUtility.HtmlDecode(token.Groups["token"].Value),
            ["username"] = user,
            ["password"] = user,
            ["returnUrl"] = "/",
        });

        using HttpResponseMessage response = await client.PostAsync(new Uri("/login", UriKind.Relative), form, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the demo signs {0} in with the user name as the password", user);

        return client;
    }
}
