using System.Reflection;

using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.Events.Protected;
using JasperFx.Events.Tags;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Events;
using Marten.Events.Archiving;
using Marten.Services;
using Marten.Storage;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Events;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Marten;

/// <summary>
/// The Marten members EV (0.4 packet: compaction, DCB tags, natural keys) depends on, pinned the way
/// <c>MartenApiSurfaceTest</c> pins the rest - a compile-only probe for the signatures, reflection for the
/// facts - in a file of its own so the packet edits nothing it does not own.
/// </summary>
/// <remarks>
/// <para>
/// Read on 2026-10-15 against Marten V9.39.0 (the floor; JasperFx 2.74.0, Weasel 9.33.0) and V9.50.2
/// (JasperFx 2.87.1, Weasel 9.45.0). <c>EventStore.StreamCompacting.cs</c> is byte for byte the same at both;
/// so are <c>EventTagTable</c>, <c>NaturalKeyTable</c> and <c>FetchNaturalKeyPlan</c>.
/// </para>
/// <para>
/// Two corrections to the packet's wording, found while reading. <c>TagTypes</c> is not on
/// <c>IReadOnlyEventStoreOptions</c> at either version: it is on <c>EventGraph</c> and on the writable
/// <c>IEventStoreOptions</c>, and <c>IDocumentStore.Options.Events</c> is the <c>EventGraph</c> - asserted
/// below. And the store-level tenant overloads of <c>IEventStore.CompactStreamAsync</c> (from 9.41.0) open
/// <c>LightweightSession(tenantId)</c>, which does not pin the scope's database, so the studio does not
/// call them; it calls the typed session-level overload on a session it pinned itself, which is
/// tenant-filtered at the floor already.
/// </para>
/// </remarks>
public class EventsApiSurfaceTests
{
    [Fact]
    public void The_compile_only_probe_is_referenced_so_nothing_has_to_be_suppressed()
    {
        Func<IDocumentStore, IMartenDatabase, CancellationToken, Task> probe = CompileOnlyAsync;

        probe.Method.Name.Should().Be(nameof(CompileOnlyAsync));
    }

    /// <summary>The cast the studio's tag and alias reads rest on.</summary>
    [Fact]
    public void A_stores_read_only_event_options_are_its_event_graph()
    {
        IDocumentStore store = Store(static options => options.Events.RegisterTagType<TagCustomer>("customer"));

        store.Options.Events.Should().BeOfType<EventGraph>();
        ((EventGraph) store.Options.Events).TagTypes.Should().ContainSingle()
            .Which.TableSuffix.Should().Be("customer");
    }

    /// <summary>
    /// <c>AggregateAliasFor</c> answers the alias Marten writes into <c>mt_streams.type</c> for a registered
    /// aggregate - the direction the studio asks in, which only fills Marten's cache with a pair it already had.
    /// </summary>
    [Fact]
    public void A_registered_aggregates_alias_is_the_snake_cased_type_name()
    {
        IDocumentStore store = Store(static options => options.Projections.Snapshot<TimeTravelOrder>(SnapshotLifecycle.Inline));

        ((EventGraph) store.Options.Events).AggregateAliasFor(typeof(TimeTravelOrder)).Should().Be("time_travel_order");
    }

    /// <summary>
    /// The typed session-level compaction is the JasperFx default interface member Marten implements: the
    /// studio's call binds to <c>IEventStoreOperations</c>, and Marten's own <c>EventStore</c> is what runs.
    /// </summary>
    [Fact]
    public void The_typed_compaction_is_declared_on_the_event_operations_and_implemented_by_marten()
    {
        Type operations = typeof(JasperFx.Events.IEventStoreOperations);

        MethodInfo[] declared = [.. operations.GetMethods().Where(static x => x.Name == "CompactStreamAsync" && x.IsGenericMethodDefinition)];

        declared.Should().HaveCount(2, "one for Guid streams and one for string streams");
        declared.Select(static x => x.GetParameters()[0].ParameterType).Should().BeEquivalentTo(new[] { typeof(Guid), typeof(string) });

        Type eventStore = typeof(DocumentStore).Assembly.GetType("Marten.Events.EventStore", throwOnError: true)!;
        eventStore.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(static x => x.Name == "CompactStreamAsync" && x.IsGenericMethodDefinition)
            .Should().HaveCount(2, "Marten implements both typed overloads rather than inheriting the refusing defaults");
    }

    /// <summary>A request the studio configures carries no archiver, which is what the dialog says.</summary>
    [Fact]
    public void A_compaction_request_has_no_archiver_unless_the_caller_gives_it_one()
    {
        var request = new StreamCompactingRequest<TimeTravelOrder>(Guid.NewGuid()) { CancellationToken = CancellationToken.None };

        request.Archiver.Should().BeNull();
        request.Version.Should().Be(0, "0 is 'compact at the latest point in the stream'");
    }

    [Fact]
    public void A_natural_key_is_declared_on_the_aggregates_projection()
    {
        IDocumentStore store = Store(static options => options.Projections.Snapshot<NaturalKeyTicket>(SnapshotLifecycle.Inline));

        IAggregateProjection projection = store.Options.Events.Projections().OfType<IAggregateProjection>()
            .Single(x => x.AggregateType == typeof(NaturalKeyTicket));

        projection.NaturalKeyDefinition.Should().NotBeNull();
        projection.NaturalKeyDefinition!.InnerType.Should().Be<string>();
        projection.NaturalKeyDefinition.Member.Name.Should().Be(nameof(NaturalKeyTicket.Number));
        projection.NaturalKeyDefinition.IsValid().Should().BeTrue();
    }

    /// <summary>Never invoked. Every line exists so the compiler checks one signature EV depends on.</summary>
    private static async Task CompileOnlyAsync(IDocumentStore store, IMartenDatabase database, CancellationToken token)
    {
        // Compaction: the session EV opens, and the typed call it makes on it.
        SessionOptions pinned = SessionOptions.ForDatabase("acme", database);
        pinned.Timeout = 30;

        await using IDocumentSession session = store.LightweightSession(pinned);

        await session.Events.CompactStreamAsync<TimeTravelOrder>(Guid.Empty, request => request.CancellationToken = token);
        await session.Events.CompactStreamAsync<TimeTravelOrder>("key", request => request.CancellationToken = token);
        await session.SaveChangesAsync(token);

        // The facts the plan is decided on.
        IReadOnlyEventStoreOptions events = store.Options.Events;
        bool skipping = events.EnableEventSkippingInProjectionsOrSubscriptions;
        TenancyStyle tenancy = events.TenancyStyle;
        IReadOnlyList<JasperFx.Events.Subscriptions.ISubscriptionSource> projections = events.Projections();
        _ = (skipping, tenancy, projections.Count);

        // Tags.
        var graph = (EventGraph) events;
        IReadOnlyList<ITagTypeRegistration> tagTypes = graph.TagTypes;
        DcbStorageMode mode = graph.DcbStorageMode;
        string alias = graph.AggregateAliasFor(typeof(TimeTravelOrder));
        ITagTypeRegistration? found = tagTypes.FindByTagName("customer");
        _ = (mode, alias, found?.TagType, found?.SimpleType, found?.TableSuffix, found?.AggregateType);

        // LINQ over events (1.1): the source, Marten's own archive and tenant extensions, the studio's ordering, and the facts
        // the member list is computed from.
        IQueryable<IEvent> raw = ((IReadOnlyEventStore) session.Events).QueryRawEvents();
        raw = raw.Where(static x => x.MaybeArchived()).Where(static x => x.IsArchived).Where(static x => x.AnyTenant());
        raw = raw.OrderBySql("d.seq_id desc").ThenBySql("d.tenant_id");
        StreamIdentity identity = events.StreamIdentity;
        bool correlation = events.MetadataConfig.CorrelationIdEnabled;
        bool causation = events.MetadataConfig.CausationIdEnabled;
        bool userName = events.MetadataConfig.UserNameEnabled;
        bool perTenant = events.UseTenantPartitionedEvents;
        string eventsSchema = events.DatabaseSchemaName;
        _ = (identity, correlation, causation, userName, perTenant, eventsSchema, raw);

        // Natural keys.
        foreach (IAggregateProjection aggregate in projections.OfType<IAggregateProjection>())
        {
            NaturalKeyDefinition? key = aggregate.NaturalKeyDefinition;
            _ = (key?.AggregateType, key?.Member, key?.InnerType, key?.IsValid());
        }
    }

    /// <summary>
    /// <c>IReadOnlyEventStore.QueryRawEvents(string? tenantId = null)</c> (jasperfx#1094, marten#5792) is the
    /// raw-event <c>IQueryable&lt;IEvent&gt;</c>: a default interface member on the JasperFx interface, implemented by
    /// Marten's internal <c>QueryEventStore</c>. LINQ mode over events (1.1) is built on it, so it is unconditional: the
    /// floor is Marten 9.57.0 with JasperFx 2.98.0, and there is no <c>RuntimeFeature</c> probe for it. Read at
    /// <c>V9.57.0</c> (<c>QueryEventStore.cs:671-685</c>) and JasperFx <c>V2.98.0</c>.
    /// </summary>
    [Fact]
    public void Query_raw_events_is_on_the_read_only_event_store_and_implemented_by_Marten()
    {
        MethodInfo? member = typeof(IReadOnlyEventStore).GetMethod("QueryRawEvents");
        Type queryEventStore = typeof(IDocumentStore).Assembly.GetType("Marten.Events.QueryEventStore", throwOnError: true)!;

        member.Should().NotBeNull();
        member!.ReturnType.Should().Be<IQueryable<IEvent>>();
        member.GetParameters().Should().ContainSingle()
            .Which.Should().Match<ParameterInfo>(x => x.ParameterType == typeof(string) && x.IsOptional && x.Name == "tenantId");
        member.IsAbstract.Should().BeFalse("it is a default interface member: a store built against older JasperFx still loads");

        queryEventStore.GetMethod("QueryRawEvents", BindingFlags.Public | BindingFlags.Instance).Should().NotBeNull(
            "Marten {0} implements it on QueryEventStore", MartenRuntime.Current.VersionOf(RuntimePackage.Marten));
    }

    /// <summary>
    /// With no argument it is the caller's own session's <c>Query&lt;IEvent&gt;()</c> - the one the studio pinned to the
    /// resolved database and tenant - and that is what <c>MartenLinqCommand.IsOnSession</c> checks at run time.
    /// </summary>
    [Fact]
    public void Query_raw_events_with_no_argument_is_a_Marten_query_on_the_callers_own_session()
    {
        using DocumentStore store = Store(static options => options.Events.TenancyStyle = TenancyStyle.Conjoined);
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase("a", store.Storage.Database));
        using IDocumentSession other = store.LightweightSession(SessionOptions.ForDatabase("a", store.Storage.Database));

        IQueryable<IEvent> raw = ((IReadOnlyEventStore) session.Events).QueryRawEvents();

        MartenLinqCommand.IsOnSession(raw, session).Should().BeTrue();
        MartenLinqCommand.IsOnSession(raw, other).Should().BeFalse("a different session is a different answer");
    }

    /// <summary>
    /// The negative paths of the run-time check: a queryable that is not Marten's at all (LINQ to objects), and a Marten
    /// queryable built from a different session, are both refused - the check answers yes only for the session it was given.
    /// </summary>
    [Fact]
    public void IsOnSession_refuses_a_queryable_that_is_not_Martens_and_one_from_a_different_session()
    {
        using DocumentStore store = Store(static options => options.Events.TenancyStyle = TenancyStyle.Conjoined);
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase("a", store.Storage.Database));
        using IDocumentSession other = store.LightweightSession(SessionOptions.ForDatabase("a", store.Storage.Database));

        IQueryable<IEvent> inMemory = Array.Empty<IEvent>().AsQueryable();
        IQueryable<IEvent> fromOther = ((IReadOnlyEventStore) other.Events).QueryRawEvents();

        MartenLinqCommand.IsOnSession(inMemory, session).Should().BeFalse("LINQ to objects is not a Marten query at all");
        MartenLinqCommand.IsOnSession(fromOther, session).Should().BeFalse("it belongs to the other session");
        MartenLinqCommand.IsOnSession(fromOther, other).Should().BeTrue("and the same queryable is on its own session");
    }

    /// <summary>
    /// Why the studio never passes the argument: for a tenant other than the session's, Marten returns a query on
    /// <c>_store.QuerySession(tenantId)</c> - another session, which ignores the database the studio resolved and is never
    /// disposed. (<c>QueryRawEventsTakesNoTenantTests</c> holds the call sites to the empty list.)
    /// </summary>
    [Fact]
    public void Query_raw_events_with_another_tenant_is_on_another_session_and_that_is_why_it_is_never_passed()
    {
        using DocumentStore store = Store(static options => options.Events.TenancyStyle = TenancyStyle.Conjoined);
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase("a", store.Storage.Database));

        IQueryable<IEvent> other = ((IReadOnlyEventStore) session.Events).QueryRawEvents("b");

        MartenLinqCommand.IsOnSession(other, session).Should().BeFalse();
    }

    [Fact]
    public void Query_raw_events_with_a_tenant_on_events_with_no_tenant_dimension_throws()
    {
        using DocumentStore store = Store(static _ => { });
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase("a", store.Storage.Database));

        Action call = () => ((IReadOnlyEventStore) session.Events).QueryRawEvents("b");

        call.Should().Throw<NotSupportedException>();
    }

    private static DocumentStore Store(Action<StoreOptions> configure) =>
        DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            configure(options);
        });
}
