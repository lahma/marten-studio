using System.Text.RegularExpressions;

using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// The six things only a browser can prove: the circuit connects, the page becomes interactive, a click
/// reaches the server, a refusal is visible, a sub-path mount serves its own script and stylesheet, and
/// the relationships diagram and its table tell the same story.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists.</b> Everything else in the repository proves the studio in bUnit or against
/// a live Postgres through its own services. Neither runs a browser, so neither can fail when
/// <c>blazor.web.js</c> 404s under a mount path, when the circuit never negotiates, or when a
/// server-rendered page looks perfect and does nothing when it is clicked — which are the failures a
/// Blazor Server RCL mounted inside somebody else's application actually has.
/// </para>
/// <para>
/// <b>Every scenario asserts its own console and network.</b> See <see cref="StudioPage" />: a page that
/// has lost its circuit still renders, so "it looked right" is not an assertion. Zero
/// <c>console.error</c>, zero <c>console.warn</c>, zero uncaught page errors, and nothing answering 400
/// or more — with the one documented exception allowed by its exact URL rather than by its class.
/// </para>
/// <para>
/// <b>Screenshots on failure only.</b> A passing run writes nothing; a failing one leaves
/// <c>browser-screenshots/{scenario}.png</c> in the test output directory and puts the sample host's own
/// log into the test output, because a Playwright timeout on its own says almost nothing.
/// </para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class StudioScenarioTests(BrowserSuiteFixture fixture)
{
    /// <summary>A seeded customer's e-mail, which exactly one document carries.</summary>
    private const string KnownCustomerEmail = "customer07@example.com";

    private static readonly Regex Digits = new(@"\d", RegexOptions.None, TimeSpan.FromSeconds(5));

    private static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static string Join(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "(none)" : Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", lines);

    // ------------------------------------------------------------------------------------------------
    // 1. The circuit connects and the landing page is live
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Signing in as <c>admin</c> and opening <c>/marten</c> gives six tiles with numbers in them, over a
    /// circuit that is actually attached — proved by moving a control and watching the server re-render.
    /// </summary>
    [BrowserFact]
    public async Task The_overview_connects_its_circuit_and_draws_numbers()
    {
        await RunAsync("overview", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl());

            ILocator tiles = studio.Page.Locator(".ms-overview-tiles .ms-stat-card");

            // The sixth, not the first: the tiles arrive as their regions do, so counting straight after
            // the first one exists is counting a page that is still filling in.
            await tiles.Nth(5).WaitForAsync();

            (await tiles.CountAsync()).Should().Be(6, "the Overview draws six tiles");

            // The Overview's regions load independently of the first render - skeletons rather than
            // spinners, deliberately - so a tile can honestly read "unknown" for a moment before its own
            // read lands, and a slower machine catches it there. Waiting for the digits is what separates
            // "this tile never fills", which is the failure worth having, from "this runner painted before
            // the query came back", which is not. The assertion below still runs either way, so a tile
            // that never fills fails with the text it was showing rather than with a timeout.
            try
            {
                await studio.Page.WaitForFunctionAsync(
                    "() => { const v = document.querySelector('.ms-overview-tiles .ms-stat-card-value');"
                    + " return v !== null && /\\d/.test(v.textContent ?? ''); }",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 15_000 });
            }
            catch (TimeoutException)
            {
                // Fall through: the assertion says what it read, which is the useful message.
            }

            string values = await studio.Page.Locator(".ms-overview-tiles .ms-stat-card-value").First.InnerTextAsync();
            Digits.IsMatch(values).Should().BeTrue(
                "the first tile must carry a number rather than a spinner or 'unknown'; it read: " + values);

            // The interactivity proof. The prerender draws the tiles too; only a live circuit turns a
            // change of the theme picker into a new attribute on the shell.
            await studio.SetThemeAsync("dark");
            await studio.SetThemeAsync("light");

            studio.WebSockets.Should().Contain(
                x => x.Contains("/marten/_blazor", StringComparison.Ordinal),
                "the studio takes its own /_blazor under its mount path");

            studio.AssertClean("opening the Overview as admin and moving the theme picker");
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 2. A click reaches the server
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Picking a collection in the rail renders rows, and expanding a JSON node inside a row's preview
    /// is a server-side <c>@onclick</c> round trip — the one thing prerendering cannot fake.
    /// </summary>
    [BrowserFact]
    public async Task The_documents_list_renders_rows_and_expands_a_json_node_over_the_circuit()
    {
        await RunAsync("documents", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl("documents"));

            await studio.Page.Locator(".ms-rail-list a[href*='documents/customer']").First.ClickAsync();

            ILocator rows = studio.Page.Locator(".ms-doc-table tbody tr.ms-doc-row");
            await rows.First.WaitForAsync();
            (await rows.CountAsync()).Should().BeGreaterThan(0, "the seeder writes 25 customers");

            // The expander is a button whose @onclick runs on the server and whose answer is a new row in
            // the table. Nothing in the prerendered HTML contains it.
            await studio.Page.Locator(".ms-doc-expander").First.ClickAsync();

            ILocator preview = studio.Page.Locator(".ms-doc-preview-row");
            await preview.First.WaitForAsync();

            ILocator jsonRows = studio.Page.Locator(".ms-doc-preview-row .ms-json-row");
            await jsonRows.First.WaitForAsync();

            int before = await jsonRows.CountAsync();
            before.Should().BeGreaterThan(1, "the preview auto-expands the root, so its properties are rows");

            ILocator collapsed = studio.Page
                .Locator(".ms-doc-preview-row .ms-json-row[aria-expanded='false']")
                .First;

            await collapsed.WaitForAsync();
            await collapsed.Locator(".ms-json-toggle").ClickAsync();

            await studio.Page
                .Locator(".ms-doc-preview-row .ms-json-row[aria-expanded='true']")
                .Nth(1)
                .WaitForAsync();

            int after = await jsonRows.CountAsync();
            after.Should().BeGreaterThan(
                before,
                "expanding a JSON node adds its children, and the server is what decides which rows exist");

            studio.AssertClean("browsing the customer collection and expanding a JSON node");
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 3. The query screen, Mode A
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Typing a Marten where clause and running it comes back as a grid, and the clause is the one that
    /// ran: the filter matches exactly one seeded customer, so a clause that never reached the server
    /// would come back with twenty-five rows rather than one.
    /// </summary>
    [BrowserFact]
    public async Task The_query_screen_runs_a_marten_where_clause_and_renders_the_result()
    {
        await RunAsync("query", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl("query?type=customer"));

            await studio.Page.Locator(".ms-query-editor-input").WaitForAsync();
            await studio.Page.FillAsync("#ms-query-where", $"where data ->> 'Email' = '{KnownCustomerEmail}'");

            // The editor commits on change, which is a blur - so the value has to leave the textarea
            // before the run, not as a side effect of the click that runs it.
            await studio.Page.Locator("#ms-query-where").BlurAsync();

            await studio.Page.Locator(".ms-query-run").ClickAsync();

            ILocator rows = studio.Page.Locator(".ms-query-marten-rows tbody tr.ms-table-row");
            await rows.First.WaitForAsync();

            (await rows.CountAsync()).Should().Be(
                1,
                "the clause matches exactly one seeded customer; more rows would mean it never reached the server");

            studio.AssertClean("running a Marten where clause from the Query screen");
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 4. What a viewer sees
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A <c>viewer</c> — who holds <c>studio:read</c> and not <c>studio:write</c> — gets the write
    /// controls rendered, disabled, and a sentence saying why; nothing they can press deletes anything.
    /// </summary>
    /// <remarks>
    /// Disabled rather than hidden is the deliberate choice (D4 and the note on
    /// <c>DocumentWriteActions</c>): "this studio can edit documents and your account may not" is a
    /// different fact from "this studio cannot edit documents", and somebody who cannot tell them apart
    /// goes and asks the wrong person. What the packet asks for — "no Delete is offered" — is asserted as
    /// the thing that matters: there is no <em>enabled</em> delete control anywhere on the page.
    /// </remarks>
    [BrowserFact]
    public async Task A_viewer_sees_the_write_controls_refused_with_a_reason()
    {
        await RunAsync("viewer", fixture.Root, "viewer", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl("documents/customer"));

            ILocator ids = studio.Page.Locator(".ms-doc-table tbody tr.ms-doc-row a.ms-doc-id");
            await ids.First.WaitForAsync();
            await ids.First.ClickAsync();

            await studio.Page.Locator(".ms-doc-write-actions").WaitForAsync();

            ILocator edit = studio.Page.Locator(".ms-doc-edit-btn");
            await edit.WaitForAsync();
            (await edit.IsDisabledAsync()).Should().BeTrue(
                "a visitor the write policy refuses gets the control disabled, not live");

            ILocator refusal = studio.Page.Locator(".ms-write-refusal");
            await refusal.WaitForAsync();
            (await refusal.InnerTextAsync()).Should().NotBeNullOrWhiteSpace(
                "the refusal is said out loud, not only in a tooltip a touch screen never shows");

            ILocator delete = studio.Page.Locator(".ms-doc-delete-btn, .ms-doc-undelete-btn");
            (await delete.CountAsync()).Should().BeGreaterThan(0, "the control is rendered so the refusal can be read");
            foreach (ILocator button in await delete.AllAsync())
            {
                (await button.IsDisabledAsync()).Should().BeTrue("no delete a viewer can press may exist");
            }

            (await studio.Page.Locator(".ms-doc-write-actions button:not([disabled])").CountAsync())
                .Should().Be(0, "every write control on this page is refused for this visitor");

            studio.AssertClean("opening a document as a viewer");
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 5. The sub-path mount
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The same first scenario against a studio mounted at <c>/ops/marten</c>, plus the four things a
    /// sub-path mount gets wrong: the framework script, the stylesheet, the browser helpers and the
    /// circuit's own negotiate all have to answer under the mount path.
    /// </summary>
    /// <remarks>
    /// This is the failure the suite exists for (D11 and D22). A studio that is served from a mount path
    /// and asks for <c>/_framework/blazor.web.js</c> at the application root renders perfectly, never
    /// becomes interactive, and says nothing in any log the host keeps.
    /// </remarks>
    [BrowserFact]
    public async Task A_sub_path_mount_serves_its_own_script_stylesheet_and_circuit()
    {
        SampleHost host = fixture.SubPathHost;

        await RunAsync("sub-path", host, "admin", async studio =>
        {
            await studio.GoAsync(host.StudioUrl());

            ILocator tiles = studio.Page.Locator(".ms-overview-tiles .ms-stat-card");

            // The sixth, for the reason the Overview scenario gives: the tiles arrive as their regions do.
            await tiles.Nth(5).WaitForAsync();
            (await tiles.CountAsync()).Should().Be(6);

            await studio.SetThemeAsync("dark");

            studio.WebSockets.Should().Contain(
                x => x.Contains(BrowserSuiteFixture.SubPath + "/_blazor", StringComparison.Ordinal),
                "the circuit's socket is mirrored under the mount path, not left at the application root");

            foreach (string path in new[]
                     {
                         BrowserSuiteFixture.SubPath + "/_framework/blazor.web.js",
                         BrowserSuiteFixture.SubPath + "/_content/MartenStudio/css/marten-studio.css",
                         BrowserSuiteFixture.SubPath + "/_content/MartenStudio/js/marten-studio.js",
                     })
            {
                IAPIResponse response = await studio.Page.APIRequest.GetAsync(host.Url(path));
                response.Status.Should().Be(200, path + " has to answer under the mount path");
            }

            IAPIResponse negotiate = await studio.Page.APIRequest.PostAsync(
                host.Url(BrowserSuiteFixture.SubPath + "/_blazor/negotiate?negotiateVersion=1"));
            negotiate.Status.Should().Be(200, "SignalR negotiates under the mount path");

            // The one documented exception, allowed by its exact URL rather than by its class: with the
            // studio at /ops/marten there is nothing at /marten, and a 404 is the correct answer.
            string unmounted = host.Url("/marten");
            studio.ExpectStatus(unmounted, 404);

            IResponse? missing = await studio.Page.GotoAsync(unmounted);
            missing.Should().NotBeNull();
            missing!.Status.Should().Be(404, "the default path is not mounted on this host");

            studio.AssertClean("driving the studio mounted at " + BrowserSuiteFixture.SubPath);
        });
    }

    // ------------------------------------------------------------------------------------------------
    // 6. The relationships screen
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The diagram draws the store's foreign keys, and the table beside it lists exactly the same edges —
    /// which is what makes the screen usable by somebody who cannot see the picture.
    /// </summary>
    [BrowserFact]
    public async Task The_relationships_diagram_and_its_table_list_the_same_edges()
    {
        await RunAsync("relationships", fixture.Root, "admin", async studio =>
        {
            await studio.GoAsync(fixture.Root.StudioUrl("relationships"));

            await studio.Page.Locator("svg.ms-graph-svg").WaitForAsync();

            // Scoped to the diagram itself: the legend beside it draws three sample arrows that carry the
            // same classes, and counting those would compare five edges against two rows.
            ILocator nodes = studio.Page.Locator("svg.ms-graph-svg .ms-graph-node");
            ILocator edges = studio.Page.Locator("svg.ms-graph-svg .ms-graph-edge");
            ILocator rows = studio.Page.Locator(".ms-graph-table tbody tr.ms-graph-row");

            (await nodes.CountAsync()).Should().BeGreaterThan(
                1, "the demo declares foreign keys between order, invoice and customer");
            (await edges.CountAsync()).Should().BeGreaterThan(0);

            (await rows.CountAsync()).Should().Be(
                await edges.CountAsync(),
                "a table that listed fewer rows than the picture has arrows is a screen telling two stories");

            List<string> titles = [];
            foreach (ILocator edge in await edges.AllAsync())
            {
                // TextContentAsync, not InnerTextAsync: an SVG <title> is not an HTMLElement and has no
                // rendered text, so innerText is undefined for it.
                titles.Add((await edge.Locator("title").TextContentAsync() ?? string.Empty).Trim());
            }

            foreach (ILocator row in await rows.AllAsync())
            {
                // The alias out of the chip, not the whole cell: the "Points at" cell also carries the
                // arrow glyph the table draws for sighted readers.
                string from = (await row.Locator("td").Nth(0).Locator(".ms-collection-alias").InnerTextAsync()).Trim();
                string to = (await row.Locator("td").Nth(1).Locator(".ms-collection-alias").InnerTextAsync()).Trim();
                string column = (await row.Locator("td").Nth(2).InnerTextAsync()).Trim();

                titles.Should().Contain(
                    x => x.StartsWith(from + "." + column + " → " + to, StringComparison.Ordinal),
                    "every row of the accessible table is an arrow on the diagram; looked for "
                    + from + "." + column + " -> " + to + " among: " + string.Join(" | ", titles));
            }

            studio.AssertClean("reading the relationships screen");
        });
    }

    // ------------------------------------------------------------------------------------------------
    // The runner
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one scenario in a browser context of its own, and on failure leaves a screenshot and the
    /// host's log behind.
    /// </summary>
    /// <param name="name">The scenario's name, which is the screenshot's file name.</param>
    /// <param name="host">The sample host to drive.</param>
    /// <param name="user">Which demo user to sign in as.</param>
    /// <param name="body">The scenario.</param>
    private async Task RunAsync(string name, SampleHost host, string user, Func<StudioPage, Task> body)
    {
        await using StudioPage studio = await StudioPage.SignInAsync(fixture.Browser, host, user);

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

            Write("Page errors:      " + Join(studio.PageErrors));
            Write("Console problems: " + Join(studio.ConsoleProblems));
            Write("Failed responses: " + Join(studio.FailedResponses));
            Write("WebSockets:       " + Join(studio.WebSockets));
            Write("Sample host (pid " + host.ProcessId + ") log, last 60 lines:");
            Write(host.LogTail());

            throw;
        }
    }
}
