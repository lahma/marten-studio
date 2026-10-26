using MartenStudio.Sample.Generation;
using MartenStudio.SampleDomain;

using Npgsql;

using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// The sample starts healthy: a demo turns it unhealthy on purpose (the landing page's switches), so the store as the
/// sample host configures it has nothing to say for itself in the configuration area.
/// </summary>
/// <remarks>
/// <para>
/// The store is the sample host's own shape - <c>SampleStore.Configure</c> plus what the switches declare on it
/// (<see cref="UnhealthyDemoStore.Configure" />) - in Development, on the suite's Testcontainers Postgres, which is the
/// kind of server the sample's own throwaway one is.
/// </para>
/// <para>
/// Stricter than "no Warning or Critical": K02 and K03 are Infos, and a store that starts healthy has none of them
/// either. Every finding the advisor does show at start-up is written to the test's output - the server's
/// (S02's <c>synchronous_commit</c> Info on a Testcontainers server) included - so a run says what a demo opens on.
/// </para>
/// <para>
/// One server finding is asserted, on purpose (W5-S2): S04's Info, because the sample's store logs in to its throwaway
/// server as that server's superuser - true of the demo, and Info because it is Development.
/// </para>
/// <para>
/// And the studio as the sample configures it (wave 6): every capability on, and the SQL console narrowed to the sample's
/// own role (<see cref="SampleConsoleRole" />), made and granted here as the sample makes and grants it - so K05, "SQL
/// console without a narrowed role", is its remedy applied, and every other check reads as that role, as the sample's do.
/// </para>
/// </remarks>
public class SampleBaselineLiveTests(PostgresFixture postgres)
{
    private const string Schema = "health_sample_baseline";

    private static readonly string[] Configuration = ["K01", "K02", "K03", "K04", "K05"];

    [PostgresFact]
    [HealthQuiet("K02")]
    [HealthQuiet("K03")]
    // K05 is asserted below but not marked: its fires and quiet proof is PostgresStudio.Integration.Tests'
    // (HealthLiveTestConventionTests.ProvenInPostgresStudioSuite), and a mark here would be a stale one.
    public async Task The_sample_store_starts_with_no_configuration_finding_at_all()
    {
        (await SampleConsoleRole.EnsureAsync(postgres.ConnectionString, cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeTrue("the suite's server lets its login make roles, as the sample's throwaway one does");

        await using HealthLiveHost host = await HealthLiveHost.CreateAsync(
            postgres,
            Schema,
            store: UnhealthyDemoStore.Configure,
            studio: static options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.SqlConsoleRole = SampleConsoleRole.Name;
            });

        await using (NpgsqlConnection connection = new(postgres.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await SampleConsoleRole.GrantAsync(connection, schemas: [Schema, Schema + "_events"], cancellationToken: TestContext.Current.CancellationToken);
        }

        HealthReport report = await host.ReportAsync();

        foreach (HealthReportFinding finding in report.Findings)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(finding.Severity + " " + finding.Code + ": " + finding.Finding.Title);
        }

        report.Findings.Should().NotContain(x => Configuration.Contains(x.Code), "the sample starts healthy, and is made unhealthy on purpose");

        foreach (string code in Configuration)
        {
            HealthLiveHost.Status(report, code).Outcome.Should().Be(HealthCheckOutcome.Checked, "{0} has to run for its silence to mean anything", code);
        }

        // W5-S2, deliberately: the sample's throwaway server, like the suite's, is a Testcontainers Postgres the store logs in
        // to as its superuser, so S04 says so - as Info, because this is Development, where that is a reasonable choice.
        HealthReportFinding superuser = report.Findings.Should().ContainSingle(x => x.Code == "S04").Subject;
        superuser.Severity.Should().Be(HealthSeverity.Info);
        superuser.Finding.Variant.Should().Be("superuser");

        // Wave 6: the console's role is the remedy applied, and the checks that read as it ran rather than failing on it.
        HealthLiveHost.Status(report, "K05").Note.Should().Contain("SqlConsoleRole").And.Contain("narrows");
        report.Findings.Should().NotContain(static x => x.Code == "K05" || x.Code == "K06");
        HealthLiveHost.Status(report, "T01").Outcome.Should().NotBe(HealthCheckOutcome.Failed, "a catalog check reads as the console's role");
    }
}
