using System.Globalization;
using System.Text.RegularExpressions;

using MartenStudio.Sample.Generation;
using MartenStudio.SampleDomain;

using Microsoft.Playwright;

using Npgsql;

using static MartenStudio.Integration.Tests.Browser.HealthBrowser;
using PostgresStudio.Internal.Sql;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// The health advisor on real Kestrel, in Chromium, made unhealthy on purpose with the sample's own demo switches:
/// a finding a switch raises, its mute and the badge, the toast for a new Critical, Database › Sessions for two
/// audiences, the Projections screen's D01 and D02 banners, and what a visitor without <c>ViewDatabaseActivity</c> is
/// not shown.
/// </summary>
/// <remarks>
/// <para>
/// <b>The switches are pressed the way a person presses them</b>: on the sample's landing page, as <c>admin</c> (the
/// only demo user the sample's <c>MartenStudioAdmin</c> policy passes), whose panel then reports the switch's state
/// from its status endpoint. Every switch a scenario turns on it turns off again in a <c>finally</c>, on a page of its
/// own and one step at a time (<see cref="HealthBrowser.CleanUpAsync" />), so a failing scenario keeps its screenshot
/// and its exception and still does not leave the next one an unhealthy database for the switch's ten-minute timeout.
/// </para>
/// <para>
/// <b>Waits are on the page.</b> A regular sweep is cached for <c>HealthInterval</c> (a minute, as the sample ships), so
/// a finding a switch causes appears on the first sweep after the cache expires: the scenarios press the page's own
/// Refresh and wait, in the page, for the finding's card - never on a delay, and never on a load state. C04 and D01 also
/// wait for their own thresholds, a minute of idleness and a minute of pinning, which run concurrently in one scenario
/// because no test-only threshold reaches a sample host in a child process.
/// </para>
/// <para>
/// <b>The toast's Critical is not a switch's.</b> The bloated table's T07 is Critical from its first sweep - the switch
/// turns autovacuum off for the table itself, which is G2's Critical (W5-V1) - so whether it toasts depends on whether the
/// circuit's first look at the database came before that sweep or after it, which is the sweep cache's timing, not the
/// scenario's. The other three do not reach Critical within a scenario: the pool hog leaves two connections free, so C01
/// never sees its pool saturated; C04 and D01 are Critical only after ten minutes; and the D02 a host without a daemon
/// raises is Critical already on the first sweep, which is exactly the case the toast stays quiet about. So the toast
/// takes the cheapest real path to a Critical that is new while the page watches: as another client of the server, the
/// scenario puts the sample's <c>Invoice</c> HiLo row past 80% of <c>int.MaxValue</c>, which T11 judges Critical on the
/// next sweep, and restores the row afterwards. The toast it waits for is T11's by its text, so a toast T07 may have
/// raised earlier cannot stand in for it.
/// </para>
/// <para>Like every browser scenario, these skip locally when Chromium is not installed and throw on CI (D19).</para>
/// </remarks>
[Collection(BrowserSuite.Name)]
public class HealthAdvisorScenarioTests(BrowserSuiteFixture fixture)
{
    private const string BloatedTable = "bloated-table";
    private const string IdleInTransaction = "idle-in-transaction";
    private const string PinnedGap = "pinned-gap";

    /// <summary>What the idle-in-transaction switch calls its session in <c>pg_stat_activity</c>.</summary>
    private const string IdleSessionName = UnhealthySwitch.ApplicationNamePrefix + "idle in transaction";

    /// <summary>
    /// Past T11's Critical line for a <c>MaxLo</c> of 1,000, Marten's default and the sample's: (2,100,000 + 1) x 1,000
    /// ids is 98% of <c>int.MaxValue</c>.
    /// </summary>
    private const long NearTheCeiling = 2_100_000;

    /// <summary>The label of an evidence line that describes one session: <c>Session 1 (longest)</c>, <c>Session 2</c>.</summary>
    private static readonly Regex SessionLine = new(@"^Session \d+( \(.+\))?$", RegexOptions.None, TimeSpan.FromSeconds(5));

    // ------------------------------------------------------------------------------------------------
    // A switch raises its finding; the mute and the badge; a new Critical toasts
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The bloated-table switch makes T07 appear after a refresh, Critical because autovacuum is off for the table itself,
    /// with its evidence and its docs anchor; muting it drops the badge by one and the mute is kept in the browser across a
    /// reload; and a Critical that appears while the page is open raises a toast.
    /// </summary>
    [BrowserFact]
    public async Task A_switch_raises_its_finding_a_mute_drops_the_badge_and_a_new_critical_raises_a_toast()
    {
        await BrowserScenario.RunAsync(fixture.Browser, "health-switch-mute-toast", fixture.Root, "admin", async studio =>
        {
            long? hiValue = null;
            bool passed = false;

            try
            {
                await TurnOnAsync(studio.Page, fixture.Root, BloatedTable);

                await studio.GoAsync(fixture.Root.StudioUrl("health"));

                ILocator t07 = studio.Page.Locator(Visible("t07"));
                await UntilShownAsync(t07, HealthRefresh(studio.Page), NextSweep, "T07 after the bloated-table switch was turned on");

                (await t07.Locator(".ms-health-code").InnerTextAsync()).Trim().Should().Be("T07");
                (await t07.Locator(".ms-health-severity").InnerTextAsync()).Trim().Should().Be(
                    "Critical", "the switch turns autovacuum off for the table itself, and a table behind with that is Critical (G2)");
                (await t07.Locator(".ms-health-evidence-row").CountAsync()).Should().BePositive("a finding carries the facts behind it");
                (await t07.InnerTextAsync()).Should().Contain(UnhealthyDemoStore.BloatTableName, "the finding names the table the switch bloated");
                (await t07.Locator("a.ms-health-docs").GetAttributeAsync("href")).Should().Contain(
                    "/docs/health/tables.md#t07-", "the finding links to its own anchor in the area's docs");

                // The badge counts the same unmuted Warnings and Criticals the page shows - read once both agree, because
                // the sidebar reads the glance on its own poll, a few seconds behind the page.
                int before = await BadgeAgreesWithThePageAsync(studio.Page);
                before.Should().BePositive("T07 is Critical, and the badge counts Warnings and Criticals");
                int stored = await MutedInStorageAsync(studio);

                await t07.Locator(".ms-health-mute").ClickAsync();
                await studio.Page.Locator(Muted("t07")).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });

                (await BadgeAgreesWithThePageAsync(studio.Page)).Should().Be(before - 1, "a muted finding is not counted by the badge");
                (await MutedInStorageAsync(studio)).Should().Be(stored + 1, "the mute is kept in this browser's localStorage");

                // A Critical that appears while the page is open. The recorder keeps every toast the page shows, because a
                // toast is on screen for four seconds and the finding's card and the toast come from the same sweep.
                await RecordToastsAsync(studio);
                hiValue = await SetInvoiceHiloAsync(NearTheCeiling);

                ILocator t11 = studio.Page.Locator(Visible("t11"));
                await UntilShownAsync(t11, HealthRefresh(studio.Page), NextSweep, "T11 after the Invoice HiLo row was put near its ceiling");
                (await t11.Locator(".ms-health-severity").InnerTextAsync()).Trim().Should().Be("Critical");

                IJSHandle recorded = await studio.Page.WaitForFunctionAsync(
                    "() => (window.__msToasts || []).find(t => t.includes('ms-toast-error') && t.includes('Health: ') && t.includes('int ids')) || null",
                    null,
                    new PageWaitForFunctionOptions { Timeout = 30_000 });
                (await recorded.JsonValueAsync<string>()).Should().Contain("see the Health page", "the toast says where to look");

                // A new circuit: the mute is still there, read back from localStorage.
                await studio.GoAsync(fixture.Root.StudioUrl("health"));
                await studio.Page.Locator(".ms-health-muted > summary").WaitForAsync();
                (await studio.Page.Locator(Muted("t07")).CountAsync()).Should().Be(1, "T07 is still muted after a reload");
                (await studio.Page.Locator(Visible("t07")).CountAsync()).Should().Be(0);

                studio.AssertClean("raising T07 with a demo switch, muting it, raising T11 and reloading the Health page");
                passed = true;
            }
            finally
            {
                await CleanUpAsync(
                    studio,
                    passed,
                    ("restore the Invoice HiLo row", _ => hiValue is { } original ? RestoreInvoiceHiloAsync(original) : Task.CompletedTask),
                    ("turn the bloated table off", OnPage(page => TurnOffAsync(page, fixture.Root, BloatedTable))));
            }
        });
    }

    // ------------------------------------------------------------------------------------------------
    // Sessions, the D01 banner, and what a viewer is not shown
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// With the idle-in-transaction and pinned-gap switches on: Database › Sessions lists the idle session by name for
    /// <c>admin</c> and only by its state for <c>viewer</c>; C04 and D01 appear once their minute has passed; the
    /// Projections screen draws the D01 banner; and the viewer's Health page shows C04 with every session withheld.
    /// </summary>
    /// <remarks>
    /// <c>viewer</c> has no <c>studio:write</c>, so the sample's write policy refuses it <c>ViewDatabaseActivity</c>
    /// (the capability is on: the sample sets <c>All()</c>). The sample sets no store policy, so the database-wide gate
    /// passes for everybody and this is the activity gate alone.
    /// <para>
    /// <b>A host of its own, started with <c>--allow-data-generation</c>.</b> The pinned gap stalls every async projection
    /// of the store, so the sample refuses it on a configured database - which every host in this suite has - unless the
    /// host was started with that switch, which says the database is a demo's to break. A host and a database of the
    /// scenario's own, rather than the flag on the shared host, so no other scenario runs beside a switch that may stall
    /// it. The idle-in-transaction switch needs no flag.
    /// </para>
    /// </remarks>
    [BrowserFact]
    public async Task Sessions_name_an_idle_session_only_through_the_activity_gate_and_the_projections_screen_draws_D01()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(fixture.Postgres, "browser_switches");

        await using SampleHost host = await SampleHost.StartAsync(connectionString, allowDataGeneration: true);
        await SampleHost.WaitForSeedAsync(connectionString);
        await BrowserSuiteFixture.WarmUpAsync(fixture.Browser, host);

        await BrowserScenario.RunAsync(fixture.Browser, "health-sessions-admin", host, "admin", async admin =>
        {
            bool passed = false;

            try
            {
                await TurnOnAsync(admin.Page, host, IdleInTransaction);
                await TurnOnAsync(admin.Page, host, PinnedGap);

                // Database › Sessions as admin: the summary, and the details that name the session.
                await admin.GoAsync(host.StudioUrl("database/sessions"));
                await admin.Page.Locator(".ms-sessions-stats").WaitForAsync();

                ILocator named = admin.Page.Locator(".ms-sessions-table td", new PageLocatorOptions { HasTextString = IdleSessionName });
                await UntilShownAsync(named, SessionsRefresh(admin.Page), NextSweep, "the idle session in admin's session details");

                (await admin.Page.Locator(".ms-sessions-table th", new PageLocatorOptions { HasTextString = "pid" }).CountAsync())
                    .Should().BePositive("admin passes the activity gate, so the details table is drawn");
                (await admin.Page.Locator("#ms-sessions-locks").CountAsync()).Should().Be(1, "the page has its locks summary");

                await BrowserScenario.RunAsync(fixture.Browser, "health-sessions-viewer", host, "viewer", async viewer =>
                {
                    await viewer.GoAsync(host.StudioUrl("database/sessions"));
                    await viewer.Page.Locator(".ms-sessions-stats").WaitForAsync();

                    ILocator idle = viewer.Page.Locator(".ms-sessions-table td", new PageLocatorOptions { HasTextString = "idle in transaction" });
                    await UntilShownAsync(idle, SessionsRefresh(viewer.Page), NextSweep, "an idle-in-transaction row in viewer's session summary");

                    (await viewer.Page.Locator(".ms-sessions-gate").InnerTextAsync()).Should().Contain("Session details are not shown");
                    (await viewer.Page.Locator(".ms-sessions-table th", new PageLocatorOptions { HasTextString = "pid" }).CountAsync())
                        .Should().Be(0, "the summary has no process ids");
                    (await viewer.Page.Locator(".ms-sessions").InnerTextAsync()).Should().NotContain(
                        UnhealthySwitch.ApplicationNamePrefix, "an application name is a detail, and viewer does not pass the activity gate");

                    viewer.AssertClean("opening Database › Sessions as viewer");
                });

                // C04 and D01, once their minute has passed - the two minutes run side by side.
                await admin.GoAsync(host.StudioUrl("health"));

                ILocator c04 = admin.Page.Locator(Visible("c04"));
                ILocator d01 = admin.Page.Locator(Visible("d01"));
                await UntilShownAsync(c04, HealthRefresh(admin.Page), AfterAMinute, "C04 a minute after the session went idle");
                await UntilShownAsync(d01, HealthRefresh(admin.Page), AfterAMinute, "D01 a minute after the gap pinned the mark");

                (await c04.InnerTextAsync()).Should().Contain(IdleSessionName, "admin passes the activity gate, so C04 names the session");

                // The Projections screen draws D01 from the same sweep, without starting one.
                await admin.GoAsync(host.StudioUrl("projections"));
                ILocator banner = admin.Page.Locator("section.ms-health-banner-d01");
                await banner.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
                (await banner.InnerTextAsync()).Should().Contain("D01");
                (await banner.Locator("a.ms-health-banner-link").GetAttributeAsync("href")).Should().Contain("#finding-d01");

                await BrowserScenario.RunAsync(fixture.Browser, "health-withheld-viewer", host, "viewer", async viewer =>
                {
                    await viewer.GoAsync(host.StudioUrl("health"));

                    ILocator withheld = viewer.Page.Locator(Visible("c04"));
                    await UntilShownAsync(withheld, HealthRefresh(viewer.Page), NextSweep, "C04 on viewer's Health page");

                    // The finding is the database's, so viewer sees it; who the sessions are is withheld, row by row: every
                    // "Session n" line, which for admin names the process, role and application.
                    int sessionRows = 0;

                    foreach (ILocator row in await withheld.Locator(".ms-health-evidence-row").AllAsync())
                    {
                        string label = (await row.Locator("dt").InnerTextAsync()).Trim();

                        if (SessionLine.IsMatch(label))
                        {
                            sessionRows++;
                            (await row.Locator("dd").InnerTextAsync()).Trim().Should().StartWith("withheld - ", "nothing names a session to viewer");
                        }
                    }

                    sessionRows.Should().BePositive("C04 lists the sessions it found, withheld");

                    string page = await viewer.Page.Locator(".ms-health").InnerTextAsync();
                    page.Should().NotContain(UnhealthySwitch.ApplicationNamePrefix, "no finding names a session's application to viewer");
                    (await viewer.Page.Locator(".ms-health-statements-note").InnerTextAsync()).Should().Contain(
                        "not shown to you", "the statement checks' findings are behind the activity gate too");

                    viewer.AssertClean("opening the Health page as viewer");
                });

                admin.AssertClean("turning on two switches, opening Sessions, Health and Projections");
                passed = true;
            }
            finally
            {
                await CleanUpAsync(
                    admin,
                    passed,
                    ("turn the pinned gap off", OnPage(page => TurnOffAsync(page, host, PinnedGap))),
                    ("turn idle in transaction off", OnPage(page => TurnOffAsync(page, host, IdleInTransaction))));
            }
        });
    }

    // ------------------------------------------------------------------------------------------------
    // D02 on a host with no daemon
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A sample host started with <c>--no-daemon</c> has async projections behind and nothing running them, so the first
    /// sweep raises D02 as Critical and the Projections screen draws it as a banner.
    /// </summary>
    /// <remarks>
    /// A host of its own, in a database of its own: the shared hosts run the daemon, and the one switch that makes a
    /// daemon fall silent does it by pinning the mark, which is D01's. Starting a host costs this scenario about as much
    /// as one sweep interval, which is the "cheaply" the plan asks for.
    /// </remarks>
    [BrowserFact]
    public async Task A_host_whose_async_projections_nothing_runs_draws_the_D02_banner()
    {
        string connectionString = await BrowserSuiteFixture.CreateDatabaseAsync(fixture.Postgres, "browser_no_daemon");

        await using SampleHost host = await SampleHost.StartAsync(connectionString, noDaemon: true);
        await SampleHost.WaitForSeedAsync(connectionString);
        await BrowserSuiteFixture.WarmUpAsync(fixture.Browser, host);

        await BrowserScenario.RunAsync(fixture.Browser, "health-d02-banner", host, "admin", async studio =>
        {
            await studio.GoAsync(host.StudioUrl("health"));

            ILocator d02 = studio.Page.Locator(Visible("d02"));
            await UntilShownAsync(d02, HealthRefresh(studio.Page), NextSweep, "D02 on a host that runs no daemon");
            (await d02.Locator(".ms-health-severity").InnerTextAsync()).Trim().Should().Be("Critical");

            await studio.GoAsync(host.StudioUrl("projections"));
            ILocator banner = studio.Page.Locator("section.ms-health-banner-d02");
            await banner.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
            (await banner.GetAttributeAsync("class")).Should().Contain("ms-alert-error", "D02 is Critical");

            studio.AssertClean("opening Health and Projections on a host that runs no daemon");
        });
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// How many mute keys this browser keeps in <c>localStorage</c>: the hashes beside its protected key (wave 6), an object
    /// the studio writes - read as none for anything else.
    /// </summary>
    private static Task<int> MutedInStorageAsync(StudioPage studio) =>
        studio.Page.EvaluateAsync<int>("() => { const kept = JSON.parse(window.localStorage.getItem('ms_health_muted') || '{}'); return Array.isArray(kept && kept.muted) ? kept.muted.length : 0; }");

    /// <summary>
    /// Keeps every toast this page shows, class and text, in <c>window.__msToasts</c> - a toast is removed after four
    /// seconds, and the assertion must not depend on catching it while it is there.
    /// </summary>
    private static async Task RecordToastsAsync(StudioPage studio) =>
        await studio.Page.EvaluateAsync(
            """
            () => {
              window.__msToasts = [];
              const seen = () => document.querySelectorAll('.ms-toast').forEach(t => {
                const entry = t.className + ' | ' + t.textContent;
                if (!window.__msToasts.includes(entry)) { window.__msToasts.push(entry); }
              });
              new MutationObserver(seen).observe(document.body, { childList: true, subtree: true, characterData: true });
              seen();
            }
            """);

    /// <summary>
    /// Sets the sample's <c>Invoice</c> HiLo row, as another client of the server would, and answers what it was. The
    /// running host keeps the block it already holds and draws no new one during the scenario.
    /// </summary>
    private async Task<long> SetInvoiceHiloAsync(long value)
    {
        string table = SqlIdentifier.Qualify(SampleStore.DocumentSchema, "mt_hilo");

        await using NpgsqlConnection connection = new(fixture.RootConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(
            "with old as (select hi_value from " + table + " where entity_name = @name for update) "
            + "update " + table + " t set hi_value = @value from old where t.entity_name = @name returning old.hi_value",
            connection);
        command.Parameters.AddWithValue("name", "Invoice");
        command.Parameters.AddWithValue("value", value);

        object? previous = await command.ExecuteScalarAsync();

        return previous is long was
            ? was
            : throw new InvalidOperationException(
                "The sample database has no Invoice row in " + table + ": the seeder stores invoices, whose int ids Marten draws from HiLo.");
    }

    /// <summary>
    /// Puts the <c>Invoice</c> HiLo row back to <paramref name="original" />, but only if it still holds
    /// <see cref="NearTheCeiling" />: compare and set, because a row that moved in between means something drew a block
    /// from it, and writing the old value back over that would let Marten hand the same ids out twice.
    /// </summary>
    private async Task RestoreInvoiceHiloAsync(long original)
    {
        string table = SqlIdentifier.Qualify(SampleStore.DocumentSchema, "mt_hilo");

        await using NpgsqlConnection connection = new(fixture.RootConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = new(
            "update " + table + " set hi_value = @original where entity_name = @name and hi_value = @expected", connection);
        command.Parameters.AddWithValue("name", "Invoice");
        command.Parameters.AddWithValue("original", original);
        command.Parameters.AddWithValue("expected", NearTheCeiling);

        if (await command.ExecuteNonQueryAsync() != 1)
        {
            throw new InvalidOperationException(
                "The Invoice HiLo row was left as it is: it no longer holds " + NearTheCeiling.ToString(CultureInfo.InvariantCulture)
                + ", so something drew a block from it since the scenario set it, and restoring " + original.ToString(CultureInfo.InvariantCulture)
                + " over that would hand out the same ids twice.");
        }
    }
}
