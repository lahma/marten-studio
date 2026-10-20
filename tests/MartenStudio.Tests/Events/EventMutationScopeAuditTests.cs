using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Services;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// SEC1 L4 for the event writes: a write whose scope the policies passed but the resolver cannot find - no such store,
/// no such database, a store that will not build - is audited as failed before it is thrown, as every other refusal of
/// <c>EventDataService.MutateAsync</c> is (hard rule 5).
/// </summary>
/// <remarks>
/// Before, those three escaped the write with nothing in the ring: an audit that only records what got as far as the
/// operation cannot say what somebody tried. They still fail closed - the exception is the caller's, as it was. No
/// database: every one of them is refused before a connection would open.
/// </remarks>
public class EventMutationScopeAuditTests
{
    private const string Unreachable =
        "Host=sec1-event-mutation-audit.invalid;Database=none;Username=none;Password=none;Timeout=2";

    private const string StreamId = "44444444-5555-4666-8777-888888888888";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("nosuchstore", "", "No Marten store is registered under 'nosuchstore'")]
    [InlineData("default", "nosuchdb", "has no database with identity 'nosuchdb'")]
    public async Task A_write_to_a_scope_that_names_nothing_here_is_audited_as_failed_and_still_refused(
        string storeKey,
        string databaseId,
        string said)
    {
        await using Harness harness = await Harness.CreateAsync();

        Func<Task> archive = () => harness.Events.ArchiveStreamAsync(new StudioScope(storeKey, databaseId, null), StreamId, Token);

        (await archive.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain(said);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle("refused before the operation, and recorded").Subject;
        entry.Action.Should().Be(EventDataService.ArchiveStreamAction);
        entry.Target.Should().Be(StreamId);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Contain(said);
        entry.Capability.Should().Be(nameof(StudioCapability.ArchiveStreams));
        entry.StoreKey.Should().Be(storeKey, "recorded against the scope that was asked for");
    }

    [Fact]
    public async Task A_write_to_a_store_that_will_not_build_is_audited_as_failed_and_still_refused()
    {
        await using Harness harness = await Harness.CreateAsync(static services =>
            services.AddSingleton<MartenStoreRegistryTest.IBrokenStore>(
                static _ => throw new InvalidOperationException("no connection string was configured")));

        Func<Task> compact = () => harness.Events.CompactStreamAsync(
            new StudioScope(nameof(MartenStoreRegistryTest.IBrokenStore), string.Empty, null), StreamId, StreamId, 3, Token);

        (await compact.Should().ThrowAsync<StudioStoreUnavailableException>()).Which.Message.Should().Contain("could not be built");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.CompactStreamAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Contain("no connection string was configured");
        entry.Capability.Should().Be(nameof(StudioCapability.CompactStreams));
    }

    /// <summary>
    /// The control: a scope the resolver finds is not refused here, and reaches the operation - which, against this
    /// unreachable host, fails on its read of the stream, not in the resolver.
    /// </summary>
    [Fact]
    public async Task A_scope_the_resolver_finds_reaches_the_operation()
    {
        await using Harness harness = await Harness.CreateAsync();

        Func<Task> archive = () => harness.Events.ArchiveStreamAsync(harness.Scope, StreamId, Token);

        (await archive.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotBe(
            EventDataService.ArchiveTenantRequiredMessage, "a store whose events are not conjoined needs no tenant to archive from");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().NotContain("has no database with identity");
    }

    /// <summary>
    /// SEC1 (a): on a conjoined event store with "All tenants" selected, Marten's archive runs as <c>*DEFAULT*</c> and
    /// archives the default tenant's stream of the id, or nothing - and was reported as archived. It is refused, before
    /// anything is read, and audited; with a tenant selected the same call gets past the refusal.
    /// </summary>
    [Fact]
    public async Task An_archive_on_a_conjoined_event_store_with_no_tenant_selected_is_refused_and_audited()
    {
        await using Harness harness = await Harness.CreateAsync(conjoined: true);

        Func<Task> archive = () => harness.Events.ArchiveStreamAsync(harness.Scope, StreamId, Token);

        (await archive.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be(EventDataService.ArchiveTenantRequiredMessage);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.ArchiveStreamAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(EventDataService.ArchiveTenantRequiredMessage);
        entry.TenantId.Should().BeNull();

        Func<Task> scoped = () => harness.Events.ArchiveStreamAsync(harness.Scope with { TenantId = "acme" }, StreamId, Token);

        (await scoped.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().NotBe(EventDataService.ArchiveTenantRequiredMessage, "with a tenant selected it reaches the stream read, which this host fails");
    }

    /// <summary>A studio over a store with an active event store that is never connected to.</summary>
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

        public static async Task<Harness> CreateAsync(Action<IServiceCollection>? configure = null, bool conjoined = false)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var services = new ServiceCollection();
            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddAuthorization();
            services.AddMarten(options =>
            {
                options.Connection(Unreachable);
                options.DatabaseSchemaName = "sec1_mutation_audit";
                options.Events.AddEventType<DeadLetterVisibilityTests.NothingHappened>();

                if (conjoined)
                {
                    options.Events.TenancyStyle = JasperFx.MultiTenancy.TenancyStyle.Conjoined;
                }
            });

            configure?.Invoke(services);

            services.AddMartenStudio(static options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.QueryTimeout = TimeSpan.FromSeconds(5);
                options.DiscoverTenantIds = false;
                options.KnownTenantIds.Add("acme");
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
