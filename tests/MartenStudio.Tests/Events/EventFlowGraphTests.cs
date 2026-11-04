using System.Collections;
using System.Reflection;

using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events;
using Marten.Events.Projections.Flattened;
using Marten.Subscriptions;

using MartenStudio.Services.Events;
using MartenStudio.Tests.Health;

namespace MartenStudio.Tests.Events;

/// <summary>
/// 1.2, M6: the event flow's model, built from a real store's configuration and never connected to - which projections and
/// subscriptions read which event types, and what they write.
/// </summary>
public class EventFlowGraphTests
{
    private static DocumentStore Store(Action<StoreOptions> configure) => DocumentStore.For(options =>
    {
        options.Connection(HealthHarness.RefusedConnectionString);
        configure(options);
    });

    private static StoreOptions SampleShaped(StoreOptions options)
    {
        options.Projections.Add(new FlowOrderProjection(), ProjectionLifecycle.Inline);
        options.Projections.Add(new FlowDayProjection(), ProjectionLifecycle.Async);
        options.Projections.Add(new FlowTrackerProjection(), ProjectionLifecycle.Async);
        options.Events.AddEventType<FlowLedgerOpened>();
        options.Events.AddEventType<FlowLedgerCredited>();

        return options;
    }

    private static string[] ReadBy(EventFlowModel model, string source) =>
        [.. model.Reads.Where(x => x.Source == source).Select(x => x.EventAlias).Order(StringComparer.Ordinal)];

    private static string AliasOf<T>(EventFlowModel model) =>
        model.EventTypes.Single(x => x.ClrName == typeof(T).Name).Key;

    // ---------------------------------------------------------------------------------------------
    // A sample-shaped store
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void An_inline_single_stream_projection_reads_exactly_the_events_it_declares_and_writes_its_document()
    {
        using DocumentStore store = Store(o => SampleShaped(o));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource orders = model.Sources.Single(x => x.Name == "Orders");
        orders.Kind.Should().Be(FlowSourceKind.SingleStream);
        orders.Lifecycle.Should().Be(ProjectionLifecycle.Inline);
        orders.Input.Should().Be(FlowInput.Declared);

        FlowEventType[] user = [.. model.Reads.Where(x => x.Source == "Orders").Select(x => model.EventTypes.Single(e => e.Key == x.EventAlias)).Where(x => !x.IsMartenOwn)];
        user.Select(static x => x.ClrName).Should().BeEquivalentTo(nameof(FlowPlaced), nameof(FlowItemAdded), nameof(FlowShipped), nameof(FlowCancelled), nameof(FlowVoided));
        model.Reads.Where(x => x.Source == "Orders").Should().OnlyContain(static x => x.Via == null, "each is declared by name, not through a base type");

        model.EventTypes.Single(static x => x.ClrName == nameof(Archived)).IsMartenOwn.Should().BeTrue();
        model.Reads.Should().Contain(x => x.Source == "Orders" && x.EventAlias == model.EventTypes.Single(e => e.ClrName == nameof(Archived)).Key,
            "a single-stream projection reads Archived");

        FlowWrite write = model.Writes.Single(static x => x.Source == "Orders");
        FlowOutput output = model.Outputs.Single(x => x.Key == write.OutputKey);
        output.Kind.Should().Be(FlowOutputKind.Document);
        output.Subtitle.Should().Be(nameof(FlowOrder));
        write.Subclass.Should().BeNull();
    }

    /// <summary>
    /// <c>Identity&lt;IFlowOrderEvent&gt;()</c> says how events are grouped, not which are taken in: JasperFx's
    /// <c>AllEventTypes</c> (what <c>AppliesTo</c> and the daemon's filter use) holds the Apply methods' types only, so the
    /// interface the sample's <c>DailySales</c> groups on is not something it reads.
    /// </summary>
    [Fact]
    public void A_multi_stream_projection_reads_the_events_it_applies_and_not_the_interface_it_groups_on()
    {
        using DocumentStore store = Store(o => SampleShaped(o));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource daily = model.Sources.Single(static x => x.Name == "Daily");
        daily.Kind.Should().Be(FlowSourceKind.MultiStream);
        daily.Lifecycle.Should().Be(ProjectionLifecycle.Async);

        ReadBy(model, "Daily").Should().BeEquivalentTo([AliasOf<FlowPlaced>(model), AliasOf<FlowItemAdded>(model), AliasOf<FlowCancelled>(model)]);
        model.Reads.Where(static x => x.Source == "Daily").Should().OnlyContain(static x => x.Via == null);
        model.EventTypes.Should().NotContain(static x => x.ClrName == nameof(IFlowOrderEvent), "no source declares it");
        model.Outputs.Single(x => x.Key == model.Writes.Single(static w => w.Source == "Daily").OutputKey).Subtitle.Should().Be(nameof(FlowDay));
    }

    [Fact]
    public void A_projection_that_declares_a_base_type_reads_every_known_event_type_that_implements_it_through_it()
    {
        using DocumentStore store = Store(o =>
        {
            var daily = new FlowDayProjection();
            daily.IncludeType<IFlowOrderEvent>();
            o.Projections.Add(daily, ProjectionLifecycle.Async);
            o.Projections.Add(new FlowOrderProjection(), ProjectionLifecycle.Inline);
        });

        EventFlowModel model = EventFlowGraph.Build(store, null);

        model.Reads.Single(x => x.Source == "Daily" && x.EventAlias == AliasOf<FlowShipped>(model)).Via.Should().Be(nameof(IFlowOrderEvent),
            "FlowShipped has no Apply in Daily; the declared base type takes it in");
        model.Reads.Single(x => x.Source == "Daily" && x.EventAlias == AliasOf<FlowPlaced>(model)).Via.Should().BeNull("declared exactly");
        model.Reads.Should().NotContain(x => x.Source == "Daily" && x.EventAlias == AliasOf<FlowVoided>(model), "FlowVoided is not an IFlowOrderEvent");
        model.EventTypes.Single(static x => x.ClrName == nameof(IFlowOrderEvent)).IsBaseType.Should().BeTrue();
    }

    [Fact]
    public void An_event_projection_that_stores_a_document_without_declaring_it_writes_none_listed()
    {
        using DocumentStore store = Store(o => SampleShaped(o));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource tracker = model.Sources.Single(static x => x.Name == "Tracker");
        tracker.Kind.Should().Be(FlowSourceKind.EventProjection);

        ReadBy(model, "Tracker").Should().BeEquivalentTo([AliasOf<FlowShipped>(model), AliasOf<FlowItemAdded>(model)],
            "its IEvent<T> parameters count as T");
        model.Writes.Where(static x => x.Source == "Tracker").Should().BeEmpty();
        tracker.UndrawnOutputs.Should().BeEmpty();
    }

    [Fact]
    public void Event_types_nothing_reads_are_unconsumed_and_Martens_own_are_never_among_them()
    {
        using DocumentStore store = Store(o => SampleShaped(o));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        model.Unconsumed.Select(static x => x.ClrName).Should().BeEquivalentTo(nameof(FlowLedgerOpened), nameof(FlowLedgerCredited));
        model.Unconsumed.Should().OnlyContain(static x => !x.IsMartenOwn);
        model.HasEverythingSource.Should().BeFalse();
    }

    [Fact]
    public void Every_edge_names_a_box_of_the_model_and_no_edge_is_duplicated()
    {
        using DocumentStore store = Store(o => SampleShaped(o));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        HashSet<string> events = [.. model.EventTypes.Select(static x => x.Key)];
        HashSet<string> sources = [.. model.Sources.Select(static x => x.Name)];
        HashSet<string> outputs = [.. model.Outputs.Select(static x => x.Key)];

        model.Reads.Should().OnlyContain(x => events.Contains(x.EventAlias) && sources.Contains(x.Source));
        model.Writes.Should().OnlyContain(x => outputs.Contains(x.OutputKey) && sources.Contains(x.Source));
        model.Reads.Should().OnlyHaveUniqueItems();
        model.Writes.Should().OnlyHaveUniqueItems();
        model.EventTypes.Select(static x => x.Key).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ReadsOf_answers_what_a_rebuild_would_replay()
    {
        using DocumentStore store = Store(o => SampleShaped(o));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowReads orders = model.ReadsOf("orders");
        orders.Input.Should().Be(FlowInput.Declared);
        orders.Aliases.Should().BeEquivalentTo(
            [AliasOf<FlowPlaced>(model), AliasOf<FlowItemAdded>(model), AliasOf<FlowShipped>(model), AliasOf<FlowCancelled>(model), AliasOf<FlowVoided>(model)]);
        orders.MartenOwnAliases.Should().NotBeEmpty("Archived, at least");
    }

    // ---------------------------------------------------------------------------------------------
    // Synthetic stores
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(SchemaNameSource.DocumentSchema, "flow_docs")]
    [InlineData(SchemaNameSource.EventSchema, "flow_events")]
    [InlineData(SchemaNameSource.Explicit, "elsewhere")]
    public void A_flat_table_is_placed_by_its_schema_source_and_create_schema_objects_is_never_called(SchemaNameSource source, string expectedSchema)
    {
        FlatTableProjection? flat = null;

        using DocumentStore store = Store(o =>
        {
            o.DatabaseSchemaName = "flow_docs";
            o.Events.DatabaseSchemaName = "flow_events";

            flat = source == SchemaNameSource.Explicit
                ? new FlatTableProjection(new Weasel.Postgresql.PostgresqlObjectName("elsewhere", "flow_table", Weasel.Postgresql.SchemaUtils.IdentifierUsage.General))
                : new FlatTableProjection("flow_table", source);
            flat.Table.AddColumn<Guid>("id").AsPrimaryKey();
            flat.Project<FlowPlaced>(static map => map.Map(static x => x.OrderId));
            flat.Delete<FlowCancelled>();
            o.Projections.Add(flat, ProjectionLifecycle.Async);
        });

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource table = model.Sources.Single();
        table.Kind.Should().Be(FlowSourceKind.FlatTable);
        ReadBy(model, table.Name).Should().BeEquivalentTo([AliasOf<FlowPlaced>(model), AliasOf<FlowCancelled>(model)]);

        FlowOutput output = model.Outputs.Single();
        output.Kind.Should().Be(FlowOutputKind.Table);
        output.Schema.Should().Be(expectedSchema);
        output.Table.Should().Be("flow_table");
        output.Key.Should().Be($"t:{expectedSchema}.flow_table");
        model.Writes.Single().OutputKey.Should().Be(output.Key);

        if (source != SchemaNameSource.Explicit)
        {
            flat!.Table.Identifier.Schema.Should().Be("public", "CreateSchemaObjects would have moved the table to the store's schema");
        }
    }

    [Fact]
    public void A_composite_is_a_group_whose_members_are_sources_of_their_own()
    {
        using DocumentStore store = Store(o => o.Projections.CompositeProjectionFor("Bundle", composite =>
        {
            composite.Add(new FlowOrderProjection());
            composite.Add(new FlowDayProjection(), 2);
        }));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource bundle = model.Sources.Single(static x => x.Name == "Bundle");
        bundle.IsComposite.Should().BeTrue();
        bundle.Input.Should().Be(FlowInput.Declared, "a composite is not a source of everything");
        ReadBy(model, "Bundle").Should().BeEmpty("what reads an event is a member");
        model.HasEverythingSource.Should().BeFalse();

        model.Sources.Single(static x => x.Name == "Orders").Composite.Should().Be("Bundle");
        model.Sources.Single(static x => x.Name == "Daily").Composite.Should().Be("Bundle");
        ReadBy(model, "Orders").Should().NotBeEmpty();

        model.ReadsOf("Bundle").Aliases.Should().Contain(AliasOf<FlowShipped>(model), "the union of its members' reads");
        model.MembersOf("Bundle").Select(static x => x.Name).Should().BeEquivalentTo("Orders", "Daily");
    }

    [Fact]
    public void A_composite_nested_in_a_composite_is_followed_and_never_loops()
    {
        using DocumentStore store = Store(o => o.Projections.CompositeProjectionFor("Outer", outer =>
        {
            var inner = new FlowInnerComposite("Inner");
            inner.AddMember(new FlowOrderProjection());
            outer.Add(inner);
            outer.Add(new FlowDayProjection(), 2);
        }));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        model.Sources.Single(static x => x.Name == "Inner").Composite.Should().Be("Outer");
        model.Sources.Single(static x => x.Name == "Orders").Composite.Should().Be("Inner");
        model.MembersOf("Outer").Select(static x => x.Name).Should().BeEquivalentTo("Inner", "Orders", "Daily");
        model.ReadsOf("Outer").Aliases.Should().Contain(AliasOf<FlowPlaced>(model));
    }

    [Fact]
    public void A_bare_IProjection_declares_nothing_so_it_is_fed_every_event()
    {
        using DocumentStore store = Store(o =>
        {
            o.Projections.Add(new FlowBareProjection(), ProjectionLifecycle.Async);
            o.Projections.Add(new FlowOrderProjection(), ProjectionLifecycle.Inline);
            o.Events.AddEventType<FlowLedgerOpened>();
        });

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource bare = model.Sources.Single(static x => x.Name == nameof(FlowBareProjection));
        bare.Kind.Should().Be(FlowSourceKind.Custom);
        bare.Input.Should().Be(FlowInput.Everything);
        ReadBy(model, bare.Name).Should().BeEmpty("one pseudo-edge from the every-event box, and none per event type");
        model.HasEverythingSource.Should().BeTrue();
        model.Unconsumed.Should().BeEmpty("a source reads everything, so no event type goes unread");
        model.ReadsOf(bare.Name).Input.Should().Be(FlowInput.Everything);
    }

    [Fact]
    public void A_hand_written_projection_that_includes_one_type_reads_only_it()
    {
        using DocumentStore store = Store(o => o.Projections.Add(new FlowTrackerProjection(), ProjectionLifecycle.Async));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        ReadBy(model, "Tracker").Should().BeEquivalentTo([AliasOf<FlowShipped>(model), AliasOf<FlowItemAdded>(model)]);
        model.Reads.Should().NotContain(x => x.EventAlias == AliasOf<FlowShipped>(model) && x.Source != "Tracker");
    }

    [Fact]
    public void A_subscription_is_listed_from_the_daemons_shards_with_its_filter()
    {
        using DocumentStore store = Store(o =>
        {
            o.Events.Subscribe(new FlowFilteredSubscription());
            o.Events.Subscribe(new FlowOpenSubscription());
            o.Projections.Add(new FlowOrderProjection(), ProjectionLifecycle.Inline);
        });

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource filtered = model.Sources.Single(static x => x.Name == "Filtered");
        filtered.Kind.Should().Be(FlowSourceKind.Subscription);
        filtered.Lifecycle.Should().Be(ProjectionLifecycle.Async);
        filtered.Input.Should().Be(FlowInput.Declared);
        filtered.StreamType.Should().Be(nameof(FlowOrder));
        filtered.IncludesArchived.Should().BeTrue();
        ReadBy(model, "Filtered").Should().BeEquivalentTo([AliasOf<FlowPlaced>(model)]);

        FlowSource open = model.Sources.Single(static x => x.Name == "Open");
        open.Kind.Should().Be(FlowSourceKind.Subscription);
        open.Input.Should().Be(FlowInput.Everything);
        model.SubscriptionsUnavailable.Should().BeNull();
    }

    [Fact]
    public void A_catch_all_aggregation_is_fed_every_event_and_not_just_Archived()
    {
        using DocumentStore store = Store(o => o.Projections.Add(new FlowCatchAllProjection(), ProjectionLifecycle.Async));

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource source = model.Sources.Single();
        source.Input.Should().Be(FlowInput.Everything, "its declared set is empty, which JasperFx reads as 'applies to everything'");
        ReadBy(model, source.Name).Should().BeEmpty();
    }

    [Fact]
    public void A_live_aggregation_writes_a_live_aggregate_and_never_a_document()
    {
        using DocumentStore store = Store(o => o.Projections.LiveStreamAggregation<FlowLive>());

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource live = model.Sources.Single(static x => x.Lifecycle == ProjectionLifecycle.Live);
        FlowOutput output = model.Outputs.Single();
        output.Kind.Should().Be(FlowOutputKind.LiveAggregate, "though FlowLive is a document type of the store, it is never written by this projection");
        output.Title.Should().Be(nameof(FlowLive));
        model.Writes.Single().Should().Be(new FlowWrite(live.Name, output.Key, null));
        ReadBy(model, live.Name).Should().Contain(AliasOf<FlowPlaced>(model));
    }

    [Fact]
    public void An_event_type_registered_and_read_by_nothing_is_unconsumed()
    {
        using DocumentStore store = Store(o =>
        {
            o.Projections.Add(new FlowOrderProjection(), ProjectionLifecycle.Inline);
            o.Events.AddEventType<FlowLedgerOpened>();
        });

        EventFlowGraph.Build(store, null).Unconsumed.Select(static x => x.ClrName).Should().Equal(nameof(FlowLedgerOpened));
    }

    [Fact]
    public void A_declared_type_the_registry_has_not_met_is_an_event_type_marked_not_registered()
    {
        using DocumentStore store = Store(o =>
        {
            var projection = new FlowTrackerProjection();
            projection.IncludeType<IFlowOrderEvent>();
            o.Projections.Add(projection, ProjectionLifecycle.Async);
            o.Events.AddEventType<FlowPlaced>();
        });

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowEventType declared = model.EventTypes.Single(static x => x.ClrName == nameof(IFlowOrderEvent));
        declared.NotRegistered.Should().BeTrue("a fresh process has met no event yet, and the picture must not wait for one");
        declared.IsBaseType.Should().BeTrue();
        model.Reads.Should().Contain(x => x.Source == "Tracker" && x.EventAlias == declared.Key && x.Via == null);
        model.Reads.Should().Contain(x => x.Source == "Tracker" && x.EventAlias == AliasOf<FlowPlaced>(model) && x.Via == nameof(IFlowOrderEvent),
            "an event type is read through the base type the projection declares");
        model.Sources.Single().AlsoDeclares.Should().BeEmpty();
    }

    [Fact]
    public void A_published_subclass_maps_to_its_root_with_the_subclass_named()
    {
        using DocumentStore store = Store(o =>
        {
            o.Schema.For<FlowVehicle>().AddSubClass<FlowCar>();
            o.Projections.Add(new FlowDeclaringProjection(), ProjectionLifecycle.Async);
        });

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowWrite write = model.Writes.Single();
        write.Subclass.Should().Be(nameof(FlowCar));
        model.Outputs.Single().Subtitle.Should().Be(nameof(FlowVehicle));
        model.Outputs.Single().Key.Should().Be(write.OutputKey);
    }

    [Fact]
    public void A_published_types_override_that_throws_is_a_note_and_the_model_still_builds()
    {
        var throwing = new FlowThrowingProjection();

        using DocumentStore store = Store(o =>
        {
            o.Projections.Add(throwing, ProjectionLifecycle.Async);
            o.Projections.Add(new FlowOrderProjection(), ProjectionLifecycle.Inline);
        });

        throwing.Armed = true;

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource described = model.Sources.Single(static x => x.Name == "Throwing");
        described.WritesNote.Should().Contain(nameof(InvalidOperationException));
        described.WritesNote.Should().NotContain("no types for you", "an exception type, never what it said");
        described.Note.Should().BeNull("what it writes is a note of its own and never overwrites what it reads");
        model.Sources.Should().Contain(static x => x.Name == "Orders");
    }

    /// <summary>
    /// Marten builds the store's document mappings from every projection's <c>PublishedTypes()</c>, so one that throws empties
    /// <c>AllKnownDocumentTypes()</c> too. No published type can then be told from a hidden one, so nothing is listed.
    /// </summary>
    [Fact]
    public void When_the_document_types_cannot_be_read_no_source_lists_what_it_writes_and_the_model_says_so_once()
    {
        var throwing = new FlowThrowingProjection();

        using DocumentStore store = Store(o =>
        {
            o.Projections.Add(throwing, ProjectionLifecycle.Async);
            o.Projections.Add(new FlowOrderProjection(), ProjectionLifecycle.Inline);
            o.Projections.Add(new FlowTrackerProjection(), ProjectionLifecycle.Async);
        });

        throwing.Armed = true;

        EventFlowModel model = EventFlowGraph.Build(store, null);

        model.DocumentTypesUnavailable.Should().Be(EventFlowGraph.DocumentTypesUnavailableText);
        model.Sources.Single(static x => x.Name == "Orders").UndrawnOutputs.Should().BeEmpty("it would otherwise read 'not a document type of this store'");
        model.Sources.Should().OnlyContain(static x => x.UndrawnOutputs.Count == 0);
        model.Writes.Should().BeEmpty();
        model.Outputs.Should().BeEmpty();
        model.Sources.Single(static x => x.Name == "Orders").Input.Should().Be(FlowInput.Declared, "what it reads is unaffected");
    }

    [Fact]
    public void When_the_document_types_can_be_read_the_model_does_not_say_otherwise()
    {
        using DocumentStore store = Store(o => SampleShaped(o));

        EventFlowGraph.Build(store, null).DocumentTypesUnavailable.Should().BeNull();
    }

    /// <summary>
    /// A composite's <c>PublishedTypes()</c> is its members' own, so mapping it as a source's output would repeat every member's
    /// documents on the composite's row: the row lists only what the composite itself adds, mapped like any output.
    /// </summary>
    [Fact]
    public void A_composites_row_lists_only_what_it_adds_and_maps_an_added_document_type_to_its_root()
    {
        using DocumentStore store = Store(o =>
        {
            o.Schema.For<FlowVehicle>().AddSubClass<FlowCar>();
            o.Projections.CompositeProjectionFor("Bundle", composite =>
            {
                composite.Add(new FlowOrderProjection());
                composite.Options.DeleteViewTypeOnTeardown<FlowCar>();
                composite.Options.DeleteViewTypeOnTeardown<FlowShipment>();
            });
        });

        EventFlowModel model = EventFlowGraph.Build(store, null);

        FlowSource bundle = model.Sources.Single(static x => x.Name == "Bundle");
        FlowWrite[] own = [.. model.Writes.Where(static x => x.Source == "Bundle")];

        own.Should().HaveCount(2, "the two types it adds; its members' documents are the members' rows, not the composite's");
        own.Single(static x => x.Subclass == nameof(FlowCar)).OutputKey.Should().Be(
            model.Outputs.Single(static x => x.Subtitle == nameof(FlowVehicle)).Key, "an added subclass is mapped to its root");
        own.Single(static x => x.Subclass is null).OutputKey.Should().Be(model.Outputs.Single(static x => x.Subtitle == nameof(FlowShipment)).Key);
        bundle.UndrawnOutputs.Should().BeEmpty("an added type that is a document type is mapped, not listed as 'not a document type'");
        model.Writes.Should().Contain(static x => x.Source == "Orders", "the member keeps its own output");
    }

    [Fact]
    public void A_dcb_tag_type_is_an_attribute_of_the_aggregation_it_keys()
    {
        using DocumentStore store = Store(o =>
        {
            o.Events.RegisterTagType<FlowTagId>("flowtag").ForAggregate<FlowTagged>();
            o.Projections.Snapshot<FlowTagged, FlowTagId>(SnapshotLifecycle.Async);
        });

        EventFlowModel model = EventFlowGraph.Build(store, null);

        model.TagTypes.Single().Name.Should().Be(nameof(FlowTagId));
        model.TagTypes.Single().AggregateName.Should().Be(nameof(FlowTagged));
        model.Sources.Single().TagType.Should().Be(nameof(FlowTagId));
    }

    // ---------------------------------------------------------------------------------------------
    // Hidden is absent
    // ---------------------------------------------------------------------------------------------

    /// <summary>Every string any record of the model holds, however deep.</summary>
    private static IEnumerable<string> StringsOf(object? value, HashSet<object>? seen = null)
    {
        seen ??= new HashSet<object>(ReferenceEqualityComparer.Instance);

        switch (value)
        {
            case null:
                yield break;
            case string text:
                yield return text;
                yield break;
            case Type type:
                yield return type.FullName ?? type.Name;
                yield break;
            case IEnumerable sequence:
                foreach (object? item in sequence)
                {
                    foreach (string text in StringsOf(item, seen))
                    {
                        yield return text;
                    }
                }

                yield break;
        }

        if (!value.GetType().IsClass || !seen.Add(value))
        {
            yield break;
        }

        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            foreach (string text in StringsOf(property.GetValue(value), seen))
            {
                yield return text;
            }
        }
    }

    [Fact]
    public void A_hidden_document_type_is_absent_from_every_string_of_the_model()
    {
        using DocumentStore store = Store(o =>
        {
            SampleShaped(o);
            o.Projections.Add(new FlowSecretProjection(), ProjectionLifecycle.Inline);
            o.Projections.Add(new FlowSecretLiveProjection(), ProjectionLifecycle.Live);
            o.Events.RegisterTagType<FlowTagId>("flowtag").ForAggregate<FlowSecretTagged>();

            // A hidden root with a subclass the delegate does not hide: the subclass lives in the hidden collection, so it is hidden
            // with it - as a published type, as a Compacted<T> event type, and as a stream type.
            o.Schema.For<FlowSecretVault>().AddSubClass<FlowSecretCell>();
            o.Projections.Add(new FlowStrongboxProjection(), ProjectionLifecycle.Async);
            o.Projections.Add(new FlowCellarProjection(), ProjectionLifecycle.Inline);
        });

        bool Visible(Type type) =>
            type != typeof(FlowSecret) && type != typeof(FlowSecretLive) && type != typeof(FlowSecretTagged) && type != typeof(FlowSecretVault);

        EventFlowModel model = EventFlowGraph.Build(store, Visible);

        string[] strings = [.. StringsOf(model)];
        strings.Should().NotBeEmpty();
        strings.Should().NotContain(static x => x.Replace("_", string.Empty, StringComparison.Ordinal).Contains("FlowSecret", StringComparison.OrdinalIgnoreCase),
            "a hidden type is absent: no output, no count, no note, and no event type that names it");
        model.TagTypes.Single().AggregateName.Should().BeNull("the tag type keeps its table; the aggregate it names is hidden");
        model.Sources.Should().Contain(static x => x.Name == "Orders", "visible projections are untouched");
        model.Sources.Should().Contain(static x => x.Name == "Strongbox", "the projection itself is named as the Projections page names it (OPEN-1)");
        model.Sources.Single(static x => x.Name == "Strongbox").UndrawnOutputs.Should().BeEmpty(
            "a subclass of a hidden root is not 'a document type this store does not have': that sentence names it and is false");
        model.Writes.Should().NotContain(static x => x.Source == "Strongbox" || x.Source == "Cellar");
        model.EventTypes.Should().NotContain(static x => x.ClrName.Contains("FlowSecretCell", StringComparison.Ordinal), "Compacted<subclass> names it");
    }

    [Fact]
    public void A_visibility_delegate_that_throws_hides_the_type()
    {
        using DocumentStore store = Store(o => SampleShaped(o));

        EventFlowModel model = EventFlowGraph.Build(store, static _ => throw new InvalidOperationException("boom"));

        model.Outputs.Should().BeEmpty("every output type was asked about, and a delegate that throws hides");
        model.Sources.Should().NotBeEmpty();
    }

    // ---------------------------------------------------------------------------------------------
    // The cache
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_cache_returns_the_same_instance_until_the_configuration_moves()
    {
        using DocumentStore store = Store(o => SampleShaped(o));
        var cache = new EventFlowCache();

        EventFlowModel first = cache.Get(store, null);
        cache.Get(store, null).Should().BeSameAs(first);

        ((EventGraph) ((IDocumentStore) store).Options.Events).AddEventType<FlowCar>();

        EventFlowModel second = cache.Get(store, null);
        second.Should().NotBeSameAs(first);
        second.EventTypes.Should().Contain(static x => x.ClrName == nameof(FlowCar));
        cache.Get(store, null).Should().BeSameAs(second);
    }

    [Fact]
    public void A_different_visibility_delegate_is_a_different_model()
    {
        using DocumentStore store = Store(o => SampleShaped(o));
        var cache = new EventFlowCache();
        Func<Type, bool> hideNothing = static _ => true;

        EventFlowModel first = cache.Get(store, null);

        cache.Get(store, hideNothing).Should().NotBeSameAs(first);
    }

    // ---------------------------------------------------------------------------------------------
    // Subscriptions the tests register
    // ---------------------------------------------------------------------------------------------

    private sealed class FlowFilteredSubscription : SubscriptionBase
    {
        public FlowFilteredSubscription()
        {
            Name = "Filtered";
            IncludeType<FlowPlaced>();
            FilterIncomingEventsOnStreamType(typeof(FlowOrder));
            IncludeArchivedEvents = true;
        }

        public override Task<IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations, CancellationToken cancellationToken) =>
            Task.FromResult(NullChangeListener.Instance);
    }

    private sealed class FlowOpenSubscription : SubscriptionBase
    {
        public FlowOpenSubscription() => Name = "Open";

        public override Task<IChangeListener> ProcessEventsAsync(EventRange page, ISubscriptionController controller, IDocumentOperations operations, CancellationToken cancellationToken) =>
            Task.FromResult(NullChangeListener.Instance);
    }
}

/// <summary>A composite a test can nest in Marten's: JasperFx's keeps its stages to subclasses.</summary>
public sealed class FlowInnerComposite : JasperFx.Events.Projections.Composite.CompositeProjection<IDocumentOperations, IQuerySession>
{
    /// <summary>Named.</summary>
    public FlowInnerComposite(string name)
        : base(name)
    {
    }

    /// <summary>Adds a member to the first stage.</summary>
    public void AddMember(JasperFx.Events.Projections.IProjectionSource<IDocumentOperations, IQuerySession> member) => StageFor(1).Add(member);
}

/// <summary>An aggregate folded live that the host will hide.</summary>
public sealed class FlowSecretLive
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A tag-keyed aggregate the host will hide.</summary>
public sealed class FlowSecretTagged
{
    /// <summary>The id.</summary>
    public FlowTagId Id { get; set; }
}
