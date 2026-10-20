using MartenStudio.Services.Events;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

// EV's members: compaction, DCB tags and natural keys. The defaults are the answers of a store with none of
// them, so a page test that is not about them renders exactly what it rendered before.
internal sealed partial class FakeEventDataService
{
    /// <summary>What the compaction read answers, by stream id; a stream not here is not offered.</summary>
    public Dictionary<string, CompactionPlan> CompactionPlans { get; } = new(StringComparer.Ordinal);

    /// <summary>The compactions asked for, as "stream id / what was typed / the version it was bounded to".</summary>
    public List<string> Compactions { get; } = [];

    /// <summary>The streams the compaction read was handed, which is the page's own read, not a second one.</summary>
    public List<StreamState> CompactionDescriptions { get; } = [];

    /// <summary>What a compaction answers when it succeeds: the sentence the service audited.</summary>
    public string CompactionOutcome { get; set; } = "compacted through version 3 into one Compacted<Order> event";

    /// <summary>What the tag-type list answers.</summary>
    public EventTagTypeList TagTypes { get; set; } = EventTagTypeList.None;

    /// <summary>What an explicit tag read answers.</summary>
    public EventTagRead TagRead { get; set; } = EventTagRead.Empty;

    /// <summary>The sequences every explicit tag read was asked about.</summary>
    public List<IReadOnlyList<long>> TagReads { get; } = [];

    /// <summary>What the natural-key lookup answers.</summary>
    public NaturalKeyLookup NaturalKeys { get; set; } = NaturalKeyLookup.None;

    /// <summary>The values the natural-key lookup was asked about.</summary>
    public List<string> NaturalKeyLookups { get; } = [];

    public Task<CompactionPlan> DescribeCompactionAsync(
        StudioScope scope,
        StreamState stream,
        CancellationToken cancellationToken = default)
    {
        CompactionDescriptions.Add(stream);

        return Task.FromResult(CompactionPlans.TryGetValue(stream.Id, out CompactionPlan? plan)
            ? plan
            : CompactionPlan.NotOffered(stream.Id, CompactionUnavailableReason.Untyped));
    }

    public Task<string> CompactStreamAsync(
        StudioScope scope,
        string streamId,
        string confirmation,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        if (MutationFailure is not null)
        {
            return Task.FromException<string>(MutationFailure);
        }

        Compactions.Add(streamId + "/" + confirmation + "/" + expectedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Task.FromResult(CompactionOutcome);
    }

    public Task<EventTagTypeList> ListTagTypesAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(TagTypes);

    public Task<EventTagRead> GetEventTagsAsync(
        StudioScope scope,
        IReadOnlyList<long> sequences,
        CancellationToken cancellationToken = default)
    {
        TagReads.Add(sequences);
        return Task.FromResult(TagRead);
    }

    public Task<NaturalKeyLookup> FindStreamsByNaturalKeyAsync(
        StudioScope scope,
        string value,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        NaturalKeyLookups.Add(value);
        return Task.FromResult(NaturalKeys);
    }

    /// <summary>A plan that offers compaction, with the facts a dialog draws.</summary>
    public static CompactionPlan Offered(
        string streamId,
        string aggregate = "OrderSummary",
        long version = 3,
        long compactedVersion = 0,
        string? tenantId = null,
        bool needsTenant = false,
        bool tagTables = false) => new()
    {
        StreamId = streamId,
        AggregateName = aggregate,
        AggregateFullName = "Sample." + aggregate,
        Version = version,
        CompactedVersion = compactedVersion,
        EventsToFold = StreamCompactionRules.EventsInStream(version, compactedVersion),
        StreamTenantId = tenantId,
        NeedsTenant = needsTenant,
        TagTablesMayRefuse = tagTables,
    };
}
