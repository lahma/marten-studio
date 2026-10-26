using MartenStudio.Integration.Tests.Browser;
using MartenStudio.Integration.Tests.Health;
using MartenStudio.Sample;
using MartenStudio.SampleDomain;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// Both answers the Marten sample host gives a stale database, through the real host on real Kestrel: on a configured
/// database it stops with one line and touches nothing, and with <c>--allow-data-generation</c> it drops the demo's
/// schemas, seeds them again and serves.
/// </summary>
/// <remarks>
/// The third case - the throwaway container a plain <c>dotnet run</c> reuses - takes the second answer by the same
/// decision (<c>EphemeralPostgres.IsThrowaway</c> or the flag), and cannot be staged here: the container would be the
/// host's own, started after the test could have put anything in it.
/// </remarks>
/// <param name="postgres">The assembly's Postgres.</param>
public class SampleDatabaseHostTests(PostgresFixture postgres)
{
    private static readonly SampleDatabaseInspection Stale = new(SampleDatabaseState.Stale, StaleSampleShape.SeedVersion);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_refusal_is_one_line_that_says_what_to_do()
    {
        string line = SampleDatabaseCheck.RefusalLine(Stale);

        line.Should().NotContain("\n").And.NotContain("\r");
        line.Should().Contain("ConnectionStrings:Marten").And.Contain("seed version 2; this build seeds " + SampleDataSeeder.SeedVersion);
        line.Should().Contain("--allow-data-generation").And.Contain("studio_sample and studio_sample_events");

        SampleDatabaseCheck.RefusalLine(new SampleDatabaseInspection(SampleDatabaseState.Unmarked, null))
            .Should().NotContain("\n").And.Contain("no seed marker").And.Contain("--allow-data-generation");
        SampleDatabaseCheck.ReseededLine(Stale).Should().NotContain("\n").And.Contain("were dropped and are being seeded again");
    }

    [PostgresFact]
    public async Task On_a_configured_stale_database_the_host_stops_with_one_line_and_changes_nothing()
    {
        string connectionString = await StaleSampleShape.CreateAsync(postgres, "sample_host_stale_refused");
        long invoices = await InvoicesAsync(connectionString);

        (int exitCode, string output) = await SampleHost.RunToExitAsync(SampleApp.Marten, connectionString, cancellationToken: Token);

        exitCode.Should().Be(1, "the host stops rather than fail halfway through starting: " + output);
        output.Should().Contain(SampleDatabaseCheck.RefusalLine(Stale));
        output.Should().NotContain("23503", "nothing reached Marten");
        output.Should().NotContain("Now listening");

        (await SampleDatabase.InspectAsync(connectionString, Token)).Should().Be(Stale, "a configured database is left as it was found");
        (await InvoicesAsync(connectionString)).Should().Be(invoices);
    }

    [PostgresFact]
    public async Task With_allow_data_generation_the_host_drops_the_stale_schemas_seeds_them_again_and_serves()
    {
        string connectionString = await StaleSampleShape.CreateAsync(postgres, "sample_host_stale_reseeded");

        await using SampleHost host = await SampleHost.StartAsync(connectionString, allowDataGeneration: true, cancellationToken: Token);
        await SampleHost.WaitForSeedAsync(connectionString, Token);

        host.Log.Should().Contain(SampleDatabaseCheck.ReseededLine(Stale));
        host.Log.Should().NotContain("23503");

        (await SampleDatabase.InspectAsync(connectionString, Token)).State.Should().Be(SampleDatabaseState.Current);
        (await LiveWait.ScalarAsync<long>(connectionString,
                "select count(*) from pg_catalog.pg_constraint where conrelid = 'studio_sample.mt_doc_invoice'::regclass and contype = 'f'"))
            .Should().Be(1, "Marten built the invoice table again, foreign key and all");

        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        using HttpResponseMessage landing = await client.GetAsync(new Uri("/", UriKind.Relative), Token);
        landing.IsSuccessStatusCode.Should().BeTrue();
    }

    private static async Task<long> InvoicesAsync(string connectionString) =>
        await LiveWait.ScalarAsync<long>(connectionString, "select count(*) from studio_sample.mt_doc_invoice");
}
