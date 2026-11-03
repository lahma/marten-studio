using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten.Events;

namespace MartenStudio.Services.Query;

/// <summary>
/// The members of <c>IEvent</c> a LINQ predicate may name, as a closed list computed from the store's public,
/// connection-free event options, and the sentence for each member a visitor might expect that is not on it (1.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a list and not reflection alone.</b> Marten maps exactly the members below to columns
/// (<c>EventQueryMapping</c>, read at Marten 9.57.0). Every other <c>IEvent</c> member falls through to the JSON
/// body - <c>d.data -&gt;&gt; 'X'</c> - and silently matches nothing, or something other than what the visitor meant:
/// <c>StreamKey</c> on a Guid store, <c>CorrelationId</c> where it is not recorded, <c>IsSkipped</c>, <c>AggregateTypeName</c>.
/// <c>IsArchived</c> is worse, because Marten <em>replaces</em> its own archive filter when a predicate names it, so the
/// Archived selector would lie. The list is the pin: <c>LinqEventMembersTests</c> builds every offered member's
/// predicate and ordering against Marten and requires a column, never the body, so the next Marten that moves one
/// fails a test before it reaches a visitor; the run-time check of <c>LinqQuerySql.ReadsJsonBody</c> is the second wall.
/// </para>
/// </remarks>
internal static class LinqEventMembers
{
    /// <summary>The placeholder-and-hint order: how the page lists the members.</summary>
    private static readonly string[] Order =
    [
        nameof(IEvent.Sequence), nameof(IEvent.Timestamp), nameof(IEvent.EventTypeName), nameof(IEvent.StreamId),
        nameof(IEvent.StreamKey), nameof(IEvent.Version), nameof(IEvent.TenantId), nameof(IEvent.CorrelationId),
        nameof(IEvent.CausationId), nameof(IEvent.UserName), nameof(IEvent.DotNetTypeName), nameof(IEvent.Id),
    ];

    /// <summary>The event schema's default ordering box text, newest first.</summary>
    public const string SequenceDescending = "Sequence desc";

    /// <summary>The ordering across tenants whose sequences are per tenant.</summary>
    public const string TimestampDescending = "Timestamp desc";

    /// <summary>Whether this store counts sequences per tenant, which Marten allows only with conjoined events.</summary>
    public static bool IsPerTenantSequences(IReadOnlyEventStoreOptions events) =>
        events.TenancyStyle == TenancyStyle.Conjoined && events.UseTenantPartitionedEvents;

    /// <summary>
    /// The members this store offers, in the hint's order.
    /// </summary>
    /// <param name="events">The store's event options.</param>
    /// <param name="tenantNarrowed">Whether Marten already narrows the query to one tenant (conjoined events and a tenant selected).</param>
    public static IReadOnlyList<string> Offered(IReadOnlyEventStoreOptions events, bool tenantNarrowed)
    {
        ArgumentNullException.ThrowIfNull(events);

        bool guid = events.StreamIdentity == StreamIdentity.AsGuid;

        return
        [
            .. Order.Where(name => name switch
            {
                nameof(IEvent.StreamId) => guid,
                nameof(IEvent.StreamKey) => !guid,
                nameof(IEvent.TenantId) => !tenantNarrowed,
                nameof(IEvent.CorrelationId) => events.MetadataConfig.CorrelationIdEnabled,
                nameof(IEvent.CausationId) => events.MetadataConfig.CausationIdEnabled,
                nameof(IEvent.UserName) => events.MetadataConfig.UserNameEnabled,
                _ => true,
            }),
        ];
    }

    /// <summary>What the page needs: the list, each omission's sentence, the default order and the schema.</summary>
    /// <param name="events">The store's event options.</param>
    /// <param name="tenant">The tenant selected, or <see langword="null" />.</param>
    public static LinqEventTargetInfo Describe(IReadOnlyEventStoreOptions events, string? tenant)
    {
        ArgumentNullException.ThrowIfNull(events);

        bool conjoined = events.TenancyStyle == TenancyStyle.Conjoined;
        bool narrowed = conjoined && tenant is { Length: > 0 };
        bool perTenant = IsPerTenantSequences(events);
        bool guid = events.StreamIdentity == StreamIdentity.AsGuid;

        List<LinqWithheldMember> withheld =
        [
            new(nameof(IEvent.IsArchived),
                "Use the Archived selector: Marten drops its own archive filter when a predicate names IsArchived."),
            guid
                ? new(nameof(IEvent.StreamKey), "This store identifies streams by Guid; use StreamId.")
                : new(nameof(IEvent.StreamId), "This store identifies streams by string; use StreamKey."),
        ];

        if (!events.MetadataConfig.CorrelationIdEnabled)
        {
            withheld.Add(new(nameof(IEvent.CorrelationId),
                "Not recorded by this store: StoreOptions.Events.MetadataConfig.CorrelationIdEnabled is false."));
        }

        if (!events.MetadataConfig.CausationIdEnabled)
        {
            withheld.Add(new(nameof(IEvent.CausationId),
                "Not recorded by this store: StoreOptions.Events.MetadataConfig.CausationIdEnabled is false."));
        }

        if (!events.MetadataConfig.UserNameEnabled)
        {
            withheld.Add(new(nameof(IEvent.UserName),
                "Not recorded by this store: StoreOptions.Events.MetadataConfig.UserNameEnabled is false."));
        }

        if (narrowed)
        {
            withheld.Add(new(nameof(IEvent.TenantId), $"Marten already narrows this query to tenant {tenant}."));
        }

        withheld.Add(new(nameof(IEvent.IsSkipped), "Marten reads it from the event body, not the is_skipped column."));
        withheld.Add(new(nameof(IEvent.AggregateTypeName), "Not stored in a column."));
        withheld.Add(new("Data, Headers, Tags, EventType",
            "The event body and headers are not queryable here: see docs/linq-mode.md#events."));

        return new LinqEventTargetInfo(
            conjoined,
            perTenant,
            events.StreamIdentity,
            Offered(events, narrowed),
            withheld,
            perTenant && conjoined && tenant is not { Length: > 0 } ? TimestampDescending : SequenceDescending,
            events.DatabaseSchemaName);
    }
}
