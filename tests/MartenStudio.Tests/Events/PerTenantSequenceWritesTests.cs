using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Events;

/// <summary>
/// SEC1-fix4: on a store whose events are numbered per tenant (<c>UseTenantPartitionedEvents</c>), below Marten 9.51 Skip
/// event and Rewind subscription are refused in the service before anything is read, and audited - Marten skips by
/// sequence alone, and the daemon rewinds to one sequence for every tenant. From 9.51 each is one tenant's, and is refused
/// only without a tenant. On a store with one sequence, neither is refused that way at any version.
/// </summary>
/// <remarks>
/// <para>No database: each refusal happens before a connection would open, and this host does not resolve.</para>
/// <para>
/// <b>Version-aware, at both ends.</b> Which branch runs is the light-up probe's answer for the Marten this run resolved
/// (<c>RuntimeFeature.TenantEventSkipping</c>, <c>TenantSubscriptionRewind</c>), and CI runs both: the floor proves the
/// refusal stands where Marten would skip or rewind every tenant, the latest that a tenant's write gets past it and a
/// tenant-less one does not.
/// </para>
/// </remarks>
public class PerTenantSequenceWritesTests
{
    private const string Unreachable =
        "Host=sec1-fix4-per-tenant-writes.invalid;Database=none;Username=none;Password=none;Timeout=2";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool TenantSkipping => RuntimeFeature.TenantEventSkipping.Probe() == true;

    private static bool TenantRewind => RuntimeFeature.TenantSubscriptionRewind.Probe() == true;

    [Fact]
    public async Task Below_Marten_9_51_skip_is_refused_and_audited_on_a_store_that_numbers_events_per_tenant()
    {
        await using Harness harness = await Harness.CreateAsync(perTenantSequences: true);

        Func<Task> skip = () => harness.Events.SkipEventAsync(harness.Scope with { TenantId = "acme" }, 42, Token);

        if (TenantSkipping)
        {
            // 9.51+: the tenant's skip gets past every refusal the store's numbering makes, and fails only where this
            // unreachable host makes it - at the connection the event's proof opens.
            Exception refused = (await skip.Should().ThrowAsync<Exception>()).Which;
            refused.Message.Should().NotBe(StreamCompactionRules.SkipNeedsOneSequence).And.NotBe(StreamCompactionRules.SkipNeedsTenant);
            return;
        }

        (await skip.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.SkipEventAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);
        entry.Capability.Should().Be(nameof(StudioCapability.ManageDeadLetters));
    }

    /// <summary>
    /// Without a tenant the skip is refused at every version: below 9.51 for the numbering, from 9.51 because a sequence
    /// alone names an event in every tenant - audited either way.
    /// </summary>
    [Fact]
    public async Task Skip_with_no_tenant_is_refused_and_audited_at_every_version()
    {
        await using Harness harness = await Harness.CreateAsync(perTenantSequences: true);

        string expected = TenantSkipping ? StreamCompactionRules.SkipNeedsTenant : StreamCompactionRules.SkipNeedsOneSequence;

        Func<Task> skip = () => harness.Events.SkipEventAsync(harness.Scope, 42, Token);

        (await skip.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(expected);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.SkipEventAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(expected);
    }

    [Fact]
    public async Task Below_Marten_9_51_rewind_is_refused_and_audited_on_a_store_that_numbers_events_per_tenant()
    {
        await using Harness harness = await Harness.CreateAsync(perTenantSequences: true);

        Func<Task> rewind = () => harness.Events.RewindSubscriptionAsync(harness.Scope with { TenantId = "acme" }, "Poison", 5, Token);

        if (TenantRewind)
        {
            // 9.51+ with JasperFx 2.91: the tenant's rewind gets past the numbering's refusal, and is refused for what
            // the store does not have - no async projection of that name - before any connection or daemon is asked.
            (await rewind.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Be(
                EventDataService.UnknownAsyncProjection("Poison").Message);
            harness.Ring.GetLatest().Should().ContainSingle().Which.Succeeded.Should().BeFalse();
            return;
        }

        (await rewind.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(StreamCompactionRules.RewindNeedsOneSequence);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.RewindSubscriptionAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(StreamCompactionRules.RewindNeedsOneSequence);
        entry.Capability.Should().Be(nameof(StudioCapability.ManageDeadLetters));
    }

    /// <summary>The store-global rewind - no tenant - is refused at every version, audited.</summary>
    [Fact]
    public async Task Rewind_with_no_tenant_is_refused_and_audited_at_every_version()
    {
        await using Harness harness = await Harness.CreateAsync(perTenantSequences: true);

        string expected = TenantRewind ? StreamCompactionRules.RewindNeedsTenant : StreamCompactionRules.RewindNeedsOneSequence;

        Func<Task> rewind = () => harness.Events.RewindSubscriptionAsync(harness.Scope, "Poison", 5, Token);

        (await rewind.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(expected);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.RewindSubscriptionAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(expected);
        entry.Capability.Should().Be(nameof(StudioCapability.ManageDeadLetters));
    }

    /// <summary>The control: on a conjoined store with one sequence neither write is refused for its numbering.</summary>
    [Fact]
    public async Task On_a_store_with_one_sequence_neither_is_refused_for_its_numbering()
    {
        await using Harness harness = await Harness.CreateAsync(perTenantSequences: false);

        Func<Task> skip = () => harness.Events.SkipEventAsync(harness.Scope with { TenantId = "acme" }, 42, Token);
        Func<Task> rewind = () => harness.Events.RewindSubscriptionAsync(harness.Scope, "Poison", 5, Token);

        (await skip.Should().ThrowAsync<Exception>()).Which.Message.Should().NotBe(StreamCompactionRules.SkipNeedsOneSequence)
            .And.NotBe(StreamCompactionRules.SkipNeedsTenant);
        (await rewind.Should().ThrowAsync<Exception>()).Which.Message.Should().NotBe(StreamCompactionRules.RewindNeedsOneSequence)
            .And.NotBe(StreamCompactionRules.RewindNeedsTenant);
    }

    /// <summary>A studio over a conjoined event store that is never connected to.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, StudioScope studioScope)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Scope = studioScope;
        }

        public IEventDataService Events => scope.ServiceProvider.GetRequiredService<IEventDataService>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public StudioScope Scope { get; }

        public static async Task<Harness> CreateAsync(bool perTenantSequences)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var services = new ServiceCollection();
            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddAuthorization();
            services.AddMarten(options =>
            {
                options.Connection(Unreachable);
                options.DatabaseSchemaName = "sec1_fix4_writes";
                options.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
                options.Events.EnableEventSkippingInProjectionsOrSubscriptions = true;
                options.Events.AddEventType<DeadLetterVisibilityTests.NothingHappened>();

                if (perTenantSequences)
                {
                    options.Events.UseTenantPartitionedEvents = true;
                    options.Events.AppendMode = JasperFx.Events.EventAppendMode.QuickWithServerTimestamps;
                }
            });
            services.AddMartenStudio(static options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.DiscoverTenantIds = false;
                options.KnownTenantIds.Add("acme");
                options.QueryTimeout = TimeSpan.FromSeconds(5);
            });
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();
            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return new Harness(provider, new StudioScope(MartenStoreRegistry.DefaultStoreKey, databases[0].Id.Identity, null));
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
