using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Support;

/// <summary>One line the studio wrote to the application's logger.</summary>
public sealed record CapturedLogEntry(LogLevel Level, EventId EventId, string Message);

/// <summary>
/// Captures what the studio logs, so the audit tests can assert the <em>event ids</em> rather than the
/// wording.
/// </summary>
/// <remarks>
/// The ids are the contract: an operator's saved query matches on the number, and the in-memory Activity
/// ring is gone at the next restart while this copy is not.
/// </remarks>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<CapturedLogEntry> entries = [];
    private readonly Lock gate = new();

    /// <summary>Everything logged so far, in order.</summary>
    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (gate)
            {
                return entries.ToArray();
            }
        }
    }

    /// <summary>A logger factory writing into this provider and nothing else.</summary>
    public ILoggerFactory CreateFactory() => LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddProvider(this);
    });

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private void Add(CapturedLogEntry entry)
    {
        lock (gate)
        {
            entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            provider.Add(new CapturedLogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }
}
