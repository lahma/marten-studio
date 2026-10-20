using System.Reflection;

using JasperFx.Core;
using JasperFx.Events;

using Marten;
using Marten.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Health.Checks;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// What the event checks share: the store facts they compute in process, and the reflection they rely on.
/// </summary>
public class EventHealthTests
{
    // ---------------------------------------------------------------------------------------------------
    // Marten facts, read in process
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_tombstone_is_called_tombstone_under_the_default_naming_style()
    {
        StoreOptions options = new();

        EventHealth.TombstoneType((IReadOnlyEventStoreOptions) options.Events).Should().Be("tombstone");
    }

    [Fact]
    public void Under_the_full_type_name_style_a_tombstone_is_called_by_its_full_name()
    {
        StoreOptions options = new();
        options.Events.EventNamingStyle = EventNamingStyle.FullTypeName;

        EventHealth.TombstoneType((IReadOnlyEventStoreOptions) options.Events).Should().Be(typeof(Tombstone).FullName);
    }

    [Fact]
    public void Reading_the_tombstone_type_registers_nothing_on_the_hosts_store()
    {
        StoreOptions options = new();
        IReadOnlyEventStoreOptions events = (IReadOnlyEventStoreOptions) options.Events;
        int before = events.AllKnownEventTypes().Count;

        _ = EventHealth.TombstoneType(events);

        events.AllKnownEventTypes().Should().HaveCount(before, "EventMappingFor would have registered a mapping as a side effect");
    }

    /// <summary>
    /// The private alias cache E05 reads to tell an upcaster's alias from an unknown one. Pinned, because it is a
    /// private field: at whatever Marten this runs, it must be there with the type the check casts it to.
    /// </summary>
    [Fact]
    public void Marten_keeps_the_upcasters_aliases_where_E05_reads_them()
    {
        FieldInfo? field = typeof(EventGraph).GetField("_byEventName", BindingFlags.Instance | BindingFlags.NonPublic);

        field.Should().NotBeNull("EventGraph._byEventName holds every alias Marten reads an event by");
        field!.FieldType.Should().Be<Cache<string, EventMapping>>();
    }

    [Fact]
    public void An_upcasters_alias_is_known_and_an_unseen_one_is_not()
    {
        StoreOptions options = new();
        options.Events.AddEventType<HealthTestEvent>();
        options.Events.Upcast<HealthTestEventV1, HealthTestEvent>("health_test_event_v1", static old => new HealthTestEvent(old.Number));

        Func<string, bool> known = EventHealth.KnownAliases((IReadOnlyEventStoreOptions) options.Events)!;

        known.Should().NotBeNull();
        known("health_test_event").Should().BeTrue("a registered mapping");
        known("health_test_event_v1").Should().BeTrue("an upcaster's old name, which AllKnownEventTypes does not list");
        known("never_seen").Should().BeFalse();
    }

    [Fact]
    public void A_dotnet_type_is_resolved_among_loaded_assemblies_and_nothing_is_loaded_to_find_one()
    {
        // A framework assembly nothing in this suite uses: resolvable by name from the app's own probing paths, so
        // the default Type.GetType would load it - which is exactly what a health read must not do.
        const string Unused = "System.Formats.Tar";
        AppDomain.CurrentDomain.GetAssemblies().Should().NotContain(
            static x => x.GetName().Name == Unused,
            "the precondition: if something in this suite starts using " + Unused + ", pick another framework assembly nothing loads");

        IReadOnlyDictionary<string, Assembly> loaded = EventHealth.LoadedAssemblies();

        EventHealth.ResolveLoaded(typeof(HealthTestEvent).AssemblyQualifiedName!, loaded).Should().Be<HealthTestEvent>();
        EventHealth.ResolveLoaded("MartenStudio.Tests.Health.HealthTestEvent, MartenStudio.Tests", loaded).Should().Be<HealthTestEvent>();
        EventHealth.ResolveLoaded("Ghost.Event, Ghost.Assembly.That.Does.Not.Exist", loaded).Should().BeNull();
        EventHealth.ResolveLoaded("not a type name at all [[", loaded).Should().BeNull();

        EventHealth.ResolveLoaded("System.Formats.Tar.TarFile, " + Unused, loaded).Should().BeNull("its assembly is not loaded, and resolving a name never loads one");
        AppDomain.CurrentDomain.GetAssemblies().Should().NotContain(static x => x.GetName().Name == Unused);
        Type.GetType("System.Formats.Tar.TarFile, " + Unused).Should().NotBeNull("the anti-vacuity half: the name does resolve, if something is allowed to load it");
        AppDomain.CurrentDomain.GetAssemblies().Should().Contain(static x => x.GetName().Name == Unused, "Type.GetType loaded it, which is what the check must not do");
    }

    [Fact]
    public void The_shape_finds_a_table_by_its_marten_name()
    {
        EventStoreRelations shape = new("events", new Dictionary<string, EventStoreRelation>(StringComparer.Ordinal)
        {
            [HealthEventQueries.EventsTable] = new(HealthEventQueries.EventsTable, "p", ["seq_id", "bdata"], 2),
        });

        shape.Events!.IsPartitioned.Should().BeTrue();
        shape.Events.Has("bdata").Should().BeTrue();
        shape.Streams.Should().BeNull();
        Action missing = () => EventHealth.RequireEvents(new EventStoreRelations("events", new Dictionary<string, EventStoreRelation>()));
        missing.Should().Throw<HealthCheckUnavailableException>("an empty database is an expected state, not a failure");
    }

    /// <summary>
    /// CL1 items 3 and 6: the one shape both areas read, sorted. The daemon checks' facts - the sequence, the progression
    /// columns, "is there a high-water store at all" - come off the same rows as the event checks', and the dead-letter
    /// table is the one <c>DeadLetterQueries.TableFor</c> names: a host that re-aliased <c>DeadLetterEvent</c> has its
    /// <c>mt_doc_failures</c> found, and a stray <c>mt_doc_deadletterevent</c> beside it is not mistaken for it.
    /// </summary>
    [Fact]
    public void The_shared_shape_answers_the_daemon_checks_and_finds_a_re_aliased_dead_letter_table()
    {
        using DocumentStore store = DocumentStore.For(static options =>
        {
            options.Connection(MartenStudio.Tests.Sql.SqlTestStore.Unreachable);
            options.DatabaseSchemaName = "studio_dl";
            options.Events.DatabaseSchemaName = "events";
            options.Events.AddEventType<MartenStudio.Tests.Sql.DeadLetterQueriesTests.SomethingHappened>();
            options.Schema.For<JasperFx.Events.Daemon.DeadLetterEvent>().DocumentAlias("failures");
        });

        DocumentTableInfo deadLetters = DeadLetterQueries.TableFor(store.Options);
        deadLetters.Table.Should().Be("mt_doc_failures", "the premise: the host's alias");

        EventStoreRelations shape = EventHealth.Shape("events", deadLetters,
        [
            Row("events", HealthEventQueries.EventsTable, "r", "seq_id"),
            Row("events", HealthEventQueries.ProgressionTable, "r", "name", "last_seq_id", "agent_status"),
            Row("events", HealthEventQueries.EventsSequence, "S"),
            Row("events", "mt_doc_deadletterevent", "r", "id", "data"),
            Row("events", "mt_doc_failures", "r", "id", "data"),
        ]);

        shape.HasSequence.Should().BeTrue();
        shape.HasHighWater.Should().BeTrue("mt_events, the sequence and the progression table are all there");
        shape.ProgressionColumns.Should().Equal("name", "last_seq_id", "agent_status");
        shape.HasDeadLetters.Should().BeTrue();
        shape.DeadLetters!.Name.Should().Be("mt_doc_failures");
        shape.DeadLetterSchema.Should().Be("events");
        shape.Relations.Keys.Should().BeEquivalentTo(
            [HealthEventQueries.EventsTable, HealthEventQueries.ProgressionTable],
            "the sequence and the dead letters are their own facts, not event tables");

        EventStoreRelations withoutSequence = EventHealth.Shape("events", deadLetters,
        [
            Row("events", HealthEventQueries.EventsTable, "r", "seq_id"),
            Row("events", HealthEventQueries.ProgressionTable, "r", "name"),
            Row("events", "mt_doc_deadletterevent", "r", "id", "data"),
        ]);

        withoutSequence.HasHighWater.Should().BeFalse("no sequence, no high-water reading");
        withoutSequence.HasDeadLetters.Should().BeFalse("the default name is not the table this store maps");

        EventStoreRelations hidden = EventHealth.Shape("events", deadLetters: null, [Row("events", "mt_doc_failures", "r", "id")]);
        hidden.HasDeadLetters.Should().BeFalse("a table that was not asked about is never found");
        hidden.DeadLetterSchema.Should().BeNull();

        static EventStoreShapeRow Row(string schema, string name, string kind, params string[] columns) =>
            new(schema, new EventStoreRelation(name, kind, columns, 0));
    }

    [Theory]
    [InlineData(500d, "500 bytes")]
    [InlineData(65_536d, "64 KB")]
    [InlineData(1_572_864d, "1.5 MB")]
    public void Sizes_read_as_a_person_says_them(double bytes, string expected)
    {
        EventHealth.Bytes(bytes).Should().Be(expected);
    }

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(600, "10 min")]
    [InlineData(3 * 3600, "3 h")]
    [InlineData(3 * 86400, "3 days")]
    public void Spans_read_as_a_person_says_them(int seconds, string expected)
    {
        EventHealth.Duration(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }
}

/// <summary>An event type of the tests' own, for the alias and type-resolution tests.</summary>
public sealed record HealthTestEvent(int Number);

/// <summary>The old shape of <see cref="HealthTestEvent" />, which an upcaster reads.</summary>
public sealed record HealthTestEventV1(int Number);
