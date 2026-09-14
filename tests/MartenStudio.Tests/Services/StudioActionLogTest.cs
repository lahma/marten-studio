using Marten;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Services;

/// <summary>
/// The audit log: the in-memory ring the Activity page reads, and the copy on the way to the
/// application's own logger with a pinned event id.
/// </summary>
public class StudioActionLogTest
{
    private const string DummyConnectionString =
        "Host=marten-studio-audit-test.invalid;Database=none;Username=none;Password=none";

    private sealed record Harness(
        StudioActionLog Log,
        StudioActionLogService Ring,
        CapturingLoggerProvider Captured,
        TestAuthenticationStateProvider Users,
        StudioState State,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static Harness CreateHarness()
    {
        var options = Options.Create(new MartenStudioOptions());
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));
        var provider = services.BuildServiceProvider();

        var users = new TestAuthenticationStateProvider();
        var authorization = new StudioAuthorization(options, new TestStoreAuthorizationService(), users);
        var catalog = new StudioScopeCatalog(
            options,
            new MartenStoreRegistry(services),
            authorization,
            new TenantDiscovery(options, NullLogger<TenantDiscovery>.Instance),
            provider,
            NullLogger<StudioScopeCatalog>.Instance);
        var state = new StudioState(catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());

        var ring = new StudioActionLogService();
        var captured = new CapturingLoggerProvider();
        var log = new StudioActionLog(ring, captured.CreateFactory().CreateLogger<StudioActionLog>(), users, state);

        return new Harness(log, ring, captured, users, state, provider);
    }

    [Fact]
    public void A_successful_action_lands_in_the_ring_and_in_the_log_as_9200()
    {
        using var harness = CreateHarness();
        harness.Users.SignIn("ops");

        harness.Log.Record("DeleteDocument", "customer/42", succeeded: true, "deleted", StudioCapability.DeleteDocuments);

        var entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.User.Should().Be("ops");
        entry.Action.Should().Be("DeleteDocument");
        entry.Target.Should().Be("customer/42");
        entry.Succeeded.Should().BeTrue();
        entry.Message.Should().Be("deleted");
        entry.Capability.Should().Be(nameof(StudioCapability.DeleteDocuments));

        var logged = harness.Captured.Entries.Should().ContainSingle().Which;
        logged.EventId.Id.Should().Be(9200);
        logged.Level.Should().Be(LogLevel.Information);
        logged.Message.Should().Contain("ops").And.Contain("DeleteDocument").And.Contain("customer/42");
    }

    /// <summary>
    /// Written on failure as well as on success. An audit that only records what worked cannot answer the
    /// question people actually ask after an incident, which is what someone tried.
    /// </summary>
    [Fact]
    public void A_failed_action_is_recorded_too_as_9201()
    {
        using var harness = CreateHarness();
        harness.Users.SignIn("viewer");

        harness.Log.Record("ArchiveStream", "order-1", succeeded: false, "the stream was already archived");

        harness.Ring.GetLatest().Should().ContainSingle().Which.Succeeded.Should().BeFalse();
        harness.Captured.Entries.Should().ContainSingle().Which.EventId.Id.Should().Be(9201);
    }

    [Fact]
    public void An_unauthenticated_circuit_is_recorded_as_anonymous()
    {
        using var harness = CreateHarness();

        harness.Log.Record("RunSql", "select 1", succeeded: true);

        harness.Ring.GetLatest().Should().ContainSingle().Which.User.Should().Be("anonymous");
    }

    [Fact]
    public void A_capability_refusal_is_recorded_and_logged_as_9202()
    {
        using var harness = CreateHarness();
        harness.Users.SignIn("viewer");

        var denial = new StudioCapabilityDeniedException(StudioCapability.EditDocuments, CapabilityDenialReason.Disabled);
        harness.Log.RecordCapabilityDenied(denial, "EditDocument", "customer/42");

        harness.Ring.GetLatest().Should().ContainSingle().Which.Succeeded.Should().BeFalse();

        var warning = harness.Captured.Entries.Should().ContainSingle(x => x.EventId.Id == 9202).Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("MartenStudioOptions.Capabilities.EditDocuments");
    }

    /// <summary>
    /// The refused scope is named in the log and never to the visitor: whoever administers the process
    /// has to be able to reconstruct the refusal, and the person refused must not learn which stores,
    /// databases and tenants exist.
    /// </summary>
    [Fact]
    public void A_scope_refusal_is_logged_as_9203_and_names_the_scope()
    {
        using var harness = CreateHarness();
        harness.Users.SignIn("viewer");

        harness.Log.RecordScopeDenied(new StudioScope("invoicing", "srv/db", "acme"), "store-policy", "OpenStore", "invoicing");

        var warning = harness.Captured.Entries.Should().ContainSingle(x => x.EventId.Id == 9203).Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Message.Should().Contain("invoicing").And.Contain("srv/db").And.Contain("acme").And.Contain("store-policy");

        harness.Ring.GetLatest().Should().ContainSingle().Which.StoreKey.Should().Be("invoicing");
    }

    [Fact]
    public void The_ring_is_newest_first_and_bounded()
    {
        var ring = new StudioActionLogService();

        for (var i = 0; i < StudioActionLogService.MaxEntries + 25; i++)
        {
            ring.Record(new StudioActionLogEntry(
                DateTimeOffset.UnixEpoch.AddSeconds(i), "ops", "default", "db", null,
                "Action" + i, "target", true, null, null));
        }

        var latest = ring.GetLatest();

        latest.Should().HaveCount(StudioActionLogService.MaxEntries);
        latest[0].Action.Should().Be("Action" + (StudioActionLogService.MaxEntries + 24), "newest first");
        latest.Should().NotContain(x => x.Action == "Action0", "the oldest fall out");
    }

    [Fact]
    public void GetLatest_clamps_what_it_is_asked_for()
    {
        var ring = new StudioActionLogService();
        for (var i = 0; i < 10; i++)
        {
            ring.Record(new StudioActionLogEntry(
                DateTimeOffset.UnixEpoch, "ops", "default", "db", null, "Action" + i, "target", true, null, null));
        }

        ring.GetLatest(0).Should().HaveCount(1);
        ring.GetLatest(3).Should().HaveCount(3);
        ring.GetLatest(int.MaxValue).Should().HaveCount(10);
    }

    /// <summary>
    /// All twelve ids are declared from the first packet that logs any of them, so the numbers are
    /// reserved rather than assigned in the order features happen to land.
    /// </summary>
    [Fact]
    public void Every_reserved_event_id_exists_exactly_once()
    {
        var declared = typeof(StudioLog)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(x => x.GetCustomAttributes(typeof(LoggerMessageAttribute), inherit: false).FirstOrDefault())
            .OfType<LoggerMessageAttribute>()
            .Select(x => x.EventId)
            .Order()
            .ToArray();

        declared.Should().Equal(9200, 9201, 9202, 9203, 9204, 9205, 9206, 9207, 9208, 9209, 9210, 9211);
    }
}
