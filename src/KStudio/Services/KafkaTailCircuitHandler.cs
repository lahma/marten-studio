using Microsoft.AspNetCore.Components.Server.Circuits;

namespace KStudio.Services;

/// <summary>
/// Pauses a live tail whose circuit lost its connection and lets it go on when the connection returns (D46). A tail whose browser vanished would otherwise read on and
/// hold its owner's scan slot until Blazor tears the circuit down, minutes later, refusing that visitor's other tabs. Added by <c>AddKafkaStudio</c> only; on a circuit
/// that holds no tail it does nothing (the registry is empty). All the decisions are the page's, behind <see cref="KafkaTailCircuitRegistry.ConnectionChangedAsync" />.
/// </summary>
/// <param name="registry">The circuit's registry of tail pages.</param>
internal sealed class KafkaTailCircuitHandler(KafkaTailCircuitRegistry registry) : CircuitHandler
{
    /// <inheritdoc />
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) => registry.ConnectionChangedAsync(up: false);

    /// <inheritdoc />
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken) => registry.ConnectionChangedAsync(up: true);
}
