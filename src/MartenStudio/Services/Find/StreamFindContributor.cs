using System.Globalization;

using JasperFx.Events;

using MartenStudio.Services.Events;

using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Services.Find;

/// <summary>
/// Find's streams (1.2): the typed value as a stream id, and as a natural key, through the reads the Streams page makes
/// (<see cref="IEventDataService.ListStreamsAsync" />, <see cref="IEventDataService.FindStreamsByNaturalKeyAsync" />).
/// </summary>
/// <remarks>
/// <para>
/// The stream-id read is <c>GetStreamAsync</c>'s own, as a list with room for several tenants: the same id can be a stream in more
/// than one. Archived streams are included and marked. A hit names at most five tenants, and a tenant the store policy refuses is
/// dropped before anything is counted.
/// </para>
/// <para>
/// Under <c>UseTenantPartitionedEvents</c>, with no tenant selected, this is skipped with a note: the streams are partitioned by
/// tenant, and a probe would plan and lock every tenant's partition. The natural-key tables are not partitioned themselves, but they
/// point at those streams (1.2, OPEN-4).
/// </para>
/// </remarks>
internal sealed class StreamFindContributor : IFindContributor
{
    /// <summary>How many rows a stream-id read asks for: five tenants, and one more to show there are others.</summary>
    internal const int RowLimit = 21;

    /// <summary>How many tenants one kind of hit names before it counts the rest.</summary>
    internal const int TenantsShown = 5;

    private readonly IEventDataService events;
    private readonly IFindEventScopes scopes;
    private readonly IOptions<MartenStudioOptions> options;

    public StreamFindContributor(IEventDataService events, IFindEventScopes scopes, IOptions<MartenStudioOptions> options)
    {
        this.events = events;
        this.scopes = scopes;
        this.options = options;
    }

    /// <inheritdoc />
    public int Order => 20;

    /// <inheritdoc />
    public string Noun => "Streams";

    /// <inheritdoc />
    public async Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        if (!query.CouldBeAnId)
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

        List<FindHit> hits = [];
        List<string> searched = [];
        List<string> notes = [];

        // A stream id: a GUID for a store whose streams are, or any text for one whose streams are strings.
        if (context.Shape.StreamIdentity == StreamIdentity.AsString || Guid.TryParse(query.Value, out _))
        {
            StreamPage page = await events
                .ListStreamsAsync(scope, new StreamListRequest { StreamId = query.Value, IncludeArchived = true, PageSize = RowLimit }, cancellationToken)
                .ConfigureAwait(false);

            if (page.Error is null)
            {
                searched.Add("streams");
                List<StreamRow> admitted = await scopes.AdmitAsync(context.Scope, page.Rows, static x => x.TenantId, cancellationToken).ConfigureAwait(false);
                AddStreamHits(hits, "Stream", context, admitted, page.HasMore || page.Rows.Count >= RowLimit, static row => "Stream " + row.Id, query.Value);
            }
            else
            {
                notes.Add("Streams: could not be searched.");
            }
        }

        // A natural key: looked up under every key whose column the value parses for.
        if (context.Shape.NaturalKeys.Count > 0)
        {
            NaturalKeyLookup lookup = await events.FindStreamsByNaturalKeyAsync(scope, query.Value, includeArchived: true, cancellationToken).ConfigureAwait(false);

            if (lookup.Error is not null)
            {
                notes.Add("Natural keys: could not be searched.");
            }
            else if (lookup.Searchable)
            {
                searched.Add("natural keys (" + string.Join(", ", lookup.Searched) + ")");

                List<NaturalKeyMatch> admitted = await scopes.AdmitAsync(context.Scope, lookup.Matches, static x => x.Stream.TenantId, cancellationToken).ConfigureAwait(false);
                foreach (IGrouping<string, NaturalKeyMatch> byKey in admitted.GroupBy(static x => x.Key, StringComparer.Ordinal))
                {
                    AddStreamHits(
                        hits,
                        "Natural key",
                        context,
                        [.. byKey.Select(static x => x.Stream)],
                        false,
                        row => byKey.Key + ": stream " + row.Id,
                        byKey.Key + "\u001f" + query.Value);
                }
            }
        }

        return new FindContribution(hits, searched.Count == 0 ? null : string.Join(", ", searched), notes);
    }

    private void AddStreamHits(
        List<FindHit> hits,
        string kind,
        FindEventContext context,
        List<StreamRow> rows,
        bool maybeMore,
        Func<StreamRow, string> title,
        string identity)
    {
        for (int index = 0; index < rows.Count && index < TenantsShown; index++)
        {
            StreamRow row = rows[index];
            StudioScope at = context.Scope with { TenantId = row.TenantId is { Length: > 0 } tenant ? tenant : context.Scope.TenantId };

            string detail = DescribeStream(row);
            if (index == TenantsShown - 1 && rows.Count > TenantsShown)
            {
                int more = rows.Count - TenantsShown;
                detail += $", and {more.ToString(CultureInfo.InvariantCulture)} more {(more == 1 ? "tenant" : "tenants")} you may see{(maybeMore ? " (or more)" : string.Empty)}";
            }

            hits.Add(new FindHit(
                Kind: kind,
                Title: title(row),
                Url: EventLinks.ToStream(options.Value, at, row.Id),
                Scope: at,
                Id: identity + "\u001f" + row.Id,
                TenantChip: row.TenantId is { Length: > 0 } ? "tenant " + row.TenantId : null,
                Mark: row.IsArchived ? "archived" : null,
                Detail: detail));
        }
    }

    private static string DescribeStream(StreamRow row)
    {
        string events = row.Version.ToString(CultureInfo.InvariantCulture) + (row.Version == 1 ? " event" : " events");
        return string.IsNullOrWhiteSpace(row.AggregateType) ? events : row.AggregateType + ", " + events;
    }
}
