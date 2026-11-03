using System.Reflection;

using JasperFx.Events;

using Marten;
using Marten.Services;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The closed list of <c>IEvent</c> members LINQ mode offers (1.1), and the pin of Marten's <c>EventQueryMapping</c> it is
/// computed from: at the Marten this run resolved (the floor and the latest are both 9.57.0), every offered member builds
/// to a column and no withheld one is quietly read from the JSON body.
/// </summary>
/// <remarks>
/// A newer Marten that moves a member into or out of <c>EventQueryMapping</c> fails here at the next latest-tested bump,
/// before a visitor can filter on something that silently matches nothing - and the run-time check of
/// <see cref="LinqQuerySql.ReadsJsonBody" /> is the second wall.
/// </remarks>
public class LinqEventMembersTests
{
    private static readonly Guid StreamGuid = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    // ------------------------------------------------------------------------------------------------
    // The list
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_guid_store_with_every_metadata_column_offers_the_whole_list_in_the_hints_order()
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqEventMembers.Offered(store.ReadOnlyEvents(), tenantNarrowed: false).Should().Equal(
            "Sequence", "Timestamp", "EventTypeName", "StreamId", "Version", "TenantId", "CorrelationId", "CausationId", "UserName",
            "DotNetTypeName", "Id");
    }

    [Fact]
    public void A_string_store_offers_StreamKey_and_not_StreamId()
    {
        using DocumentStore store = LinqEventTestSupport.Store(static options => options.Events.StreamIdentity = StreamIdentity.AsString);

        string[] offered = [.. LinqEventMembers.Offered(store.ReadOnlyEvents(), tenantNarrowed: false)];

        offered.Should().Contain("StreamKey").And.NotContain("StreamId");
    }

    [Fact]
    public void Metadata_the_store_does_not_record_is_not_offered_and_says_why()
    {
        using DocumentStore store = LinqEventTestSupport.Store();

        LinqEventTargetInfo info = LinqEventMembers.Describe(store.ReadOnlyEvents(), tenant: null);

        info.Members.Should().NotContain(["CorrelationId", "CausationId", "UserName"]);
        info.Withheld.Single(static x => x.Name == "CorrelationId").Reason.Should().Contain("CorrelationIdEnabled is false");
        info.Withheld.Single(static x => x.Name == "CausationId").Reason.Should().Contain("CausationIdEnabled is false");
        info.Withheld.Single(static x => x.Name == "UserName").Reason.Should().Contain("UserNameEnabled is false");
    }

    [Fact]
    public void TenantId_is_withheld_exactly_when_Marten_already_narrows_to_a_tenant()
    {
        using DocumentStore conjoined = LinqEventTestSupport.ConjoinedStore();
        using DocumentStore single = LinqEventTestSupport.Store();

        LinqEventMembers.Describe(conjoined.ReadOnlyEvents(), "acme").Members.Should().NotContain("TenantId");
        LinqEventMembers.Describe(conjoined.ReadOnlyEvents(), "acme").Withheld.Single(static x => x.Name == "TenantId")
            .Reason.Should().Be("Marten already narrows this query to tenant acme.");
        LinqEventMembers.Describe(conjoined.ReadOnlyEvents(), tenant: null).Members.Should().Contain("TenantId");

        // Single-tenanted events: Marten adds no tenant term at all, so there is nothing for TenantId to be redundant with.
        LinqEventMembers.Describe(single.ReadOnlyEvents(), "acme").Members.Should().Contain("TenantId");
    }

    [Fact]
    public void Every_withheld_member_has_a_sentence_and_the_unqueryable_ones_are_named_together()
    {
        using DocumentStore store = LinqEventTestSupport.Store();

        LinqEventTargetInfo info = LinqEventMembers.Describe(store.ReadOnlyEvents(), tenant: null);

        info.Withheld.Should().OnlyContain(static x => x.Reason.Length > 0 && x.Reason.EndsWith('.'));
        info.Withheld.Select(static x => x.Name).Should().Contain(["IsArchived", "StreamKey", "IsSkipped", "AggregateTypeName", "Data, Headers, Tags, EventType"]);
        info.Withheld.Single(static x => x.Name == "IsArchived").Reason.Should().Contain("Archived selector");
        info.Withheld.Single(static x => x.Name == "Data, Headers, Tags, EventType").Reason.Should().Contain("docs/linq-mode.md#events");
    }

    [Fact]
    public void The_default_order_is_newest_first_and_by_timestamp_across_tenants_with_per_tenant_sequences()
    {
        using DocumentStore plain = LinqEventTestSupport.ConjoinedStore();
        using DocumentStore partitioned = LinqEventTestSupport.ConjoinedStore(static options => options.Events.UseTenantPartitionedEvents = true);

        LinqEventMembers.Describe(plain.ReadOnlyEvents(), tenant: null).DefaultOrdering.Should().Be("Sequence desc");
        LinqEventMembers.Describe(partitioned.ReadOnlyEvents(), tenant: null).DefaultOrdering.Should().Be("Timestamp desc");
        LinqEventMembers.Describe(partitioned.ReadOnlyEvents(), "acme").DefaultOrdering.Should().Be("Sequence desc");
        LinqEventMembers.Describe(partitioned.ReadOnlyEvents(), tenant: null).PerTenantSequences.Should().BeTrue();
    }

    [Fact]
    public void The_events_schema_is_the_one_the_Configuration_page_shows()
    {
        using DocumentStore store = LinqEventTestSupport.Store(static options => options.Events.DatabaseSchemaName = "evt");

        LinqEventMembers.Describe(store.ReadOnlyEvents(), tenant: null).EventsSchema.Should().Be("evt");
    }

    // ------------------------------------------------------------------------------------------------
    // The pin: every offered member is a column
    // ------------------------------------------------------------------------------------------------

    private static readonly (string Member, string Predicate, string Column)[] Offered =
    [
        ("Sequence", "Sequence > 5", "d.seq_id"),
        ("Timestamp", "Timestamp > \"2026-01-01T00:00:00Z\"", "d.timestamp"),
        ("EventTypeName", "EventTypeName == \"order_placed\"", "d.type"),
        ("StreamId", "StreamId == \"" + "aaaaaaaa-0000-0000-0000-000000000001" + "\"", "d.stream_id"),
        ("Version", "Version >= 2", "d.version"),
        ("TenantId", "TenantId == \"acme\"", "d.tenant_id"),
        ("CorrelationId", "CorrelationId == \"c1\"", "d.correlation_id"),
        ("CausationId", "CausationId == \"k1\"", "d.causation_id"),
        ("UserName", "UserName == \"ada\"", "d.user_name"),
        ("DotNetTypeName", "DotNetTypeName == \"X\"", "d.mt_dotnet_type"),
        ("Id", "Id == \"" + "aaaaaaaa-0000-0000-0000-000000000002" + "\"", "d.id"),
    ];

    public static TheoryData<string, string, string> OfferedMembers()
    {
        var data = new TheoryData<string, string, string>();

        foreach ((string member, string predicate, string column) in Offered)
        {
            data.Add(member, predicate, column);
        }

        return data;
    }

    [DynamicLinqTheory]
    [MemberData(nameof(OfferedMembers))]
    public void An_offered_members_predicate_is_a_column_and_never_the_JSON_body(string member, string predicate, string column)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqEventMembers.Offered(store.ReadOnlyEvents(), tenantNarrowed: false).Should().Contain(member);

        string sql = LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, predicate));

        sql.Should().Contain(column + " ");
        LinqQuerySql.ReadsJsonBody(sql).Should().BeFalse("'{0}' is a column in Marten's EventQueryMapping: {1}", member, sql);
    }

    [DynamicLinqTheory]
    [MemberData(nameof(OfferedMembers))]
    public void An_offered_members_ordering_is_a_column_and_never_the_JSON_body(string member, string _, string column)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        foreach (string order in new[] { member, member + " desc" })
        {
            string sql = LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, string.Empty, order));

            sql.Should().Contain("order by " + column);
            LinqQuerySql.ReadsJsonBody(sql).Should().BeFalse("ordering by {0}: {1}", member, sql);
        }
    }

    [DynamicLinqFact]
    public void StreamKey_is_a_column_in_a_string_store()
    {
        using DocumentStore store = LinqEventTestSupport.Store(static options => options.Events.StreamIdentity = StreamIdentity.AsString);

        string sql = LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, "StreamKey == \"order/1\""));

        sql.Should().Contain("d.stream_id ");
        LinqQuerySql.ReadsJsonBody(sql).Should().BeFalse();
    }

    // ------------------------------------------------------------------------------------------------
    // The pin: every withheld member really is a body read, or Marten's archive filter replaced
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Marten, asked directly with no screen, for each member the studio withholds because Marten reads the body: the
    /// statement says <c>d.data</c>. This is the reason they are withheld, and the test that fails when Marten starts
    /// mapping one - at which point the list may offer it.
    /// </summary>
    [Fact]
    public void A_withheld_member_Marten_keeps_no_column_for_is_read_from_the_JSON_body()
    {
        using DocumentStore guid = LinqEventTestSupport.Store();
        using DocumentStore text = LinqEventTestSupport.Store(static options => options.Events.StreamIdentity = StreamIdentity.AsString);

        (DocumentStore Store, string Why, Func<IQueryable<IEvent>, IQueryable<IEvent>> Query)[] cases =
        [
            (guid, "StreamKey on a Guid store", static q => q.Where(static x => x.StreamKey == "a")),
            (text, "StreamId on a string store", static q => q.Where(static x => x.StreamId == StreamGuid)),
            (guid, "CorrelationId where it is not recorded", static q => q.Where(static x => x.CorrelationId == "c")),
            (guid, "CausationId where it is not recorded", static q => q.Where(static x => x.CausationId == "c")),
            (guid, "UserName where it is not recorded", static q => q.Where(static x => x.UserName == "u")),
            (guid, "IsSkipped", static q => q.Where(static x => x.IsSkipped)),
            (guid, "AggregateTypeName", static q => q.Where(static x => x.AggregateTypeName == "a")),
        ];

        foreach ((DocumentStore store, string why, Func<IQueryable<IEvent>, IQueryable<IEvent>> query) in cases)
        {
            using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));
            IQueryable<IEvent> raw = query(((IReadOnlyEventStore) session.Events).QueryRawEvents());

            LinqQuerySql.ReadsJsonBody(MartenLinqCommand.Build(raw, session.TenantId).Command.CommandText)
                .Should().BeTrue("{0} is withheld because Marten reads it from d.data", why);
        }
    }

    /// <summary>
    /// <c>IsArchived</c> is not a body read - it is worse: a predicate that names it <em>replaces</em> Marten's own
    /// <c>is_archived = FALSE</c>, so the Archived selector's default could not be trusted if a visitor could name it.
    /// </summary>
    [Fact]
    public void A_predicate_naming_IsArchived_replaces_Martens_own_archive_filter()
    {
        using DocumentStore store = LinqEventTestSupport.Store();
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));
        IQueryable<IEvent> raw = ((IReadOnlyEventStore) session.Events).QueryRawEvents();

        string plain = MartenLinqCommand.Build(raw.Where(static x => x.Sequence > 0), session.TenantId).Command.CommandText;
        string named = MartenLinqCommand.Build(raw.Where(static x => x.IsArchived == true), session.TenantId).Command.CommandText;

        plain.Should().Contain("d.is_archived = FALSE");
        named.Should().NotContain("d.is_archived = FALSE", "naming IsArchived replaces Marten's own filter");
    }

    [Fact]
    public void Every_scalar_IEvent_member_is_offered_or_withheld_with_a_reason()
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqEventTargetInfo info = LinqEventMembers.Describe(store.ReadOnlyEvents(), "acme");
        string withheldText = string.Join(" ", info.Withheld.Select(static x => x.Name));

        string[] scalars =
        [
            .. typeof(IEvent).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static x => LinqDocumentGraph.IsScalar(x.PropertyType))
                .Select(static x => x.Name),
        ];

        scalars.Should().NotBeEmpty();

        foreach (string name in scalars)
        {
            (info.Members.Contains(name) || withheldText.Contains(name, StringComparison.Ordinal))
                .Should().BeTrue("the scalar IEvent.{0} must be offered or withheld with a sentence, never left to fall through", name);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // The pin: Marten's own tenant term holds whatever a predicate says about TenantId
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The reason <c>TenantId</c> is withheld when a tenant is selected is redundancy, not a hole: at this Marten the
    /// tenant term survives any predicate. If a later Marten dropped it when a predicate named the column, this fails -
    /// and the withholding is what would have kept the widening out of reach.
    /// </summary>
    [DynamicLinqTheory]
    [InlineData("TenantId == \"b\"")]
    [InlineData("TenantId != \"a\"")]
    [InlineData("not (TenantId == \"a\") or Version > 0")]
    [InlineData("TenantId == \"b\" or TenantId == \"a\"")]
    [InlineData("TenantId in (\"b\", \"c\")")]
    public void Marten_keeps_its_tenant_term_beside_a_predicate_that_names_TenantId(string predicate)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        ComposedLinqQuery composed = LinqEventTestSupport.Compose(store, "a", predicate, offerTenantId: true);
        string sql = LinqEventTestSupport.Sql(composed);

        sql.Should().MatchRegex(@"and\s+d\.tenant_id = :p\d+", "Marten's tenant term is a top-level conjunct, whatever the predicate says");

        NpgsqlParameter[] parameters = [.. composed.Command!.Parameters.Cast<NpgsqlParameter>()];
        parameters.Select(static x => x.Value).Should().Contain("a", "the term is bound to the session's tenant");
        parameters.Count(static x => Equals(x.Value, "a")).Should().BeGreaterThanOrEqualTo(1);
    }

    // ------------------------------------------------------------------------------------------------
    // The composition: the studio's terms
    // ------------------------------------------------------------------------------------------------

    [DynamicLinqFact]
    public void A_tenant_selected_on_conjoined_events_binds_that_tenant_and_no_AnyTenant()
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        ComposedLinqQuery composed = LinqEventTestSupport.Compose(store, "a", "Version > 0");

        LinqEventTestSupport.Sql(composed).Should().Contain("d.tenant_id = :p").And.NotContain("1=1");
        composed.Command!.Parameters.Cast<NpgsqlParameter>().Select(static x => x.Value).Should().Contain("a");
    }

    [DynamicLinqFact]
    public void No_tenant_selected_on_conjoined_events_reads_every_tenant_and_orders_by_the_tenant_last()
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        string sql = LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, string.Empty));

        sql.Should().Contain("1=1").And.Contain("order by d.seq_id desc, d.tenant_id");
    }

    [DynamicLinqFact]
    public void Single_tenanted_events_carry_no_tenant_term_even_with_a_tenant_selected()
    {
        using DocumentStore store = LinqEventTestSupport.Store();

        string sql = LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, "a", "Version > 0"));

        sql.Should().NotContain("d.tenant_id", "Marten adds no tenant term: reach, not scope, is the service's question");
    }

    [DynamicLinqFact]
    public void The_default_order_is_the_sequence_descending_and_a_typed_order_is_broken_by_it()
    {
        using DocumentStore store = LinqEventTestSupport.Store();

        LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, string.Empty))
            .Should().Contain("order by d.seq_id desc");
        LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, string.Empty, "Version"))
            .Should().Contain("order by d.version, d.seq_id desc");
    }

    [DynamicLinqFact]
    public void Across_tenants_with_per_tenant_sequences_the_default_order_is_the_timestamp()
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore(static options => options.Events.UseTenantPartitionedEvents = true);

        LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, string.Empty))
            .Should().Contain("order by d.timestamp desc, d.seq_id desc, d.tenant_id");
    }

    [DynamicLinqTheory]
    [InlineData("Exclude", "d.is_archived = FALSE")]
    [InlineData("Include", "1 = 1")]
    [InlineData("Only", "d.is_archived = TRUE")]
    public void The_archived_selector_is_Martens_own_filter(string selector, string expected)
    {
        ArchivedFilter archived = Enum.Parse<ArchivedFilter>(selector);
        using DocumentStore store = LinqEventTestSupport.Store();

        string sql = LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, string.Empty, archived: archived));

        sql.Should().Contain(expected);
        if (archived != ArchivedFilter.Exclude)
        {
            sql.Should().NotContain("d.is_archived = FALSE");
        }
    }

    [DynamicLinqFact]
    public void The_page_asks_one_row_past_it()
    {
        using DocumentStore store = LinqEventTestSupport.Store();

        ComposedLinqQuery composed = LinqEventTestSupport.Compose(store, tenant: null, string.Empty, offset: 100);

        LinqEventTestSupport.Sql(composed).Should().Contain("OFFSET").And.Contain("LIMIT");
        composed.Command!.Parameters.Cast<NpgsqlParameter>().Select(static x => x.Value).Should().Contain(100).And.Contain(51);
    }

    [DynamicLinqTheory]
    [InlineData("EventTypeName in (\"a\", \"b\")", "= ANY")]
    [InlineData("EventTypeName.StartsWith(\"order\")", "LIKE")]
    [InlineData("CorrelationId == null", "is null")]
    [InlineData("Timestamp >= \"2026-11-01T00:00:00+02:00\" and Timestamp < \"2026-11-02\"", "d.timestamp")]
    public void The_dialect_Marten_translates_reaches_SQL(string predicate, string expected)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqEventTestSupport.Sql(LinqEventTestSupport.Compose(store, tenant: null, predicate))
            .Should().ContainEquivalentOf(expected);
    }

    [DynamicLinqFact]
    public void A_Timestamp_literal_is_bound_as_the_instant_it_names()
    {
        using DocumentStore store = LinqEventTestSupport.Store();

        ComposedLinqQuery composed = LinqEventTestSupport.Compose(store, tenant: null, "Timestamp > \"2026-11-01T02:00:00+02:00\"");

        composed.Command!.Parameters.Cast<NpgsqlParameter>().Select(static x => x.Value).OfType<DateTimeOffset>().Should()
            .ContainSingle().Which.UtcDateTime.Should().Be(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [DynamicLinqFact]
    public void Marten_refuses_an_expression_it_cannot_translate_and_the_refusal_is_Martens_first_line()
    {
        using DocumentStore store = LinqEventTestSupport.Store();

        ComposedLinqQuery composed = LinqEventTestSupport.Compose(store, tenant: null, "EventTypeName.Length > 3");

        composed.Refusal.Should().NotBeNull();
        composed.Refusal!.Stage.Should().Be(LinqStage.Translate);
        composed.Refusal.Message.Should().StartWith("Marten cannot translate this query: ");
        composed.Command.Should().BeNull();
    }
}
