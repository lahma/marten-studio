using Microsoft.Extensions.Logging;

namespace MartenStudio.Integration.Tests.Logging;

/// <summary>One line Marten Studio wrote to the host's log.</summary>
/// <param name="Category">The logger category, which is the studio type's full name.</param>
/// <param name="Level">The level it was written at.</param>
/// <param name="EventId">The event id, which is what an operator's query matches on.</param>
/// <param name="Message">The rendered message, for a failure to quote.</param>
internal sealed record StudioLogLine(string Category, LogLevel Level, EventId EventId, string Message)
{
    public override string ToString() => $"{Level} {EventId.Id} {Category}: {Message}";
}

/// <summary>
/// Everything the studio logs in a real host, at every level, and nothing anybody else logs.
/// </summary>
/// <remarks>
/// <para>
/// Added to the host's own logging builder, so it sees exactly what an application's provider would -
/// the same categories, the same filters, the same levels - rather than a logger a test handed to one
/// service. Only categories under <c>MartenStudio</c> are kept: Marten, Npgsql and the generic host log
/// a great deal at Debug, and what they say is not what these tests are about.
/// </para>
/// <para>
/// Hand-written rather than taken from a package: the budget is complete (AGENTS.md hard rule 1).
/// </para>
/// </remarks>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly List<StudioLogLine> lines = [];
    private readonly Lock gate = new();

    /// <summary>Everything the studio logged so far, in order.</summary>
    public IReadOnlyList<StudioLogLine> Lines
    {
        get
        {
            lock (gate)
            {
                return [.. lines];
            }
        }
    }

    /// <summary>Every line at Warning or above - which, for the hosts these tests build, must be none.</summary>
    public IReadOnlyList<StudioLogLine> WarningsOrWorse => [.. Lines.Where(static x => x.Level >= LogLevel.Warning)];

    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith("MartenStudio", StringComparison.Ordinal)
            ? new Capturing(this, categoryName)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }

    private void Add(StudioLogLine line)
    {
        lock (gate)
        {
            lines.Add(line);
        }
    }

    private sealed class Capturing(LogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            owner.Add(new StudioLogLine(category, logLevel, eventId, formatter(state, exception)));
    }
}
