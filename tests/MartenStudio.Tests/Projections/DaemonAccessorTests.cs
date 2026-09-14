using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;

using JasperFxDaemonMode = JasperFx.Events.Daemon.DaemonMode;
using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// Which coordinator the studio asks for, and what happens when nobody registered one.
/// </summary>
/// <remarks>
/// Descriptors only. Nothing is built and no connection is opened - building an <c>IDocumentStore</c>
/// compiles the store and reaches for Postgres, and the question here is about the shape of the
/// registration rather than about a database.
/// </remarks>
public class DaemonAccessorTests
{
    private const string DummyConnectionString = "Host=localhost;Database=marten_studio_never_opened";

    /// <summary>A marker interface, exactly as a host would declare one for an ancillary store.</summary>
    public interface ISampleAncillaryStore : IDocumentStore;

    [Fact]
    public void The_default_store_is_asked_for_the_non_generic_coordinator()
    {
        var registration = new MartenStoreRegistration("default", "Default", typeof(IDocumentStore));

        DaemonAccessor.CoordinatorServiceType(registration).Should().Be<MartenCoordinator>();
    }

    /// <summary>
    /// U15: <c>AddMartenStore&lt;T&gt;().AddAsyncDaemon()</c> registers <em>only</em>
    /// <c>IProjectionCoordinator&lt;T&gt;</c>. Asking for the non-generic one on an ancillary store's
    /// behalf would hand back the main store's daemon, or nothing.
    /// </summary>
    [Fact]
    public void An_ancillary_store_is_asked_for_the_generic_coordinator_closed_over_its_marker()
    {
        var registration = new MartenStoreRegistration(
            nameof(ISampleAncillaryStore), "Sample Ancillary Store", typeof(ISampleAncillaryStore));

        Type? serviceType = DaemonAccessor.CoordinatorServiceType(registration);

        serviceType.Should().Be(
            typeof(global::Marten.Events.Daemon.Coordination.IProjectionCoordinator<>).MakeGenericType(typeof(ISampleAncillaryStore)));
    }

    [Fact]
    public void The_service_type_the_accessor_asks_for_is_the_one_AddAsyncDaemon_registers()
    {
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString)).AddAsyncDaemon(JasperFxDaemonMode.Solo);

        Type? serviceType = DaemonAccessor.CoordinatorServiceType(
            new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)));

        services.Should().Contain(x => x.ServiceType == serviceType);
    }

    [Fact]
    public void The_generic_service_type_is_the_one_AddMartenStore_registers()
    {
        var services = new ServiceCollection();
        services.AddMartenStore<ISampleAncillaryStore>(x => x.Connection(DummyConnectionString))
            .AddAsyncDaemon(JasperFxDaemonMode.Solo);

        Type? serviceType = DaemonAccessor.CoordinatorServiceType(
            new MartenStoreRegistration(nameof(ISampleAncillaryStore), "Sample", typeof(ISampleAncillaryStore)));

        services.Should().Contain(x => x.ServiceType == serviceType);
        services.Should().NotContain(x => x.ServiceType == typeof(MartenCoordinator),
            "AddMartenStore never registers the non-generic coordinator");
    }

    /// <summary>
    /// No <c>AddAsyncDaemon</c> means no coordinator, which is a value and not a failure (AGENTS.md hard
    /// rule 11).
    /// </summary>
    [Fact]
    public void No_registered_coordinator_resolves_to_nothing_rather_than_throwing()
    {
        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));
        services.AddLogging();

        using ServiceProvider provider = services.BuildServiceProvider();

        var accessor = new DaemonAccessor(
            provider,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DaemonAccessor>>());

        accessor.ResolveCoordinator(new MartenStoreRegistration("default", "Default", typeof(IDocumentStore)))
            .Should().BeNull();
    }

    /// <summary>
    /// A marker type the coordinator's constraints refuse cannot have had a coordinator registered for it
    /// either, so the answer is the same "not hosted here" rather than a <c>MakeGenericType</c> throw.
    /// </summary>
    [Fact]
    public void A_marker_type_that_cannot_close_the_generic_is_answered_with_null()
    {
        var registration = new MartenStoreRegistration("odd", "Odd", typeof(string));

        DaemonAccessor.CoordinatorServiceType(registration).Should().BeNull();
    }

    /// <summary>
    /// The one rule this whole class exists to protect: the studio never builds a daemon of its own.
    /// </summary>
    /// <remarks>
    /// <c>*.razor</c> as well as <c>*.cs</c>: a page's <c>@code</c> block is C# that this scanner could
    /// not see, and half of what the studio does to the daemon is driven from one.
    /// </remarks>
    [Fact]
    public void Nothing_in_the_studio_calls_BuildProjectionDaemonAsync()
    {
        List<string> offenders = [.. StudioSourceFiles().Where(static file => File.ReadLines(file).Any(IsACall))];

        offenders.Should().BeEmpty(
            "a second daemon fights the host's own over the same advisory locks until one hangs (AGENTS.md hard rule 11)");
    }

    /// <summary>
    /// The scanner above strips comments, so it is exactly the kind of rule that can go quietly vacuous.
    /// This is the anti-vacuity half: it must see a synthetic call and must not see one in prose.
    /// </summary>
    [Fact]
    public void The_scanner_sees_a_call_and_ignores_one_in_prose()
    {
        IsACall("        await store.BuildProjectionDaemonAsync();").Should().BeTrue();
        IsACall("    <text>await store.BuildProjectionDaemonAsync();</text>").Should().BeTrue();
        IsACall("// never call store.BuildProjectionDaemonAsync() here").Should().BeFalse();
        IsACall("    * BuildProjectionDaemonAsync( starts a second daemon").Should().BeFalse();

        // And it really is reading the Razor pages, not only the .cs files.
        StudioSourceFiles().Should().Contain(static file => file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Every hand-written source file in the shipped library, generated output excluded.</summary>
    private static IEnumerable<string> StudioSourceFiles()
    {
        string source = Conventions.RepositoryRoot.Combine("src", "MartenStudio");

        return Directory
            .EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(static file =>
                file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(static file =>
                !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether a line really calls it, rather than naming it in a comment. The rule is worth explaining in
    /// prose exactly where it is enforced, and a scanner that cannot tell the two apart would make that
    /// impossible.
    /// </summary>
    private static bool IsACall(string line)
    {
        string trimmed = line.TrimStart();

        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
        {
            return false;
        }

        return trimmed.Contains("BuildProjectionDaemonAsync(", StringComparison.Ordinal);
    }
}
