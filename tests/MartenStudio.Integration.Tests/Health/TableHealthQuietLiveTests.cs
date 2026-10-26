using Marten;

using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Documents;

using Npgsql;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The anti-cry-wolf half: the healthy sample store, seeded and read the way a demo is, says nothing about its
/// tables and indexes - and a hidden document type never appears, whatever is wrong with its table.
/// </summary>
public class TableHealthQuietLiveTests(PostgresFixture postgres)
{
    private static readonly string[] TableCodes = ["T01", "T03", "T04", "T05", "T06", "T07", "T08", "T09", "T10", "T11", "T12", "T16", "T24"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Two regular sweeps an interval apart - so the rate checks have a window - with the seeder's writes and a
    /// visitor's reads before them, and the deeper checks run once.
    /// </summary>
    [PostgresFact]
    [HealthQuiet("T01")]
    [HealthQuiet("T03")]
    [HealthQuiet("T04")]
    [HealthQuiet("T05")]
    [HealthQuiet("T06")]
    [HealthQuiet("T07")]
    [HealthQuiet("T08")]
    [HealthQuiet("T09")]
    [HealthQuiet("T10")]
    [HealthQuiet("T11")]
    [HealthQuiet("T12")]
    [HealthQuiet("T16")]
    [HealthQuiet("T24")]
    public async Task The_seeded_sample_store_is_quiet_on_every_table_and_index_check()
    {
        // A clock a week past the statistics' start and an ANALYZE to prove it from, so T01, T05 and T06 judge rather than
        // withhold: their silence below means something (W5-B1 review F3).
        ManualClock clock = new(Covering);
        await using HealthLiveHost host = await HostAsync(postgres, "health_tables_quiet", clock);

        await new SampleDataSeeder().Populate(host.Store, Token);
        await ExecuteAsync(postgres, "analyze " + TableOf<Customer>(host.Store));

        HealthReport first = await ReportAsync(host);
        first.Findings.Should().NotContain(static x => TableCodes.Contains(x.Code), string.Join(" | ", first.Findings.Select(static x => x.Finding.Title)));

        // The cost budget: the regular table checks share one table read and one index read through the sweep's
        // memo, so the whole area is nine statements - G1M's one catalog list of the tables beside the store, those two,
        // T10's pg_stats read for the soft-deleted Order, T11's mt_hilo existence check and one-row read for the int-id
        // Invoice, and H9's three: the wraparound read T12, T13 and T14 share, T14's holders read and the replication slots
        // read it shares with R01. It was eight before the table checks judged the whole database (issue #8, G1), and nine
        // before W5-V1's T16, whose three are the workers read, the progress read the Maintenance page shares, and the
        // sweep's one server-facts read, which T16 is the first table check to ask for (T24 shares the table read). Twelve
        // before T08's since-restart was wired to W5-CAP's statistics window (W5b-int): the window's one read, which T08 shares
        // with C08 and C11 through the same memo, so the sweep as a whole runs it once either way. Thirteen before W5-CAP-fix2
        // read pg_control_checkpoint() and pg_control_system() apart from the window: a has_function_privilege probe, then a
        // call of only the functions it allowed - two more statements behind the same shared window read. Fifteen before
        // W5-B1's five: T15's foreign keys, the usage guard's counters-age read T01, T05 and T06 share, T26's integer keys,
        // the partition read T19 and T20 share, and T17's document-named tables.
        first.Checks.Where(static x => x.Code.StartsWith('T')).SelectMany(static x => x.Statements).Select(static x => x.Sql)
            .Distinct(StringComparer.Ordinal).Should().HaveCount(20, "the regular table checks' whole catalog cost on the sample store");
        first.Checks.Single(static x => x.Code == "T08").Statements.Should().Contain(
            static x => x.Sql.Contains("pg_catalog.pg_stat_archiver", StringComparison.Ordinal), "T08 reads the statistics window for since-restart");
        first.Checks.Single(static x => x.Code == "T08").Statements.Should().Contain(
            static x => x.Sql.Contains("has_function_privilege", StringComparison.Ordinal), "the window's control functions are probed before they are called");

        await using (IQuerySession session = host.Store.QuerySession())
        {
            (await session.Query<Customer>().Take(10).ToListAsync(Token)).Should().NotBeEmpty();
            (await session.Query<Order>().Take(10).ToListAsync(Token)).Should().NotBeEmpty();
        }

        clock.Advance(TimeSpan.FromMinutes(2));
        HealthReport second = await ReportAsync(host, deeper: true);

        second.Findings.Should().NotContain(static x => TableCodes.Contains(x.Code), string.Join(" | ", second.Findings.Select(static x => x.Finding.Title)));

        foreach (string code in TableCodes)
        {
            HealthCheckStatus status = HealthLiveHost.Status(second, code);
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} is quiet because it looked, not because it did not run: {1}", code, status.Problem);
            status.Statements.Should().NotBeEmpty("{0} read the catalog", code);
        }

        foreach (string code in (string[]) ["T01", "T05", "T06"])
        {
            (HealthLiveHost.Status(second, code).Note ?? string.Empty).Should().NotStartWith("Withheld:", "{0} judged usage over a covered window", code);
        }

        host.Logs.WarningsOrWorse.Should().BeEmpty("an expected state is never a warning");
    }

    /// <summary>
    /// Acceptance 3: a hidden document type never appears - not in a finding, not in a status row's note, not in
    /// the text of a statement Show SQL prints - although its table has an invalid index, a redundant one and an
    /// unused one. Its visible twin, shaped the same way, is the proof the conditions are real.
    /// </summary>
    [PostgresFact]
    public async Task A_hidden_document_type_never_appears_though_its_table_has_everything_wrong_with_it()
    {
        // A clock a week past the statistics' start, or T05's unused index is withheld rather than found (issue #8 G4).
        await using HealthLiveHost host = await HostAsync(
            postgres,
            "health_tables_hidden",
            new ManualClock(Covering),
            store: static o =>
            {
                o.Schema.For<HealthSecretThing>().Index(x => x.Name).Index(new System.Linq.Expressions.Expression<Func<HealthSecretThing, object>>[] { x => x.Name, x => x.Rank });
                o.Schema.For<HealthIndexedThing>().Index(x => x.Name).Index(new System.Linq.Expressions.Expression<Func<HealthIndexedThing, object>>[] { x => x.Name, x => x.Rank });
            },
            studio: static o => o.IsDocumentTypeVisible = static type => type != typeof(HealthSecretThing));
        await ApplyAsync(host.Store);

        foreach (string table in new[] { TableOf<HealthSecretThing>(host.Store), TableOf<HealthIndexedThing>(host.Store) })
        {
            // A hundred thousand writes and distinct names - so the declared indexes are past T05's megabyte, which a
            // b-tree's deduplication would never let one repeated name reach - then the two duplicates the failed
            // unique build is about.
            await InsertAsync(postgres, table, 110_000);
            await InsertAsync(postgres, table, 2, "jsonb_build_object('Name', 'same')");

            try
            {
                await ExecuteAsync(postgres, "create unique index concurrently " + (table.Contains("secret", StringComparison.Ordinal) ? "secret_unique" : "visible_unique")
                    + " on " + table + " ((data ->> 'Name'))");
            }
            catch (PostgresException exception) when (exception.SqlState == "23505")
            {
                // Expected: the duplicate is the point, and the invalid index is what it leaves.
            }

            // ANALYZE refreshes the indexes' relpages, which is what T05 sizes them by: inserts alone do not.
            await ExecuteAsync(postgres, "analyze " + table);
        }

        HealthReport report = await ReportAsync(host);

        report.Findings.Should().Contain(static x => x.Code == "T03" && x.Finding.Subject!.Contains("healthindexedthing", StringComparison.Ordinal),
            "the visible twin's invalid index is found, so the hidden one's absence means something");
        report.Findings.Should().Contain(static x => x.Code == "T04" && x.Finding.Subject!.Contains("healthindexedthing", StringComparison.Ordinal));
        report.Findings.Should().Contain(static x => x.Code == "T05" && x.Finding.Subject!.Contains("healthindexedthing", StringComparison.Ordinal),
            "the visible twin's unscanned index on a table of 110,000 inserts is found too");

        foreach (HealthReportFinding finding in report.Findings)
        {
            string text = finding.Finding.Title + " " + finding.Finding.WhyItMatters + " " + finding.Finding.Subject + " "
                + string.Join(" ", finding.Evidence.Select(static x => x.Value));
            text.Should().NotContainEquivalentOf("secret", "{0} names nothing a hidden type owns", finding.Code);
            finding.Finding.DocumentTypes.Should().NotContain(typeof(HealthSecretThing));
        }

        foreach (HealthCheckStatus status in report.Checks.Where(static x => x.Code.StartsWith('T')))
        {
            (status.Note ?? string.Empty).Should().NotContainEquivalentOf("secret");
            status.Statements.Should().OnlyContain(static x => !x.Sql.Contains("secret", StringComparison.OrdinalIgnoreCase),
                "a hidden type's table name is never in a statement's text");
        }
    }
}
