using DataStudio.Services.Settings;
using DataStudio.Kafka.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DataStudio.Kafka.Internal;

/// <summary>
/// Refuses to start a host that cannot serve the Kafka module safely (D34, D35): one that enabled a Kafka capability
/// that no policy answers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registered at <c>AddKafka()</c> time, checked at host start.</b> A refusal at registration would break order independence with the
/// host's own registrations; there is no map call of the module's own to refuse at, and the shell's map call must not name Kafka. The
/// shell's own endpoint guard is the same mechanism (<c>StudioEndpointAuthorizationGuard</c>). The module is added to the shell's builder, so
/// it cannot be registered without a shell.
/// </para>
/// <para>
/// <b>A capability with no policy.</b> Kafka records are personal data across every topic the principal can read, and the cluster policy is the
/// only per-topic boundary the studio has. A capability turned on with the host's master switch off and both Kafka policies blank is the
/// "I forgot" state D5 refuses for the endpoint. There is no escape hatch: a development host states a one-line policy
/// (<c>p.RequireAssertion(_ =&gt; true)</c>), which is as greppable and deliberate as <c>AllowAnonymous()</c>.
/// </para>
/// </remarks>
/// <param name="provider">The application's services.</param>
internal sealed class KafkaStartupGuard(IServiceProvider provider) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        KafkaOptions options = provider.GetRequiredService<IOptions<KafkaOptions>>().Value;
        IShellSettings settings = provider.GetRequiredService<IShellSettings>();
        IReadOnlyList<KafkaCapability> configured = KafkaCapabilityGuard.ConfiguredIn(options.Capabilities);

        if (configured.Count > 0
            && !settings.ReadOnly
            && string.IsNullOrWhiteSpace(options.ClusterAuthorizationPolicy)
            && string.IsNullOrWhiteSpace(options.CapabilityAuthorizationPolicy))
        {
            throw new InvalidOperationException(
                KafkaText.CapabilityWithoutPolicy(string.Join(", ", configured), settings.ReadOnlyOptionName));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
