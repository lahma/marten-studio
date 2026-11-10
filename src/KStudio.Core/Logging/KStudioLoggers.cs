using Microsoft.Extensions.Logging;

namespace KStudio.Core.Logging;

/// <summary>
/// The engine's loggers, by category (decision 11): one filter on <c>KStudio.Core</c> covers the engine.
/// </summary>
internal sealed class KStudioLoggers(ILoggerFactory factory)
{
    /// <summary>Admin clients, scan consumers and librdkafka's own lines: <c>KStudio.Core.Clients</c>.</summary>
    public ILogger Clients { get; } = factory.CreateLogger("KStudio.Core.Clients");

    /// <summary>Catalog and window reads: <c>KStudio.Core.Catalog</c>.</summary>
    public ILogger Catalog { get; } = factory.CreateLogger("KStudio.Core.Catalog");

    /// <summary>Scans: <c>KStudio.Core.Scans</c>.</summary>
    public ILogger Scans { get; } = factory.CreateLogger("KStudio.Core.Scans");
}
