using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;

using MartenStudio.Integration.Tests.Logging;
using MartenStudio.Sample;
using MartenStudio.Sample.Auth;
using MartenStudio.Sample.Generation;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// The "Make it unhealthy" endpoints' gates and the panel's markup. No Marten and no Docker: the switches are stand-ins
/// that hold nothing, because what is being tested is who may flip one and what the page says - the switches' own
/// effect on a database is <see cref="UnhealthySwitchesLiveTests" />'.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the fast suite because the fast suite cannot see the sample, exactly as <c>SampleLoginTests</c>.
/// </para>
/// <para>
/// The visitor is named by a header a test authentication scheme turns into the demo user's own principal
/// (<see cref="DevUsers.Authenticate" />), so the claims the admin policy reads are the real ones; an anonymous request
/// is challenged (401) and a signed-in one the policy refuses is forbidden (403).
/// </para>
/// </remarks>
public class UnhealthySwitchEndpointTests
{
    private const string UserHeader = "X-Demo-User";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Who may flip a switch ------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("viewer", HttpStatusCode.Forbidden)]
    [InlineData("ops", HttpStatusCode.Forbidden)]
    public async Task Only_the_admin_may_turn_a_switch_on_or_off_or_read_their_state(string? user, HttpStatusCode refused)
    {
        await using TestApp app = await TestApp.StartAsync();
        (string token, string cookies) = await app.TokenAsync("admin");

        foreach (string path in (string[]) ["/sample/unhealthy/idle-in-transaction/on", "/sample/unhealthy/idle-in-transaction/off"])
        {
            using HttpResponseMessage response = await app.PostAsync(path, user, token, cookies);
            response.StatusCode.Should().Be(refused, "POST {0} as {1}", path, user ?? "nobody");
        }

        using (HttpResponseMessage status = await app.GetAsync(UnhealthySwitchEndpoints.StatusPath, user))
        {
            status.StatusCode.Should().Be(refused);
        }

        app.Board.Status(UnhealthySwitchKind.IdleInTransaction).State.Should().Be(UnhealthySwitchState.Off, "nothing was turned on");
    }

    [Fact]
    public async Task The_admin_turns_a_switch_on_and_off_and_is_sent_back_to_the_panel()
    {
        await using TestApp app = await TestApp.StartAsync();
        (string token, string cookies) = await app.TokenAsync("admin");

        using (HttpResponseMessage on = await app.PostAsync("/sample/unhealthy/pool-hog/on", "admin", token, cookies))
        {
            on.StatusCode.Should().Be(HttpStatusCode.Redirect);
            on.Headers.Location!.OriginalString.Should().StartWith("/?unhealthy=").And.EndWith("#unhealthy");
            WebUtility.UrlDecode(on.Headers.Location.OriginalString).Should().Contain("Pool hog is on").And.Contain("C01");
        }

        await app.WaitForAsync(UnhealthySwitchKind.PoolHog, UnhealthySwitchState.On);

        using (HttpResponseMessage again = await app.PostAsync("/sample/unhealthy/pool-hog/on", "admin", token, cookies))
        {
            WebUtility.UrlDecode(again.Headers.Location!.OriginalString).Should().Contain("already on", "a second turn-on is refused, not queued");
        }

        using (HttpResponseMessage status = await app.GetAsync(UnhealthySwitchEndpoints.StatusPath, "admin"))
        {
            status.StatusCode.Should().Be(HttpStatusCode.OK);
            using JsonDocument json = JsonDocument.Parse(await status.Content.ReadAsStringAsync(Token));
            JsonElement[] switches = [.. json.RootElement.GetProperty("switches").EnumerateArray()];

            switches.Select(static x => x.GetProperty("slug").GetString()).Should().Equal("pinned-gap", "idle-in-transaction", "bloated-table", "pool-hog");

            JsonElement hog = switches.Single(static x => x.GetProperty("slug").GetString() == "pool-hog");
            hog.GetProperty("state").GetString().Should().Be("On");
            hog.GetProperty("running").GetBoolean().Should().BeTrue();
            hog.GetProperty("detail").GetString().Should().Be(FakeSwitch.Holding);
            hog.GetProperty("codes").EnumerateArray().Select(static x => x.GetString()).Should().Equal("C01");

            DateTimeOffset turnsOffAt = hog.GetProperty("turnsOffAt").GetDateTimeOffset();
            DateTimeOffset startedAt = hog.GetProperty("startedAt").GetDateTimeOffset();
            (turnsOffAt - startedAt).Should().Be(UnhealthySwitchBoard.DefaultTimeout, "every switch turns itself off after ten minutes");

            switches.Where(static x => x.GetProperty("slug").GetString() != "pool-hog")
                .Should().OnlyContain(static x => x.GetProperty("state").GetString() == "Off", "all four are off until turned on");
        }

        using (HttpResponseMessage off = await app.PostAsync("/sample/unhealthy/pool-hog/off", "admin", token, cookies))
        {
            off.StatusCode.Should().Be(HttpStatusCode.Redirect);
            WebUtility.UrlDecode(off.Headers.Location!.OriginalString).Should().Contain("Pool hog is off, and cleaned up");
        }

        UnhealthySwitchStatus after = app.Board.Status(UnhealthySwitchKind.PoolHog);
        after.State.Should().Be(UnhealthySwitchState.Off, "turning off waits for the clean-up");
        after.LastOutcome.Should().StartWith("Turned off");
        app.Switch(UnhealthySwitchKind.PoolHog).CleanedUp.Should().Be(1);
    }

    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_refused_and_turns_nothing_on()
    {
        await using TestApp app = await TestApp.StartAsync();

        using HttpResponseMessage response = await app.PostAsync("/sample/unhealthy/bloated-table/on", "admin", token: null, cookies: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        app.Board.Status(UnhealthySwitchKind.BloatedTable).State.Should().Be(UnhealthySwitchState.Off);
    }

    [Fact]
    public async Task An_unknown_switch_is_not_found()
    {
        await using TestApp app = await TestApp.StartAsync();
        (string token, string cookies) = await app.TokenAsync("admin");

        using HttpResponseMessage response = await app.PostAsync("/sample/unhealthy/drop-everything/on", "admin", token, cookies);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The gate that decides whether the endpoints are mapped at all (Program.cs maps them only where this says so);
    /// <c>UnhealthySwitchesSampleHostTests</c> proves the real host answers 404 in Production without the flag.
    /// </summary>
    [Theory]
    [InlineData("Development", false, true)]
    [InlineData("Development", true, true)]
    [InlineData("Production", false, false)]
    [InlineData("Production", true, true)]
    [InlineData("Staging", false, false)]
    public void The_switches_are_mapped_only_in_Development_or_with_the_flag(string environment, bool flag, bool mapped)
    {
        new SampleOptions(Anonymous: false, ReadOnly: false, Path: null, AllowDataGeneration: flag)
            .DataGenerationEnabled(new NamedEnvironment(environment))
            .Should().Be(mapped);
    }

    /// <summary>
    /// F3: on a configured database the two switches that reach beyond the demo's own rows need the flag said outright -
    /// Development alone maps the endpoints but does not make somebody's database the demo's to break.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void The_pinned_gap_and_the_pool_hog_run_only_on_the_throwaway_database_or_with_the_flag(bool throwaway, bool flag, bool refused)
    {
        UnhealthySwitchesDatabase database = new(throwaway, flag);
        FakeSwitch[] switches = TestApp.NewSwitches();

        foreach (FakeSwitch @switch in switches)
        {
            bool risky = @switch.Kind is UnhealthySwitchKind.PinnedGap or UnhealthySwitchKind.PoolHog;
            (database.RefusalFor(@switch) is not null).Should().Be(risky && refused, "{0}", @switch.Kind);
        }

        // And the real switches say which they are.
        new PinnedGapSwitch(null!).OnlyOnTheThrowawayDatabase.Should().BeTrue();
        new PoolHogSwitch(null!).OnlyOnTheThrowawayDatabase.Should().BeTrue();
        new IdleInTransactionSwitch(null!).OnlyOnTheThrowawayDatabase.Should().BeFalse();
        new BloatedTableSwitch(null!).OnlyOnTheThrowawayDatabase.Should().BeFalse();
    }

    [Fact]
    public async Task On_a_configured_database_the_board_refuses_the_pool_hog_and_the_panel_says_why()
    {
        await using TestApp app = await TestApp.StartAsync(new UnhealthySwitchesDatabase(IsThrowaway: false, AllowedExplicitly: false));
        (string token, string cookies) = await app.TokenAsync("admin");

        using (HttpResponseMessage on = await app.PostAsync("/sample/unhealthy/pool-hog/on", "admin", token, cookies))
        {
            on.StatusCode.Should().Be(HttpStatusCode.Redirect);
            WebUtility.UrlDecode(on.Headers.Location!.OriginalString).Should().Contain("Pool hog is refused here").And.Contain("--allow-data-generation");
        }

        app.Board.Status(UnhealthySwitchKind.PoolHog).State.Should().Be(UnhealthySwitchState.Off, "the refusal is the board's, not the button's");
        app.Board.TryTurnOn(UnhealthySwitchKind.PinnedGap, out string? error).Should().BeFalse();
        error.Should().Contain("refused here");
        app.Board.TryTurnOn(UnhealthySwitchKind.IdleInTransaction, out error).Should().BeTrue(error);

        string html = UnhealthySwitchesPanel.Render(true, true, "token", "/marten", null, app.Board);
        html.Should().Contain("was configured (ConnectionStrings:Marten); it is not the sample&#39;s throwaway container");
        Section(html, "pool-hog").Should().Contain("data-field=\"refused\">Pool hog is refused here").And.Contain("<button type=\"submit\" disabled>Turn on</button>");
        Section(html, "pinned-gap").Should().Contain("<button type=\"submit\" disabled>Turn on</button>");
        Section(html, "bloated-table").Should().NotContain("disabled").And.NotContain("data-field=\"refused\"");
    }

    [Fact]
    public async Task A_clean_up_that_fails_is_shown_as_Failed_with_its_reason_and_logged()
    {
        await using TestApp app = await TestApp.StartAsync();
        app.Switch(UnhealthySwitchKind.IdleInTransaction).FailCleanUpWith = "the rollback could not reach the server";

        app.Board.TryTurnOn(UnhealthySwitchKind.IdleInTransaction, out string? error).Should().BeTrue(error);
        await app.WaitForAsync(UnhealthySwitchKind.IdleInTransaction, UnhealthySwitchState.On);

        // Turned off through the endpoint, so the message the panel shows is the one under test too.
        (string token, string cookies) = await app.TokenAsync("admin");
        using (HttpResponseMessage off = await app.PostAsync("/sample/unhealthy/idle-in-transaction/off", "admin", token, cookies))
        {
            off.StatusCode.Should().Be(HttpStatusCode.Redirect);
            string message = WebUtility.UrlDecode(off.Headers.Location!.OriginalString);
            message.Should().Contain("Idle in transaction: Failed: Idle in transaction could not clean up after itself: the rollback could not reach the server");
            message.Should().NotContain("cleaned up.", "a clean-up that failed is not reported as done");
        }

        UnhealthySwitchStatus status = app.Board.Status(UnhealthySwitchKind.IdleInTransaction);
        status.State.Should().Be(UnhealthySwitchState.Failed, "a switch that could not undo itself is not off");
        status.LastOutcome.Should().Be("Failed: Idle in transaction could not clean up after itself: the rollback could not reach the server");

        app.Logs.WarningsOrWorse.Should().ContainSingle(static x => x.Message.Contains("Idle in transaction", StringComparison.Ordinal))
            .Which.Category.Should().Be(typeof(UnhealthySwitchBoard).FullName);
    }

    // ---- The board ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_switch_past_its_timeout_turns_itself_off_and_says_so()
    {
        await using TestApp app = await TestApp.StartAsync();

        app.Board.TryTurnOn(UnhealthySwitchKind.PinnedGap, out string? error, TimeSpan.FromMilliseconds(300)).Should().BeTrue(error);

        await app.WaitForAsync(UnhealthySwitchKind.PinnedGap, UnhealthySwitchState.Off);

        app.Board.Status(UnhealthySwitchKind.PinnedGap).LastOutcome.Should().StartWith("Turned itself off after 0.3 s");
        app.Switch(UnhealthySwitchKind.PinnedGap).CleanedUp.Should().Be(1);
    }

    [Fact]
    public async Task Stopping_the_host_turns_every_switch_off()
    {
        await using TestApp app = await TestApp.StartAsync();

        foreach (UnhealthySwitchKind kind in Enum.GetValues<UnhealthySwitchKind>())
        {
            app.Board.TryTurnOn(kind, out string? error).Should().BeTrue(error);
            await app.WaitForAsync(kind, UnhealthySwitchState.On);
        }

        await app.App.StopAsync(Token);

        foreach (UnhealthySwitchKind kind in Enum.GetValues<UnhealthySwitchKind>())
        {
            UnhealthySwitchStatus status = app.Board.Status(kind);
            status.State.Should().Be(UnhealthySwitchState.Off, "{0} is cleaned up before the host finishes stopping", kind);
            status.LastOutcome.Should().StartWith("Turned off when the host stopped");
            app.Switch(kind).CleanedUp.Should().Be(1);
        }

        app.Board.TryTurnOn(UnhealthySwitchKind.PoolHog, out string? refused).Should().BeFalse();
        refused.Should().Contain("stopping");
    }

    [Fact]
    public async Task A_switch_that_fails_is_off_and_says_why()
    {
        await using TestApp app = await TestApp.StartAsync();
        app.Switch(UnhealthySwitchKind.BloatedTable).FailWith = "the demo table could not be created";

        app.Board.TryTurnOn(UnhealthySwitchKind.BloatedTable, out string? error).Should().BeTrue(error);
        await app.WaitForAsync(UnhealthySwitchKind.BloatedTable, UnhealthySwitchState.Failed);

        app.Board.Status(UnhealthySwitchKind.BloatedTable).LastOutcome.Should().Be("Failed: the demo table could not be created");
        app.Board.TryTurnOn(UnhealthySwitchKind.BloatedTable, out _).Should().BeTrue("a failed switch can be turned on again");
    }

    [Theory]
    [InlineData(100, 1000, 90)]
    [InlineData(20, 1000, 18)]
    [InlineData(10, 1000, 8)]
    [InlineData(100, 40, 36)]
    [InlineData(3, 1000, 1)]
    [InlineData(100, 0, 1)]
    public void The_pool_hog_takes_a_bounded_share_and_leaves_the_server_room(int maxPoolSize, int serverRoom, int expected)
    {
        PoolHogSwitch.Target(maxPoolSize, serverRoom).Should().Be(expected);
    }

    // ---- The panel ----------------------------------------------------------------------------------------

    [Fact]
    public void The_panel_for_the_admin_shows_every_switch_its_codes_and_a_button_that_fits()
    {
        using UnhealthySwitchBoard board = TestApp.NewBoard(out _);

        string html = UnhealthySwitchesPanel.Render(true, true, "tok<en>", "/ops/marten", "Pool hog is <on>.", board);

        html.Should().Contain("<h2 id=\"unhealthy\">Make it unhealthy</h2>");
        html.Should().Contain("Pool hog is &lt;on&gt;.", "the message is the query string's, so it is encoded");
        html.Should().Contain("value=\"tok&lt;en&gt;\"").And.NotContain("tok<en>");
        html.Should().Contain("href=\"/ops/marten/health\"");
        html.Should().Contain("after 10 min");

        foreach ((string slug, string[] codes) in (IEnumerable<(string, string[])>)
            [("pinned-gap", ["D01", "C04"]), ("idle-in-transaction", ["C04"]), ("bloated-table", ["T07"]), ("pool-hog", ["C01"])])
        {
            string section = Section(html, slug);

            section.Should().Contain("data-state=\"Off\"");
            foreach (string code in codes)
            {
                section.Should().Contain("<code>" + code + "</code>");
            }

            section.Should().Contain("action=\"/sample/unhealthy/" + slug + "/on\" data-when=\"off\">", "an off switch offers Turn on");
            section.Should().Contain("action=\"/sample/unhealthy/" + slug + "/off\" data-when=\"on\" hidden>");
            section.Should().Contain("data-field=\"until\" hidden>", "an off switch has nothing to turn itself off at");
        }
    }

    [Fact]
    public async Task While_a_switch_is_on_the_panel_says_what_it_is_doing_and_when_it_turns_itself_off()
    {
        await using TestApp app = await TestApp.StartAsync();
        app.Board.TryTurnOn(UnhealthySwitchKind.IdleInTransaction, out string? error).Should().BeTrue(error);
        await app.WaitForAsync(UnhealthySwitchKind.IdleInTransaction, UnhealthySwitchState.On);

        string section = Section(UnhealthySwitchesPanel.Render(true, true, "token", "/marten", null, app.Board), "idle-in-transaction");

        DateTimeOffset at = app.Board.Status(UnhealthySwitchKind.IdleInTransaction).TurnsOffAt!.Value;

        section.Should().Contain("data-state=\"On\"");
        section.Should().Contain(FakeSwitch.Holding);
        section.Should().Contain("Turns itself off at " + at.UtcDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC.");
        section.Should().Contain("data-field=\"until\">");
        section.Should().Contain("action=\"/sample/unhealthy/idle-in-transaction/on\" data-when=\"off\" hidden>");
        section.Should().Contain("action=\"/sample/unhealthy/idle-in-transaction/off\" data-when=\"on\">", "an on switch offers Turn off");
    }

    [Theory]
    [InlineData(false, true, "--allow-data-generation")]
    [InlineData(true, false, "MartenStudioAdmin")]
    public void Anybody_else_is_told_why_there_are_no_switches(bool enabled, bool isAdmin, string why)
    {
        using UnhealthySwitchBoard board = TestApp.NewBoard(out _);

        string html = UnhealthySwitchesPanel.Render(enabled, isAdmin, "token", "/marten", "ignored", board);

        html.Should().Contain("Make it unhealthy").And.Contain(why);
        html.Should().NotContain("<form").And.NotContain("ignored").And.NotContain("token");
    }

    /// <summary>One switch's section of the panel, so an assertion cannot pass on another switch's markup.</summary>
    private static string Section(string html, string slug)
    {
        int start = html.IndexOf("<section class=\"switch\" id=\"unhealthy-" + slug + "\"", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the panel has a section for {0}", slug);

        int end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    // ---- The test host ------------------------------------------------------------------------------------

    /// <summary>A switch that holds nothing: it reports, waits to be cancelled, and counts its clean-ups.</summary>
    internal sealed class FakeSwitch(UnhealthySwitchKind kind, string slug, string title, string[] codes) : UnhealthySwitch
    {
        public const string Holding = "Holding nothing at all.";

        private int cleanedUp;

        public override UnhealthySwitchKind Kind => kind;

        public override string Slug => slug;

        public override string Title => title;

        public override string Description => title + " does nothing here.";

        public override IReadOnlyList<string> Codes => codes;

        public override string WhereToLook => "Nowhere.";

        public int CleanedUp => Volatile.Read(ref cleanedUp);

        public string? FailWith { get; set; }

        public string? FailCleanUpWith { get; set; }

        /// <summary>As the real switches of the same kind say.</summary>
        public override bool OnlyOnTheThrowawayDatabase => kind is UnhealthySwitchKind.PinnedGap or UnhealthySwitchKind.PoolHog;

        public override async Task RunAsync(Action<string> report, CancellationToken cancellationToken)
        {
            try
            {
                if (FailWith is { } failure)
                {
                    FailWith = null;
                    throw new InvalidOperationException(failure);
                }

                report(Holding);
                await UntilCancelledAsync(cancellationToken);
            }
            finally
            {
                // Through the base class's clean-up wrapper, as the real switches' finally blocks are.
                await CleanUpAsync(_ => CleanUp());
            }
        }

        private Task CleanUp()
        {
            Interlocked.Increment(ref cleanedUp);
            return FailCleanUpWith is { } failure ? Task.FromException(new InvalidOperationException(failure)) : Task.CompletedTask;
        }
    }

    private sealed class TestApp : IAsyncDisposable
    {
        /// <summary>The throwaway container: every switch may run.</summary>
        public static readonly UnhealthySwitchesDatabase Throwaway = new(IsThrowaway: true, AllowedExplicitly: false);

        private TestApp(WebApplication app, IReadOnlyDictionary<UnhealthySwitchKind, FakeSwitch> switches, LogCapture logs)
        {
            App = app;
            Switches = switches;
            Logs = logs;
            Client = app.GetTestClient();
        }

        public WebApplication App { get; }

        public LogCapture Logs { get; }

        public HttpClient Client { get; }

        public IReadOnlyDictionary<UnhealthySwitchKind, FakeSwitch> Switches { get; }

        public UnhealthySwitchBoard Board => App.Services.GetRequiredService<UnhealthySwitchBoard>();

        public FakeSwitch Switch(UnhealthySwitchKind kind) => Switches[kind];

        public static FakeSwitch[] NewSwitches() =>
        [
            new(UnhealthySwitchKind.PinnedGap, "pinned-gap", "Pinned gap", ["D01", "C04"]),
            new(UnhealthySwitchKind.IdleInTransaction, "idle-in-transaction", "Idle in transaction", ["C04"]),
            new(UnhealthySwitchKind.BloatedTable, "bloated-table", "Bloated table", ["T07"]),
            new(UnhealthySwitchKind.PoolHog, "pool-hog", "Pool hog", ["C01"]),
        ];

        /// <summary>A board over stand-ins, outside any host, for the markup tests.</summary>
        public static UnhealthySwitchBoard NewBoard(out FakeSwitch[] switches)
        {
            switches = NewSwitches();
            return new UnhealthySwitchBoard(switches, Throwaway, new StandAloneLifetime(), NullLogger<UnhealthySwitchBoard>.Instance);
        }

        public static async Task<TestApp> StartAsync(UnhealthySwitchesDatabase? database = null)
        {
            FakeSwitch[] switches = NewSwitches();
            LogCapture logs = new();

            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.AddSingleton(database ?? Throwaway);

            builder.Services
                .AddAuthentication(DemoUserScheme.Name)
                .AddScheme<AuthenticationSchemeOptions, DemoUserScheme>(DemoUserScheme.Name, static _ => { });
            builder.Services.AddAuthorization(SamplePolicies.Configure);
            builder.Services.AddAntiforgery();

            foreach (FakeSwitch @switch in switches)
            {
                builder.Services.AddSingleton<UnhealthySwitch>(@switch);
            }

            builder.Services.AddSingleton<UnhealthySwitchBoard>();
            builder.Services.AddHostedService(static provider => provider.GetRequiredService<UnhealthySwitchBoard>());

            WebApplication app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseAntiforgery();

            // What the landing page does to put a request token in its forms, as the visitor the header names.
            app.MapGet("/token", static (HttpContext context, IAntiforgery antiforgery) =>
                Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken ?? string.Empty));

            app.MapUnhealthySwitches();

            await app.StartAsync(Token);

            return new TestApp(app, switches.ToDictionary(static x => x.Kind), logs);
        }

        public async Task<(string Token, string Cookies)> TokenAsync(string user)
        {
            using HttpResponseMessage response = await GetAsync("/token", user);
            response.EnsureSuccessStatusCode();

            IEnumerable<string> setCookies = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values) ? values : [];
            return (await response.Content.ReadAsStringAsync(Token), string.Join("; ", setCookies.Select(static x => x.Split(';')[0])));
        }

        public async Task<HttpResponseMessage> GetAsync(string path, string? user)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri(path, UriKind.Relative));
            AddUser(request, user);
            return await Client.SendAsync(request, Token);
        }

        public async Task<HttpResponseMessage> PostAsync(string path, string? user, string? token, string? cookies)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(path, UriKind.Relative))
            {
                Content = new FormUrlEncodedContent(token is null
                    ? []
                    : new Dictionary<string, string>(StringComparer.Ordinal) { [LoginEndpoints.AntiforgeryFieldName] = token }),
            };

            AddUser(request, user);

            if (cookies is { Length: > 0 })
            {
                request.Headers.Add("Cookie", cookies);
            }

            return await Client.SendAsync(request, Token);
        }

        /// <summary>Waits on the board's own state, never on a fixed delay.</summary>
        public async Task WaitForAsync(UnhealthySwitchKind kind, UnhealthySwitchState state)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(20));

            while (Board.Status(kind).State != state)
            {
                await timer.WaitForNextTickAsync(deadline.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }

        private static void AddUser(HttpRequestMessage request, string? user)
        {
            if (user is not null)
            {
                request.Headers.Add(UserHeader, user);
            }
        }
    }

    /// <summary>Signs in the demo user a header names, with the demo's own claims.</summary>
    private sealed class DemoUserScheme(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "DemoUser";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? user = Request.Headers[UserHeader];

            return Task.FromResult(user is not null && DevUsers.Authenticate(user, user) is { } principal
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(principal.Claims, Name)), Name))
                : AuthenticateResult.NoResult());
        }
    }

    /// <summary>An environment that is only its name.</summary>
    private sealed class NamedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "MartenStudio.Sample";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>A lifetime nothing stops, for a board outside any host.</summary>
    internal sealed class StandAloneLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => stopping.Cancel();

        public void Dispose() => stopping.Dispose();
    }
}
