using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Integration.Tests.Events;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// E09 live: a stream compacted into an empty snapshot, made the way Marten below 9.57 made one - a <c>Compacted&lt;T&gt;</c>
/// at the stream's watermark whose snapshot is JSON <c>null</c> - and found by probing the event at each compacted stream's
/// watermark; quiet over a healthy compacted stream. On a conjoined store each damaged stream is shown only through the
/// activity gate, to a visitor who may see its tenant.
/// </summary>
public class EmptyCompactedSnapshotLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Basket(StoreOptions options) =>
        options.Projections.Add(new CompactionBasketProjection(), ProjectionLifecycle.Async);

    private static void ConjoinedBasket(StoreOptions options)
    {
        Basket(options);
        options.Events.TenancyStyle = TenancyStyle.Conjoined;
        options.Policies.AllDocumentsAreMultiTenanted();
    }

    [PostgresFact]
    [HealthFires("E09")]
    public async Task A_stream_compacted_into_an_empty_snapshot_is_E09_and_a_healthy_compacted_stream_beside_it_is_not()
    {
        await using EventHealthHost host = await EventHealthHost.CreateAsync(postgres, "health_e09_fires", store: Basket);

        Guid healthy = await CompactedAsync(host, tenant: null);
        Guid damagedOne = await CompactedAsync(host, tenant: null);
        Guid damagedTwo = await CompactedAsync(host, tenant: null);
        await StreamCompactionFixture.StartBasketAsync(host.Store, tenantId: null, closed: false);

        await DamageAsync(host, damagedOne);
        await DamageAsync(host, damagedTwo);

        HealthReport report = await host.DeeperReportAsync();

        HealthReportFinding finding = report.Findings.Should().ContainSingle(static x => x.Code == "E09").Subject;
        finding.Severity.Should().Be(HealthSeverity.Warning);
        finding.Finding.Title.Should().Be("2 streams were compacted into an empty snapshot");
        finding.Finding.TenantId.Should().BeNull("a store that is not tenanted per event has no tenant to name");
        finding.Evidence.Should().Contain(new HealthEvidence("Streams compacted into an empty snapshot", "2"));
        finding.Evidence.Should().Contain(new HealthEvidence("Compacted streams probed", "3"));

        string listed = finding.Evidence.Single(static x => x.Label.StartsWith("Streams (up to", StringComparison.Ordinal)).Value;
        listed.Should().Contain(damagedOne.ToString()).And.Contain(damagedTwo.ToString())
            .And.NotContain(healthy.ToString(), "a healthy compacted stream is not named");
        listed.Should().Contain("(version 3)");

        HealthCheckStatus status = EventHealthHost.Status(report, "E09");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
    }

    [PostgresFact]
    [HealthQuiet("E09")]
    public async Task A_healthy_compacted_stream_and_a_store_that_never_compacted_are_quiet()
    {
        await using EventHealthHost compacted = await EventHealthHost.CreateAsync(postgres, "health_e09_quiet", store: Basket);

        await CompactedAsync(compacted, tenant: null);

        HealthReport report = await compacted.DeeperReportAsync();

        report.Findings.Should().NotContain(static x => x.Code == "E09");
        HealthCheckStatus status = EventHealthHost.Status(report, "E09");
        status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
        status.Note.Should().Contain("1 compacted stream").And.Contain("holds a snapshot");

        await using EventHealthHost never = await EventHealthHost.CreateAsync(postgres, "health_e09_never");
        await EventHealthData.GenerateSampleAsync(never.Store);

        HealthReport sample = await never.DeeperReportAsync();

        sample.Findings.Should().NotContain(static x => x.Code == "E09");
        EventHealthHost.Status(sample, "E09").Note.Should().Contain("No stream has a compaction watermark");
    }

    /// <summary>
    /// Acceptance, live and both ways, as E03's: on a conjoined store the damaged stream belongs to a tenant, so the finding
    /// names it and is withheld whole from a visitor short of the activity gate, and shown - tenant and stream - to one through
    /// it. The other tenant's healthy compacted stream is nowhere in it.
    /// </summary>
    [PostgresFact]
    [HealthFires("E09")]
    public async Task On_a_conjoined_store_a_damaged_stream_and_its_tenant_are_shown_only_through_the_activity_gate()
    {
        Guid damaged;

        await using (EventHealthHost closed = await EventHealthHost.CreateAsync(postgres, "health_e09_tenants", store: ConjoinedBasket))
        {
            await CompactedAsync(closed, "globex");
            damaged = await CompactedAsync(closed, "acme");
            await DamageAsync(closed, damaged, "acme");

            HealthReport report = await closed.DeeperReportAsync();

            report.Findings.Should().NotContain(static x => x.Code == "E09", "the finding names a tenant, and ViewDatabaseActivity is off");
            string shown = string.Join("|", report.Findings.SelectMany(static x => x.Evidence.Select(e => e.Value)));
            shown.Should().NotContain(damaged.ToString()).And.NotContain("acme");
            HealthCheckStatus status = EventHealthHost.Status(report, "E09");
            status.Outcome.Should().Be(HealthCheckOutcome.Checked, status.Problem);
            status.FindingsWithheld.Should().BeTrue("E09's own finding is the one withheld, not some other check's");
        }

        await using EventHealthHost open = await EventHealthHost.CreateAsync(
            postgres,
            "health_e09_tenants_open",
            store: ConjoinedBasket,
            studio: static options => options.Capabilities.ViewDatabaseActivity = true);

        Guid openHealthy = await CompactedAsync(open, "globex");
        Guid openDamaged = await CompactedAsync(open, "acme");
        await DamageAsync(open, openDamaged, "acme");

        HealthReport opened = await open.DeeperReportAsync();

        HealthReportFinding finding = opened.Findings.Should().ContainSingle(static x => x.Code == "E09").Subject;
        finding.Finding.TenantId.Should().Be("acme");
        finding.Finding.Key.Should().Be("E09|acme");
        finding.Evidence.Should().Contain(new HealthEvidence("Tenant", "acme"));
        finding.Evidence.Should().Contain(static x => x.Label == "Stream" && x.Value.EndsWith("(version 3)", StringComparison.Ordinal));
        finding.Evidence.Single(static x => x.Label == "Stream").Value.Should().StartWith(openDamaged.ToString())
            .And.NotContain(openHealthy.ToString());
    }

    /// <summary>A basket stream, compacted by Marten at the floor (which writes a real snapshot).</summary>
    private static async Task<Guid> CompactedAsync(EventHealthHost host, string? tenant)
    {
        Guid id = await StreamCompactionFixture.StartBasketAsync(host.Store, tenant, closed: false);

        await using IDocumentSession session = tenant is null ? host.Store.LightweightSession() : host.Store.LightweightSession(tenant);
        await session.Events.CompactStreamAsync<CompactionBasket>(id, static _ => { });
        await session.SaveChangesAsync(Token);

        return id;
    }

    /// <summary>
    /// Makes a compacted stream what Marten below 9.57 left behind for one its own aggregate had deleted: the
    /// <c>Compacted&lt;T&gt;</c> at its watermark, holding a snapshot of JSON <c>null</c>.
    /// </summary>
    private async Task DamageAsync(EventHealthHost host, Guid id, string? tenant = null)
    {
        string events = SqlIdentifier.Qualify(host.EventSchema, "mt_events");
        string where = "where stream_id = @id and version = 3 and mt_dotnet_type like 'JasperFx.Events.Compacted%'"
            + (tenant is null ? string.Empty : " and tenant_id = @tenant");

        (string, object)[] parameters = tenant is null ? [("id", id)] : [("id", id), ("tenant", tenant)];

        // Not vacuous: the row is there to be changed, and it is the snapshot Marten wrote.
        (await EventHealthData.ScalarAsync(postgres, $"select count(*) from {events} where stream_id = '{id}' and version = 3 and mt_dotnet_type like 'JasperFx.Events.Compacted%'"))
            .Should().BeGreaterThan(0);

        await EventHealthData.ExecuteAsync(
            postgres,
            $"update {events} set data = (data - 'Snapshot' - 'snapshot') || '{{\"Snapshot\": null}}'::jsonb {where}",
            parameters);
    }
}
