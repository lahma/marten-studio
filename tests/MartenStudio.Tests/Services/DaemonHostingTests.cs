using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Database;

using Microsoft.Extensions.DependencyInjection;

using JasperFxDaemonMode = JasperFx.Events.Daemon.DaemonMode;
using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-1 S4: <c>DaemonAccessor.HostingOf</c> says where a store's async projections run from configuration alone: the
/// store's options and the container's registrations. It constructs no coordinator and reads no database.
/// </summary>
public class DaemonHostingTests
{
    private const string Connection = "Host=scope1-daemon.invalid;Database=never;Username=u;Password=p";

    private static readonly MartenStoreRegistration Default = new(MartenStoreRegistry.DefaultStoreKey, "Default", typeof(IDocumentStore));

    [Fact]
    public void A_store_an_external_system_runs_is_externally_managed_whatever_the_container_holds()
    {
        using IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(Connection);
            options.Projections.AsyncMode = JasperFxDaemonMode.ExternallyManaged;
            options.Projections.Add<DbOrderFlatProjection>(ProjectionLifecycle.Async);
        });

        ServiceCollection services = new();
        services.AddSingleton<MartenCoordinator>(static _ => throw new InvalidOperationException("never built"));
        using ServiceProvider provider = services.BuildServiceProvider();

        StoreDaemonHosting hosting = DaemonAccessor.HostingOf(Default, store, provider);

        hosting.Kind.Should().Be(StoreDaemonHostingKind.ExternallyManaged);
        hosting.Words.Should().Be("Run by an external system");
    }

    [Fact]
    public void A_store_with_no_async_work_has_nothing_to_host()
    {
        using IDocumentStore store = DocumentStore.For(options => options.Connection(Connection));

        StoreDaemonHosting hosting = DaemonAccessor.HostingOf(Default, store, new ServiceCollection().BuildServiceProvider());

        hosting.Kind.Should().Be(StoreDaemonHostingKind.NoAsyncWork);
        hosting.Words.Should().Be("No async projections");
    }

    /// <summary>
    /// The answer is the registration, never the coordinator: a factory that throws proves nothing was constructed, and the
    /// words name the configured mode and send the reader to Projections for running state.
    /// </summary>
    [Fact]
    public void A_registered_coordinator_is_registered_here_and_is_never_constructed()
    {
        using IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(Connection);
            options.Projections.Add<DbOrderFlatProjection>(ProjectionLifecycle.Async);
            options.Projections.AsyncMode = JasperFxDaemonMode.HotCold;
        });

        int constructed = 0;
        ServiceCollection services = new();
        services.AddSingleton<MartenCoordinator>(_ =>
        {
            constructed++;
            throw new InvalidOperationException("The coordinator must not be built to answer this.");
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        StoreDaemonHosting hosting = DaemonAccessor.HostingOf(Default, store, provider);

        hosting.Kind.Should().Be(StoreDaemonHostingKind.RegisteredHere);
        hosting.Mode.Should().Be(JasperFxDaemonMode.HotCold);
        hosting.Words.Should().Be("A projection coordinator is registered in this process (HotCold); Projections says whether it runs");
        constructed.Should().Be(0);
    }

    [Fact]
    public void Async_work_with_no_registered_coordinator_is_not_hosted_here()
    {
        using IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(Connection);
            options.Projections.Add<DbOrderFlatProjection>(ProjectionLifecycle.Async);
        });

        StoreDaemonHosting hosting = DaemonAccessor.HostingOf(Default, store, new ServiceCollection().BuildServiceProvider());

        hosting.Kind.Should().Be(StoreDaemonHostingKind.NotHostedHere);
        hosting.Words.Should().Be("Not hosted in this process");
    }

    [Fact]
    public void An_ancillary_store_is_asked_for_its_own_coordinator_and_not_the_main_one()
    {
        using IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(Connection);
            options.Projections.Add<DbOrderFlatProjection>(ProjectionLifecycle.Async);
        });

        MartenStoreRegistration ancillary = new(nameof(IScopeOneAncillaryStore), "Ancillary", typeof(IScopeOneAncillaryStore));

        // Only the main store's coordinator is registered: the ancillary store's own is not.
        ServiceCollection services = new();
        services.AddSingleton<MartenCoordinator>(static _ => throw new InvalidOperationException("never built"));
        using ServiceProvider provider = services.BuildServiceProvider();

        DaemonAccessor.HostingOf(ancillary, store, provider).Kind.Should().Be(StoreDaemonHostingKind.NotHostedHere);
    }

    /// <summary>A marker interface, as a host declares one for an ancillary store.</summary>
    public interface IScopeOneAncillaryStore : IDocumentStore;

}
