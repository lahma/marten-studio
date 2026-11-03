using System.Linq.Expressions;

using JasperFx.Events;

using Marten;

using MartenStudio.Services.Query;

// The overloads Dynamic LINQ produces (StartsWith(string), an in-list's inline array) are the ones under test.
#pragma warning disable CA1861, CA1866

namespace MartenStudio.Tests.Query;

/// <summary>
/// The event graph and the walls that read it (1.1): the root restriction, the screen's refusals with a caret, and the
/// guard on hand-built <c>IEvent</c> lambdas. The graph is the one wall both the screen and the guard go through, so one
/// restriction holds both.
/// </summary>
public class LinqEventScreenTests
{
    private static LinqDocumentGraph Graph(string? tenant = null, Action<StoreOptions>? configure = null)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore(configure);

        return LinqEventTestSupport.Graph(store, tenant);
    }

    // ------------------------------------------------------------------------------------------------
    // The graph
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_graph_offers_exactly_the_closed_list_and_hints_it_in_the_lists_order()
    {
        LinqDocumentGraph graph = Graph();

        graph.IsEvents.Should().BeTrue();
        graph.Root.Should().Be<IEvent>();
        graph.HintMembers.Should().Equal(
            "Sequence", "Timestamp", "EventTypeName", "StreamId", "Version", "TenantId", "CorrelationId", "CausationId", "UserName",
            "DotNetTypeName", "Id");
        graph.MemberNames.Should().BeEquivalentTo(graph.HintMembers);
    }

    [Fact]
    public void The_unrestricted_IEvent_has_scalars_the_graph_withholds_and_leaves_out_the_rest()
    {
        // The probe's finding, pinned: the unrestricted graph is the scalar members only - Data (object), EventType (Type),
        // Headers (a dictionary) and Tags (a JasperFx collection) never make it - and the restriction takes more away.
        LinqDocumentGraph unrestricted = LinqDocumentGraph.For(typeof(IEvent), new HashSet<Type>());

        unrestricted.MemberNames.Should().NotContain(["Data", "EventType", "Headers", "Tags"]);
        unrestricted.MemberNames.Should().Contain(["IsArchived", "IsSkipped", "AggregateTypeName", "StreamKey"]);

        LinqDocumentGraph restricted = Graph();

        restricted.MemberNames.Should().NotContain(["IsArchived", "IsSkipped", "AggregateTypeName", "StreamKey"]);
    }

    [Fact]
    public void The_restriction_follows_the_stores_identity_metadata_and_tenant()
    {
        Graph(tenant: "a").MemberNames.Should().NotContain("TenantId");
        Graph().MemberNames.Should().Contain("TenantId");

        LinqDocumentGraph text = Graph(configure: static options => options.Events.StreamIdentity = StreamIdentity.AsString);
        text.MemberNames.Should().Contain("StreamKey").And.NotContain("StreamId");
    }

    [Fact]
    public void A_withheld_member_is_not_found_by_the_graph_whichever_way_it_is_asked()
    {
        LinqDocumentGraph graph = Graph(tenant: "a");

        graph.Bind(typeof(IEvent), "IsArchived").Should().BeNull();
        graph.Bind(typeof(IEvent), "isarchived").Should().BeNull("Dynamic LINQ matches without case, and so does the graph's refusal");
        graph.Bind(typeof(IEvent), "TenantId").Should().BeNull();
        graph.Bind(typeof(IEvent), "Sequence").Should().NotBeNull();
        graph.TryGetMember(typeof(IEvent), typeof(IEvent).GetProperty(nameof(IEvent.IsArchived))!, out _).Should().BeFalse();
        graph.TryGetMember(typeof(IEvent), typeof(IEvent).GetProperty(nameof(IEvent.Version))!, out _).Should().BeTrue();
    }

    [Fact]
    public void A_documents_graph_is_not_the_events_and_is_not_restricted()
    {
        LinqDocumentGraph documents = LinqTestStore.Graph<LinqOrder>();

        documents.IsEvents.Should().BeFalse();
        documents.MemberNames.Should().Contain("Name");
    }

    // ------------------------------------------------------------------------------------------------
    // The screen
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("IsArchived", 0)]
    [InlineData("Version > 1 and IsArchived == false", 16)]
    [InlineData("IsSkipped", 0)]
    [InlineData("AggregateTypeName == \"x\"", 0)]
    [InlineData("StreamKey == \"x\"", 0)]
    [InlineData("TenantId == \"b\"", 0)]
    [InlineData("Data == null", 0)]
    [InlineData("EventType == null", 0)]
    [InlineData("Headers.Count > 0", 0)]
    [InlineData("Tags.Any(it == \"x\")", 0)]
    public void The_screen_refuses_a_withheld_member_and_points_at_it(string predicate, int position)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqRefusal? refusal = LinqEventTestSupport.Screen(store, "a", predicate);

        refusal.Should().NotBeNull();
        refusal!.Clause.Should().Be(LinqClause.Where);
        refusal.Position.Should().Be(position);
        refusal.Message.Should().Contain("not a member LINQ mode can query here");
    }

    [Fact]
    public void The_screen_refuses_the_wrong_streams_identity_member_in_either_kind_of_store()
    {
        using DocumentStore guid = LinqEventTestSupport.Store();
        using DocumentStore text = LinqEventTestSupport.Store(static options => options.Events.StreamIdentity = StreamIdentity.AsString);

        LinqEventTestSupport.Screen(guid, null, "StreamKey == \"a\"").Should().NotBeNull();
        LinqEventTestSupport.Screen(guid, null, "StreamId == \"aaaaaaaa-0000-0000-0000-000000000001\"").Should().BeNull();
        LinqEventTestSupport.Screen(text, null, "StreamId == \"aaaaaaaa-0000-0000-0000-000000000001\"").Should().NotBeNull();
        LinqEventTestSupport.Screen(text, null, "StreamKey == \"a\"").Should().BeNull();
    }

    [Fact]
    public void The_screen_refuses_metadata_the_store_does_not_record()
    {
        using DocumentStore bare = LinqEventTestSupport.Store();
        using DocumentStore recording = LinqEventTestSupport.ConjoinedStore();

        foreach (string member in new[] { "CorrelationId", "CausationId", "UserName" })
        {
            LinqEventTestSupport.Screen(bare, null, member + " == \"x\"").Should().NotBeNull(member + " is not recorded by a bare store");
            LinqEventTestSupport.Screen(recording, null, member + " == \"x\"").Should().BeNull(member + " is recorded");
        }
    }

    [Theory]
    [InlineData("IsArchived", "is not an event member that can be ordered by")]
    [InlineData("Version, IsSkipped desc", "is not an event member that can be ordered by")]
    [InlineData("Data", "is not an event member that can be ordered by")]
    public void An_ordering_that_names_a_withheld_member_says_event_and_not_document(string orderBy, string message)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqRefusal? refusal = LinqEventTestSupport.Screen(store, "a", string.Empty, orderBy);

        refusal.Should().NotBeNull();
        refusal!.Clause.Should().Be(LinqClause.OrderBy);
        refusal.Message.Should().Contain(message).And.NotContain("document type");
    }

    [Fact]
    public void An_ordering_a_document_graph_refuses_still_says_document_type()
    {
        LinqRefusal? refusal = LinqTextScreen.Check(LinqClause.OrderBy, "Nope", LinqTestStore.Graph<LinqOrder>());

        refusal!.Message.Should().Contain("not a member of this document type that can be ordered by");
    }

    [Theory]
    [InlineData("Sequence desc, Timestamp")]
    [InlineData("EventTypeName, Version desc")]
    public void An_ordering_on_offered_members_is_let_through(string orderBy)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqEventTestSupport.Screen(store, null, string.Empty, orderBy).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------
    // The guard, on hand-built IEvent lambdas
    // ------------------------------------------------------------------------------------------------

    private static LinqExpressionGuard Guard(string? tenant = "a")
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        return new LinqExpressionGuard(LinqEventTestSupport.Graph(store, tenant), static _ => null, jsonEnumsAsName: false);
    }

    public static TheoryData<string, Expression<Func<IEvent, bool>>> Accepted() => new()
    {
        { "a sequence", x => x.Sequence > 5 },
        { "a type name", x => x.EventTypeName == "order_placed" },
        { "a type name prefix", x => x.EventTypeName.StartsWith("order") },
        { "a version range", x => x.Version >= 1 && x.Version < 10 },
        { "a nullable metadata member", x => x.CorrelationId == null },
        { "a metadata text", x => x.UserName != null && x.UserName.StartsWith("a") },
        { "an in-list of type names", x => Enumerable.Contains(new[] { "a", "b" }, x.EventTypeName) },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void The_guard_accepts_the_events_dialect(string what, Expression<Func<IEvent, bool>> predicate)
    {
        Guard().CheckPredicate(predicate).Should().BeNull(what);
    }

    public static TheoryData<string, Expression<Func<IEvent, bool>>> Refused() => new()
    {
        { "IsArchived", x => x.IsArchived },
        { "IsArchived compared", x => x.IsArchived == true },
        { "IsSkipped", x => x.IsSkipped },
        { "AggregateTypeName", x => x.AggregateTypeName == "a" },
        { "StreamKey on a Guid store", x => x.StreamKey == "a" },
        { "TenantId where a tenant is selected", x => x.TenantId == "b" },
        { "Data", x => x.Data == null },
        { "Headers", x => x.Headers != null },
        { "Tags", x => x.Tags != null },
        { "EventType", x => x.EventType == null },
        { "Marten's own AnyTenant", x => x.AnyTenant() },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void The_guard_refuses_what_the_graph_withholds_with_no_screen_in_front(string what, Expression<Func<IEvent, bool>> predicate)
    {
        Guard().CheckPredicate(predicate).Should().NotBeNull(what);
    }

    [Fact]
    public void The_guard_refuses_an_ordering_key_the_graph_withholds()
    {
        Guard().CheckOrderKey((Expression<Func<IEvent, bool>>) (x => x.IsArchived)).Should().NotBeNull();
        Guard().CheckOrderKey((Expression<Func<IEvent, long>>) (x => x.Sequence)).Should().BeNull();
    }

    [Fact]
    public void TenantId_is_accepted_by_the_guard_where_no_tenant_is_selected()
    {
        Guard(tenant: null).CheckPredicate((Expression<Func<IEvent, bool>>) (x => x.TenantId == "b")).Should().BeNull();
    }
}
