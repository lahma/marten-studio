using Microsoft.Extensions.Logging;

namespace MartenStudio.Services;

// DEPS' events, 9320-9329: the Marten 9.51 light-ups. In a file of their own so that no other packet's event block meets
// them in one file; the class's own documentation is MartenLog.cs's.
internal static partial class MartenLog
{
    /// <remarks>
    /// <para>
    /// Warning, once per rewind a visitor pressed: Marten or JasperFx refused a dead-letter rewind as an unknown
    /// subscription, although the studio had checked the name against the store's async projections - so something
    /// disagrees, and the daemon may have stopped the agents before Marten refused (it stops them first).
    /// </para>
    /// <para>
    /// The exception is Marten's own, attached whole: its message is "Unknown subscription name 'x'. Available options are
    /// ..." - every subscription the store has. That is why the page and the audit ring are told
    /// <c>StreamCompactionRules.SubscriptionNotRecognised</c> instead, and why this line, which only the host's own log
    /// sees, is where Marten's words are kept.
    /// </para>
    /// </remarks>
    [LoggerMessage(EventId = 9320, Level = LogLevel.Warning, Message = "Marten refused Marten Studio's rewind of subscription {Projection} (tenant {TenantId}) in store {StoreKey}, database {DatabaseId} as an unknown subscription; the page and the audit were told without Marten's words, which are attached here")]
    public static partial void SubscriptionRewindRefused(this ILogger logger, Exception exception, string projection, string tenantId, string storeKey, string databaseId);
}
