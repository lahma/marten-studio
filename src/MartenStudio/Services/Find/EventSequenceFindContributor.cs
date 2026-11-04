using System.Globalization;

using MartenStudio.Services.Events;

using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Services.Find;

/// <summary>
/// Find's events (1.2): <c>#123</c>, or a plain positive number, as the global sequence of an event, through
/// <see cref="IEventDataService.GetEventBySequenceAsync" />.
/// </summary>
/// <remarks>
/// <para>
/// That read applies the scope's tenant predicate, so a sequence is proven to be in scope before anything is linked from it
/// (hard rule 15); the event's own tenant is then asked of the store policy, and an event in a tenant the visitor may not see reads as
/// no event. The hit opens the event's stream at the event (<see cref="EventLinks.ToStreamAt" />).
/// </para>
/// <para>
/// Under <c>UseTenantPartitionedEvents</c> with no tenant selected a sequence names an event in every tenant, so it is skipped with the
/// streams, with the same note.
/// </para>
/// </remarks>
internal sealed class EventSequenceFindContributor : IFindContributor
{
    private readonly IEventDataService events;
    private readonly IFindEventScopes scopes;
    private readonly IOptions<MartenStudioOptions> options;

    public EventSequenceFindContributor(IEventDataService events, IFindEventScopes scopes, IOptions<MartenStudioOptions> options)
    {
        this.events = events;
        this.scopes = scopes;
        this.options = options;
    }

    /// <inheritdoc />
    public int Order => 30;

    /// <inheritdoc />
    public string Noun => "Events";

    /// <inheritdoc />
    public async Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Refusal is not null || query.Sequence is not { } sequence)
        {
            return FindContribution.NotApplicable;
        }

        FindEventContext? context = await scopes.ContextAsync(scope, query, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return FindContribution.NotApplicable;
        }

        if (context.NeedsTenant)
        {
            return new FindContribution([], null, [FindEventContext.PerTenantNote]);
        }

        EventRow? row = await events.GetEventBySequenceAsync(scope, sequence, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return new FindContribution([], "event sequences", []);
        }

        List<EventRow> admitted = await scopes.AdmitAsync(context.Scope, [row], static x => x.TenantId, cancellationToken).ConfigureAwait(false);
        if (admitted.Count == 0)
        {
            return new FindContribution([], "event sequences", []);
        }

        StudioScope at = context.Scope with { TenantId = row.TenantId is { Length: > 0 } tenant ? tenant : context.Scope.TenantId };
        string number = sequence.ToString(CultureInfo.InvariantCulture);

        return new FindContribution(
            [
                new FindHit(
                    Kind: "Event",
                    Title: "Event #" + number,
                    Url: EventLinks.ToStreamAt(options.Value, at, row.StreamId, row.Version),
                    Scope: at,
                    Id: number,
                    TenantChip: row.TenantId is { Length: > 0 } ? "tenant " + row.TenantId : null,
                    Mark: row.IsArchived ? "archived" : null,
                    Detail: $"{row.EventType}, version {row.Version.ToString(CultureInfo.InvariantCulture)} of stream {row.StreamId}"),
            ],
            "event sequences",
            []);
    }
}
