using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services;

// H8's events, 9270-9274: the opt-in ASP.NET Core health check. In a file of their own so that no other packet's
// event block meets this one; the class's own documentation is StudioLog.cs's.
internal static partial class StudioLog
{
    /// <remarks>
    /// <para>
    /// The health export failed in the studio itself - a fault, not anything the host's databases said: those are
    /// the checks' own rows, and 9222. The health check then reports Degraded with the exception's type and none
    /// of its words, because the endpoint has no visitor to show them to; the words are here.
    /// </para>
    /// <para>
    /// An orchestrator probes a health endpoint every few seconds and the run repeats every
    /// <c>HealthInterval</c>, so the level comes from <see cref="StudioLogThrottle" />: a Warning once per kind of
    /// failure in ten minutes, Debug in between.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9270, Message = "Marten Studio's health export failed, so its health check reports Degraded")]
    public static partial void HealthExportFailed(this ILogger logger, LogLevel level, Exception exception);
}
