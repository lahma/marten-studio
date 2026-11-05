using System.Globalization;

using JasperFx.Events;

using MartenStudio.Services.Events;

using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Services.Find;

/// <summary>
/// Find's streams (1.2): the typed value as a stream id, and as a natural key, through the reads the Streams page makes
/// (<see cref="IEventDataService.ListStreamsAsync" />, <c>FindStreamsByNaturalKeyAsync</c>).
/// </summary>
/// <remarks>
/// <para>
/// The stream-id read is <c>GetStreamAsync</c>'s own, as a list with room for several tenants: the same id can be a stream in more
/// than one. Archived streams are included and marked. A hit names at most five tenants, and a tenant the store policy refuses is
/// dropped before anything is counted. With a policy in force and no tenant selected, the reads are narrowed to the tenants the visitor
/// may address (<see cref="FindTenants" />), so that other tenants' streams cannot fill the page.
/// </para>
/// <para>
/// Under <c>UseTenantPartitionedEvents</c>, with no tenant selected, this is skipped with a note: the streams are partitioned by
/// tenant, and a probe would plan and lock every tenant's partition. The natural-key tables are not partitioned themselves, but they
/// point at those streams (1.2, OPEN-4).
/// </para>
/// <para>
/// A store whose event store is not conjoined has no tenant on its streams as far as the visitor is concerned: Marten writes
/// <c>*DEFAULT*</c> into every row, and that is neither asked of the policy nor shown (<see cref="FindContext.TenantOf" />).
/// </para>
/// </remarks>
internal sealed class StreamFindContributor : IFindContributor
{
    /// <summary>How many rows a stream-id read asks for: five tenants, and one more to show there are others.</summary>
    internal const int RowLimit = 21;

    /// <summary>How many tenants one kind of hit names before it counts the rest.</summary>
    internal const int TenantsShown = 5;

    private readonly IEventDataService events;
    private readonly IFindScopes scopes;
    private readonly IOptions<MartenStudioOptions> options;

    public StreamFindContributor(IEventDataService events, IFindScopes scopes, IOptions<MartenStudioOptions> options)
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

        FindContext? context = await scopes.ContextAsync(scope, query, cancellationToken).ConfigureAwait(false);
        if (context is not { Shape.EventTablesExist: true })
        {
            return FindContribution.NotApplicable;
        }

        if (context.NeedsTenant)
        {
            return new FindContribution([], null, [FindContext.PerTenantNote]);
        }

        List<FindHit> hits = [];
        List<string> searched = [];
        List<string> notes = [];

        // Whether a read was made across tenants in the visitor's tenants only: said once, the same words in every case (FindTenants).
        bool restricted = false;

        // A stream id: a GUID for a store whose streams are, or any text for one whose streams are strings.
        if (context.Shape.StreamIdentity == StreamIdentity.AsString || Guid.TryParse(query.Value, out _))
        {
            StreamPage page = await events
                .ListStreamsAsync(
                    scope,
                    new StreamListRequest { StreamId = query.Value, IncludeArchived = true, PageSize = RowLimit, Tenants = context.StreamTenants },
                    cancellationToken)
                .ConfigureAwait(false);

            restricted |= context.StreamTenants is not null;

            if (page.Error is null)
            {
                searched.Add("streams");
                List<StreamRow> admitted = await scopes.AdmitAsync(context.Scope, page.Rows, x => context.TenantOf(x.TenantId), cancellationToken).ConfigureAwait(false);

                // "Or more" only when the page was full and every row on it was the visitor's to see (see DocumentFindContributor).
                bool maybeMore = (page.HasMore || page.Rows.Count >= RowLimit) && admitted.Count == page.Rows.Count;
                AddStreamHits(hits, "Stream", context, admitted, maybeMore, static row => "Stream " + row.Id, query.Value);
            }
            else
            {
                notes.Add("Streams: could not be searched.");
            }
        }

        // A natural key: looked up under every key whose column the value parses for.
        if (context.Shape.NaturalKeys.Count > 0)
        {
            NaturalKeyLookup lookup = await events
                .FindStreamsByNaturalKeyAsync(scope, query.Value, includeArchived: true, context.StreamTenants, cancellationToken)
                .ConfigureAwait(false);

            restricted |= context.StreamTenants is not null;

            if (lookup.Error is not null)
            {
                notes.Add("Natural keys: could not be searched.");
            }
            else if (lookup.Searchable)
            {
                searched.Add("natural keys (" + string.Join(", ", lookup.Searched) + ")");

                List<NaturalKeyMatch> admitted = await scopes.AdmitAsync(context.Scope, lookup.Matches, x => context.TenantOf(x.Stream.TenantId), cancellationToken).ConfigureAwait(false);
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

        if (restricted)
        {
            notes.Add(FindTenants.RestrictedNote);
        }

        return new FindContribution(hits, searched.Count == 0 ? null : string.Join(", ", searched), notes);
    }

    private void AddStreamHits(
        List<FindHit> hits,
        string kind,
        FindContext context,
        List<StreamRow> rows,
        bool maybeMore,
        Func<StreamRow, string> title,
        string identity)
    {
        for (int index = 0; index < rows.Count && index < TenantsShown; index++)
        {
            StreamRow row = rows[index];
            string? rowTenant = context.TenantOf(row.TenantId);
            StudioScope at = context.Scope with { TenantId = rowTenant ?? context.Scope.TenantId };

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
                TenantChip: rowTenant is null ? null : "tenant " + rowTenant,
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
