using MartenStudio.Services.Health.Checks;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

using static MartenStudio.Integration.Tests.Health.TableHealthLive;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// D10 against a real Postgres, at the floor and at the latest-tested Marten: a row a renamed projection left, and one an
/// inline projection's async past left, fire it - one Info finding each - while the registered shards' rows and Marten's
/// own bookkeeping keep it quiet.
/// </summary>
/// <remarks>
/// The rows are the test's own DML in the test's own schema, written as a daemon would have left them; the studio writes
/// none. The sample store registers <c>DailySales</c> and <c>ShipmentTracker</c> async and <c>OrderSummary</c> inline.
/// </remarks>
/// <param name="postgres">The assembly's container.</param>
public class OrphanedProgressionHealthLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    [HealthFires("D10")]
    public async Task A_renamed_projections_row_and_an_inline_projections_old_row_are_D10_one_finding_each()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_d10_fires", clock);
        await ApplyAsync(host.Store);

        string events = host.Schema + "_events";
        await WriteProgressionAsync(
            events,
            ("HighWaterMark", 30),
            ("DailySales:All", 30),
            ("DailySalesBeforeRename:All", 12),
            ("OrderSummary:All", 7));

        HealthReport report = await ReportAsync(host);

        HealthCheckStatus status = HealthLiveHost.Status(report, "D10");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Statements.Should().Contain(static x => x.Sql.Contains("<> all(@registered)", StringComparison.Ordinal),
            "D10 read the rows nothing registered by name, so its findings mean something");

        IReadOnlyList<HealthReportFinding> findings = Of(report, "D10");
        findings.Select(static x => x.Finding.Subject).Should().BeEquivalentTo(["DailySalesBeforeRename", "OrderSummary"]);
        findings.Should().OnlyContain(static x => x.Severity == HealthSeverity.Info);

        HealthReportFinding renamed = findings.Single(static x => x.Finding.Subject == "DailySalesBeforeRename");
        renamed.Finding.Title.Should().Be("A progression row of DailySalesBeforeRename that nothing in this store advances");
        renamed.Finding.WhyItMatters.Should().StartWith("No projection or subscription named DailySalesBeforeRename is registered");
        renamed.Evidence.Should().Contain(new HealthEvidence("Rows by name", "DailySalesBeforeRename:All at 12"));
        renamed.Finding.Remedy!.Code.Should().Contain(
            "await ((IEventDatabase) database).DeleteProjectionProgressByShardNameAsync(\"DailySalesBeforeRename:All\");",
            "the remedy is Marten's own removal by the row's exact name");

        HealthReportFinding inline = findings.Single(static x => x.Finding.Subject == "OrderSummary");
        inline.Finding.WhyItMatters.Should().StartWith("OrderSummary is registered inline or live");

        host.Logs.WarningsOrWorse.Should().BeEmpty();
    }

    [PostgresFact]
    [HealthQuiet("D10")]
    public async Task Registered_shards_rows_and_Martens_bookkeeping_are_quiet()
    {
        ManualClock clock = new(Start);
        await using HealthLiveHost host = await HostAsync(postgres, "health_d10_quiet", clock);
        await ApplyAsync(host.Store);

        string events = host.Schema + "_events";
        await WriteProgressionAsync(
            events,
            [
                ("HighWaterMark", 30),
                ("HighWaterAllocationFence", 30),
                ("HighWaterStuckGap", 20),
                .. DaemonCheckConfiguration.AsyncShardsOf(host.Store).Select(static x => (x.Identity, 30L)),
            ]);

        HealthReport report = await ReportAsync(host);

        HealthCheckStatus status = HealthLiveHost.Status(report, "D10");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        Of(report, "D10").Should().BeEmpty();
    }

    /// <summary>Writes progression rows as a daemon would have left them - the test's own DML.</summary>
    private async Task WriteProgressionAsync(string schema, params (string Name, long Sequence)[] rows)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        foreach ((string name, long sequence) in rows)
        {
            await using NpgsqlCommand upsert = new(
                "insert into " + SqlIdentifier.Qualify(schema, "mt_event_progression") + " (name, last_seq_id, last_updated) "
                + "values (@name, @sequence, now()) on conflict (name) do update set last_seq_id = excluded.last_seq_id, last_updated = now()",
                connection);
            upsert.Parameters.AddWithValue("name", name);
            upsert.Parameters.AddWithValue("sequence", sequence);
            await upsert.ExecuteNonQueryAsync(Token);
        }
    }
}
