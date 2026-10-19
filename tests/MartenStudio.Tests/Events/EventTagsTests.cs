using JasperFx.MultiTenancy;

using Marten;
using Marten.Events;

using MartenStudio.Services.Events;
using MartenStudio.Services.Query;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Events;

/// <summary>A strong-typed id registered as a DCB tag type: one public property and a constructor taking it.</summary>
/// <param name="Value">The customer.</param>
public sealed record TagCustomer(string Value);

/// <summary>A second tag type, keyed by GUID, registered under a suffix that spells the first type's CLR name.</summary>
/// <param name="Value">The region.</param>
public sealed record TagRegion(Guid Value);

/// <summary>
/// F4's model: reading the store's tag configuration, the <c>tag:</c> filter's syntax and values, the index
/// verdict, and pairing a tag read with its events - against real Marten registrations, no Postgres.
/// </summary>
public class EventTagsTests
{
    // ------------------------------------------------------------------------------------------------
    // The model, from StoreOptions
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_store_with_no_tag_type_has_no_tags()
    {
        EventTagModel model = Model(static _ => { });

        model.Storage.Should().Be(EventTagStorage.None);
        model.Types.Should().BeEmpty();
    }

    [Fact]
    public void A_registered_tag_type_is_a_tag_table_named_after_its_suffix()
    {
        EventTagModel model = Model(static options => options.Events.RegisterTagType<TagCustomer>("customer"));

        model.Storage.Should().Be(EventTagStorage.TagTables);
        EventTagTypeModel type = model.Types.Should().ContainSingle().Subject;
        type.Name.Should().Be(nameof(TagCustomer));
        type.Suffix.Should().Be("customer");
        type.SimpleType.Should().Be<string>();
        type.Table.Should().Be("mt_event_tag_customer");
        model.IsConjoined.Should().BeFalse();
    }

    [Fact]
    public void Hstore_mode_and_conjoined_tenancy_are_read_off_the_store()
    {
        EventTagModel model = Model(static options =>
        {
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.DcbStorageMode = DcbStorageMode.HStore;
            options.Events.RegisterTagType<TagCustomer>("customer");
        });

        model.Storage.Should().Be(EventTagStorage.HStore);
        model.IsConjoined.Should().BeTrue();
    }

    /// <summary>
    /// A tag type keeps its table and its filter when its aggregate is hidden - a tag is not that aggregate's
    /// state - but the hidden aggregate's name is not put on the screen.
    /// </summary>
    [Fact]
    public void A_hidden_aggregate_is_not_named_beside_its_tag_type()
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            options.Events.RegisterTagType<TagCustomer>("customer").ForAggregate<TimeTravelOrder>();
        });

        EventTagModel.From(store.Options).Types.Single().AggregateName.Should().Be(nameof(TimeTravelOrder));

        EventTagTypeModel hidden = EventTagModel.From(store.Options, static type => type != typeof(TimeTravelOrder)).Types.Single();
        hidden.AggregateName.Should().BeNull();
        hidden.Table.Should().Be("mt_event_tag_customer", "the tag type itself is still listed");
    }

    /// <summary>
    /// JasperFx's own rule: the CLR name first across every type, then the suffix - so a suffix that
    /// collides with another type's name cannot shadow that type.
    /// </summary>
    [Fact]
    public void A_name_is_found_by_clr_name_before_suffix_ignoring_case()
    {
        EventTagModel model = Model(static options =>
        {
            options.Events.RegisterTagType<TagCustomer>("customer");
            options.Events.RegisterTagType<TagRegion>("tagcustomer");
        });

        model.Find("tagcustomer")!.Name.Should().Be(nameof(TagCustomer),
            "the CLR name TagCustomer wins over the other type's suffix tagcustomer");
        model.Find("CUSTOMER")!.Name.Should().Be(nameof(TagCustomer), "by its own suffix");
        model.Find("tagregion")!.Suffix.Should().Be("tagcustomer");
        model.Find("nothing").Should().BeNull();
        model.Find("  ").Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------------
    // The tag: filter
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("tag:customer=acme-1", "customer", "acme-1")]
    [InlineData("customer=acme-1", "customer", "acme-1")]
    [InlineData("TAG:customer = acme-1 ", "customer", "acme-1")]
    [InlineData("tag:key=a=b", "key", "a=b")]
    public void A_filter_is_type_equals_value_with_an_optional_prefix(string raw, string type, string value)
    {
        EventTagFilter filter = EventTagFilter.Parse(raw)!;

        filter.TypeName.Should().Be(type);
        filter.Value.Should().Be(value);
        filter.Token.Should().Be(type + "=" + value);
        filter.Display.Should().Be("tag:" + type + "=" + value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("customer")]
    [InlineData("tag:=acme")]
    [InlineData("customer=")]
    [InlineData("tag: = ")]
    public void Anything_else_is_not_a_filter(string? raw) =>
        EventTagFilter.Parse(raw).Should().BeNull();

    [Theory]
    [InlineData(typeof(string), " acme ", "acme")]
    [InlineData(typeof(int), "42", 42)]
    [InlineData(typeof(long), "9000000000", 9_000_000_000L)]
    [InlineData(typeof(short), "7", (short) 7)]
    public void A_value_is_parsed_to_its_tag_types_primitive(Type simpleType, string text, object expected)
    {
        EventTagValues.TryParse(simpleType, text, out object? value, out string? error).Should().BeTrue();

        value.Should().Be(expected);
        error.Should().BeNull();
    }

    [Fact]
    public void A_guid_tag_value_is_parsed_as_a_guid()
    {
        EventTagValues.TryParse(typeof(Guid), "8f1d5a6e-1a2b-4c3d-9e8f-000000000001", out object? value, out _).Should().BeTrue();

        value.Should().Be(Guid.Parse("8f1d5a6e-1a2b-4c3d-9e8f-000000000001"));
    }

    [Fact]
    public void A_value_its_type_cannot_hold_is_an_answer_that_names_the_type()
    {
        EventTagValues.TryParse(typeof(int), "acme", out _, out string? error).Should().BeFalse();

        error.Should().Contain("'acme'").And.Contain("Int32");
    }

    /// <summary>The hstore column holds Marten's own text for a value, so the containment test must too.</summary>
    [Fact]
    public void An_hstore_value_is_written_the_way_marten_writes_it()
    {
        EventTagValues.ToHStoreText(Guid.Parse("8F1D5A6E-1A2B-4C3D-9E8F-000000000001"))
            .Should().Be("8f1d5a6e-1a2b-4c3d-9e8f-000000000001");
        EventTagValues.ToHStoreText(42L).Should().Be("42");
        EventTagValues.ToHStoreText("acme").Should().Be("acme");
    }

    // ------------------------------------------------------------------------------------------------
    // The index verdict
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("CREATE UNIQUE INDEX pk_mt_event_tag_customer ON s.mt_event_tag_customer USING btree (value, seq_id)", true)]
    [InlineData("CREATE UNIQUE INDEX pk_mt_event_tag_customer ON s.mt_event_tag_customer USING btree (value, tenant_id, seq_id)", true)]
    [InlineData("CREATE UNIQUE INDEX pk ON s.t USING btree (\"value\")", true)]
    [InlineData("CREATE INDEX ix ON s.mt_event_tag_customer USING btree (seq_id, value)", false)]
    [InlineData("CREATE INDEX ix ON s.mt_event_tag_customer USING hash (value)", false)]
    [InlineData("", false)]
    public void An_index_leads_with_value_only_when_value_is_its_first_btree_key(string definition, bool leads) =>
        EventTagIndexes.LeadsWith(definition, "value", "btree").Should().Be(leads);

    [Fact]
    public void A_tag_table_whose_primary_key_leads_with_value_is_green()
    {
        (IndexVerdictLevel level, string reason) = EventTagIndexes.TagTable(
            [new PostgresIndex("pk_mt_event_tag_customer", "CREATE UNIQUE INDEX pk_mt_event_tag_customer ON s.mt_event_tag_customer USING btree (value, seq_id)")],
            "mt_event_tag_customer");

        level.Should().Be(IndexVerdictLevel.Green);
        reason.Should().Contain("pk_mt_event_tag_customer");
    }

    [Fact]
    public void A_tag_table_without_such_an_index_is_red_and_says_the_database_differs_from_the_store()
    {
        (IndexVerdictLevel level, string reason) = EventTagIndexes.TagTable([], "mt_event_tag_customer");

        level.Should().Be(IndexVerdictLevel.Red);
        reason.Should().Contain("reads the whole tag table");
    }

    [Fact]
    public void The_hstore_column_is_green_only_with_a_gin_index_over_it()
    {
        EventTagIndexes.HStore([new PostgresIndex("idx_mt_events_tags", "CREATE INDEX idx_mt_events_tags ON s.mt_events USING gin (tags)")])
            .Level.Should().Be(IndexVerdictLevel.Green);

        EventTagIndexes.HStore([new PostgresIndex("pkey_mt_events_seq_id", "CREATE UNIQUE INDEX pkey_mt_events_seq_id ON s.mt_events USING btree (seq_id)")])
            .Level.Should().Be(IndexVerdictLevel.Red);
    }

    // ------------------------------------------------------------------------------------------------
    // Pairing a tag read with its events
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Under tenant-partitioned events <c>seq_id</c> repeats across tenants, so an "all tenants" page can hold
    /// two events numbered 5: each must get its own tenant's tags and never the other's.
    /// </summary>
    [Fact]
    public void Tags_pair_with_events_by_sequence_and_tenant()
    {
        EventRow acme = FakeEventDataService.Event(5) with { TenantId = "acme" };
        EventRow globex = FakeEventDataService.Event(5) with { TenantId = "globex" };
        EventRow untagged = FakeEventDataService.Event(6) with { TenantId = "acme" };

        List<EventRow> merged = EventTagView.Merge(
            [acme, globex, untagged],
            [
                new EventTagRow(5, "acme", new EventTag("customer", "a-1")),
                new EventTagRow(5, "globex", new EventTag("customer", "g-1")),
                new EventTagRow(99, "acme", new EventTag("customer", "not on the page")),
            ]);

        merged[0].Tags.Should().ContainSingle().Which.Value.Should().Be("a-1");
        merged[1].Tags.Should().ContainSingle().Which.Value.Should().Be("g-1");
        merged[2].Tags.Should().BeEmpty();
    }

    [Fact]
    public void A_card_is_handed_its_tags_as_type_value_pairs()
    {
        EventRow row = FakeEventDataService.Event(1) with { Tags = [new EventTag("customer", "a-1"), new EventTag("region", "eu")] };

        EventTagView.Pairs(row).Should().Equal(
            new KeyValuePair<string, string>("customer", "a-1"),
            new KeyValuePair<string, string>("region", "eu"));

        EventTagView.Pairs(FakeEventDataService.Event(2)).Should().BeEmpty();
    }

    private static EventTagModel Model(Action<StoreOptions> configure)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            configure(options);
        });

        return EventTagModel.From(store.Options);
    }
}
