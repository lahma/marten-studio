using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using MartenStudio.Integration.Tests.Browser;
using MartenStudio.Integration.Tests.Health;
using MartenStudio.Sample.Auth;
using MartenStudio.Sample.Generation;
using MartenStudio.SampleDomain;

using Npgsql;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// The "make it unhealthy" switches through the real sample host on real Kestrel - the host the browser suite and the
/// large-data pass start - signed in as the demo's users over HTTP: the wiring, the gates and the clean-up, as somebody
/// pressing the buttons gets them.
/// </summary>
/// <remarks>
/// <para>
/// What each switch does to the database is checked from outside the host, as any other client of the server would
/// see it: the sessions a switch names after itself in <c>pg_stat_activity</c>, and the demo table's storage parameters.
/// Whether the studio's checks fire on them is <see cref="UnhealthySwitchesLiveTests" />' - in that process, since C01
/// reads the pool of the process it runs in.
/// </para>
/// <para>
/// <b>A database of its own</b>, because the sample's schema names are constants, and a <b>pool of 20</b>, so the pool
/// hog holds 18 connections of the shared server rather than 90 while the rest of the suite runs beside it.
/// </para>
/// </remarks>
public class UnhealthySwitchesSampleHostTests(PostgresFixture postgres)
{
    private const string Database = "unhealthy_sample_host";

    private static readonly Regex RequestToken = new(
        "name=\"" + LoginEndpoints.AntiforgeryFieldName + "\" value=\"(?<token>[^\"]+)\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static readonly (string Slug, string Application)[] Sessions =
    [
        ("pinned-gap", UnhealthySwitch.ApplicationNamePrefix + "pinned gap"),
        ("idle-in-transaction", UnhealthySwitch.ApplicationNamePrefix + "idle in transaction"),
        ("pool-hog", UnhealthySwitch.ApplicationNamePrefix + "pool hog"),
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_admin_flips_every_switch_on_and_off_through_the_host_and_nothing_is_left_behind()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, Database);
        string pooled = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 20 }.ConnectionString;

        // A configured database, so the pinned gap and the pool hog need the flag said outright.
        await using SampleHost host = await SampleHost.StartAsync(pooled, allowDataGeneration: true);
        await SampleHost.WaitForSeedAsync(connectionString, Token);

        // Created at start-up from the store's declaration, so the Schema screen never shows it missing.
        (await LiveWait.ScalarAsync<bool>(connectionString, "select pg_catalog.to_regclass(@table) is not null",
            ("table", SampleStore.DocumentSchema + "." + UnhealthyDemoStore.BloatTableName)))
            .Should().BeTrue("the host creates the bloated-table switch's demo table when it starts: " + host.LogTail());

        using HttpClient admin = await SignInAsync(host, "admin");

        string page = await (await admin.GetAsync(new Uri("/", UriKind.Relative), Token)).Content.ReadAsStringAsync(Token);
        page.Should().Contain("Make it unhealthy");
        foreach (string slug in (string[]) ["pinned-gap", "idle-in-transaction", "bloated-table", "pool-hog"])
        {
            page.Should().Contain("id=\"unhealthy-" + slug + "\" data-switch=\"" + slug + "\" data-state=\"Off\"", "all four are off at start-up");
        }

        string token = RequestTokenIn(page);

        foreach ((string slug, string application) in Sessions)
        {
            await FlipAsync(admin, token, slug, on: true);
            (await SessionsAsync(connectionString, application)).Should().BePositive("{0} holds its sessions while it is on", slug);

            await FlipAsync(admin, token, slug, on: false);
            (await LiveWait.ScalarAsync<long>(connectionString,
                "select count(*) from pg_catalog.pg_stat_activity where datname = current_database() and application_name = @name and state <> 'idle'",
                ("name", application))).Should().Be(0, "{0} leaves no session working or in a transaction", slug);
        }

        (await SessionsAsync(connectionString, Sessions[2].Application)).Should().Be(0, "the pool hog's connections are closed, not left idle in the pool");

        await FlipAsync(admin, token, "bloated-table", on: true);
        (await ReloptionsAsync(connectionString)).Should().Contain("autovacuum_enabled=false");
        await FlipAsync(admin, token, "bloated-table", on: false);
        (await ReloptionsAsync(connectionString)).Should().BeNull("autovacuum is restored");

        host.Log.Should().Contain("The Pool hog switch is off: Turned off", "the board logs how each run ended");
    }

    [PostgresFact]
    public async Task A_viewer_cannot_flip_a_switch_and_on_a_configured_database_the_admin_cannot_flip_the_two_that_reach_beyond_the_demo()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, Database + "_viewer");

        // Development maps the endpoints; the database is configured and the flag was not given.
        await using SampleHost host = await SampleHost.StartAsync(connectionString);

        using (HttpClient admin = await SignInAsync(host, "admin"))
        {
            string adminPage = await (await admin.GetAsync(new Uri("/", UriKind.Relative), Token)).Content.ReadAsStringAsync(Token);
            adminPage.Should().Contain("was configured (ConnectionStrings:Marten); it is not the sample&#39;s throwaway container");

            using FormUrlEncodedContent adminForm = new(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [LoginEndpoints.AntiforgeryFieldName] = RequestTokenIn(adminPage),
            });

            foreach (string slug in (string[]) ["pool-hog", "pinned-gap"])
            {
                using HttpResponseMessage refused = await admin.PostAsync(
                    new Uri(UnhealthySwitchEndpoints.BasePath + "/" + slug + "/on", UriKind.Relative), adminForm, Token);
                refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
                WebUtility.UrlDecode(refused.Headers.Location!.OriginalString).Should().Contain("is refused here", "{0} on a configured database", slug);
            }

            using JsonDocument board = JsonDocument.Parse(await admin.GetStringAsync(new Uri(UnhealthySwitchEndpoints.StatusPath, UriKind.Relative), Token));
            JsonElement[] switches = [.. board.RootElement.GetProperty("switches").EnumerateArray()];
            switches.Should().OnlyContain(static x => x.GetProperty("state").GetString() == "Off");
            switches.Where(static x => x.GetProperty("refused").ValueKind == JsonValueKind.String)
                .Select(static x => x.GetProperty("slug").GetString()).Should().BeEquivalentTo("pinned-gap", "pool-hog");
        }

        (await SessionsAsync(connectionString, Sessions[2].Application)).Should().Be(0, "the pool hog was refused");

        using HttpClient viewer = await SignInAsync(host, "viewer");

        string page = await (await viewer.GetAsync(new Uri("/", UriKind.Relative), Token)).Content.ReadAsStringAsync(Token);
        page.Should().Contain("Make it unhealthy").And.Contain("MartenStudioAdmin");
        page.Should().NotContain("/sample/unhealthy/", "a viewer is given no switch to press");

        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LoginEndpoints.AntiforgeryFieldName] = RequestTokenIn(page),
        });

        using HttpResponseMessage on = await viewer.PostAsync(new Uri("/sample/unhealthy/idle-in-transaction/on", UriKind.Relative), form, Token);
        on.StatusCode.Should().Be(HttpStatusCode.Redirect);
        on.Headers.Location!.AbsolutePath.Should().Be("/login", "the admin policy sends a signed-in visitor it refuses to the access-denied page");

        using HttpResponseMessage status = await viewer.GetAsync(new Uri(UnhealthySwitchEndpoints.StatusPath, UriKind.Relative), Token);
        status.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await SessionsAsync(connectionString, Sessions[1].Application)).Should().Be(0, "nothing was turned on");
    }

    /// <summary>
    /// In Production without <c>--allow-data-generation</c> the switches are not mapped at all: a POST and the status
    /// answer 404 - there is nothing there to refuse - and the page says which switch would map them.
    /// </summary>
    [PostgresFact]
    public async Task In_Production_without_the_flag_the_switches_are_not_there()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, Database + "_production");

        await using SampleHost host = await SampleHost.StartAsync(connectionString, environment: "Production");

        using HttpClient admin = await SignInAsync(host, "admin");

        string page = await (await admin.GetAsync(new Uri("/", UriKind.Relative), Token)).Content.ReadAsStringAsync(Token);
        page.Should().Contain("Make it unhealthy").And.Contain("--allow-data-generation");
        page.Should().NotContain(UnhealthySwitchEndpoints.BasePath + "/", "nothing is offered that is not mapped");

        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LoginEndpoints.AntiforgeryFieldName] = RequestTokenIn(page),
        });

        // A POST is answered exactly as one to a path the host never mapped. That is 405 rather than 404 here: outside
        // Development this build-output host has a GET/HEAD-only route that matches any path (measured with the control
        // below: Allow: GET, HEAD), so routing reports the method rather than the path. Either way nothing accepts a POST.
        using HttpResponseMessage on = await admin.PostAsync(new Uri(UnhealthySwitchEndpoints.BasePath + "/pool-hog/on", UriKind.Relative), form, Token);
        using HttpResponseMessage never = await admin.PostAsync(new Uri("/sample/never-mapped/at-all", UriKind.Relative), form, Token);

        on.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        on.StatusCode.Should().Be(never.StatusCode, "the switch's path is as unmapped as a path that never existed");
        on.Content.Headers.Allow.Should().NotContain("POST");

        using HttpResponseMessage status = await admin.GetAsync(new Uri(UnhealthySwitchEndpoints.StatusPath, UriKind.Relative), Token);
        status.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Turns a switch on or off and waits, on its own status, until it says so.</summary>
    private static async Task FlipAsync(HttpClient client, string token, string slug, bool on)
    {
        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LoginEndpoints.AntiforgeryFieldName] = token,
        });

        using HttpResponseMessage response = await client.PostAsync(
            new Uri(UnhealthySwitchEndpoints.BasePath + "/" + slug + (on ? "/on" : "/off"), UriKind.Relative), form, Token);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("/?" + UnhealthySwitchEndpoints.MessageQueryKey + "=");

        await LiveWait.UntilAsync(
            async () =>
            {
                using JsonDocument status = JsonDocument.Parse(await client.GetStringAsync(new Uri(UnhealthySwitchEndpoints.StatusPath, UriKind.Relative), Token));
                JsonElement entry = status.RootElement.GetProperty("switches").EnumerateArray()
                    .Single(x => x.GetProperty("slug").GetString() == slug);

                string? state = entry.GetProperty("state").GetString();
                state.Should().NotBe("Failed", "{0} failed: {1}", slug, entry.GetProperty("lastOutcome").GetString());

                return on
                    ? state == "On" && entry.GetProperty("turnsOffAt").ValueKind == JsonValueKind.String
                    : state == "Off" && entry.GetProperty("lastOutcome").GetString()!.StartsWith("Turned off", StringComparison.Ordinal);
            },
            TimeSpan.FromSeconds(60),
            slug + (on ? " to be on" : " to be off"));
    }

    private static async Task<HttpClient> SignInAsync(SampleHost host, string user)
    {
        HttpClientHandler handler = new() { CookieContainer = new CookieContainer(), UseCookies = true, AllowAutoRedirect = false };
        HttpClient client = new(handler, disposeHandler: true) { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };

        string login = await client.GetStringAsync(new Uri("/login", UriKind.Relative), Token);

        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LoginEndpoints.AntiforgeryFieldName] = RequestTokenIn(login),
            ["username"] = user,
            ["password"] = user,
            ["returnUrl"] = "/",
        });

        using HttpResponseMessage response = await client.PostAsync(new Uri("/login", UriKind.Relative), form, Token);
        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the demo signs {0} in with the user name as the password", user);

        return client;
    }

    private static string RequestTokenIn(string html)
    {
        Match match = RequestToken.Match(html);
        match.Success.Should().BeTrue("the page's forms carry a request token");
        return WebUtility.HtmlDecode(match.Groups["token"].Value);
    }

    private static async Task<long> SessionsAsync(string connectionString, string application) =>
        await LiveWait.ScalarAsync<long>(
            connectionString,
            "select count(*) from pg_catalog.pg_stat_activity where datname = current_database() and application_name = @name",
            ("name", application));

    private static Task<string?> ReloptionsAsync(string connectionString) =>
        LiveWait.ScalarAsync<string>(
            connectionString,
            "select pg_catalog.array_to_string(c.reloptions, ',') from pg_catalog.pg_class c "
            + "join pg_catalog.pg_namespace n on n.oid = c.relnamespace where n.nspname = @schema and c.relname = @table",
            ("schema", SampleStore.DocumentSchema),
            ("table", UnhealthyDemoStore.BloatTableName));
}
