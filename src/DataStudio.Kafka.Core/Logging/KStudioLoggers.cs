using Microsoft.Extensions.Logging;

namespace DataStudio.Kafka.Core.Logging;

/// <summary>
/// The engine's loggers, by category (decision 11): one filter on <c>DataStudio.Kafka.Core</c> covers the engine.
/// </summary>
internal sealed class KStudioLoggers(ILoggerFactory factory)
{
    /// <summary>Admin clients, scan consumers and librdkafka's own lines: <c>DataStudio.Kafka.Core.Clients</c>.</summary>
    public ILogger Clients { get; } = factory.CreateLogger("DataStudio.Kafka.Core.Clients");

    /// <summary>Catalog and window reads: <c>DataStudio.Kafka.Core.Catalog</c>.</summary>
    public ILogger Catalog { get; } = factory.CreateLogger("DataStudio.Kafka.Core.Catalog");

    /// <summary>Scans: <c>DataStudio.Kafka.Core.Scans</c>.</summary>
    public ILogger Scans { get; } = factory.CreateLogger("DataStudio.Kafka.Core.Scans");
}
