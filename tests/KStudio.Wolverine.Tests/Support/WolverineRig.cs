using Confluent.Kafka;

using KStudio;
using KStudio.Core.Clusters;
using KStudio.Internal;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Wolverine;
using Wolverine.Kafka;
using Wolverine.Kafka.Internals;

namespace KStudioWolverine.Tests.Support;

/// <summary>A <see cref="WolverineOptions" /> built in a test, and the adapter run over it the way the registry runs it.</summary>
internal static class WolverineRig
{
    /// <summary>Options for an application named <paramref name="service" />, with nothing configured.</summary>
    public static WolverineOptions Options(string service = "orders-app") => new() { ServiceName = service };

    /// <summary>The default Kafka transport of <paramref name="options" />, configured with <c>UseKafka</c>.</summary>
    public static KafkaTransport Default(WolverineOptions options, string bootstrap = "broker:9092")
    {
        options.UseKafka(bootstrap);
        return options.Transports.OfType<KafkaTransport>().Single(static x => x.Protocol == "kafka");
    }

    /// <summary>A named broker's transport, configured with <c>AddNamedKafkaBroker</c>.</summary>
    public static KafkaTransport Named(WolverineOptions options, string name, string bootstrap = "named:9092")
    {
        options.AddNamedKafkaBroker(new BrokerName(name), bootstrap);
        return options.Transports.OfType<KafkaTransport>().Single(x => x.Protocol == name);
    }

    /// <summary>Marks a topic a listener, as <c>ListenToKafkaTopic</c> does when Wolverine compiles it. The fluent configuration is deferred until then.</summary>
    public static KafkaTopic Listen(KafkaTransport transport, string topic)
    {
        KafkaTopic endpoint = transport.Topics[topic];
        endpoint.IsListener = true;
        return endpoint;
    }

    /// <summary>Marks a topic a sender of <typeparamref name="T" />.</summary>
    public static KafkaTopic Send<T>(KafkaTransport transport, string topic)
    {
        KafkaTopic endpoint = transport.Topics[topic];
        endpoint.Subscriptions.Add(Wolverine.Runtime.Routing.Subscription.ForType(typeof(T)));
        return endpoint;
    }

    /// <summary>Sets a topic's own consumer configuration, whose setter is internal to Wolverine.</summary>
    public static void OwnConsumerConfig(KafkaTopic topic, ConsumerConfig config) =>
        typeof(KafkaTopic).GetProperty(nameof(KafkaTopic.ConsumerConfig))!.SetValue(topic, config);

    /// <summary>Sets a topic's own producer configuration, whose setter is internal to Wolverine.</summary>
    public static void OwnProducerConfig(KafkaTopic topic, ProducerConfig config) =>
        typeof(KafkaTopic).GetProperty(nameof(KafkaTopic.ProducerConfig))!.SetValue(topic, config);

    /// <summary>What Wolverine's runtime does to a tenant when it connects the parent: clones the parent's configuration onto its child transport (<c>KafkaTenant.Compile</c>).</summary>
    public static void ConnectTenants(WolverineOptions options, KafkaTransport parent)
    {
        object cache = typeof(KafkaTransport).GetProperty("Tenants", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(parent)!;

        foreach (object tenant in (System.Collections.IEnumerable) cache)
        {
            tenant.GetType().GetMethod("Compile")!.Invoke(tenant, [parent, options]);
        }
    }

    /// <summary>Runs the adapter over <paramref name="options" /> as the registry does.</summary>
    public static KafkaClusterContribution Contribute(WolverineOptions? options, Action<KafkaWolverineOptions>? configure = null, IHostApplicationLifetime? lifetime = null)
    {
        ServiceCollection services = new();

        if (options is not null)
        {
            services.AddSingleton(options);
        }

        if (lifetime is not null)
        {
            services.AddSingleton(lifetime);
        }

        KafkaWolverineOptions settings = new();
        configure?.Invoke(settings);

        return new WolverineClusterSource(services.BuildServiceProvider(), WolverineSourceSettings.Take(settings)).Contribute();
    }

    /// <summary>The registry for <paramref name="options" />, with the adapter as its source, through the public registration.</summary>
    /// <param name="options">The Wolverine configuration.</param>
    /// <param name="host">Adds the host's own clusters.</param>
    /// <param name="configure">The adapter's options.</param>
    /// <param name="lifetime">The host's lifetime, when the application has one.</param>
    /// <param name="asked">Counts every time the registry asks the adapter.</param>
    public static KafkaClusterRegistry Registry(
        WolverineOptions options,
        Action<KafkaEngineOptions>? host = null,
        Action<KafkaWolverineOptions>? configure = null,
        IHostApplicationLifetime? lifetime = null,
        Action? asked = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(options);

        if (lifetime is not null)
        {
            services.AddSingleton(lifetime);
        }

        KafkaEngineOptions engine = new();
        host?.Invoke(engine);
        engine.UseWolverineKafka(configure);

        if (asked is not null)
        {
            Func<IServiceProvider, IKafkaClusterSource> inner = engine.ClusterSources[^1];
            engine.ClusterSources[^1] = provider => new CountingSource(inner(provider), asked);
        }

        return new KafkaClusterRegistry(engine, services.BuildServiceProvider());
    }

    /// <summary>Every key and value of a configuration, ordinal by key.</summary>
    public static string[] Pairs(ClientConfig config) =>
        [.. config.OrderBy(static x => x.Key, StringComparer.Ordinal).Select(static x => x.Key + "=" + x.Value)];
}

/// <summary>A host lifetime whose <c>ApplicationStarted</c> a test fires.</summary>
internal sealed class FakeLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource started = new();

    public CancellationToken ApplicationStarted => started.Token;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    /// <summary>The application has started.</summary>
    public void Start() => started.Cancel();

    public void StopApplication()
    {
    }

    public void Dispose() => started.Dispose();
}

/// <summary>Counts how often the registry asks a source.</summary>
internal sealed class CountingSource(IKafkaClusterSource inner, Action asked) : IKafkaClusterSource
{
    public string Name => inner.Name;

    public KafkaClusterContribution Contribute()
    {
        asked();
        return inner.Contribute();
    }
}
