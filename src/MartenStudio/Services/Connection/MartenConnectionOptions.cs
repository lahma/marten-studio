using Marten;
using Marten.Storage;

using Npgsql;

namespace MartenStudio.Services.Connection;

/// <summary>
/// The store options that bear on connections, read once for the Connection page's Marten section and the Configuration page's
/// connection rows - so both say the same thing.
/// </summary>
/// <remarks>
/// <b>Public members of <see cref="StoreOptions" /> only</b>, with no reflection, and nothing that ensures storage or opens a session
/// (hard rule 14). <c>RlsTenantSessionSetting</c> is reduced to whether it is set: it is a setting name the host chose, and the
/// studio says "configured" and never what it is.
/// </remarks>
/// <param name="CommandTimeout">The command timeout, as far as public members can prove it.</param>
/// <param name="StickyConnections"><c>UseStickyConnectionLifetimes</c>.</param>
/// <param name="ReadPreference"><c>Advanced.MultiHostSettings.ReadSessionPreference</c>.</param>
/// <param name="WritePreference"><c>Advanced.MultiHostSettings.WriteSessionPreference</c>.</param>
/// <param name="NpgsqlLoggingDisabled"><c>DisableNpgsqlLogging</c>.</param>
/// <param name="TrackConnections"><c>OpenTelemetry.TrackConnections</c>, as its name.</param>
/// <param name="HiloMaxLo"><c>Advanced.HiloSequenceDefaults.MaxLo</c>.</param>
/// <param name="HiloMaxAdvanceAttempts"><c>Advanced.HiloSequenceDefaults.MaxAdvanceToNextHiAttempts</c>.</param>
/// <param name="UpdateBatchSize"><c>UpdateBatchSize</c>.</param>
/// <param name="ApplyChangesLockId"><c>ApplyChangesLockId</c>.</param>
/// <param name="RlsConfigured">Whether <c>RlsTenantSessionSetting</c> is set.</param>
/// <param name="UseIdentityMapForAggregates"><c>Events.UseIdentityMapForAggregates</c>.</param>
/// <param name="AutoDiscoverTagTypes"><c>Events.AutoDiscoverTagTypes</c>.</param>
internal sealed record MartenConnectionOptions(
    MartenCommandTimeout CommandTimeout,
    bool StickyConnections,
    TargetSessionAttributes ReadPreference,
    TargetSessionAttributes WritePreference,
    bool NpgsqlLoggingDisabled,
    string TrackConnections,
    int HiloMaxLo,
    int HiloMaxAdvanceAttempts,
    int UpdateBatchSize,
    int ApplyChangesLockId,
    bool RlsConfigured,
    bool UseIdentityMapForAggregates,
    bool AutoDiscoverTagTypes)
{
    /// <summary>Marten's defaults (9.57), which the rows print beside the value.</summary>
    internal static class Defaults
    {
        /// <summary><c>MultiHostSettings.*SessionPreference</c>.</summary>
        public const TargetSessionAttributes SessionPreference = TargetSessionAttributes.ReadWrite;

        /// <summary><c>HiloSettings.MaxLo</c>.</summary>
        public const int HiloMaxLo = 1000;

        /// <summary><c>HiloSettings.MaxAdvanceToNextHiAttempts</c>.</summary>
        public const int HiloMaxAdvanceAttempts = 30;

        /// <summary><c>StoreOptions.UpdateBatchSize</c>.</summary>
        public const int UpdateBatchSize = 500;

        /// <summary><c>StoreOptions.ApplyChangesLockId</c>.</summary>
        public const int ApplyChangesLockId = 4004;
    }

    /// <summary>Whether a write preference can send a write to a standby: <c>Any</c>, <c>Standby</c>, <c>PreferStandby</c> and <c>ReadOnly</c> can.</summary>
    public bool WritesCanLandOnAStandby => WritePreference is TargetSessionAttributes.Any
        or TargetSessionAttributes.Standby or TargetSessionAttributes.PreferStandby or TargetSessionAttributes.ReadOnly;

    /// <summary>Reads the options that bear on connections.</summary>
    /// <param name="options">The store's options.</param>
    /// <param name="database">The database the page is about, or <see langword="null" /> when the connection string's timeout is not to be read.</param>
    public static MartenConnectionOptions Read(StoreOptions options, IMartenDatabase? database)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new MartenConnectionOptions(
            MartenCommandTimeout.Read(options, database),
            options.UseStickyConnectionLifetimes,
            options.Advanced.MultiHostSettings.ReadSessionPreference,
            options.Advanced.MultiHostSettings.WriteSessionPreference,
            options.DisableNpgsqlLogging,
            options.OpenTelemetry.TrackConnections.ToString(),
            options.Advanced.HiloSequenceDefaults.MaxLo,
            options.Advanced.HiloSequenceDefaults.MaxAdvanceToNextHiAttempts,
            options.UpdateBatchSize,
            options.ApplyChangesLockId,
            options.RlsTenantSessionSetting is { Length: > 0 },
            options.Events.UseIdentityMapForAggregates,
            options.Events.AutoDiscoverTagTypes);
    }
}
