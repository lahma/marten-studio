using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// E02, E03 and E04 live: each produced on a real store and found by the deeper checks, and each quiet on the
/// healthy sample store with the sample domain's own demo data in it.
/// </summary>
public class EventTrafficLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------------
    // E02
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Failed saves on a Rich store leave tombstones: here, starting streams whose ids already exist - a save that
    /// fails after Marten drew its sequence numbers, which it then fills with tombstones in the background.
    /// </summary>
    [PostgresFact]
    [HealthFires("E02")]
    public async Task Failed_saves_on_a_Rich_store_leave_tombstones_and_E02_finds_them()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e02_fires", store: EventHealthData.Rich);

        IReadOnlyList<Guid> streams = await EventHealthData.AppendAsync(host.Store, streams: 300, eventsPerStream: 4);

        const int Failures = 30;
        for (int i = 0; i < Failures; i++)
        {
            await using IDocumentSession session = host.Store.LightweightSession();
            session.Events.StartStream(streams[i], new HealthPing(1), new HealthPing(2));

            Func<Task> save = () => session.SaveChangesAsync(Token);
            await save.Should().ThrowAsync<Exception>("the stream id is already taken");
        }

        string tombstones = $"select count(*) from \"{host.EventSchema}\".mt_events where type = 'tombstone'";
        await EventHealthData.WaitForAsync(
            async () => await EventHealthData.ScalarAsync(postgres, tombstones) >= Failures * 2,
            "Marten's tombstone block to write the failed saves' tombstones");

        HealthReport report = await host.DeeperReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E02").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(static x => x.Label == "Append mode" && x.Value == "Rich");
        finding.Evidence.Should().Contain(new HealthEvidence("Tombstone event type", "tombstone"));
    }

    [PostgresFact]
    [HealthQuiet("E02")]
    public async Task The_healthy_sample_store_has_no_tombstones_to_speak_of()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e02_quiet");
        await EventHealthData.GenerateSampleAsync(host.Store);

        HealthReport report = await host.DeeperReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E02");
        HealthCheckStatus status = EventHealthHost.Status(report, "E02");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("0 of the recent window's", "the window held enough events to be judged");
    }

    /// <summary>Acceptance 2: the window is bounded in rows - the newest 100,000 events, however many there are.</summary>
    [PostgresFact]
    public async Task The_window_is_the_last_hundred_thousand_events_of_a_larger_store()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e02_bounded");
        await EventHealthData.InsertSyntheticAsync(postgres, host.EventSchema, events: 120_000);

        HealthReport report = await host.DeeperReportAsync();

        EventHealthHost.Status(report, "E02").Note.Should().Contain("of the recent window's 100,000 events");
    }

    // ---------------------------------------------------------------------------------------------------
    // E03
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("E03")]
    public async Task One_stream_taking_most_of_the_appends_is_E03_and_names_the_stream()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e03_fires");

        await EventHealthData.AppendAsync(host.Store, streams: 300, eventsPerStream: 3);
        Guid hot = (await EventHealthData.AppendAsync(host.Store, streams: 1, eventsPerStream: 1))[0];
        await EventHealthData.AppendToAsync(host.Store, hot, events: 900);

        HealthReport report = await host.DeeperReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E03").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.TenantId.Should().BeNull("a single-tenant store's streams are the database's");
        finding.Evidence.Should().Contain(new HealthEvidence("Stream", hot.ToString()));
    }

    [PostgresFact]
    [HealthQuiet("E03")]
    public async Task The_healthy_sample_store_spreads_its_appends()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e03_quiet");
        await EventHealthData.GenerateSampleAsync(host.Store);

        HealthReport report = await host.DeeperReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E03");
        HealthCheckStatus status = EventHealthHost.Status(report, "E03");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("The busiest stream took");
    }

    /// <summary>
    /// Acceptance 3, live and both ways: on a conjoined store the hot stream names its tenant, so it is withheld whole
    /// from a visitor short of the activity gate and shown, tenant and stream, to one through it.
    /// </summary>
    [PostgresFact]
    [HealthFires("E03")]
    public async Task On_a_conjoined_store_the_hot_stream_and_its_tenant_are_shown_only_through_the_activity_gate()
    {
        // Marten refuses a single-tenanted aggregate over conjoined events, so the sample's documents go with them.
        static void Conjoined(StoreOptions options)
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
        }

        Guid hot;

        await using (EventHealthHost writer = await EventHealthHost.CreateAsync(postgres, "health_e03_tenants", store: Conjoined))
        {
            await EventHealthData.AppendAsync(writer.Store, streams: 200, eventsPerStream: 3, tenant: "globex");
            hot = (await EventHealthData.AppendAsync(writer.Store, streams: 1, eventsPerStream: 1, tenant: "acme"))[0];
            await EventHealthData.AppendToAsync(writer.Store, hot, events: 900, tenant: "acme");

            HealthReport closed = await writer.DeeperReportAsync();

            closed.Findings.Should().NotContain(static x => x.Code == "E03", "the finding names a tenant, and ViewDatabaseActivity is off");
            closed.WithheldCount.Should().BePositive();
            string shown = string.Join("|", closed.Findings.SelectMany(static x => x.Evidence.Select(e => e.Value)));
            shown.Should().NotContain(hot.ToString()).And.NotContain("acme");
        }

        await using EventHealthHost open = await EventHealthHost.CreateAsync(
            postgres,
            "health_e03_tenants_open",
            store: Conjoined,
            studio: static options => options.Capabilities.ViewDatabaseActivity = true);

        await EventHealthData.AppendAsync(open.Store, streams: 200, eventsPerStream: 3, tenant: "globex");
        Guid openHot = (await EventHealthData.AppendAsync(open.Store, streams: 1, eventsPerStream: 1, tenant: "acme"))[0];
        await EventHealthData.AppendToAsync(open.Store, openHot, events: 900, tenant: "acme");

        HealthReport report = await open.DeeperReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E03").Subject;
        finding.Finding.TenantId.Should().Be("acme");
        finding.Evidence.Should().Contain(new HealthEvidence("Stream", openHot.ToString()));
        finding.Evidence.Should().Contain(new HealthEvidence("Tenant", "acme"));
    }

    // ---------------------------------------------------------------------------------------------------
    // E04
    // ---------------------------------------------------------------------------------------------------

    [PostgresFact]
    [HealthFires("E04")]
    public async Task Large_payloads_are_E04()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e04_fires");

        // Random text compresses badly, so TOAST cannot hide the size: about 96 KB stored per event.
        await EventHealthData.AppendAsync(host.Store, streams: 50, eventsPerStream: 4, padding: static _ => Incompressible(96 * 1024));

        HealthReport report = await host.DeeperReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E04").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Evidence.Should().Contain(static x => x.Label == "Event type health_ping");
    }

    [PostgresFact]
    [HealthQuiet("E04")]
    public async Task The_healthy_sample_stores_events_are_small()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e04_quiet");
        await EventHealthData.GenerateSampleAsync(host.Store);

        HealthReport report = await host.DeeperReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E04");
        HealthCheckStatus status = EventHealthHost.Status(report, "E04");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("95th percentile");
    }

    private static string Incompressible(int length)
    {
        byte[] bytes = new byte[length * 3 / 4];
        Random.Shared.NextBytes(bytes);
        return Convert.ToBase64String(bytes);
    }
}
