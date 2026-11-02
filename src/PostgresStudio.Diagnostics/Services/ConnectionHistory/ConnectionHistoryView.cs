namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #43: what Database › Sessions › Holders says of a server's connection history - the window it covers, the peak against the
/// client budget and when, the instance breaks in it, and (past the activity gate) each application's own peak.
/// </summary>
/// <param name="Since">The first sample's time.</param>
/// <param name="Until">The last sample's time.</param>
/// <param name="Samples">How many samples the window holds.</param>
/// <param name="Peak">The most client connections one sample counted.</param>
/// <param name="PeakAt">When that was, the first time it was reached.</param>
/// <param name="ClientLimit">The client budget at the peak.</param>
/// <param name="ClientLimitNow">The client budget at the last sample - what a peak is judged against once a cluster is resized.</param>
/// <param name="InstanceChanges">How many times the answering instance changed between two samples - a failover or a restart.</param>
/// <param name="Applications">Each application's own peak, most first; empty below the activity gate, where no name is shown.</param>
internal sealed record ConnectionHistoryView(
    DateTimeOffset Since,
    DateTimeOffset Until,
    int Samples,
    long Peak,
    DateTimeOffset PeakAt,
    int ClientLimit,
    int ClientLimitNow,
    int InstanceChanges,
    IReadOnlyList<ConnectionHistoryPeak> Applications)
{
    /// <summary>The most applications the page lists.</summary>
    public const int MaxApplications = 20;

    /// <summary>
    /// The view of <paramref name="samples" /> (oldest first), or <see langword="null" /> for none. Pure.
    /// </summary>
    /// <param name="samples">The server's samples in the window.</param>
    /// <param name="names">Whether applications may be named: the activity gate passed.</param>
    public static ConnectionHistoryView? Of(IReadOnlyList<PostgresConnectionSample> samples, bool names)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            return null;
        }

        PostgresConnectionSample peak = samples[0];
        int changes = 0;

        for (int i = 1; i < samples.Count; i++)
        {
            if (samples[i].Connections > peak.Connections)
            {
                peak = samples[i];
            }

            if (samples[i].Instance is { } instance && samples[i - 1].Instance is { } before && !string.Equals(instance, before, StringComparison.Ordinal))
            {
                changes++;
            }
        }

        IReadOnlyList<ConnectionHistoryPeak> applications = names
            ? [.. samples
                .SelectMany(static sample => sample.ByApplication.Select(counts => (counts.ApplicationName, counts.Total, sample.At)))
                .GroupBy(static x => x.ApplicationName ?? string.Empty, StringComparer.Ordinal)
                .Select(static group =>
                {
                    (string? name, int total, DateTimeOffset at) = group.MaxBy(static x => x.Total);
                    return new ConnectionHistoryPeak(name, total, at);
                })
                .OrderByDescending(static x => x.Peak)
                .ThenBy(static x => x.Application, StringComparer.Ordinal)
                .Take(MaxApplications)]
            : [];

        return new ConnectionHistoryView(samples[0].At, samples[^1].At, samples.Count, peak.Connections, peak.At, peak.ClientLimit, samples[^1].ClientLimit, changes, applications);
    }
}

/// <summary>One application's own peak in a connection history window.</summary>
/// <param name="Application">Its <c>application_name</c>, or <see langword="null" /> for sessions that set none.</param>
/// <param name="Peak">The most connections it held in one sample.</param>
/// <param name="At">When, the first time.</param>
internal sealed record ConnectionHistoryPeak(string? Application, int Peak, DateTimeOffset At);
