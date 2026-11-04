using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Schema;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// 1.2-APPLY, live: the apply section says what a previewed migration will lock and rewrite - the table's estimated rows and size
/// from the catalog's statistics, and the sessions on it now for a visitor through the Activity gate and one withheld line for anyone
/// else. Notes, never a refusal.
/// </summary>
/// <remarks>
/// The pending migration is <see cref="SchemaFixture.Drifted" />'s: the store as applied, plus a plain index on
/// <c>mt_doc_customer</c> that the database does not have, which Weasel writes as a <c>CREATE INDEX</c> without <c>CONCURRENTLY</c>.
/// </remarks>
public class MigrationImpactLiveTests(PostgresFixture postgres)
{
    private const string Customers = "mt_doc_customer";

    private const int Rows = 500;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_pending_index_on_a_populated_table_says_what_it_locks_how_many_rows_it_reads_and_what_to_set()
    {
        await using SchemaFixture schema = await StartPopulatedAsync("apply_impact_rows");

        MigrationPreview preview = await schema.Drifted().SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));

        preview.Withheld.Should().BeNull();
        preview.Sql.Should().Contain(SchemaFixture.DriftIndexName);
        preview.IsDestructive.Should().BeFalse("the apply still waits for a tick only for what removes something");

        MigrationImpact impact = preview.Impact;
        impact.Notice.Should().BeNull();

        TableImpact table = impact.Tables.Should().ContainSingle().Subject;
        table.Display.Should().Be(schema.Schema + "." + Customers);
        table.Statements.Should().ContainSingle(static x => x.IsBlockingIndexBuild && x.Lock == TableLockMode.Share);
        table.Rewrite.Should().Be(TableRewrite.No);

        table.Footprint.Should().NotBeNull();
        table.Footprint!.RowsKnown.Should().BeTrue();
        table.Footprint.EstimatedRows.Should().Be(Rows, "a table this small is analyzed whole");
        table.Footprint.HeapBytes.Should().BeGreaterThan(0);
        table.Footprint.TotalBytes.Should().BeGreaterThanOrEqualTo(table.Footprint.HeapBytes);
        table.Footprint.RowsText.Should().StartWith("~500");

        impact.Advice.Should().ContainSingle().Which.Should().Contain("BuildIndexesConcurrentlyOnAlter");
    }

    [PostgresFact]
    public async Task Without_the_activity_gate_the_sessions_are_one_withheld_line_and_nobody_is_read()
    {
        await using SchemaFixture schema = await StartPopulatedAsync("apply_impact_off");
        SchemaFixture.StudioHost drifted = schema.Drifted();

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, $"lock table {schema.Schema}.{Customers} in access share mode");

        MigrationPreview preview = await drifted.SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));

        preview.Impact.Tables.Should().ContainSingle().Which.Sessions.Should().BeNull();
        preview.Impact.Tables.Single().Footprint.Should().NotBeNull("rows and size are not activity");
        preview.Impact.SessionsNotice.Should().Contain("ViewDatabaseActivity").And.Contain("MartenStudioOptions.Capabilities");

        drifted.Audit.GetLatest().Should().NotContain(
            static x => x.Action == SchemaDataService.ImpactSessionsAction,
            "no session was read for a visitor the gate refuses");

        await holding.RollbackAsync(Token);
    }

    [PostgresFact]
    public async Task A_visitor_through_the_activity_gate_sees_one_session_by_state_and_never_what_it_ran()
    {
        await using SchemaFixture schema = await StartPopulatedAsync("apply_impact_on");
        SchemaFixture.StudioHost drifted = schema.Drifted(static options => options.Capabilities.ViewDatabaseActivity = true);

        MigrationPreview before = await drifted.SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));
        before.Impact.SessionsNotice.Should().BeNull();
        before.Impact.Tables.Single().Sessions!.Summary.Should().Contain("no sessions");

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, $"lock table {schema.Schema}.{Customers} in access share mode");

        MigrationPreview during = await drifted.SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));

        TableSessions sessions = during.Impact.Tables.Single().Sessions!;
        sessions.Holding.Should().Be(1);
        sessions.Waiting.Should().Be(0);
        sessions.Summary.Should().Be("1 session holds a lock (1 idle in transaction)");
        sessions.Summary.Should().NotContainEquivalentOf("lock table", "counts and states only, never the statement");

        // Counts only reach the audit ring too: one entry per read, under the capability, naming no table and no session.
        StudioActionLogEntry entry = drifted.Audit.GetLatest().First(static x => x.Action == SchemaDataService.ImpactSessionsAction);
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
        entry.Message.Should().NotContain(Customers).And.NotContainEquivalentOf("lock table");

        await holding.RollbackAsync(Token);

        MigrationPreview after = await drifted.SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));
        after.Impact.Tables.Single().Sessions!.Holding.Should().Be(0);
    }

    [PostgresFact]
    public async Task A_write_policy_that_refuses_the_capability_withholds_the_sessions_though_the_preview_is_shown()
    {
        await using SchemaFixture schema = await StartPopulatedAsync("apply_impact_policy");
        SchemaFixture.StudioHost drifted = schema.Drifted(static options =>
        {
            options.Capabilities.ViewDatabaseActivity = true;
            options.WriteAuthorizationPolicy = SchemaFixture.DenyPolicy;
        });

        await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, $"lock table {schema.Schema}.{Customers} in access share mode");

        MigrationPreview preview = await drifted.SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));

        preview.Withheld.Should().BeNull("the preview itself is the store policy's, and the store policy allows it");
        preview.Impact.Tables.Should().ContainSingle().Which.Sessions.Should().BeNull();
        preview.Impact.SessionsNotice.Should().Contain("write policy").And.Contain("ViewDatabaseActivity");
        drifted.Audit.GetLatest().Should().NotContain(static x => x.Action == SchemaDataService.ImpactSessionsAction);

        await holding.RollbackAsync(Token);
    }

    /// <summary>
    /// The sessions read counts this database only. Object ids are server-wide, so a name-based join proves nothing: the other database
    /// here is a template clone of this one, where the table has the very same oid, and a lock held on it there must not be counted
    /// when the table is asked about in this database. (The read filters <c>pg_locks.database</c>.)
    /// </summary>
    [PostgresFact]
    public async Task A_session_in_a_clone_database_holding_a_lock_on_the_same_table_id_is_not_counted()
    {
        string suffix = Guid.NewGuid().ToString("N")[..8];
        string a = "apply_impact_a_" + suffix;
        string b = "apply_impact_b_" + suffix;

        NpgsqlConnectionStringBuilder InDatabase(string name) => new(postgres.ConnectionString) { Database = name, Pooling = false };

        try
        {
            uint tableId;

            await using (NpgsqlConnection admin = await postgres.OpenAsync(Token))
            {
                await ExecuteAsync(admin, $"create database {a}");
            }

            await using (var first = new NpgsqlConnection(InDatabase(a).ConnectionString))
            {
                await first.OpenAsync(Token);
                await ExecuteAsync(first, "create schema tpl");
                await ExecuteAsync(first, "create table tpl.t (id int)");
                tableId = await ScalarAsync<uint>(first, "select 'tpl.t'::regclass::oid");
            }

            // The clone: same catalog, same object ids, a database of its own. A template must have no connections to it.
            await using (NpgsqlConnection admin = await postgres.OpenAsync(Token))
            {
                await ExecuteAsync(admin, $"create database {b} template {a}");
            }

            await using var cloneHolder = new NpgsqlConnection(InDatabase(b).ConnectionString);
            await cloneHolder.OpenAsync(Token);
            await using NpgsqlTransaction cloneHolding = await cloneHolder.BeginTransactionAsync(Token);
            await ExecuteAsync(cloneHolder, "lock table tpl.t in access share mode");

            // Anti-vacuity: the clone's table has the same id, and the lock table shows the clone's session holding it.
            (await ScalarAsync<uint>(cloneHolder, "select 'tpl.t'::regclass::oid")).Should().Be(tableId);

            await using (NpgsqlConnection admin = await postgres.OpenAsync(Token))
            {
                await using var held = new NpgsqlCommand(
                    """
                    select count(*) from pg_locks l
                    where l.database = (select d.oid from pg_database d where d.datname = @clone)
                      and l.locktype = 'relation' and l.relation = @id and l.granted
                    """,
                    admin);
                held.Parameters.AddWithValue("clone", b);
                held.Parameters.Add(new NpgsqlParameter("id", NpgsqlTypes.NpgsqlDbType.Oid) { Value = tableId });
                ((long) (await held.ExecuteScalarAsync(Token))!).Should().BePositive("the clone's session holds a lock on that very id");
            }

            // The read, in database A, for A's table id: nobody holds it here.
            await using var reader = new NpgsqlConnection(InDatabase(a).ConnectionString);
            await reader.OpenAsync(Token);
            await using NpgsqlTransaction reading = await reader.BeginTransactionAsync(Token);

            (await MigrationImpactQueries.ReadSessionsAsync(reader, reading, [tableId], 30, Token))
                .Should().BeEmpty("the lock is held in the other database");

            // And the same read does see a holder in this database, so an empty answer above is not an answer to nothing.
            await using var holder = new NpgsqlConnection(InDatabase(a).ConnectionString);
            await holder.OpenAsync(Token);
            await using NpgsqlTransaction holding = await holder.BeginTransactionAsync(Token);
            await ExecuteAsync(holder, "lock table tpl.t in access share mode");

            (await MigrationImpactQueries.ReadSessionsAsync(reader, reading, [tableId], 30, Token))
                .Should().ContainSingle().Which.Sessions.Should().Be(1);

            await holding.RollbackAsync(Token);
            await cloneHolding.RollbackAsync(Token);
            await reading.RollbackAsync(Token);
        }
        finally
        {
            await using NpgsqlConnection admin = await postgres.OpenAsync(Token);
            await ExecuteAsync(admin, $"drop database if exists {b} with (force)");
            await ExecuteAsync(admin, $"drop database if exists {a} with (force)");
        }
    }

    /// <summary>
    /// Under a narrowed <c>SqlConsoleRole</c> the read cannot see another role's session state, and says so: the count is still right
    /// and the state is "state not visible".
    /// </summary>
    [PostgresFact]
    public async Task A_narrowed_console_role_counts_the_session_and_says_its_state_is_not_visible()
    {
        await using SchemaFixture schema = await StartPopulatedAsync("apply_impact_role");
        string role = "ms_apply_role_" + Guid.NewGuid().ToString("N")[..8];

        await using (NpgsqlConnection admin = await postgres.OpenAsync(Token))
        {
            await ExecuteAsync(admin, $"create role {role} nologin");
        }

        try
        {
            SchemaFixture.StudioHost drifted = schema.Drifted(options =>
            {
                options.Capabilities.ViewDatabaseActivity = true;
                options.SqlConsoleRole = role;
            });

            await using NpgsqlConnection holder = await postgres.OpenAsync(Token);
            await using NpgsqlTransaction holding = await holder.BeginTransactionAsync(Token);
            await ExecuteAsync(holder, $"lock table {schema.Schema}.{Customers} in access share mode");

            MigrationPreview preview = await drifted.SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));

            preview.Impact.SessionsNotice.Should().BeNull();
            TableSessions sessions = preview.Impact.Tables.Should().ContainSingle().Subject.Sessions!;
            sessions.Holding.Should().Be(1, "pg_locks is readable by every role");
            sessions.Summary.Should().Be("1 session holds a lock (1 state not visible)");

            await holding.RollbackAsync(Token);
        }
        finally
        {
            await using NpgsqlConnection admin = await postgres.OpenAsync(Token);
            await ExecuteAsync(admin, $"drop role if exists {role}");
        }
    }

    /// <summary>
    /// D27, measured: the two reads open no table. They finish while another session holds the table in <c>ACCESS EXCLUSIVE</c> mode,
    /// with a short <c>lock_timeout</c>, and this backend holds no lock on any relation of the schema afterwards. The held lock is also
    /// what the sessions read finds.
    /// </summary>
    [PostgresFact]
    public async Task The_reads_finish_while_the_table_is_held_access_exclusive_and_hold_no_lock_afterwards()
    {
        await using SchemaFixture schema = await StartPopulatedAsync("apply_impact_locks");

        await using NpgsqlConnection reader = await postgres.OpenAsync(Token);
        await using NpgsqlConnection migration = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await ExecuteAsync(migration, $"lock table {schema.Schema}.{Customers} in access exclusive mode");

        await using NpgsqlTransaction reading = await reader.BeginTransactionAsync(Token);
        await ExecuteAsync(reader, "set local lock_timeout = '2s'");

        IReadOnlyList<RelationFootprintRow> footprints = await MigrationImpactQueries.ReadFootprintsAsync(
            reader, reading, [(schema.Schema, Customers)], 30, Token);
        IReadOnlyList<TableSessionRow> sessions = await MigrationImpactQueries.ReadSessionsAsync(
            reader, reading, [.. footprints.Select(static x => x.TableOid)], 30, Token);

        footprints.Should().ContainSingle().Which.EstimatedRows.Should().Be(Rows);
        sessions.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new TableSessionRow(footprints[0].TableOid, "idle in transaction", Granted: true, 1));

        (await RelationLocksAsync(reader, schema.Schema)).Should().BeEmpty("the footprint and sessions reads open no table");

        await reading.RollbackAsync(Token);
        await holding.RollbackAsync(Token);
    }

    /// <summary>
    /// The lock test's anti-vacuity: the measurement above can see what it is looking for. A size function over the same held table
    /// really does wait for the lock and gives up at the <c>lock_timeout</c>, which is why the footprint read uses none.
    /// </summary>
    [PostgresFact]
    public async Task The_lock_measurement_sees_a_size_function_wait_for_the_table()
    {
        await using SchemaFixture schema = await StartPopulatedAsync("apply_impact_proof");

        await using NpgsqlConnection reader = await postgres.OpenAsync(Token);
        await using NpgsqlConnection migration = await postgres.OpenAsync(Token);
        await using NpgsqlTransaction holding = await migration.BeginTransactionAsync(Token);
        await ExecuteAsync(migration, $"lock table {schema.Schema}.{Customers} in access exclusive mode");

        await using NpgsqlTransaction reading = await reader.BeginTransactionAsync(Token);
        await ExecuteAsync(reader, "set local lock_timeout = '1s'");

        Func<Task> sizing = () => ExecuteAsync(reader, $"select pg_total_relation_size('{schema.Schema}.{Customers}'::regclass)");

        (await sizing.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("55P03");

        await holding.RollbackAsync(Token);
    }

    /// <summary>
    /// The footprint of a partitioned table is its partitions' added up, and an index resolves to the table it is on.
    /// </summary>
    [PostgresFact]
    public async Task A_partitioned_table_adds_its_partitions_up_and_an_index_name_resolves_to_its_table()
    {
        string schemaName = "apply_impact_part";
        await postgres.CreateSchemaAsync(schemaName, Token);

        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await ExecuteAsync(connection, $"create table {schemaName}.pt (id int not null, tenant text not null) partition by list (tenant)");
        await ExecuteAsync(connection, $"create table {schemaName}.pt_a partition of {schemaName}.pt for values in ('a')");
        await ExecuteAsync(connection, $"create table {schemaName}.pt_b partition of {schemaName}.pt for values in ('b')");
        await ExecuteAsync(connection, $"insert into {schemaName}.pt select i, case when i % 2 = 0 then 'a' else 'b' end from generate_series(1, 200) i");
        await ExecuteAsync(connection, $"create index pt_a_id_idx on {schemaName}.pt_a (id)");
        await ExecuteAsync(connection, $"analyze {schemaName}.pt_a");
        await ExecuteAsync(connection, $"analyze {schemaName}.pt_b");

        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Token);

        IReadOnlyList<RelationFootprintRow> rows = await MigrationImpactQueries.ReadFootprintsAsync(
            connection,
            transaction,
            [(schemaName, "pt"), (schemaName, "pt_a_id_idx"), (schemaName, "nope"), ("nowhere", "pt")],
            30,
            Token);

        rows.Select(static x => x.Ordinal).Should().Equal(0, 1);

        rows[0].Table.Should().Be("pt");
        rows[0].IsPartitioned.Should().BeTrue();
        rows[0].EstimatedRows.Should().Be(200, "the partitions' estimates are added up");
        rows[0].HeapBytes.Should().BeGreaterThan(0);
        rows[0].TotalBytes.Should().BeGreaterThan(rows[0].HeapBytes, "the partition's index is in the total");

        rows[1].Table.Should().Be("pt_a", "an index is asked about the table it is on");
        rows[1].IsPartitioned.Should().BeFalse();
        rows[1].EstimatedRows.Should().Be(100);

        await transaction.RollbackAsync(Token);
    }

    private async Task<SchemaFixture> StartPopulatedAsync(string schemaName)
    {
        SchemaFixture schema = await SchemaFixture.StartAsync(postgres, schemaName, cancellationToken: Token);

        IDocumentStore store = schema.Studio.Services.GetRequiredService<IDocumentStore>();
        await using (IDocumentSession session = store.LightweightSession())
        {
            for (int i = 0; i < Rows; i++)
            {
                session.Store(new SchemaCustomer { Id = Guid.NewGuid(), Email = $"customer{i}@example.test", Name = "Customer " + i });
            }

            await session.SaveChangesAsync(Token);
        }

        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await ExecuteAsync(connection, $"analyze {schema.Schema}.{Customers}");

        return schema;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        (await reader.ReadAsync(Token)).Should().BeTrue();

        return reader.GetFieldValue<T>(0);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Every relation lock this connection's backend holds on a relation of <paramref name="schema" />.</summary>
    private static async Task<IReadOnlyList<string>> RelationLocksAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand(
            """
            select c.relname || ' ' || l.mode
            from pg_locks l
            join pg_class c on c.oid = l.relation
            join pg_namespace n on n.oid = c.relnamespace
            where l.pid = pg_backend_pid()
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
}
