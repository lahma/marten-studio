using System.Runtime.CompilerServices;

using KStudio.Internal;

namespace KStudio;

/// <summary>Lists a Wolverine application's Kafka clusters and topics in KStudio (D43).</summary>
public static class KafkaWolverineExtensions
{
    /// <summary>
    /// Lists the Kafka clusters and topics of this application's Wolverine configuration in KStudio. It reads the built <c>WolverineOptions</c> once,
    /// when KStudio first needs its clusters, and starts, connects and writes nothing: no message store, no handler graph, and none of Wolverine's builder
    /// callbacks. It adds no redaction rule, so declare rules for every topic that carries personal data.
    /// </summary>
    /// <param name="options">KStudio's options: <c>services.AddKafkaStudio(k =&gt; k.UseWolverineKafka())</c>.</param>
    /// <param name="configure">How the clusters are listed.</param>
    /// <returns>These options.</returns>
    /// <exception cref="InvalidOperationException">KStudio.Core is not the build this KStudio.Wolverine was packed with.</exception>
    /// <exception cref="ArgumentException">The cluster key is not a valid one.</exception>
    public static KafkaEngineOptions UseWolverineKafka(this KafkaEngineOptions options, Action<KafkaWolverineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Names only KafkaEngineOptions, which is public: everything of KStudio.Core's internals is behind the guard (KStudioWolverineVersionGuardTests).
        KStudioWolverineVersionGuard.Ensure();

        KafkaWolverineOptions settings = new();
        configure?.Invoke(settings);

        return AddSource(options, settings);
    }

    /// <summary>The body of <see cref="UseWolverineKafka" />, once KStudio.Core is known to be the right build.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static KafkaEngineOptions AddSource(KafkaEngineOptions options, KafkaWolverineOptions settings)
    {
        WolverineSourceSettings snapshot = WolverineSourceSettings.Take(settings);

        options.ClusterSources.Add(provider => new WolverineClusterSource(provider, snapshot));
        return options;
    }
}
