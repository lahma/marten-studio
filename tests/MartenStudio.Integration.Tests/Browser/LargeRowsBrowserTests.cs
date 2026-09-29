using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

using MartenStudio.Integration.Tests.Generation;
using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Relational;

using Microsoft.Playwright;

using Npgsql;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>Whether the large-rows browser pass runs: Docker, Chromium and the large-data opt-in.</summary>
/// <remarks>
/// The same gate as <see cref="LargeBrowserData" /> - <c>MARTENSTUDIO_LARGE=1</c> - with a reason of its own,
/// because this pass generates nothing through the demo panel: it writes one table of its own in a second.
/// </remarks>
public static class LargeRowsBrowserData
{
    /// <summary>Why the pass skipped.</summary>
    public const string SkipReason =
        "The large-rows browser pass is opt-in: set " + LargeDataSet.EnvironmentVariable + "=1, have Docker "
        + "running, and install Chromium (`" + BrowserAvailability.InstallCommand + "`). It starts a sample host "
        + "of its own and writes one table of " + LargeRowsBrowserFixture.RowCountText + " rows beside the demo schemas.";

    /// <summary>Whether the pass runs.</summary>
    public static bool IsEnabled => LargeBrowserData.IsEnabled;
}

/// <summary>A fact that needs a browser, Docker and the large-data opt-in, for the large-rows pass.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LargeRowsBrowserFactAttribute : FactAttribute
{
    /// <summary>Wires up xunit's conditional skip against <see cref="LargeRowsBrowserData.IsEnabled" />.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler; xunit uses it to locate the test.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler; xunit uses it to locate the test.</param>
    public LargeRowsBrowserFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = LargeRowsBrowserData.SkipReason;
        SkipType = typeof(LargeRowsBrowserData);
        SkipUnless = nameof(LargeRowsBrowserData.IsEnabled);
    }
}

/// <summary>
/// A sample host of its own, with one plain table of <see cref="RowCount" /> rows beside the demo schemas.
/// </summary>
/// <remarks>
/// <para>
/// <b>In <c>legacy</c>, not in a schema of its own.</b> The sample's <c>BrowsableSchemas</c> is
/// <c>quartz, legacy</c>, set in its <c>Program.cs</c> and not read from configuration, so a table in any
/// other schema is one the database browser rightly refuses to read. The table goes into <c>legacy</c> after
/// the demo seeder has made that schema, in a database this fixture owns, so nothing any other test reads
/// changes.
/// </para>
/// <para>
/// <b>More than a hundred thousand rows, on purpose.</b> A filter no index serves is withheld until "Run
/// anyway" when the relation holds <em>more</em> than <c>MartenStudioOptions.ExactCountThreshold</c> rows,
/// which is 100 000 by default; a table of exactly that many would never be withheld. So the table holds a
/// fifth more, and is <c>ANALYZE</c>d, because the verdict reads <c>pg_class.reltuples</c> and a table
/// Postgres has never analysed has none.
/// </para>
/// </remarks>
public sealed class LargeRowsBrowserFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The database this pass owns.</summary>
    public const string Database = "browser_large_rows";

    /// <summary>The schema the table is written into: one the sample admits to the database browser.</summary>
    public const string Schema = RelationalDemoSchema.LegacySchemaName;

    /// <summary>The table.</summary>
    public const string Table = "bulk_readings";

    /// <summary>How many rows it holds.</summary>
    public const int RowCount = 120_000;

    /// <summary><see cref="RowCount" />, as the skip reason says it.</summary>
    public const string RowCountText = "120 000";

    private SampleHost? host;

    /// <summary>The host, with the table in its database.</summary>
    internal SampleHost Host => host
        ?? throw new InvalidOperationException(
            "The large-rows host was not started. A test that needs it must be a [LargeRowsBrowserFact].");

    /// <summary>How long writing and analysing the table took, for the test output.</summary>
    public TimeSpan WriteTime { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!LargeRowsBrowserData.IsEnabled)
        {
            return;
        }

        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(postgres, Database);

        host = await SampleHost.StartAsync(connectionString);

        // After the seed, which is what creates `legacy`: the table is added to a schema the demo made,
        // before anything has opened a studio page and cached the catalog.
        await SampleHost.WaitForSeedAsync(connectionString);

        long started = Stopwatch.GetTimestamp();
        await WriteTableAsync(connectionString);
        WriteTime = Stopwatch.GetElapsedTime(started);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (host is not null)
        {
            await host.DisposeAsync();
        }
    }

    /// <summary>
    /// The table: a bigint key, which is what keyset paging walks, and a <c>sensor</c> column with no index,
    /// which is what a withheld filter is made of.
    /// </summary>
    private static async Task WriteTableAsync(string connectionString)
    {
        // Constants of this class, quoted through the studio's own SqlIdentifier like every identifier in
        // this suite (see BrowserSuiteFixture.CreateDatabaseAsync); the row count is a parameter.
        string table = SqlIdentifier.Qualify(Schema, Table);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var create = new NpgsqlCommand(
                         "create table " + table + " (\n"
                         + "    id bigint primary key,\n"
                         + "    sensor text not null,\n"
                         + "    reading numeric(10, 2) not null,\n"
                         + "    taken_at timestamptz not null\n"
                         + ")",
                         connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        await using (var fill = new NpgsqlCommand(
                         "insert into " + table + " (id, sensor, reading, taken_at)\n"
                         + "select g, 'sensor-' || (g % 500), (g % 1000) / 10.0, timestamptz '2026-01-01 00:00:00+00' + g * interval '1 minute'\n"
                         + "from pg_catalog.generate_series(1, @rows) as g",
                         connection))
        {
            fill.Parameters.AddWithValue("rows", (long) RowCount);
            fill.CommandTimeout = 300;
            await fill.ExecuteNonQueryAsync();
        }

        await using var analyze = new NpgsqlCommand("analyze " + table, connection);
        await analyze.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// The database browser's Rows tab against a table of more than a hundred thousand rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it proves.</b> The first page and the next one are keyset reads on the primary key - an index
/// scan at any size - so they have to paint inside the same ceiling a document list page does
/// (<see cref="PageResponsivenessBudgets.DocumentsPage" />). And a filter on a column no index serves must
/// come back as "Run anyway", not as a sequential scan somebody did not ask for.
/// </para>
/// <para>
/// <b>Two measurements, two shapes.</b> The first page is a navigation to the first row on screen, measured
/// exactly as <see cref="LargeDataBrowserTests" /> measures every screen. The next page is what a person does
/// next - pressing Next on a live circuit - timed from the click to the grid showing a different first row
/// with its busy state cleared. Both are medians of <see cref="ResponsivenessBudgets.Samples" />.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class LargeRowsBrowserTests(BrowserSuiteFixture browsers, LargeRowsBrowserFixture data)
    : IClassFixture<LargeRowsBrowserFixture>
{
    private const string FirstKey = ".ms-row-grid tbody tr.ms-row-item a.ms-row-open-link";

    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// The Rows tab's first and next pages paint inside the document-list budget, and an unindexed filter is
    /// withheld rather than run.
    /// </summary>
    [LargeRowsBrowserFact]
    public async Task The_rows_tab_pages_a_large_table_inside_its_budget_and_withholds_a_scan()
    {
        Write(string.Format(
            CultureInfo.InvariantCulture,
            "Wrote and analysed {0:N0} rows into {1}.{2} in {3:F1} s",
            LargeRowsBrowserFixture.RowCount,
            LargeRowsBrowserFixture.Schema,
            LargeRowsBrowserFixture.Table,
            data.WriteTime.TotalSeconds));

        SampleHost host = data.Host;

        // No scope in the URL, on purpose: the studio writes it in once the circuit has attached, which is
        // how a measurement below knows the page is live before it presses anything (StudioPage.GoAsync).
        string rowsUrl = host.StudioUrl(
            "database/object?schema=" + LargeRowsBrowserFixture.Schema + "&name=" + LargeRowsBrowserFixture.Table + "&tab=rows");

        await BrowserScenario.RunAsync(browsers.Browser, "large-rows", host, "admin", async studio =>
        {
            IPage page = studio.Page;
            var report = new BudgetReport(Write);

            // Warm the page once, so neither measurement is the first request's code generation.
            await studio.GoAsync(rowsUrl);
            await page.Locator(FirstKey).First.WaitForAsync();

            (TimeSpan first, _) = await ResponsivenessBudgets.MeasureAsync(async () =>
            {
                await page.GotoAsync(rowsUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                await page.Locator(FirstKey).First.WaitForAsync();
                return true;
            });

            report.Check("rows tab first page", first, PageResponsivenessBudgets.DocumentsPage);

            TimeSpan next = await MeasureNextPageAsync(studio, rowsUrl);
            report.Check("rows tab next page", next, PageResponsivenessBudgets.DocumentsPage);

            // A filter on a column no index serves, over more rows than ExactCountThreshold: withheld, with the
            // reason and the button, and no rows read.
            await studio.GoAsync(rowsUrl + "&q=" + Uri.EscapeDataString("sensor = 'sensor-42'"));

            ILocator withheld = page.Locator(".ms-row-filter-strip .ms-row-withheld");
            await withheld.WaitForAsync();

            (await page.Locator(".ms-row-tab").GetAttributeAsync("data-state")).Should().Be(
                "withheld", "the service answered with a verdict rather than a page of rows");
            (await withheld.InnerTextAsync()).Should().Contain(
                "ExactCountThreshold", "the reason names the option that decides what counts as large");
            (await withheld.Locator("button", new LocatorLocatorOptions { HasTextString = "Run anyway" }).CountAsync()).Should().Be(
                1, "reading it anyway is the visitor's decision, one press away");
            (await page.Locator(".ms-row-grid").CountAsync()).Should().Be(0, "nothing was scanned to draw this");

            // TextContent, not InnerText: the badge is upper-cased by the stylesheet, and InnerText is what is
            // painted.
            ILocator verdict = page.Locator(".ms-row-filter-strip .ms-verdict-badge");
            (await verdict.TextContentAsync() ?? string.Empty).Trim().Should().Be("full scan", "no index serves sensor");

            studio.AssertClean("paging and filtering a table of " + LargeRowsBrowserFixture.RowCountText + " rows");

            report.Unexpected.Should().BeEmpty(report.Because);
        });
    }

    /// <summary>
    /// The median, over <see cref="ResponsivenessBudgets.Samples" /> fresh first pages, of pressing Next until
    /// the grid shows the page after it.
    /// </summary>
    /// <remarks>
    /// Only the press is timed. Each sample opens the first page anew and waits for the studio to write the
    /// scope into the address bar - its own proof that the circuit is attached - because a Next pressed on a
    /// prerendered page does nothing, and timing that would time a timeout.
    /// </remarks>
    private static async Task<TimeSpan> MeasureNextPageAsync(StudioPage studio, string rowsUrl)
    {
        IPage page = studio.Page;
        List<TimeSpan> durations = [];

        for (int sample = 0; sample < ResponsivenessBudgets.Samples; sample++)
        {
            await studio.GoAsync(rowsUrl);

            ILocator firstKey = page.Locator(FirstKey).First;
            await firstKey.WaitForAsync();
            string before = (await firstKey.TextContentAsync() ?? string.Empty).Trim();

            ILocator next = page.Locator(".ms-pager[aria-label='Row pages'] button", new PageLocatorOptions { HasTextString = "Next" });

            long started = Stopwatch.GetTimestamp();

            await next.ClickAsync();
            await page.WaitForFunctionAsync(
                """
                before => {
                    const tab = document.querySelector('.ms-row-tab');
                    const key = document.querySelector('.ms-row-grid tbody tr.ms-row-item a.ms-row-open-link');
                    return tab !== null && tab.getAttribute('aria-busy') === 'false'
                        && key !== null && key.textContent.trim() !== before;
                }
                """,
                before);

            durations.Add(Stopwatch.GetElapsedTime(started));

            page.Url.Should().Contain("cursor=", "Next is a keyset step, and the cursor is in the address bar (D9)");
        }

        durations.Sort();

        return durations[durations.Count / 2];
    }
}
