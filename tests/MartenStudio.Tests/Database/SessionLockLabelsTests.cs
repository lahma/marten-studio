using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Services.Database;
using MartenStudio.Tests.Sql;

namespace MartenStudio.Tests.Database;

/// <summary>
/// What a lock is on, in Marten's terms (C06 and Database › Sessions), from a real store's configuration and no
/// connection: the apply-changes lock, the daemon's lock and a shard's, an <c>mt_streams</c> row, a document table
/// by alias - and, by the owner the lock read resolved it to, never an index's, a partition's or a TOAST table's own
/// name; a hidden type's table, another store's table and an <c>mt_</c> name nobody declares named to nobody.
/// </summary>
public class SessionLockLabelsTests
{
    private const string Customers = "mt_doc_dbcustomer";

    private static DocumentStore Store(Action<StoreOptions>? configure = null) => DocumentStore.For(options =>
    {
        options.Connection(SqlTestStore.Unreachable);
        options.DatabaseSchemaName = DatabaseTestStores.DocumentSchema;
        options.Events.DatabaseSchemaName = DatabaseTestStores.EventSchema;
        options.Schema.For<DbCustomer>();
        options.Schema.For<DbSecret>();
        options.Events.AddEventType<DbOrderPlaced>();
        options.Projections.Add<DbOrderFlatProjection>(ProjectionLifecycle.Async);
        configure?.Invoke(options);
    });

    /// <summary>The labels with the classifier over both test stores, the default one first and <c>DbSecret</c> hidden.</summary>
    private static SessionLockLabels Labels(IDocumentStore store) =>
        SessionLockLabels.For(store, "default", DatabaseTestStores.Classifier());

    private static SessionLockLabels Labels() => Labels(Store());

    private static LockedRelation Owner(string schema, string name, LockedPart part = LockedPart.Itself, string kind = "r") =>
        new(schema, name, kind, part);

    [Fact]
    public void The_apply_changes_lock_is_named_with_its_id_as_the_store_sets_it()
    {
        SessionLockLabels labels = Labels(Store(static options => options.ApplyChangesLockId = 4321));

        LockTarget target = labels.Describe("advisory", advisoryKey: 4321);

        target.Kind.Should().Be(LockTargetKind.ApplyChangesLock);
        target.Label.Should().Contain("ApplyChangesLockId 4321");
        labels.Advisory(4004).Kind.Should().Be(LockTargetKind.OtherAdvisoryLock, "the default id is not this store's once it set its own");
    }

    [Fact]
    public void The_daemons_lock_and_each_shards_are_the_ids_Martens_own_high_water_detection_computes()
    {
        IDocumentStore store = Store();
        SessionLockLabels labels = Labels(store);
        StoreOptions options = (StoreOptions) store.Options;

        int baseId = options.Projections.DaemonLockId;
        var shard = options.Projections.AllShards().Should().ContainSingle().Subject;
        int shardId = ProjectionLockIds.Compute(DatabaseTestStores.EventSchema, shard.Name, baseId);

        labels.Advisory(baseId).Kind.Should().Be(LockTargetKind.DaemonLock);
        labels.Advisory(shardId).Kind.Should().Be(LockTargetKind.ShardLock);
        labels.Advisory(shardId).Label.Should().Contain(shard.Name.Identity);
        labels.DaemonLockIds.Should().BeEquivalentTo(new long[] { baseId, shardId });
    }

    [Fact]
    public void The_advisory_keys_need_no_classifier()
    {
        SessionLockLabels labels = SessionLockLabels.For(Store(), "default", classifier: null);

        labels.DaemonLockIds.Should().NotBeEmpty("C04 builds the labels for the daemon's keys alone");
        labels.Describe("relation", Owner(DatabaseTestStores.DocumentSchema, Customers)).Kind.Should().Be(
            LockTargetKind.UnnamedRelation, "without every store's declarations nothing is called anybody's (fail closed)");
    }

    [Fact]
    public void An_advisory_lock_that_is_none_of_Martens_is_said_to_be_so()
    {
        Labels().Advisory(987_654_321).Kind.Should().Be(LockTargetKind.OtherAdvisoryLock);
        Labels().Advisory(null).Kind.Should().Be(LockTargetKind.OtherAdvisoryLock, "a two-key advisory lock is never Marten's");
    }

    [Fact]
    public void A_row_wait_on_mt_streams_is_an_mt_streams_row()
    {
        LockTarget target = Labels().Describe("transactionid", row: Owner(DatabaseTestStores.EventSchema, "mt_streams"));

        target.Kind.Should().Be(LockTargetKind.EventTable);
        target.Row.Should().BeTrue();
        target.Label.Should().Be("an mt_streams row (" + DatabaseTestStores.EventSchema + ")");
    }

    [Fact]
    public void A_row_of_a_partition_is_a_row_of_its_table()
    {
        LockTarget target = Labels().Describe("transactionid", row: Owner(DatabaseTestStores.EventSchema, "mt_streams", LockedPart.Partition));

        target.Label.Should().Be("an mt_streams row (" + DatabaseTestStores.EventSchema + ")", "which partition holds the row is nobody's business");
    }

    [Fact]
    public void A_document_table_is_named_by_its_alias()
    {
        LockTarget target = Labels().Describe("relation", Owner(DatabaseTestStores.DocumentSchema, Customers));

        target.Kind.Should().Be(LockTargetKind.DocumentTable);
        target.Label.Should().Be("the dbcustomer document table (" + DatabaseTestStores.DocumentSchema + "." + Customers + ")");
        target.IsMarten.Should().BeTrue();
    }

    // The theories pass the part by name: the enum is internal.
    [Theory]
    [InlineData(nameof(LockedPart.Index), "an index of the dbcustomer document table")]
    [InlineData(nameof(LockedPart.Partition), "a partition of the dbcustomer document table")]
    [InlineData(nameof(LockedPart.Toast), "the TOAST data of the dbcustomer document table")]
    [InlineData(nameof(LockedPart.OwnedSequence), "a sequence owned by the dbcustomer document table")]
    public void A_lock_on_part_of_a_table_is_labelled_by_the_table_and_the_part(string part, string expected)
    {
        LockTarget target = Labels().Describe("relation", Owner(DatabaseTestStores.DocumentSchema, Customers, Enum.Parse<LockedPart>(part)));

        target.Label.Should().StartWith(expected);
        target.Schema.Should().Be(DatabaseTestStores.DocumentSchema);
        target.Name.Should().Be(Customers, "the details name the table, never the index or the partition");
    }

    [Theory]
    [InlineData(nameof(LockedPart.Itself))]
    [InlineData(nameof(LockedPart.Index))]
    [InlineData(nameof(LockedPart.Partition))]
    public void A_hidden_document_types_table_is_named_to_nobody_and_its_part_is_not_said(string part)
    {
        LockTarget target = Labels().Describe("relation", Owner(DatabaseTestStores.DocumentSchema, "mt_doc_dbsecret", Enum.Parse<LockedPart>(part)));

        target.Kind.Should().Be(LockTargetKind.UnnamedRelation);
        target.Label.Should().Be("a relation", "not even that it is an index of something");
        target.Schema.Should().BeNull("not even the details may name it");
        target.Name.Should().BeNull();
    }

    [Theory]
    [InlineData(DatabaseTestStores.OtherSchema, "mt_doc_dbinvoice")]
    [InlineData(DatabaseTestStores.OtherSchema, DatabaseTestStores.OtherReportTable)]
    public void Another_stores_table_is_Martens_and_named_to_nobody(string schema, string name)
    {
        LockTarget target = Labels().Describe("relation", Owner(schema, name, LockedPart.Index));

        target.Kind.Should().Be(LockTargetKind.UndeclaredMartenRelation);
        target.Label.Should().Be("an index of a Marten relation this store does not declare");
        target.Label.Should().NotContain(schema).And.NotContain(name);
        target.Schema.Should().BeNull("another store's alias is not this visitor's to learn, details included");
        target.Name.Should().BeNull();
        target.IsMarten.Should().BeTrue();
    }

    [Fact]
    public void An_mt_name_no_store_declares_is_named_to_nobody()
    {
        // A stale table, or a type Marten has not learned yet - which may be a hidden one.
        LockTarget target = Labels().Describe("tuple", Owner(DatabaseTestStores.DocumentSchema, "mt_doc_dbpayroll"));

        target.Kind.Should().Be(LockTargetKind.UndeclaredMartenRelation);
        target.Label.Should().Be("a row of a Marten relation this store does not declare");
        target.Name.Should().BeNull();
    }

    [Fact]
    public void A_tenants_event_sequence_rolls_up_and_never_says_the_tenant()
    {
        LockTarget target = Labels().Describe(
            "relation", Owner(DatabaseTestStores.EventSchema, "mt_events_sequence_acme", kind: LockedRelation.SequenceKind));

        target.Kind.Should().Be(LockTargetKind.EventTable);
        target.Label.Should().Be("a tenant's event sequence (" + DatabaseTestStores.EventSchema + ")");
        target.Name.Should().Be("mt_events_sequence", "the details name the store's sequence, not the tenant's");
        target.Label.Should().NotContain("acme");

        Labels().Describe("relation", Owner(DatabaseTestStores.OtherSchema, "mt_events_sequence_acme", kind: LockedRelation.SequenceKind))
            .Kind.Should().Be(LockTargetKind.UndeclaredMartenRelation, "a schema whose event store is not this store's names nothing");
    }

    [Fact]
    public void This_stores_own_event_sequence_is_named()
    {
        Labels().Describe("relation", Owner(DatabaseTestStores.EventSchema, "mt_events_sequence", kind: LockedRelation.SequenceKind))
            .Label.Should().Be("Marten's " + DatabaseTestStores.EventSchema + ".mt_events_sequence");
    }

    [Fact]
    public void A_relation_Marten_does_not_own_is_labelled_by_kind_and_named_only_for_the_details()
    {
        LockTarget target = Labels().Describe("relation", Owner("legacy", "employees", LockedPart.Index));

        target.Kind.Should().Be(LockTargetKind.OtherRelation);
        target.Label.Should().Be("an index of a relation Marten does not own");
        target.Schema.Should().Be("legacy");
        target.Name.Should().Be("employees", "the table, never its index");
    }

    [Fact]
    public void A_system_catalog_is_Postgres_own_and_named()
    {
        Labels().Describe("relation", Owner("pg_catalog", "pg_class")).Label.Should().Be("the system catalog pg_catalog.pg_class");
        Labels().Describe("relation", Owner("pg_catalog", "pg_class", LockedPart.Index)).Label.Should().Be("an index of the system catalog pg_catalog.pg_class");
    }

    [Fact]
    public void A_toast_table_nothing_owns_and_a_temporary_table_are_never_named()
    {
        LockTarget toast = Labels().Describe("relation", Owner("pg_toast", "pg_toast_16384", kind: "t"));
        toast.Label.Should().Be("a TOAST table");
        toast.Name.Should().BeNull("its name is an oid");

        LockTarget temporary = Labels().Describe("relation", Owner("pg_temp_3", "payroll_scratch"));
        temporary.Label.Should().Be("a temporary table");
        temporary.Name.Should().BeNull("a temporary table's name is its session's");
    }

    [Fact]
    public void A_lock_the_read_could_not_resolve_names_nothing()
    {
        Labels().Describe("relation").Kind.Should().Be(LockTargetKind.UnnamedRelation);
        Labels().Describe("tuple").Label.Should().Be("a row of a relation");
    }

    [Fact]
    public void A_wait_on_another_transaction_with_no_row_known_says_so()
    {
        Labels().Describe("transactionid").Kind.Should().Be(LockTargetKind.Transaction);
        Labels().Describe("extend").Label.Should().Be("a lock of kind 'extend'");
    }
}
