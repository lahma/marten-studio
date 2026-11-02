using Microsoft.Extensions.Options;

namespace PostgresStudio.Services.ConnectionHistory;

/// <summary>
/// Issue #43: the connection history's samples in this process's memory, for <see cref="PostgresConnectionHistoryOptions.Retention" />,
/// when the host registers no store of its own. Empty again after a restart - which is why a service that deploys often, or runs as
/// several processes, registers a table of its own.
/// </summary>
internal sealed class InMemoryConnectionHistoryStore(IOptions<PostgresConnectionHistoryOptions> options, TimeProvider? time = null)
    : IPostgresConnectionHistoryStore
{
    /// <summary>
    /// The most samples kept a server, the oldest dropped first: two weeks at the default interval, whatever <c>Interval</c> and
    /// <c>Retention</c> allow together (fifteen seconds for ninety days would be half a million). A host that wants more keeps the
    /// history in a store of its own.
    /// </summary>
    internal const int MaxSamplesPerServer = 20_160;

    private readonly Dictionary<string, LinkedList<PostgresConnectionSample>> series = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    /// <inheritdoc />
    public ValueTask AppendAsync(PostgresConnectionSample sample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sample);

        DateTimeOffset oldest = clock.GetUtcNow() - options.Value.Retention;

        lock (gate)
        {
            if (!series.TryGetValue(sample.Server, out LinkedList<PostgresConnectionSample>? samples))
            {
                series[sample.Server] = samples = new LinkedList<PostgresConnectionSample>();
            }

            samples.AddLast(sample);

            while (samples.First is { } first && (first.Value.At < oldest || samples.Count > MaxSamplesPerServer))
            {
                samples.RemoveFirst();
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PostgresConnectionSample>> ReadAsync(string server, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);

        lock (gate)
        {
            IReadOnlyList<PostgresConnectionSample> read = series.TryGetValue(server, out LinkedList<PostgresConnectionSample>? samples)
                ? [.. samples.Where(x => x.At >= since)]
                : [];

            return ValueTask.FromResult(read);
        }
    }
}
