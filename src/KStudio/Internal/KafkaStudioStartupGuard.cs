using KStudio.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Settings;

namespace KStudio.Internal;

/// <summary>
/// Refuses to start a host that cannot serve KStudio safely (D34, D35): one that registered no studio, and one that enabled a Kafka capability
/// that no policy answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered at <c>AddKafkaStudio()</c> time, checked at host start.</b> A refusal at registration would break order independence with the
/// studio's own <c>Add*Studio()</c>; there is no <c>MapKafkaStudio()</c> to refuse at, and the host studio's map call must not name KStudio. The
/// studio's own endpoint guard is the same mechanism (<c>StudioEndpointAuthorizationGuard</c>).
/// </para>
/// <para>
/// <b>No studio.</b> <c>AddPostgresDiagnostics()</c> alone registers no studio either, so it is refused the same way.
/// </para>
/// <para>
/// <b>A capability with no policy.</b> Kafka records are personal data across every topic the principal can read, and the cluster policy is the
/// only per-topic boundary the studio has. A capability turned on with the host's master switch off and both Kafka policies blank is the
/// "I forgot" state D5 refuses for the endpoint. There is no escape hatch: a development host states a one-line policy
/// (<c>p.RequireAssertion(_ =&gt; true)</c>), which is as greppable and deliberate as <c>AllowAnonymous()</c>.
/// </para>
/// </remarks>
/// <param name="provider">The application's services.</param>
internal sealed class KafkaStudioStartupGuard(IServiceProvider provider) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (provider.GetService<StudioRegistrationMarker>() is null)
        {
            throw new InvalidOperationException(KafkaStudioText.NoStudio);
        }

        KafkaStudioOptions options = provider.GetRequiredService<IOptions<KafkaStudioOptions>>().Value;
        IStudioSettings settings = provider.GetRequiredService<IStudioSettings>();
        IReadOnlyList<KafkaCapability> configured = KafkaCapabilityGuard.ConfiguredIn(options.Capabilities);

        if (configured.Count > 0
            && !settings.ReadOnly
            && string.IsNullOrWhiteSpace(options.ClusterAuthorizationPolicy)
            && string.IsNullOrWhiteSpace(options.CapabilityAuthorizationPolicy))
        {
            throw new InvalidOperationException(
                KafkaStudioText.CapabilityWithoutPolicy(string.Join(", ", configured), settings.ReadOnlyOption()));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
