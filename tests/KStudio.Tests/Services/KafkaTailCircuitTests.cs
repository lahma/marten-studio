using KStudio.Services;

using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Tests.Services;

/// <summary>The module's circuit handler (AGENTS.md D46): a no-op on a circuit with no tail, a relay to the pages that registered, and added by <c>AddKafkaStudio</c> only.</summary>
public class KafkaTailCircuitTests
{
    [Fact]
    public void A_circuit_with_no_tail_page_does_nothing_and_finishes_at_once()
    {
        KafkaTailCircuitRegistry registry = new();

        registry.ConnectionChangedAsync(up: false).IsCompletedSuccessfully.Should().BeTrue();
        registry.ConnectionChangedAsync(up: true).IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task The_handler_tells_every_registered_page_in_order_until_it_unregisters()
    {
        KafkaTailCircuitRegistry registry = new();
        KafkaTailCircuitHandler handler = new(registry);
        List<string> told = [];

        IDisposable first = registry.Register(up =>
        {
            told.Add("a:" + up);
            return Task.CompletedTask;
        });

        using IDisposable second = registry.Register(up =>
        {
            told.Add("b:" + up);
            return Task.CompletedTask;
        });

        await handler.OnConnectionDownAsync(null!, CancellationToken.None);
        await handler.OnConnectionUpAsync(null!, CancellationToken.None);
        first.Dispose();
        await handler.OnConnectionDownAsync(null!, CancellationToken.None);

        told.Should().Equal("a:False", "b:False", "a:True", "b:True", "b:False");
    }

    [Fact]
    public void The_module_registers_its_handler_once_and_both_are_the_circuits_own()
    {
        ServiceCollection services = [];
        services.AddKafkaStudio(static _ => { });
        services.AddKafkaStudio(static _ => { });

        services.Where(static x => x.ServiceType == typeof(CircuitHandler) && x.ImplementationType == typeof(KafkaTailCircuitHandler))
            .Should().ContainSingle().Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Single(static x => x.ServiceType == typeof(KafkaTailCircuitRegistry)).Lifetime.Should().Be(ServiceLifetime.Scoped);
    }
}
