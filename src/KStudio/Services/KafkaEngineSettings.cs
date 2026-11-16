using DataStudio.Services.Settings;
using KStudio.Core.Clusters;

namespace KStudio.Services;

/// <summary>What the engine takes from the studio that hosts it: how long a catalog read is reused, which is the studio's <c>RefreshInterval</c>.</summary>
/// <param name="studio">The host studio's settings.</param>
internal sealed class KafkaEngineSettings(IShellSettings studio) : IKafkaEngineSettings
{
    /// <inheritdoc />
    public TimeSpan CatalogTimeToLive => studio.RefreshInterval;
}
