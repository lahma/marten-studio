using Microsoft.Playwright;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// Health › Snapshot › "Compare with an earlier snapshot" in a real browser (1.2-H-B): Download JSON, then compare that very
/// file under the mount.
/// </summary>
/// <remarks>
/// <b>Why a browser.</b> bUnit proves the component reads a file; only a browser proves that Blazor's <c>InputFile</c> streams
/// it in chunks through the mounted <c>blazor.web.js</c> and the circuit's receive limit, which is why the compare takes a
/// file and not a paste.
/// </remarks>
[Collection(BrowserSuite.Name)]
public class SnapshotCompareBrowserTests(BrowserSuiteFixture fixture)
{
    private const string HealthPath = "health?store=tenants&db=tenant_c";

    [BrowserFact]
    public Task A_downloaded_snapshot_compares_with_now_through_the_mounted_input_file() =>
        BrowserScenario.RunAsync(fixture.Browser, "snapshot-compare", fixture.Host, "admin", async studio =>
        {
            IPage page = studio.Page;

            await studio.GoAsync(fixture.Host.StudioUrl(HealthPath));
            await studio.WaitForCircuitAsync();
            await page.Locator("table.ms-health-check-table tbody tr").First.WaitForAsync();

            string path = Path.Combine(Path.GetTempPath(), "ms-compare-" + Guid.NewGuid().ToString("N") + ".json");

            try
            {
                await page.Locator(".ms-snapshot-menu-button").ClickAsync();

                IDownload download = await page.RunAndWaitForDownloadAsync(async () =>
                    await page.Locator(".ms-snapshot-menu-popover .ms-menu-item", new PageLocatorOptions { HasTextString = "Download JSON" }).ClickAsync());

                download.SuggestedFilename.Should().EndWith(".json");
                await download.SaveAsAsync(path);

                await page.Locator(".ms-snapshot-menu-button").ClickAsync();
                await page.Locator(".ms-snapshot-menu-popover .ms-menu-item", new PageLocatorOptions { HasTextString = "Compare with an earlier snapshot" }).ClickAsync();

                await page.Locator("section.ms-snapshot-compare").WaitForAsync();
                await page.Locator("#ms-snapshot-compare-file").SetInputFilesAsync(path);

                ILocator verdict = page.Locator(".ms-snapshot-compare-verdict");
                await verdict.WaitForAsync();

                (await verdict.TextContentAsync()).Should().Contain("Compared with the snapshot from");
                (await page.Locator(".ms-snapshot-compare-name").TextContentAsync()).Should().Be(Path.GetFileName(path), "the file the visitor chose is named, and nothing else of it");
                (await page.Locator(".ms-snapshot-compare-problem").CountAsync()).Should().Be(0);

                studio.AssertClean("comparing a downloaded snapshot with now");
            }
            finally
            {
                File.Delete(path);
            }
        });
}
