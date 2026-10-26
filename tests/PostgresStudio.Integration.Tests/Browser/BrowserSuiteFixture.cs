using Microsoft.Playwright;

using Npgsql;

using PostgresStudio.Integration.Tests.Database;
using PostgresStudio.SampleData;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// The xunit collection every browser scenario belongs to: one Chromium and one sample host, scenarios one after another.
/// </summary>
/// <remarks>
/// Kept out of the parallel run, as the Marten suite's is: a Chromium, a .NET process and a Postgres are the most
/// expensive things a test here starts, and a sign-in must never wait behind the rest of the suite. It also keeps the
/// sample's tenant databases - <c>tenant_a</c>, <c>tenant_b</c>, <c>tenant_c</c>, fixed names - away from
/// <see cref="TenantDemoLiveTests" />, which drops them: xunit runs non-parallel collections one after the other.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BrowserSuite : ICollectionFixture<BrowserSuiteFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "browser";
}

/// <summary>
/// Chromium, and <c>samples/PostgresStudio.Sample</c> on real Kestrel over a database of this suite's container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pointed at the suite's container</b> rather than started zero-config (<see cref="PostgresStudioSampleHostTests" />
/// proves that start): so the scenarios can read what the sample's databases hold. The admin database is
/// <see cref="AdminDatabase" />, and the sample - started with <c>--allow-data-generation</c>, without which it writes nothing
/// to a database it did not start - creates its three tenant databases beside it, as it would on any server.
/// </para>
/// <para>
/// <b>A snapshot before anybody looks.</b> The sample seeds before it listens, so once it answers, its databases hold
/// everything they will hold; <see cref="Before" /> is read then, before the warm-up navigates anywhere, and the
/// first-navigation scenario compares the databases against it after visiting every core page.
/// </para>
/// <para>
/// <b>Nothing is started when the gates are shut</b> - no Docker, or no Chromium locally - and on CI either one missing is
/// a failure raised here (D19).
/// </para>
/// </remarks>
public sealed class BrowserSuiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The sample's admin database on the suite's container.</summary>
    public const string AdminDatabase = "ps_browser_admin";

    private IPlaywright? playwright;
    private IBrowser? browser;
    private SampleHost? host;
    private ServerSnapshot? before;
    private IReadOnlyList<string> databases = [];

    /// <summary>The shared browser.</summary>
    public IBrowser Browser => browser
        ?? throw new InvalidOperationException(
            "The browser was not started. A test that needs it must be a [BrowserFact] so that it skips instead of failing "
            + "when Docker or Chromium is absent.");

    /// <summary>The sample host.</summary>
    internal SampleHost Host => host ?? throw new InvalidOperationException("The sample host was not started.");

    /// <summary>The admin database and the three tenant databases, by connection string.</summary>
    internal IReadOnlyList<string> Databases => databases;

    /// <summary>What the sample's databases held once it was seeded, before any page was opened.</summary>
    internal ServerSnapshot Before => before ?? throw new InvalidOperationException("The sample's databases were not read.");

    /// <summary>The prefix the snapshot lists the server's databases by: the tenants'.</summary>
    internal const string SnapshotPrefix = "tenant_";

    /// <summary>Starts Chromium and the sample, unless a gate is shut.</summary>
    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();
        BrowserAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!BrowserAvailability.CanRun)
        {
            return;
        }

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

        // A reused container may hold tenant databases an earlier run's sample or TenantDemoLiveTests made.
        await TenantDatabases.DropAsync(postgres, CancellationToken.None);
        string admin = await postgres.CreateDatabaseAsync(AdminDatabase);

        // A database of the suite's, not the sample's own container: the sample writes its demo data only when told to.
        host = await SampleHost.StartAsync(admin, allowDataGeneration: true);

        databases =
        [
            admin,
            .. TenantDemo.TenantNames.Select(tenant => new NpgsqlConnectionStringBuilder(admin) { Database = tenant }.ConnectionString),
        ];

        before = await ServerSnapshot.ReadAsync(databases, SnapshotPrefix, CancellationToken.None);

        // The first sign-in and studio page of a fresh host pay for JIT, component discovery and the first circuit:
        // paid here, outside any scenario's timeout.
        await using StudioPage studio = await StudioPage.SignInAsync(browser, host, "admin", StudioPage.WarmUpTimeout);
        await studio.Page.GotoAsync(
            host.StudioUrl(),
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = (float) StudioPage.WarmUpTimeout.TotalMilliseconds });
    }

    /// <summary>Stops the host by pid, then the browser, then drops the databases the sample made.</summary>
    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
            await TenantDatabases.DropAsync(postgres, CancellationToken.None);
            await postgres.DropDatabaseAsync(AdminDatabase);
        }

        if (browser is not null)
        {
            await browser.CloseAsync();
        }

        playwright?.Dispose();
    }
}

/// <summary>
/// The runner every scenario shares: a browser context of its own, signed in, and on failure a screenshot, the page's
/// console and network, and the sample host's log - as <c>MartenStudio.Integration.Tests</c>' <c>BrowserScenario</c>.
/// </summary>
internal static class BrowserScenario
{
    /// <summary>Runs one scenario, leaving evidence behind when it fails.</summary>
    public static async Task RunAsync(IBrowser browser, string name, SampleHost host, string user, Func<StudioPage, Task> body)
    {
        await using StudioPage studio = await StudioPage.SignInAsync(browser, host, user);

        try
        {
            await body(studio);
        }
        catch
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "browser-screenshots");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name + ".png");

            try
            {
                await studio.ScreenshotAsync(path);
                Write("Screenshot: " + path);
            }
            catch (PlaywrightException exception)
            {
                Write("No screenshot could be taken: " + exception.Message);
            }

            Write("URL:              " + studio.Page.Url);
            Write("Page errors:      " + Join(studio.PageErrors));
            Write("Console problems: " + Join(studio.ConsoleProblems));
            Write("Failed responses: " + Join(studio.FailedResponses));
            Write("Sample host (pid " + host.ProcessId + ") log, last 60 lines:");
            Write(host.LogTail());

            throw;
        }
    }

    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static string Join(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "(none)" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", lines);
}
