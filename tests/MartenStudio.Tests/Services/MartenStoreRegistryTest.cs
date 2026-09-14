using System.Reflection;

using Marten;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Services;

/// <summary>
/// The scan that finds this application's Marten stores, and what happens when one will not build.
/// </summary>
public class MartenStoreRegistryTest
{
    /// <summary>Never connected to: only descriptors are inspected, and the store is never used.</summary>
    private const string DummyConnectionString =
        "Host=marten-studio-registry-test.invalid;Database=none;Username=none;Password=none";

    /// <summary>A marker interface, the way <c>AddMartenStore&lt;T&gt;()</c> wants one.</summary>
    public interface IInvoicingStore : IDocumentStore;

    /// <summary>A store whose registration throws when it is resolved.</summary>
    public interface IBrokenStore : IDocumentStore;

    [Fact]
    public void The_hosts_own_store_is_the_default_registration()
    {
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));

        var registrations = new MartenStoreRegistry(services).Registrations(includeAncillaryStores: true);

        registrations.Should().ContainSingle();
        registrations[0].Key.Should().Be("default");
        registrations[0].DisplayName.Should().Be("Default");
        registrations[0].ServiceType.Should().Be<IDocumentStore>();
    }

    /// <summary>
    /// <c>AddMartenStore&lt;T&gt;()</c> registers <c>T</c> and never <c>IDocumentStore</c>, so scanning for
    /// descriptors whose service type is assignable to <c>IDocumentStore</c> is what finds both kinds in
    /// one pass.
    /// </summary>
    [Fact]
    public void An_ancillary_store_is_found_under_its_marker_interface()
    {
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));
        services.AddMartenStore<IInvoicingStore>(x => x.Connection(DummyConnectionString));

        var registrations = new MartenStoreRegistry(services).Registrations(includeAncillaryStores: true);

        registrations.Should().HaveCount(2);
        registrations[0].Key.Should().Be("default", "the host's own store is offered first");
        registrations[1].Key.Should().Be(nameof(IInvoicingStore));
        registrations[1].DisplayName.Should().Be("Invoicing Store");
        registrations[1].ServiceType.Should().Be<IInvoicingStore>();
    }

    [Fact]
    public void IncludeAncillaryStores_false_leaves_only_the_hosts_own_store()
    {
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));
        services.AddMartenStore<IInvoicingStore>(x => x.Connection(DummyConnectionString));

        var registrations = new MartenStoreRegistry(services).Registrations(includeAncillaryStores: false);

        registrations.Should().ContainSingle();
        registrations[0].Key.Should().Be("default");
    }

    /// <summary>
    /// U13: the registry holds the collection and scans it on first use, which for a built application
    /// means after <c>MakeReadOnly()</c>. Enumeration has to keep working there, or the whole lazy-scan
    /// design collapses.
    /// </summary>
    [Fact]
    public void The_scan_still_works_after_the_collection_has_been_made_read_only()
    {
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));
        services.AddMartenStore<IInvoicingStore>(x => x.Connection(DummyConnectionString));

        var registry = new MartenStoreRegistry(services);
        services.MakeReadOnly();

        registry.Registrations(includeAncillaryStores: true).Should().HaveCount(2);
    }

    /// <summary>
    /// Marten registers <c>IProjectionCoordinator&lt;T&gt;</c> as an open generic. Asking
    /// <c>IsAssignableFrom</c> about one answers nonsense, so the scan skips them outright.
    /// </summary>
    [Fact]
    public void Open_generic_registrations_are_not_mistaken_for_stores()
    {
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));
        services.AddMartenStore<IInvoicingStore>(x => x.Connection(DummyConnectionString))
            .AddAsyncDaemon(JasperFx.Events.Daemon.DaemonMode.Solo);

        var registrations = new MartenStoreRegistry(services).Registrations(includeAncillaryStores: true);

        registrations.Should().HaveCount(2);
        registrations.Should().OnlyContain(x => !x.ServiceType.ContainsGenericParameters);
    }

    [Fact]
    public void A_store_that_resolves_is_available()
    {
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));
        var registry = new MartenStoreRegistry(services);

        using var provider = services.BuildServiceProvider();
        var registration = registry.Registrations(includeAncillaryStores: true)[0];

        var availability = registry.TryResolve(registration, provider, out var store);

        availability.IsAvailable.Should().BeTrue();
        availability.Message.Should().BeNull();
        store.Should().NotBeNull();
    }

    /// <summary>
    /// A store the application registered but that will not build is a state the picker draws, not an
    /// exception the page throws: it is offered, disabled, with what it said (plan section 4.8).
    /// </summary>
    [Fact]
    public void A_store_that_will_not_build_is_unavailable_rather_than_absent()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(capture.CreateFactory());
        services.AddSingleton<IBrokenStore>(static _ => throw new InvalidOperationException("no connection string was configured"));

        var registry = new MartenStoreRegistry(services);
        using var provider = services.BuildServiceProvider();
        var registration = registry.Registrations(includeAncillaryStores: true).Single();

        var availability = registry.TryResolve(registration, provider, out var store);

        availability.IsAvailable.Should().BeFalse();
        availability.Message.Should().Contain("no connection string was configured");
        availability.At.Should().NotBeNull();
        store.Should().BeNull();

        capture.Entries.Should().ContainSingle(x => x.EventId.Id == 9210)
            .Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    /// The failure is cached for ten seconds, so a page listing five broken stores does not try to build
    /// each of them on every render - and the cache expires, so a store that comes back is picked up.
    /// </summary>
    [Fact]
    public void A_build_failure_is_cached_for_ten_seconds_and_then_retried()
    {
        var attempts = 0;
        var services = new ServiceCollection();
        services.AddSingleton<IBrokenStore>(_ =>
        {
            attempts++;
            throw new InvalidOperationException("still broken");
        });

        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var registry = new MartenStoreRegistry(services, time);
        using var provider = services.BuildServiceProvider();
        var registration = registry.Registrations(includeAncillaryStores: true).Single();

        registry.TryResolve(registration, provider, out _).IsAvailable.Should().BeFalse();
        registry.TryResolve(registration, provider, out _).IsAvailable.Should().BeFalse();
        attempts.Should().Be(1, "the second call inside the window is answered from the cache");

        time.Advance(TimeSpan.FromSeconds(11));

        registry.TryResolve(registration, provider, out _).IsAvailable.Should().BeFalse();
        attempts.Should().Be(2, "the window has passed, so the store is tried again");
    }

    [Theory]
    [InlineData("IInvoicingStore", "Invoicing Store")]
    [InlineData("InvoicingStore", "Invoicing Store")]
    [InlineData("IDocumentStore", "Document Store")]
    [InlineData("IHTTPStore", "HTTP Store")]
    [InlineData("Store", "Store")]
    public void A_marker_interface_reads_as_words(string typeName, string expected)
    {
        // The derivation is over the type name, so a synthetic type is enough to pin the shapes.
        var type = new FakeNamedType(typeName);

        MartenStoreRegistry.DisplayNameFor(type).Should().Be(expected);
    }

    /// <summary>A <see cref="Type" /> whose only interesting property is its name.</summary>
    private sealed class FakeNamedType(string name) : TypeDelegator(typeof(object))
    {
        public override string Name => name;
    }
}
