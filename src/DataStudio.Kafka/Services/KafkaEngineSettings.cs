using DataStudio.Services.Settings;
using DataStudio.Kafka.Core.Clusters;

namespace DataStudio.Kafka.Services;

/// <summary>What the engine takes from the studio that hosts it: how long a catalog read is reused, which is the studio's <c>RefreshInterval</c>.</summary>
/// <param name="studio">The shell's settings.</param>
internal sealed class KafkaEngineSettings(IShellSettings studio) : IKafkaEngineSettings
{
    /// <inheritdoc />
    public TimeSpan CatalogTimeToLive => studio.RefreshInterval;
}
