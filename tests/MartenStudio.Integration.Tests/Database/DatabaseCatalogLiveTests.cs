using MartenStudio.Services.Database;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// Acceptance 7: the database browser's catalog reads against a real Postgres 17, one kind of object at a
/// time, through the studio's own service - so every read went through the gate and the read-only session.
/// </summary>
public class DatabaseCatalogLiveTests(DatabaseCatalogLiveTests.Fixture fixture) : IClassFixture<DatabaseCatalogLiveTests.Fixture>
{
    /// <summary>This class's schemas.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres);

    [PostgresFact]
    public async Task A_Quartz_shaped_table_has_its_composite_key_and_its_composite_foreign_key()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectDetail triggers = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.QuartzSchema, "qrtz_triggers"));

        triggers.Found.Should().BeTrue(triggers.Reason);
        triggers.Relation!.Ownership.Owner.Should().Be(DatabaseObjectOwner.Other);
        triggers.Relation.Ownership.RecognisedAs.Should().Be("Quartz.NET");
        triggers.Relation.Rows.Allowed.Should().BeTrue(triggers.Relation.Rows.Reason);
        triggers.Relation.Readable.Should().BeTrue();
        triggers.Relation.Quotable.Should().BeTrue();

        triggers.RowKey!.Source.Should().Be(DatabaseRowKeySource.PrimaryKey);
        triggers.RowKey.Columns.Should().Equal("sched_name", "trigger_name", "trigger_group");

        DatabaseForeignKeyInfo key = triggers.ForeignKeysOut.Should().ContainSingle().Which;
        key.Columns.Should().Equal("sched_name", "job_name", "job_group");
        key.LinkedSchema.Should().Be(fixture.QuartzSchema);
        key.LinkedTable.Should().Be("qrtz_job_details");
        key.LinkedColumns.Should().Equal("sched_name", "job_name", "job_group");
        key.Validated.Should().BeTrue();

        DatabaseIndexInfo nextFire = triggers.Indexes.Single(static x => x.Name == "idx_qrtz_t_next_fire_time");
        nextFire.KeyColumns.Should().Equal(["next_fire_time"], "an INCLUDE column is not part of the key");

        DatabaseColumnInfo nextFireTime = triggers.Columns.Single(static x => x.Name == "next_fire_time");
        nextFireTime.Type.Should().Be("bigint");
        nextFireTime.Nullable.Should().BeTrue();
        nextFireTime.Sortable.Should().BeTrue();
        triggers.Columns.Single(static x => x.Name == "sched_name").Type.Should().Be("character varying(120)");

        DatabaseObjectDetail details = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.QuartzSchema, "qrtz_job_details"));
        details.ForeignKeysIn.Should().ContainSingle().Which.Table.Should().Be("qrtz_triggers");
    }

    [PostgresFact]
    public async Task A_keyless_table_has_no_row_key_and_a_unique_not_null_index_is_one()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectDetail keyless = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "keyless_log"));
        keyless.Found.Should().BeTrue(keyless.Reason);
        keyless.RowKey.Should().BeNull();
        keyless.Relation!.HasPrimaryKey.Should().BeFalse();

        DatabaseObjectDetail codes = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "codes"));
        codes.RowKey!.Source.Should().Be(DatabaseRowKeySource.UniqueIndex);
        codes.RowKey.Columns.Should().Equal(["code"], "label is an INCLUDE column, never a key");
    }

    [PostgresFact]
    public async Task Views_and_materialized_views_populated_or_not()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList views = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Views, fixture.LegacySchema)));

        views.Refusal.Should().Be(DatabaseRefusal.None, views.Reason);

        var byName = views.Items.Cast<DatabaseRelationSummary>().ToDictionary(static x => x.Name);

        byName["job_summary"].Kind.Should().Be(DatabaseObjectKind.View);
        byName["job_summary"].Rows.Allowed.Should().BeTrue(byName["job_summary"].Rows.Reason);
        byName["job_summary"].DefinitionAvailable.Should().BeTrue();
        byName["populated_stats"].Kind.Should().Be(DatabaseObjectKind.MaterializedView);
        byName["populated_stats"].Populated.Should().BeTrue();
        byName["empty_stats"].Populated.Should().BeFalse("it was created WITH NO DATA and never refreshed");

        DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.LegacySchema, "job_summary")));

        definition.Found.Should().BeTrue(definition.Reason);
        definition.Sql.Should().Contain("qrtz_triggers");

        DatabaseObjectDetail detail = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "job_summary"));
        detail.Dependencies.Should().Contain(x => x.Name == "qrtz_triggers" && x.Visible && x.Depth == 1);
    }

    /// <summary>A view over a hidden type's table: listed, its rows refused, its query not shown.</summary>
    [PostgresFact]
    public async Task A_view_over_a_hidden_types_table_is_refused_its_rows_and_its_definition()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectDetail detail = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "secret_peek"));

        detail.Found.Should().BeTrue(detail.Reason);
        detail.Relation!.Rows.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);
        detail.Dependencies.Should().ContainSingle(static x => !x.Visible && x.Name == null,
            "the hidden table is not named, not even as a dependency");

        DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.View, fixture.LegacySchema, "secret_peek")));

        definition.Found.Should().BeFalse();
        definition.Refusal.Should().Be(DatabaseRefusal.HiddenDependency);
        definition.Sql.Should().BeNull();
    }

    [PostgresFact]
    public async Task A_partitioned_table_is_listed_once_with_its_partitions_counted()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList tables = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.LegacySchema)));

        var measurements = tables.Items.Cast<DatabaseRelationSummary>().Single(static x => x.Name == "measurements");

        measurements.Kind.Should().Be(DatabaseObjectKind.PartitionedTable);
        measurements.PartitionCount.Should().Be(2);

        tables.Items.Select(static x => x.Name).Should().NotContain(static x => x.StartsWith("measurements_", StringComparison.Ordinal),
            "a partition's name is never listed");
    }

    [PostgresFact]
    public async Task An_enum_a_domain_a_composite_and_a_range_are_described()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList types = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Types, fixture.LegacySchema)));

        var byName = types.Items.Cast<DatabaseTypeSummary>().ToDictionary(static x => x.Name);

        byName["order_status"].Kind.Should().Be(DatabaseObjectKind.EnumType);
        byName["order_status"].Labels.Should().Equal("pending", "shipped", "cancelled");
        byName["order_status"].UsedByColumns.Should().Be(1);

        byName["positive_amount"].Kind.Should().Be(DatabaseObjectKind.DomainType);
        byName["positive_amount"].BaseType.Should().Be("numeric(12,2)");
        byName["positive_amount"].Checks.Should().ContainSingle().Which.Should().Contain("VALUE > ");

        byName["address"].Kind.Should().Be(DatabaseObjectKind.CompositeType);
        byName["address"].Attributes.Should().Equal(new DatabaseTypeAttribute("street", "text"), new DatabaseTypeAttribute("city", "text"));

        byName["price_range"].Kind.Should().Be(DatabaseObjectKind.RangeType);
        byName["price_range"].RangeSubtype.Should().Be("numeric");

        byName.Keys.Should().NotContain("price_multirange", "Postgres made that one, and a multirange is not listed");
        byName.Keys.Should().NotContain("orders", "a table's row type is not a type of its own");

        DatabaseObjectDefinition described = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, byName["order_status"].Ref));

        described.Found.Should().BeTrue(described.Reason);
        described.Sql.Should().BeNull("a type is described, not printed as DDL");
        described.Type!.Labels.Should().Equal("pending", "shipped", "cancelled");
        described.Type.UsedByColumns.Should().Be(1);
    }

    [PostgresFact]
    public async Task Owned_and_standalone_sequences()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList sequences = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Sequences, fixture.LegacySchema)));

        var byName = sequences.Items.Cast<DatabaseSequenceSummary>().ToDictionary(static x => x.Name);

        byName["orders_id_seq"].OwnerTable.Should().Be("orders");
        byName["orders_id_seq"].OwnerColumn.Should().Be("id", "an identity column owns its sequence");
        byName["orders_legacy_number_seq"].OwnerColumn.Should().Be("legacy_number", "so does a serial one");

        DatabaseSequenceSummary standalone = byName["standalone_numbers"];
        standalone.OwnerTable.Should().BeNull();
        standalone.Start.Should().Be(100);
        standalone.CanReadValue.Should().BeTrue();
        standalone.LastValue.Should().BeNull("nothing has called nextval on it");
    }

    [PostgresFact]
    public async Task An_enabled_and_a_disabled_trigger_and_never_a_foreign_keys_internal_ones()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList triggers = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Triggers, fixture.LegacySchema)));

        var byName = triggers.Items.Cast<DatabaseTriggerSummary>().ToDictionary(static x => x.Name);

        byName.Keys.Should().BeEquivalentTo(["orders_enabled", "orders_disabled"],
            "order_notes' foreign key has triggers of its own, and they are internal");

        byName["orders_enabled"].Enabled.Should().BeTrue();
        byName["orders_enabled"].Timing.Should().Be("BEFORE");
        byName["orders_enabled"].Events.Should().Equal("UPDATE");
        byName["orders_enabled"].ForEachRow.Should().BeTrue();
        byName["orders_enabled"].FunctionName.Should().Be("touch");

        byName["orders_disabled"].Enabled.Should().BeFalse();
        byName["orders_disabled"].EnabledMode.Should().Be("D");
        byName["orders_disabled"].Timing.Should().Be("AFTER");

        DatabaseObjectDefinition definition = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope,
            new DatabaseObjectRef(DatabaseObjectKind.Trigger, fixture.LegacySchema, "orders_enabled", Table: "orders")));

        definition.Sql.Should().Contain("BEFORE UPDATE");
    }

    /// <summary>
    /// A function, a procedure and an aggregate - and the <c>pg_get_functiondef</c> finding: on PG 17 it
    /// answers for a procedure and raises 42809 only for an aggregate.
    /// </summary>
    [PostgresFact]
    public async Task A_function_a_procedure_and_an_aggregate()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList routines = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Functions, fixture.LegacySchema)));

        routines.Items.Select(static x => x.Name).Should().NotContain(["price_range", "price_multirange"],
            "Postgres writes those constructors for the range type; nobody here wrote them");

        var byName = routines.Items.Cast<DatabaseRoutineSummary>().ToDictionary(static x => x.Name);

        byName["recalculate"].Kind.Should().Be(DatabaseObjectKind.Function);
        byName["recalculate"].IdentityArguments.Should().Be("order_id bigint");
        byName["recalculate"].Language.Should().Be("plpgsql");
        byName["archive_orders"].Kind.Should().Be(DatabaseObjectKind.Procedure);
        byName["archive_orders"].Result.Should().BeNull();
        byName["archive_orders"].DefinitionAvailable.Should().BeTrue();
        byName["sum_amounts"].Kind.Should().Be(DatabaseObjectKind.Aggregate);
        byName["sum_amounts"].DefinitionAvailable.Should().BeFalse();

        DatabaseObjectDefinition function = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, byName["recalculate"].Ref));
        function.Sql.Should().Contain("CREATE OR REPLACE FUNCTION");

        DatabaseObjectDefinition procedure = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, byName["archive_orders"].Ref));
        procedure.Found.Should().BeTrue(procedure.Reason);
        procedure.Sql.Should().Contain("CREATE OR REPLACE PROCEDURE");

        DatabaseObjectDefinition aggregate = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, byName["sum_amounts"].Ref));
        aggregate.Refusal.Should().Be(DatabaseRefusal.NotApplicable);

        // The finding itself, measured directly: procedures are fine, aggregates raise 42809.
        await using NpgsqlConnection connection = await fixture.Postgres.OpenAsync();

        await using (var ofProcedure = new NpgsqlCommand(
            "select pg_get_functiondef(p.oid) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = @schema and p.proname = 'archive_orders'",
            connection))
        {
            ofProcedure.Parameters.AddWithValue("schema", fixture.LegacySchema);
            ((string?) await ofProcedure.ExecuteScalarAsync()).Should().StartWith("CREATE OR REPLACE PROCEDURE");
        }

        await using var ofAggregate = new NpgsqlCommand(
            "select pg_get_functiondef(p.oid) from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = @schema and p.proname = 'sum_amounts'",
            connection);
        ofAggregate.Parameters.AddWithValue("schema", fixture.LegacySchema);

        Func<Task> act = () => ofAggregate.ExecuteScalarAsync();
        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42809");
    }

    [PostgresFact]
    public async Task A_name_with_a_double_quote_is_listed_and_marked_not_quotable()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList tables = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.LegacySchema, NameFilter: "bad")));

        DatabaseRelationSummary bad = tables.Items.Cast<DatabaseRelationSummary>().Should().ContainSingle().Which;

        bad.Name.Should().Be("bad\"name");
        bad.Quotable.Should().BeFalse();
        bad.Rows.Refusal.Should().Be(DatabaseRefusal.Unquotable);
    }

    [PostgresFact]
    public async Task Marten_tables_are_classified_and_the_hidden_type_is_absent()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList tables = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.DocumentSchema)));

        var byName = tables.Items.Cast<DatabaseRelationSummary>().ToDictionary(static x => x.Name);

        byName[fixture.VisibleTable].Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenDocument);
        byName[fixture.VisibleTable].Rows.Refusal.Should().Be(DatabaseRefusal.MartenOwned);
        byName["host_settings"].Ownership.Owner.Should().Be(DatabaseObjectOwner.Other);
        byName.Keys.Should().NotContain(fixture.HiddenTable);

        DatabaseObjectList events = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.EventSchema)));

        events.Items.Should().Contain(static x => x.Name == "mt_events" && x.Ownership.Owner == DatabaseObjectOwner.MartenEventStore);

        DatabaseObjectDetail hidden = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.DocumentSchema, fixture.HiddenTable));
        hidden.Found.Should().BeFalse();
        hidden.Refusal.Should().Be(DatabaseRefusal.NotFound);
    }

    /// <summary>The overview names the visible schemas, flags the store's own, and counts the rest.</summary>
    [PostgresFact]
    public async Task The_overview_shows_the_store_schemas_and_the_browsable_ones_and_only_counts_the_rest()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseBrowserOverview overview = await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope));

        overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);
        overview.Access.IsOpen.Should().BeTrue();

        overview.Schemas.Select(static x => x.Name).Should().Equal(
            fixture.DocumentSchema, fixture.EventSchema, fixture.LegacySchema, fixture.QuartzSchema);

        overview.Schemas.Where(static x => x.IsStoreSchema).Select(static x => x.Name)
            .Should().Equal(fixture.DocumentSchema, fixture.EventSchema);

        DatabaseSchemaSummary quartz = overview.Schemas.Single(x => x.Name == fixture.QuartzSchema);
        quartz.Tables.Should().Be(2);
        quartz.RowsBrowsable.Should().BeTrue();

        overview.Access.WithheldSchemaCount.Should().BeGreaterThan(0, "public and every other test class's schemas exist");
    }

    /// <summary>
    /// Without the capability: the store's own structure, and no other schema - and a request for one is
    /// refused and audited before the database is asked about it.
    /// </summary>
    [PostgresFact]
    public async Task Without_the_capability_only_the_store_schemas_structure_is_there()
    {
        await using BrowserHost host = fixture.Host(static options => options.Capabilities.BrowseDatabase = false);

        DatabaseBrowserOverview overview = await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope));

        overview.Schemas.Select(static x => x.Name).Should().Equal(fixture.DocumentSchema, fixture.EventSchema);
        overview.Access.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);

        DatabaseObjectList quartz = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.QuartzSchema)));

        quartz.Items.Should().BeEmpty();
        quartz.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);

        DatabaseObjectDefinition hostFunction = await host.ObjectsAsync(x => x.GetDefinitionAsync(
            BrowserHost.Scope, new DatabaseObjectRef(DatabaseObjectKind.Function, fixture.DocumentSchema, "host_fn", string.Empty)));

        hostFunction.Refusal.Should().Be(DatabaseRefusal.CapabilityOff, "the host's code in the store's schema is still a BrowseDatabase read");

        DatabaseObjectList functions = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Functions, fixture.DocumentSchema)));

        DatabaseRoutineSummary martens = functions.Items.Cast<DatabaseRoutineSummary>()
            .First(static x => x.Name == "mt_immutable_timestamp");

        martens.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenInfrastructure);
        martens.DefinitionAvailable.Should().BeTrue();

        functions.Items.Cast<DatabaseRoutineSummary>().Single(static x => x.Name == "host_fn")
            .DefinitionAvailable.Should().BeFalse();

        DatabaseObjectDefinition martenFunction = await host.ObjectsAsync(x => x.GetDefinitionAsync(BrowserHost.Scope, martens.Ref));

        martenFunction.Found.Should().BeTrue("Marten's own functions in the store's schema are what the Schema screen shows: " + martenFunction.Reason);

        host.Ring.GetLatest().Should().Contain(static x => !x.Succeeded && x.Capability == "BrowseDatabase");
    }

    [PostgresFact]
    public async Task With_the_capability_a_schema_BrowsableSchemas_does_not_name_is_refused()
    {
        await using BrowserHost host = fixture.Host(options =>
        {
            options.BrowsableSchemas.Clear();
            options.BrowsableSchemas.Add(fixture.QuartzSchema);
        });

        DatabaseObjectList legacy = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.LegacySchema)));

        legacy.Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable);
        legacy.Reason.Should().Contain("MartenStudioOptions.BrowsableSchemas");

        DatabaseObjectDetail orders = await host.ObjectsAsync(x => x.GetObjectAsync(BrowserHost.Scope, fixture.LegacySchema, "orders"));
        orders.Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable);

        DatabaseBrowserOverview overview = await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope));
        overview.Schemas.Select(static x => x.Name).Should().NotContain(fixture.LegacySchema);
    }
}
