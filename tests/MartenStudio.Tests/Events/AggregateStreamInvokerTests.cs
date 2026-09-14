using JasperFx.Events;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Aggregation;

using MartenStudio.Services.Events;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Events;

/// <summary>
/// Aggregate-type discovery, against real Marten registrations rather than a hand-written fake that
/// would agree with the test and disagree with Marten.
/// </summary>
/// <remarks>
/// <c>DocumentStore.For</c> never opens a connection (see <see cref="SqlTestStore" />), so a store can be
/// configured exactly as a host would and asked what it registered.
/// </remarks>
public class AggregateStreamInvokerTests
{
    [Fact]
    public void A_self_aggregating_snapshot_is_offered_as_a_single_stream_projection()
    {
        IReadOnlyList<AggregateTypeCandidate> candidates = Candidates(options =>
            options.Projections.Snapshot<TimeTravelOrder>(SnapshotLifecycle.Inline));

        AggregateTypeCandidate? order = candidates.FirstOrDefault(x => x.Type == typeof(TimeTravelOrder));

        order.Should().NotBeNull("Snapshot<T>() closes SingleStreamProjection<T, TId> for you");
        order!.Source.Should().Be(AggregateCandidateSource.SingleStreamProjection);
        order.Name.Should().Be(nameof(TimeTravelOrder));
    }

    [Fact]
    public void A_hand_written_single_stream_projection_is_offered_too()
    {
        IReadOnlyList<AggregateTypeCandidate> candidates = Candidates(options =>
            options.Projections.Add(new TimeTravelSummaryProjection(), ProjectionLifecycle.Inline));

        candidates.Should().Contain(x =>
            x.Type == typeof(TimeTravelSummary) && x.Source == AggregateCandidateSource.SingleStreamProjection);
    }

    /// <summary>
    /// The projections come first, because they are the ones that answer "what does this stream fold
    /// into"; the manual picker over every document type is the fallback the plan asks for.
    /// </summary>
    [Fact]
    public void Every_document_type_is_offered_as_the_manual_fallback_after_the_projections()
    {
        IReadOnlyList<AggregateTypeCandidate> candidates = Candidates(options =>
        {
            options.Projections.Snapshot<TimeTravelOrder>(SnapshotLifecycle.Inline);
            options.Schema.For<TimeTravelNote>();
        });

        candidates.Should().Contain(x =>
            x.Type == typeof(TimeTravelNote) && x.Source == AggregateCandidateSource.DocumentType);

        int projectionIndex = IndexOf(candidates, typeof(TimeTravelOrder));
        int documentIndex = IndexOf(candidates, typeof(TimeTravelNote));

        projectionIndex.Should().BeLessThan(documentIndex);
    }

    /// <summary>
    /// A projection appears once, not twice: registering it also registers its aggregate as a document
    /// type, and the picker offering the same type under two labels would be a bug people report.
    /// </summary>
    [Fact]
    public void A_type_that_is_both_a_projection_and_a_document_appears_once()
    {
        IReadOnlyList<AggregateTypeCandidate> candidates = Candidates(options =>
            options.Projections.Snapshot<TimeTravelOrder>(SnapshotLifecycle.Inline));

        candidates.Count(x => x.Type == typeof(TimeTravelOrder)).Should().Be(1);
    }

    /// <summary>
    /// "Whose stream matches", from the other side: a string-keyed projection on a string-identified
    /// store is a projection candidate.
    /// </summary>
    [Fact]
    public void A_string_keyed_projection_is_a_candidate_on_a_string_identified_store()
    {
        IReadOnlyList<AggregateTypeCandidate> candidates = Candidates(options =>
        {
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Projections.Add(new TimeTravelKeyedProjection(), ProjectionLifecycle.Inline);
        });

        candidates.Should().Contain(x =>
            x.Type == typeof(TimeTravelKeyed) && x.Source == AggregateCandidateSource.SingleStreamProjection);
    }

    /// <summary>
    /// Marten refuses the mismatch itself, at store construction, which is worth knowing: the studio's
    /// own identity check can therefore never fire on a store that was built at all. It stays because the
    /// wrapper path reads <c>ImplementationType</c>'s generic arguments rather than a validated
    /// registration, and because a value that cannot be wrong is cheaper to keep than to re-derive.
    /// </summary>
    [Fact]
    public void Marten_itself_refuses_a_guid_keyed_projection_on_a_string_identified_store()
    {
        Action build = () => Candidates(options =>
        {
            options.Events.StreamIdentity = StreamIdentity.AsString;
            options.Projections.Add(new TimeTravelSummaryProjection(), ProjectionLifecycle.Inline);
        });

        build.Should().Throw<InvalidProjectionException>().WithMessage("*Id type mismatch*");
    }

    /// <summary>
    /// <c>AggregateStreamAsync&lt;T&gt;</c> is constrained <c>where T : class</c>, so a value type can
    /// never be closed over - and a candidate list that offered one would fail at the click rather than
    /// at the listing.
    /// </summary>
    [Fact]
    public void No_candidate_is_ever_a_value_type()
    {
        IReadOnlyList<AggregateTypeCandidate> candidates = Candidates(options =>
        {
            options.Projections.Snapshot<TimeTravelOrder>(SnapshotLifecycle.Inline);
            options.Schema.For<TimeTravelNote>();
        });

        candidates.Should().NotBeEmpty();
        candidates.Should().OnlyContain(x => !x.Type.IsValueType);
    }

    // ------------------------------------------------------------------------------------------------
    // The wrapper path: a container-scoped or composite registration is an ISubscriptionSource but not an
    // IAggregateProjection, so the aggregate has to come off ImplementationType's base chain.
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The base is matched by <b>type identity</b> against <c>SingleStreamProjection&lt;,&gt;</c> and
    /// <c>JasperFxSingleStreamProjectionBase&lt;,,,&gt;</c>, which is what the remarks claimed and the code
    /// did not: it matched <c>GetGenericTypeDefinition().Name.StartsWith("SingleStreamProjection")</c>.
    /// </summary>
    [Fact]
    public void The_wrapper_path_reads_the_aggregate_off_the_real_base_type()
    {
        AggregateStreamInvoker.FromImplementationType(typeof(TimeTravelSummaryProjection), StreamIdentity.AsGuid)
            .Should().Be<TimeTravelSummary>();

        AggregateStreamInvoker.FromImplementationType(typeof(TimeTravelKeyedProjection), StreamIdentity.AsString)
            .Should().Be<TimeTravelKeyed>();
    }

    /// <summary>A projection whose identity does not match this store's streams is not a candidate.</summary>
    [Fact]
    public void The_wrapper_path_drops_a_projection_keyed_the_other_way()
    {
        AggregateStreamInvoker.FromImplementationType(typeof(TimeTravelSummaryProjection), StreamIdentity.AsString)
            .Should().BeNull();

        AggregateStreamInvoker.FromImplementationType(typeof(TimeTravelKeyedProjection), StreamIdentity.AsGuid)
            .Should().BeNull();
    }

    /// <summary>
    /// The regression the name match could not see. A host type that is merely <em>called</em>
    /// <c>SingleStreamProjectionOfMine&lt;TDoc,TId&gt;</c> and is not a projection at all used to be read
    /// as one, and its first generic argument offered in the picker as an aggregate — which would have
    /// thrown at the click. Type identity cannot make that mistake, and a JasperFx rename now breaks the
    /// build rather than silently matching nothing.
    /// </summary>
    [Fact]
    public void A_type_that_only_looks_like_a_single_stream_projection_is_not_one()
    {
        AggregateStreamInvoker
            .FromImplementationType(typeof(SingleStreamProjectionOfMine<TimeTravelNote, Guid>), StreamIdentity.AsGuid)
            .Should().BeNull();
    }

    [Fact]
    public void The_wrapper_path_answers_nothing_for_a_type_that_is_not_a_projection()
    {
        AggregateStreamInvoker.FromImplementationType(typeof(TimeTravelNote), StreamIdentity.AsGuid)
            .Should().BeNull();

        AggregateStreamInvoker.FromImplementationType(null, StreamIdentity.AsGuid).Should().BeNull();
    }

    [Fact]
    public void Replaying_into_a_value_type_is_refused_with_the_reason()
    {
        Action replay = () => AggregateStreamInvoker.AggregateAtVersionAsync(
            SessionFor(static _ => { }), typeof(int), Guid.NewGuid(), 1, CancellationToken.None);

        replay.Should().Throw<ArgumentException>().WithMessage("*reference types*");
    }

    private static IQuerySession SessionFor(Action<StoreOptions> configure) =>
        DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            configure(options);
        }).QuerySession();

    private static int IndexOf(IReadOnlyList<AggregateTypeCandidate> candidates, Type type)
    {
        for (int index = 0; index < candidates.Count; index++)
        {
            if (candidates[index].Type == type)
            {
                return index;
            }
        }

        return -1;
    }

    private static IReadOnlyList<AggregateTypeCandidate> Candidates(Action<StoreOptions> configure)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            configure(options);
        });

        return AggregateStreamInvoker.Candidates(store.Options);
    }
}

/// <summary>A self-aggregating aggregate, the shape <c>Snapshot&lt;T&gt;()</c> wants.</summary>
public class TimeTravelOrder
{
    public Guid Id { get; set; }

    public int Total { get; set; }

    public void Apply(TimeTravelOrderPlaced placed) => Total = placed.Total;
}

/// <summary>An aggregate written by a hand-rolled projection.</summary>
public class TimeTravelSummary
{
    public Guid Id { get; set; }

    public int Count { get; set; }
}

/// <summary>A plain document type, so the manual picker has something to offer.</summary>
public class TimeTravelNote
{
    public Guid Id { get; set; }

    public string Text { get; set; } = string.Empty;
}

/// <summary>The event the aggregates fold.</summary>
/// <param name="Total">The order total.</param>
public record TimeTravelOrderPlaced(int Total);

/// <summary>A single-stream projection registered the long way round.</summary>
public class TimeTravelSummaryProjection : SingleStreamProjection<TimeTravelSummary, Guid>
{
    /// <summary>Counts the events in the stream.</summary>
    public static void Apply(TimeTravelOrderPlaced placed, TimeTravelSummary summary) => summary.Count++;
}

/// <summary>An aggregate keyed by a stream key rather than a stream id.</summary>
public class TimeTravelKeyed
{
    public string Id { get; set; } = string.Empty;

    public int Count { get; set; }
}

/// <summary>A single-stream projection for a string-identified store.</summary>
public class TimeTravelKeyedProjection : SingleStreamProjection<TimeTravelKeyed, string>
{
    /// <summary>Counts the events in the stream.</summary>
    public static void Apply(TimeTravelOrderPlaced placed, TimeTravelKeyed keyed) => keyed.Count++;
}

/// <summary>
/// A decoy: two generic arguments and a name that starts with <c>SingleStreamProjection</c>, and nothing
/// whatever to do with Marten. The name-prefix match used to read <typeparamref name="TDoc" /> off it and
/// offer it in the time-travel picker.
/// </summary>
/// <typeparam name="TDoc">Would have been taken for the aggregate.</typeparam>
/// <typeparam name="TId">Would have been taken for its identity.</typeparam>
public class SingleStreamProjectionOfMine<TDoc, TId>
    where TDoc : class
    where TId : notnull
{
    /// <summary>Whatever this host type is actually for.</summary>
    public TDoc? Value { get; set; }

    /// <summary>Its key.</summary>
    public TId? Key { get; set; }
}
