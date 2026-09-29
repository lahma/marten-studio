using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Projections;

using Marten;
using Marten.Events.Projections.Flattened;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using Weasel.Postgresql;
using Weasel.Postgresql.Tables;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// DB-7 against a real Postgres: the Schema screen classifies with the database browser's rules, rolls
/// partitions up without taking a lock on any of them, withholds what the browser withholds, and tells the
/// truth about what an apply does to an <c>ExtendedSchemaObjects</c> table's indexes.
/// </summary>
/// <remarks>
/// <para>
/// One store per test, in schemas of the test's own, shaped to carry every case at once: a visible and a
/// hidden document type, a <c>long</c>-id type (so Marten creates <c>mt_hilo</c>), an event store, a
/// flat-table projection and a table handed to <c>ExtendedSchemaObjects</c> - all applied by Marten - and
/// then, by hand, what a host puts beside them: a Quartz.NET table <em>in the event schema</em> (the old
/// Tables tab badged it "event store" for living there), a list-partitioned table with a partition per
/// "tenant", an index nobody declared on the extended table, and a function, a procedure and an
/// aggregate of the host's own.
/// </para>
/// <para>
/// Everything is driven through the studio's own services, so the gate is exercised rather than bypassed.
/// </para>
/// </remarks>
public class SchemaAlignmentLiveTests(PostgresFixture fixture)
{
    private const string Partitioned = "host_measurements";

    private static readonly string[] Tenants = ["acme", "globex", "initech"];

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ------------------------------------------------------------------------------------------------
    // Tables
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_tables_tab_classifies_every_table_by_the_database_browsers_rules()
    {
        await using Host host = await StartAsync("db7_tables");

        SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(Scope));

        tables.Reason.Should().BeNull();

        TableStats quartz = tables.Tables.Single(x => x.Table == "qrtz_locks");
        quartz.Schema.Should().Be(host.EventSchema);
        quartz.Ownership.Owner.Should().Be(DatabaseObjectOwner.Other,
            "living in the event store's schema does not make a Quartz.NET table the event store's");
        quartz.Ownership.RecognisedAs.Should().Be("Quartz.NET");
        quartz.IsEventTable.Should().BeFalse();

        tables.Tables.Single(x => x.Table == "mt_hilo").Ownership.Owner
            .Should().Be(DatabaseObjectOwner.MartenInfrastructure);

        tables.Tables.Single(x => x.Table == "flat_orders").Ownership.Owner
            .Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);

        tables.Tables.Single(x => x.Table == "ext_things").Ownership.Owner
            .Should().Be(DatabaseObjectOwner.MartenProjectionOrExtended);

        tables.Tables.Single(x => x.Table == "mt_events").IsEventTable.Should().BeTrue();

        TableStats customer = tables.Tables.Single(x => x.Table == "mt_doc_aligncustomer");
        customer.Ownership.Owner.Should().Be(DatabaseObjectOwner.MartenDocument);
        customer.CollectionAlias.Should().Be("aligncustomer");
    }

    /// <summary>A hidden document type is absent - not "not Marten", not anything.</summary>
    [PostgresFact]
    public async Task A_hidden_document_types_table_is_not_on_the_tables_tab()
    {
        await using Host host = await StartAsync("db7_hidden");

        (await host.TableNamesAsync()).Should().Contain("mt_doc_alignsecret", "the table is really there");

        SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(Scope));

        tables.Tables.Should().NotContain(x => x.Table.Contains("alignsecret", StringComparison.Ordinal));
    }

    /// <summary>
    /// A partitioned table is one row with its partitions' figures in it, and no partition is ever a row -
    /// their names are the tenant list. With the gate shut the count of them is withheld too.
    /// </summary>
    [PostgresFact]
    public async Task Partitions_roll_up_into_their_parent_and_their_count_is_withheld_while_the_gate_is_shut()
    {
        await using Host host = await StartAsync("db7_rollup");

        SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(Scope));

        tables.Tables.Should().NotContain(x => x.Table.StartsWith(Partitioned + "_", StringComparison.Ordinal),
            "a partition is never a row: Marten names a tenant's partition after the tenant");

        TableStats parent = tables.Tables.Single(x => x.Table == Partitioned);
        parent.IsPartitioned.Should().BeTrue();
        parent.PartitionCount.Should().BeNull("a per-tenant partition count is the number of tenants");
        tables.PartitionCountsWithheld.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        // The partitions' figures are in the parent's row: 300 rows analysed in each of three partitions,
        // and pages on disk that a partitioned table of its own does not have.
        parent.EstimatedRows.Should().Be(Tenants.Length * RowsPerTenant);
        parent.HeapBytes.Should().BeGreaterThan(0);
        parent.TotalBytes.Should().BeGreaterThanOrEqualTo(parent.HeapBytes + parent.IndexBytes);
        parent.IndexBytes.Should().BeGreaterThan(0);
        parent.LastAnalyze.Should().NotBeNull("the partitions were analysed, and their latest is the parent's");
    }

    [PostgresFact]
    public async Task Partition_counts_are_shown_to_a_visitor_past_the_database_browsers_gate()
    {
        await using Host host = await StartAsync("db7_open", open: true);

        SchemaTables tables = await host.SchemaAsync(x => x.TablesAsync(Scope));

        tables.PartitionCountsWithheld.Should().BeNull();
        tables.Tables.Single(x => x.Table == Partitioned).PartitionCount.Should().Be(Tenants.Length);
        tables.Tables.Should().NotContain(x => x.Table.StartsWith(Partitioned + "_", StringComparison.Ordinal),
            "past the gate the count is shown, and the names still are not");
    }

    /// <summary>
    /// The lock test. <c>pg_partition_tree()</c> holds one <c>AccessShareLock</c> per partition until the
    /// transaction ends, and a size function over a partition queues behind any migration holding one - so
    /// the Tables and Indexes reads are run here inside a transaction, over a parent one of whose
    /// partitions another session holds in <c>ACCESS EXCLUSIVE</c> mode, with a short
    /// <c>lock_timeout</c>. They must finish, and <c>pg_locks</c> must show this backend holding no lock on
    /// any partition afterwards.
    /// </summary>
    [PostgresFact]
    public async Task The_tables_and_indexes_reads_take_no_lock_on_any_partition()
    {
        await using Host host = await StartAsync("db7_locks");
        string[] schemas = [host.Schema, host.EventSchema];

        await using NpgsqlConnection migration = await fixture.OpenAsync(Token);
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await ExecuteAsync(migration, $"lock table {host.Schema}.{Partitioned}_{Tenants[0]} in access exclusive mode");

        await using NpgsqlConnection reader = await fixture.OpenAsync(Token);
        await using NpgsqlTransaction reading = await reader.BeginTransactionAsync(Token);
        await ExecuteAsync(reader, "set local lock_timeout = '2s'");

        IReadOnlyList<TableStatsRow> tables = await SchemaStatsQueries.ReadTablesAsync(reader, reading, schemas, 30, Token);
        IReadOnlyList<IndexStatsRow> indexes = await SchemaStatsQueries.ReadIndexesAsync(reader, reading, schemas, 30, Token);

        tables.Should().Contain(x => x.Table == Partitioned && x.IsPartitioned && x.PartitionCount == Tenants.Length);
        tables.Should().NotContain(x => x.Table.StartsWith(Partitioned + "_", StringComparison.Ordinal));
        indexes.Should().NotContain(x => x.Table.StartsWith(Partitioned + "_", StringComparison.Ordinal),
            "an index on a partition is named after the partition");
        indexes.Should().Contain(x => x.Table == Partitioned, "the partitioned parent's own indexes are listed");

        (await PartitionLocksAsync(reader, host.Schema)).Should().BeEmpty(
            "the reads walk pg_inherits and catalog columns and never open a partition");

        await reading.RollbackAsync(Token);
        await holding.RollbackAsync(Token);
    }

    /// <summary>
    /// The lock test's anti-vacuity: the measurement above can see what it is looking for. A size function
    /// over a partition really does wait behind the other session's lock, and
    /// <c>pg_partition_tree()</c> really does leave a lock on every partition behind it.
    /// </summary>
    [PostgresFact]
    public async Task The_lock_measurement_sees_a_read_that_does_lock_the_partitions()
    {
        await using Host host = await StartAsync("db7_lockproof");
        string partition = $"{host.Schema}.{Partitioned}_{Tenants[0]}";

        await using (NpgsqlConnection migration = await fixture.OpenAsync(Token))
        {
            await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
            await ExecuteAsync(migration, $"lock table {partition} in access exclusive mode");

            await using NpgsqlConnection sizing = await fixture.OpenAsync(Token);
            await using NpgsqlTransaction trying = await sizing.BeginTransactionAsync(Token);
            await ExecuteAsync(sizing, "set local lock_timeout = '500ms'");

            Func<Task> size = () => ExecuteAsync(sizing, $"select pg_total_relation_size('{partition}'::regclass)");

            (await size.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55P03",
                "a size function over a partition queues behind a migration's lock on it");

            await holding.RollbackAsync(Token);
        }

        await using NpgsqlConnection tree = await fixture.OpenAsync(Token);
        await using NpgsqlTransaction walking = await tree.BeginTransactionAsync(Token);

        await ExecuteAsync(tree, $"select count(*) from pg_partition_tree('{host.Schema}.{Partitioned}'::regclass)");

        (await PartitionLocksAsync(tree, host.Schema)).Should().HaveCount(Tenants.Length,
            "pg_partition_tree holds an AccessShareLock on every partition until the transaction ends");

        await walking.RollbackAsync(Token);
    }

    // ------------------------------------------------------------------------------------------------
    // Indexes
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_indexes_tab_treats_an_extended_table_as_managed_and_leaves_out_partitions_and_hidden_types()
    {
        await using Host host = await StartAsync("db7_indexes");

        SchemaIndexes indexes = await host.SchemaAsync(x => x.IndexesAsync(Scope));

        indexes.Reason.Should().BeNull();

        IndexInfo declared = indexes.Indexes.Single(x => x.Name == ExtendedIndex);
        declared.OnMartenTable.Should().BeTrue();
        declared.DeclaredByMarten.Should().BeTrue("the extended table's own definition declares it");
        declared.WouldBeDropped.Should().BeFalse();

        IndexInfo handMade = indexes.Indexes.Single(x => x.Name == HandMadeOnExtended);
        handMade.OnMartenTable.Should().BeTrue("Marten migrates ExtendedSchemaObjects like its own tables");
        handMade.WouldBeDropped.Should().BeTrue();
        handMade.Suggestion.Should().Be(IndexAdvice.ManagedTableSuggestion);

        indexes.Indexes.Should().NotContain(x => x.Table.StartsWith(Partitioned + "_", StringComparison.Ordinal));
        indexes.Indexes.Should().NotContain(x => x.Table.Contains("alignsecret", StringComparison.Ordinal)
            || x.Name.Contains("alignsecret", StringComparison.Ordinal));
        indexes.Missing.Should().NotContain(x => x.Table.Contains("alignsecret", StringComparison.Ordinal));

        IndexInfo onParent = indexes.Indexes.Single(x => x.Table == Partitioned && x.IsPrimaryKey);
        onParent.OnMartenTable.Should().BeFalse();
        onParent.WouldBeDropped.Should().BeFalse();
        onParent.Suggestion.Should().Be(IndexAdvice.ForeignTableSuggestion);
        onParent.Bytes.Should().BeGreaterThan(0, "a partitioned index carries its partitions' pages");
    }

    /// <summary>
    /// The live proof behind the corrected wording. The old Indexes tab said an <c>ExtendedSchemaObjects</c>
    /// table was one "no migration from here touches"; this applies a migration under
    /// <c>CreateOrUpdate</c> - the studio's own mode - and the index nobody declared on it is gone.
    /// </summary>
    [PostgresFact]
    public async Task An_apply_drops_an_index_nobody_declared_on_an_extended_table()
    {
        await using Host host = await StartAsync("db7_extdrop");

        (await host.IndexNamesAsync("ext_things")).Should().Contain(HandMadeOnExtended);

        IDocumentStore store = host.Services.GetRequiredService<IDocumentStore>();
        await store.Storage.Database.ApplyAllConfiguredChangesToDatabaseAsync(AutoCreate.CreateOrUpdate, ct: Token);

        IReadOnlyList<string> after = await host.IndexNamesAsync("ext_things");
        after.Should().NotContain(HandMadeOnExtended, "AutoCreate.CreateOrUpdate is not additive, on an extended table too");
        after.Should().Contain(ExtendedIndex, "and the declared one is kept");
    }

    /// <summary>
    /// DB-7-fix, item 4: the same drop, seen before it happens - through the studio's own preview, where
    /// <see cref="MigrationRisk" /> lists the hand-made index on the extended table as a destructive
    /// <c>drop index</c> for the dialog to show before the typed confirmation. The store hides a document type,
    /// so the visitor is past the database browser's gate to be shown the script at all.
    /// </summary>
    [PostgresFact]
    public async Task The_preview_lists_the_extended_tables_undeclared_index_as_a_destructive_drop_index()
    {
        await using Host host = await StartAsync("db7_extpreview", open: true);

        MigrationPreview preview = await host.SchemaAsync(x => x.PreviewAsync(Scope));

        preview.Withheld.Should().BeNull();
        preview.Notice.Should().BeNull();
        preview.IsDestructive.Should().BeTrue();

        DestructiveStatement drop = preview.DestructiveStatements
            .Should().ContainSingle(static x => x.Statement.Contains(HandMadeOnExtended, StringComparison.Ordinal)).Subject;
        drop.Kind.Should().Be("drops an index");
        drop.Statement.Should().StartWithEquivalentOf("drop index");

        preview.DestructiveStatements.Should().NotContain(static x => x.Statement.Contains(ExtendedIndex, StringComparison.Ordinal),
            "the declared index is kept");
        MigrationRisk.KindsIn(preview.Sql).Should().Contain("drops an index");

        (await host.IndexNamesAsync("ext_things")).Should().Contain(HandMadeOnExtended, "a preview runs nothing");
    }

    /// <summary>The same store, with the gate shut: the preview names the hidden type's table, so it is withheld.</summary>
    [PostgresFact]
    public async Task With_the_gate_shut_the_preview_of_a_store_that_hides_a_type_is_withheld()
    {
        await using Host host = await StartAsync("db7_extpreview_shut");

        MigrationPreview preview = await host.SchemaAsync(x => x.PreviewAsync(Scope));

        preview.Withheld!.Kind.Should().Be(DatabaseRefusal.CapabilityOff);
        preview.HasSql.Should().BeFalse();
        preview.DestructiveStatements.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // Functions
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// With the gate shut, the host's own routines are listed - each saying what it is - and their bodies
    /// are withheld with a sentence naming the option; Marten's own are not. The definition read refuses
    /// the host's body too, and audits it: the list's answer is the service's.
    /// </summary>
    [PostgresFact]
    public async Task With_the_gate_shut_a_host_functions_body_is_refused_and_Martens_own_are_not()
    {
        await using Host host = await StartAsync("db7_fn_shut");

        SchemaFunctions functions = await host.SchemaAsync(x => x.FunctionsAsync(Scope));

        functions.Reason.Should().BeNull();

        FunctionInfo touch = functions.Functions.Single(x => x.Name == "host_touch");
        touch.Ownership.IsMarten.Should().BeFalse();
        touch.DefinitionAvailable.Should().BeFalse();
        touch.DefinitionRefusal.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase");

        FunctionInfo procedure = functions.Functions.Single(x => x.Name == "host_proc");
        procedure.Kind.Should().Be(DatabaseObjectKind.Procedure);
        procedure.DefinitionAvailable.Should().BeFalse();

        FunctionInfo aggregate = functions.Functions.Single(x => x.Name == "host_sum");
        aggregate.Kind.Should().Be(DatabaseObjectKind.Aggregate);
        aggregate.DefinitionAvailable.Should().BeFalse();
        aggregate.DefinitionRefusal.Should().Be(DatabaseObjectService.AggregateHasNoBody);

        FunctionInfo append = functions.Functions.Single(x => x.Name == "mt_quick_append_events");
        append.DefinitionAvailable.Should().BeTrue("Marten's own bodies in the store's own schemas need no capability");

        DatabaseObjectDefinition refused = await host.DefinitionAsync(touch.Ref);
        refused.Found.Should().BeFalse();
        refused.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        host.Audit.GetLatest().Should().Contain(x => !x.Succeeded && x.Target.Contains("host_touch", StringComparison.Ordinal));

        DatabaseObjectDefinition martens = await host.DefinitionAsync(append.Ref);
        martens.Found.Should().BeTrue(martens.Reason);
        martens.Sql.Should().Contain("CREATE OR REPLACE FUNCTION");
    }

    /// <summary>
    /// Past the gate - the capability, the write policy, and the store's schema in
    /// <c>BrowsableSchemas</c> - the host's function and procedure bodies are read; an aggregate is still
    /// listed without one, because Postgres prints none.
    /// </summary>
    [PostgresFact]
    public async Task Past_the_gate_a_host_functions_and_a_procedures_bodies_are_read_and_an_aggregate_has_none()
    {
        await using Host host = await StartAsync("db7_fn_open", open: true);

        SchemaFunctions functions = await host.SchemaAsync(x => x.FunctionsAsync(Scope));

        FunctionInfo touch = functions.Functions.Single(x => x.Name == "host_touch");
        touch.DefinitionAvailable.Should().BeTrue(touch.DefinitionRefusal);

        DatabaseObjectDefinition touchBody = await host.DefinitionAsync(touch.Ref);
        touchBody.Found.Should().BeTrue(touchBody.Reason);
        touchBody.Sql.Should().Contain("select 42");

        FunctionInfo procedure = functions.Functions.Single(x => x.Name == "host_proc");
        procedure.Kind.Should().Be(DatabaseObjectKind.Procedure);
        procedure.DefinitionAvailable.Should().BeTrue(procedure.DefinitionRefusal);

        DatabaseObjectDefinition procedureBody = await host.DefinitionAsync(procedure.Ref);
        procedureBody.Found.Should().BeTrue(procedureBody.Reason);
        procedureBody.Sql.Should().Contain("PROCEDURE", "pg_get_functiondef prints a procedure's body; it refuses only aggregates");

        FunctionInfo aggregate = functions.Functions.Single(x => x.Name == "host_sum");
        aggregate.Kind.Should().Be(DatabaseObjectKind.Aggregate);
        aggregate.DefinitionAvailable.Should().BeFalse();

        DatabaseObjectDefinition aggregateBody = await host.DefinitionAsync(aggregate.Ref);
        aggregateBody.Found.Should().BeFalse();
        aggregateBody.Refusal.Should().Be(DatabaseRefusal.NotApplicable);
    }

    // ------------------------------------------------------------------------------------------------
    // The store
    // ------------------------------------------------------------------------------------------------

    private const int RowsPerTenant = 300;

    private const string ExtendedIndex = "ext_things_idx_name";

    private const string HandMadeOnExtended = "hand_rolled_on_extended";

    /// <summary>
    /// Builds the store, lets Marten apply it, and then adds the host's own objects by hand.
    /// </summary>
    /// <param name="schema">The document schema; the event schema is this plus <c>_events</c>.</param>
    /// <param name="open">
    /// Whether the visitor is past the database browser's gate: every capability, and the document schema
    /// in <c>BrowsableSchemas</c>. No policy is configured, so the write policy's answer is yes.
    /// </param>
    private async Task<Host> StartAsync(string schema, bool open = false)
    {
        string eventSchema = schema + "_events";

        await fixture.CreateSchemaAsync(schema, Token);
        await fixture.CreateSchemaAsync(eventSchema, Token);

        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = eventSchema;
            options.AutoCreateSchemaObjects = AutoCreate.None;

            options.Schema.For<AlignCustomer>();
            options.Schema.For<AlignSecret>().Index(x => x.Code);
            options.Schema.For<AlignNumbered>();

            options.Events.AddEventType<AlignOrderPlaced>();
            options.Projections.Add(new AlignFlatProjection(schema), ProjectionLifecycle.Inline);

            var extended = new Table(new PostgresqlObjectName(schema, "ext_things", SchemaUtils.IdentifierUsage.General));
            extended.AddColumn<int>("id").AsPrimaryKey();
            extended.AddColumn<string>("name");
            extended.Indexes.Add(new IndexDefinition(ExtendedIndex) { Columns = ["name"] });
            options.Storage.ExtendedSchemaObjects.Add(extended);
        });

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;
            options.IsDocumentTypeVisible = static type => type != typeof(AlignSecret);

            if (open)
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.BrowsableSchemas.Add(schema);
            }
        });

        ServiceProvider provider = services.BuildServiceProvider();
        var host = new Host(provider, fixture, schema, eventSchema);

        try
        {
            IDocumentStore store = provider.GetRequiredService<IDocumentStore>();
            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            await using NpgsqlConnection connection = await fixture.OpenAsync(Token);

            // What a host keeps beside Marten. Raw SQL in a test, deliberately: nothing in StoreOptions knows
            // about any of it, which is the point.
            string partitions = string.Concat(Tenants.Select((tenant, index) =>
                $"create table {schema}.{Partitioned}_{tenant} partition of {schema}.{Partitioned} for values in ('{tenant}');" +
                $"insert into {schema}.{Partitioned} select g, '{tenant}', repeat('v', 200) from generate_series({index * RowsPerTenant + 1}, {(index + 1) * RowsPerTenant}) g;"));

            await ExecuteAsync(connection, $"""
                create table {eventSchema}.qrtz_locks (sched_name varchar(120) not null, lock_name varchar(40) not null, primary key (sched_name, lock_name));
                create table {schema}.{Partitioned} (id int not null, tenant text not null, v text, primary key (id, tenant)) partition by list (tenant);
                create index {Partitioned}_v on {schema}.{Partitioned} (v);
                {partitions}
                analyze {schema}.{Partitioned};
                create index {HandMadeOnExtended} on {schema}.ext_things (lower(name));
                create function {schema}.host_touch() returns int language sql as $$ select 42 $$;
                create procedure {schema}.host_proc() language sql as $$ select 1 $$;
                create aggregate {schema}.host_sum(int) (sfunc = int4pl, stype = int);
                """);

            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Every lock this connection's backend holds on a partition in <paramref name="schema" />.</summary>
    private static async Task<IReadOnlyList<string>> PartitionLocksAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            """
            select c.relname || ' ' || l.mode
            from pg_locks l
            join pg_class c on c.oid = l.relation
            join pg_namespace n on n.oid = c.relnamespace
            where l.pid = pg_backend_pid()
              and c.relispartition
              and n.nspname = @schema
            order by 1
            """,
            connection);

        command.Parameters.AddWithValue("schema", schema);

        List<string> locks = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            locks.Add(reader.GetString(0));
        }

        return locks;
    }

    /// <summary>One built studio over one pair of schemas.</summary>
    private sealed class Host(ServiceProvider provider, PostgresFixture postgres, string schema, string eventSchema)
        : IAsyncDisposable
    {
        public string Schema => schema;

        public string EventSchema => eventSchema;

        public IServiceProvider Services => provider;

        public StudioActionLogService Audit => provider.GetRequiredService<StudioActionLogService>();

        public async Task<T> SchemaAsync<T>(Func<ISchemaDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<ISchemaDataService>());
        }

        /// <summary>What the Functions tab asks for when a row is opened.</summary>
        public async Task<DatabaseObjectDefinition> DefinitionAsync(DatabaseObjectRef reference)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>()
                .GetDefinitionAsync(Scope, reference, Token);
        }

        public async Task<IReadOnlyList<string>> TableNamesAsync() =>
            await NamesAsync("select tablename from pg_tables where schemaname = @schema order by 1", null);

        public async Task<IReadOnlyList<string>> IndexNamesAsync(string table) =>
            await NamesAsync("select indexname from pg_indexes where schemaname = @schema and tablename = @table order by 1", table);

        public async ValueTask DisposeAsync() => await provider.DisposeAsync();

        private async Task<IReadOnlyList<string>> NamesAsync(string sql, string? table)
        {
            await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("schema", schema);

            if (table is not null)
            {
                command.Parameters.AddWithValue("table", table);
            }

            List<string> names = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }
    }

    /// <summary>Somebody is signed in; who they are is not what these tests are about.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}

/// <summary>A visible document type.</summary>
[DocumentAlias("aligncustomer")]
public class AlignCustomer
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>The document type the host hides from the studio, with an index of its own.</summary>
[DocumentAlias("alignsecret")]
public class AlignSecret
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;
}

/// <summary>A <c>long</c> id, so Marten's HiLo feature is active and an apply creates <c>mt_hilo</c>.</summary>
[DocumentAlias("alignnumbered")]
public class AlignNumbered
{
    public long Id { get; set; }
}

/// <summary>The one event the flat table is written from.</summary>
public class AlignOrderPlaced
{
    public decimal Amount { get; set; }
}

/// <summary>A flat-table projection into <c>{schema}.flat_orders</c>: a plain Weasel table the host shaped.</summary>
public class AlignFlatProjection : FlatTableProjection
{
    public AlignFlatProjection(string schema)
        : base(new PostgresqlObjectName(schema, "flat_orders", SchemaUtils.IdentifierUsage.General))
    {
        Table.AddColumn<Guid>("id").AsPrimaryKey();
        Table.AddColumn<decimal>("amount");

        Project<AlignOrderPlaced>(map => map.Map(x => x.Amount, "amount"));
    }
}
