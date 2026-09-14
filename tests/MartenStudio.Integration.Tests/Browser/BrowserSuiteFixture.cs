using MartenStudio.Internal.Sql;

using Microsoft.Playwright;

using Npgsql;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// The xunit collection every browser scenario belongs to.
/// </summary>
/// <remarks>
/// One collection, so the scenarios run one after another against one browser and two sample hosts. They
/// are the most expensive tests in the repository — a Chromium, two .NET processes and a Postgres — and
/// running two of them at once would measure the machine rather than the studio.
/// </remarks>
[CollectionDefinition(BrowserSuite.Name)]
public sealed class BrowserSuite : ICollectionFixture<BrowserSuiteFixture>
{
    /// <summary>The collection name.</summary>
    public const string Name = "browser";
}

/// <summary>
/// One Chromium and two sample hosts, shared by every scenario in the collection.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two hosts, two databases.</b> One is mounted at the default <c>/marten</c> and one at
/// <c>/ops/marten</c>, because sub-path mounting is the failure this suite exists for (D11) and it cannot
/// be reconfigured on a running host. They get a database each rather than a schema each: the sample's
/// schema names are constants in <c>SampleStore</c>, so two hosts on one database would race to create
/// the same tables and then seed them twice.
/// </para>
/// <para>
/// <b>Nothing is started when the gates are shut.</b> Without Docker there is no Postgres to point a host
/// at, and without Chromium there is nothing to drive; in both cases every scenario skips, so the fixture
/// does no work at all. On CI either one missing is a failure rather than a skip, and both are raised
/// here — before a scenario can quietly not happen.
/// </para>
/// </remarks>
public sealed class BrowserSuiteFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The database the default-mount host owns.</summary>
    public const string RootDatabase = "browser_root";

    /// <summary>The database the sub-path host owns.</summary>
    public const string SubPathDatabase = "browser_subpath";

    /// <summary>Where the sub-path host mounts the studio.</summary>
    public const string SubPath = "/ops/marten";

    private IPlaywright? playwright;
    private IBrowser? browser;
    private SampleHost? root;
    private SampleHost? subPath;

    /// <summary>The shared browser.</summary>
    public IBrowser Browser => browser
        ?? throw new InvalidOperationException(
            "The browser was not started. A test that needs it must be a [BrowserFact] so that it skips "
            + "instead of failing when Docker or Chromium is absent.");

    /// <summary>The host with the studio at its default <c>/marten</c>.</summary>
    internal SampleHost Root => root
        ?? throw new InvalidOperationException("The default-mount sample host was not started.");

    /// <summary>The host with the studio at <see cref="SubPath" />.</summary>
    internal SampleHost SubPathHost => subPath
        ?? throw new InvalidOperationException("The sub-path sample host was not started.");

    /// <summary>Starts Chromium and both hosts, unless a gate is shut.</summary>
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

        string rootConnection = await CreateDatabaseAsync(postgres, RootDatabase);
        string subPathConnection = await CreateDatabaseAsync(postgres, SubPathDatabase);

        root = await SampleHost.StartAsync(rootConnection);
        subPath = await SampleHost.StartAsync(subPathConnection, SubPath);

        await SampleHost.WaitForSeedAsync(rootConnection);
        await SampleHost.WaitForSeedAsync(subPathConnection);
    }

    /// <summary>Stops both hosts by pid, then the browser.</summary>
    public async ValueTask DisposeAsync()
    {
        if (root is not null)
        {
            await root.DisposeAsync();
        }

        if (subPath is not null)
        {
            await subPath.DisposeAsync();
        }

        if (browser is not null)
        {
            await browser.CloseAsync();
        }

        playwright?.Dispose();
    }

    /// <summary>
    /// A database of this suite's own on the assembly's container, dropped first so that a reused
    /// container carrying an earlier run's data still gives this one an empty store.
    /// </summary>
    /// <param name="postgres">The assembly's container.</param>
    /// <param name="name">The database name; a literal constant of this class, never anything typed.</param>
    internal static async Task<string> CreateDatabaseAsync(PostgresFixture postgres, string name)
    {
        ArgumentNullException.ThrowIfNull(postgres);

        // Quoted through the studio's own SqlIdentifier, which is how every identifier reaches SQL
        // anywhere in this repository - the same rule PostgresFixture.CreateSchemaAsync follows.
        string quoted = SqlIdentifier.Quote(name);

        await using (NpgsqlConnection connection = await postgres.OpenAsync())
        {
            // `with (force)` disconnects whatever is still attached from a previous run; without it a
            // reused container with a leaked session makes the drop hang rather than fail.
            await using (var drop = new NpgsqlCommand($"drop database if exists {quoted} with (force)", connection))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await using var create = new NpgsqlCommand($"create database {quoted}", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name }.ConnectionString;
    }
}
