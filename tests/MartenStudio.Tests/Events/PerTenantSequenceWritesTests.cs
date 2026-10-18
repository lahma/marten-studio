using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Events;

/// <summary>
/// SEC1-fix4: on a store whose events are numbered per tenant (<c>UseTenantPartitionedEvents</c>), Skip event and Rewind
/// subscription are refused in the service before anything is read, and audited - Marten skips by sequence alone, and
/// the daemon rewinds to one sequence for every tenant. On a store with one sequence, neither is refused that way.
/// </summary>
/// <remarks>No database: each refusal happens before a connection would open, and this host does not resolve.</remarks>
public class PerTenantSequenceWritesTests
{
    private const string Unreachable =
        "Host=sec1-fix4-per-tenant-writes.invalid;Database=none;Username=none;Password=none;Timeout=2";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Skip_is_refused_and_audited_on_a_store_that_numbers_events_per_tenant()
    {
        await using Harness harness = await Harness.CreateAsync(perTenantSequences: true);

        Func<Task> skip = () => harness.Events.SkipEventAsync(harness.Scope with { TenantId = "acme" }, 42, Token);

        (await skip.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.SkipEventAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(StreamCompactionRules.SkipNeedsOneSequence);
        entry.Capability.Should().Be(nameof(StudioCapability.ManageDeadLetters));
    }

    [Fact]
    public async Task Rewind_is_refused_and_audited_on_a_store_that_numbers_events_per_tenant()
    {
        await using Harness harness = await Harness.CreateAsync(perTenantSequences: true);

        Func<Task> rewind = () => harness.Events.RewindSubscriptionAsync(harness.Scope, "Poison", 5, Token);

        (await rewind.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(StreamCompactionRules.RewindNeedsOneSequence);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.RewindSubscriptionAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(StreamCompactionRules.RewindNeedsOneSequence);
        entry.Capability.Should().Be(nameof(StudioCapability.ManageDeadLetters));
    }

    /// <summary>The control: on a conjoined store with one sequence neither write is refused for its numbering.</summary>
    [Fact]
    public async Task On_a_store_with_one_sequence_neither_is_refused_for_its_numbering()
    {
        await using Harness harness = await Harness.CreateAsync(perTenantSequences: false);

        Func<Task> skip = () => harness.Events.SkipEventAsync(harness.Scope with { TenantId = "acme" }, 42, Token);
        Func<Task> rewind = () => harness.Events.RewindSubscriptionAsync(harness.Scope, "Poison", 5, Token);

        (await skip.Should().ThrowAsync<Exception>()).Which.Message.Should().NotBe(StreamCompactionRules.SkipNeedsOneSequence);
        (await rewind.Should().ThrowAsync<Exception>()).Which.Message.Should().NotBe(StreamCompactionRules.RewindNeedsOneSequence);
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
