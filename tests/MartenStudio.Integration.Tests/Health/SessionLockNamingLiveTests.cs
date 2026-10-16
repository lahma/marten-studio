using JasperFx;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Database;
using MartenStudio.Services.Health.Checks;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>A second registered store in the test's database, for a lock on another store's table.</summary>
public interface IH3OtherStore : IDocumentStore;

/// <summary>The other store's document type.</summary>
public sealed class H3OtherThing
{
    /// <summary>Its id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A conjoined document type whose table the test makes list-partitioned by tenant.</summary>
public sealed class H3TenantNote
{
    /// <summary>Its id.</summary>
    public Guid Id { get; set; }
}

/// <summary>
/// B1 and B2 of the H3 security review, live: what a lock and a statement say about relations, against real
/// sessions holding real locks. A hidden document type's indexes, another store's table and a tenant partition are
/// locked by sessions that stay in their transactions; the labels everybody the store policy passes reads, the
/// details' relation column, C06's own read and the statements are then checked for every name they must not carry.
/// </summary>
public class SessionLockNamingLiveTests(PostgresFixture postgres)
{
    private const string OtherSchema = "h3_other_store";

    private const string PartitionedSchema = "h3_part";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_hidden_types_indexes_and_another_stores_table_are_named_to_nobody_and_a_statement_about_the_hidden_type_is_withheld()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_sessions_naming");

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database,
            studio: static options =>
            {
                options.Capabilities.ViewDatabaseActivity = true;
                options.IsDocumentTypeVisible = static type => type != typeof(Customer);
            },
            services: services => services.AddMartenStore<IH3OtherStore>(options =>
            {
                options.Connection(database);
                options.DatabaseSchemaName = OtherSchema;
                options.AutoCreateSchemaObjects = AutoCreate.All;
                options.Schema.For<H3OtherThing>();
            }));

        await host.Provider.GetRequiredService<IH3OtherStore>().Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        string customers = SqlIdentifier.Qualify(SampleStore.DocumentSchema, "mt_doc_customer");
        string others = SqlIdentifier.Qualify(OtherSchema, "mt_doc_h3otherthing");

        // Planning a query locks the table and every index on it, until the transaction ends: the hidden type's
        // unique index on its duplicated Email (mt_doc_customer_idx_email) and its primary key among them.
        await using NpgsqlConnection hidden = await host.OpenAsync();
        await using NpgsqlTransaction hiddenTransaction = await hidden.BeginTransactionAsync(Token);
        await ExecuteAsync(hidden, hiddenTransaction, $"select count(*) from {customers} where email = 'someone@example.com'");

        await using NpgsqlConnection other = await host.OpenAsync();
        await using NpgsqlTransaction otherTransaction = await other.BeginTransactionAsync(Token);
        await ExecuteAsync(other, otherTransaction, $"select count(*) from {others}");

        // And one session waiting behind the hidden holder, for C06's own read and labels.
        await using NpgsqlConnection waiter = await host.OpenAsync();
        await using NpgsqlTransaction waiterTransaction = await waiter.BeginTransactionAsync(Token);
        await using NpgsqlCommand waiting = new($"lock table {customers} in access exclusive mode", waiter, waiterTransaction) { CommandTimeout = 0 };
        Task blocked = waiting.ExecuteNonQueryAsync(Token);

        int hiddenPid = hidden.ProcessID;
        int otherPid = other.ProcessID;
        int waiterPid = waiter.ProcessID;

        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select exists (select 1 from pg_stat_activity where pid = @pid and wait_event_type = 'Lock' and now() - state_change > interval '2 seconds')",
                    ("pid", waiterPid)),
                TimeSpan.FromSeconds(20),
                "the waiter to queue behind the hidden type's holder");

            // Not vacuous: the holders really hold locks on indexes, whose names are what must not appear.
            (await LiveWait.ScalarAsync<long>(
                    database,
                    "select count(*) from pg_locks l join pg_class c on c.oid = l.relation where l.pid = @pid and c.relkind = 'i' and c.relname like '%idx_email%'",
                    ("pid", hiddenPid)))
                .Should().BePositive("the hidden type's duplicated-field index is locked by its holder");

            await using AsyncServiceScope circuit = host.Circuit();
            ISessionActivityService service = circuit.ServiceProvider.GetRequiredService<ISessionActivityService>();

            // What anybody the store policy passes reads.
            SessionActivitySummary summary = await service.GetSummaryAsync(host.Scope, Token);
            summary.Refusal.Should().Be(DatabaseRefusal.None, summary.Reason);

            string labels = string.Join("\n", summary.Locks.Select(static x => x.Target));
            labels.Should().NotContainEquivalentOf("customer", "a hidden type is named to nobody")
                .And.NotContainEquivalentOf("idx_email", "nor its index, which names an indexed member")
                .And.NotContainEquivalentOf("pkey")
                .And.NotContainEquivalentOf("h3otherthing", "another store's alias is not this visitor's to learn")
                .And.NotContain(OtherSchema);
            labels.Should().Contain("a Marten relation this store does not declare");
            summary.Locks.Should().Contain(static x => x.Target == "a relation", "the hidden type's table and indexes, by kind alone");

            // What the details add - and still not those names.
            SessionActivityDetails details = await service.GetDetailsAsync(host.Scope, Token);
            details.Refusal.Should().Be(DatabaseRefusal.None, details.Reason);

            details.Locks.Where(x => x.ProcessId == hiddenPid).Should().NotBeEmpty()
                .And.OnlyContain(static x => x.Relation == null && x.Target == "a relation", "not even that it is an index of something");

            details.Locks.Where(x => x.ProcessId == otherPid).Should().NotBeEmpty()
                .And.OnlyContain(static x => x.Relation == null && x.Target.EndsWith("a Marten relation this store does not declare", StringComparison.Ordinal));

            details.Locks.Should().NotContain(static x => x.Relation != null
                && (x.Relation.Contains("customer", StringComparison.OrdinalIgnoreCase) || x.Relation.Contains("h3otherthing", StringComparison.OrdinalIgnoreCase)));

            // B2: the hidden holder's and the waiter's statements name the hidden type's table: withheld, with a note.
            foreach (int pid in new[] { hiddenPid, waiterPid })
            {
                SessionStatementText statement = details.Sessions.Single(x => x.ProcessId == pid).Statement;
                statement.Text.Should().BeNull("a statement about a hidden type spells out its table and its members");
                statement.Note.Should().Be(SessionQueryText.HiddenTypeNote);
            }

            // C06's own read: the sweep has no circuit, and labels with every store's declarations all the same.
            LockWaitFacts facts = await host.GatherAsync(new LockWaitCheck());
            LockWaiter waits = facts.Waiters.Should().ContainSingle(x => x.ProcessId == waiterPid).Subject;
            waits.Target.Label.Should().Be("a relation");
            waits.Target.Schema.Should().BeNull();
        }
        finally
        {
            await hiddenTransaction.RollbackAsync(CancellationToken.None);
            await blocked;
            await waiterTransaction.RollbackAsync(CancellationToken.None);
            await otherTransaction.RollbackAsync(CancellationToken.None);
        }
    }

    [PostgresFact]
    public async Task A_lock_on_a_tenant_partition_is_labelled_by_its_table_and_never_says_the_tenant()
    {
        string database = await ConnectionsLiveHost.CreateDatabaseAsync(postgres.ConnectionString, "health_sessions_partition");

        static void Store(StoreOptions options)
        {
            options.DatabaseSchemaName = PartitionedSchema;
            options.AutoCreateSchemaObjects = AutoCreate.None;
            options.Schema.For<H3TenantNote>().MultiTenanted();
        }

        await using ConnectionsLiveHost host = await ConnectionsLiveHost.CreateAsync(
            database, sample: false, store: Store, studio: static options => options.Capabilities.ViewDatabaseActivity = true);

        // The same store, with hiding configured - which hides nothing here, and makes a name nobody declares suspect.
        await using ConnectionsLiveHost hiding = await ConnectionsLiveHost.CreateAsync(
            database,
            sample: false,
            store: Store,
            studio: static options =>
            {
                options.Capabilities.ViewDatabaseActivity = true;
                options.IsDocumentTypeVisible = static _ => true;
            });

        // The declared table, made list-partitioned by tenant the way Marten's partitioned tenancy makes it.
        await using (NpgsqlConnection setup = await host.OpenAsync())
        {
            await ExecuteAsync(setup, null,
                "create schema h3_part;"
                + " create table h3_part.mt_doc_h3tenantnote (tenant_id varchar not null, id uuid not null, data jsonb not null,"
                + " primary key (tenant_id, id)) partition by list (tenant_id);"
                + " create table h3_part.mt_doc_h3tenantnote_acme partition of h3_part.mt_doc_h3tenantnote for values in ('acme');");
        }

        await using NpgsqlConnection holder = await host.OpenAsync();
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);
        await ExecuteAsync(holder, held, "insert into h3_part.mt_doc_h3tenantnote (tenant_id, id, data) values ('acme', gen_random_uuid(), '{}')");

        // Planning a read through the parent locks the partition's index too, until the transaction ends.
        await ExecuteAsync(holder, held, "select count(*) from h3_part.mt_doc_h3tenantnote where tenant_id = 'acme' and id = '00000000-0000-0000-0000-000000000001'");

        await using NpgsqlConnection waiter = await host.OpenAsync();
        await using NpgsqlTransaction waiterTransaction = await waiter.BeginTransactionAsync(Token);
        await using NpgsqlCommand waiting = new("lock table h3_part.mt_doc_h3tenantnote_acme in access exclusive mode", waiter, waiterTransaction) { CommandTimeout = 0 };
        Task blocked = waiting.ExecuteNonQueryAsync(Token);

        int holderPid = holder.ProcessID;
        int waiterPid = waiter.ProcessID;
        const string Table = "the h3tenantnote document table (" + PartitionedSchema + ".mt_doc_h3tenantnote)";

        try
        {
            await LiveWait.UntilAsync(
                async () => await LiveWait.ScalarAsync<bool>(
                    database,
                    "select exists (select 1 from pg_stat_activity where pid = @pid and wait_event_type = 'Lock' and now() - state_change > interval '2 seconds')",
                    ("pid", waiterPid)),
                TimeSpan.FromSeconds(20),
                "the waiter to queue behind the partition's holder");

            (await LiveWait.ScalarAsync<long>(
                    database,
                    "select count(*) from pg_locks l join pg_class c on c.oid = l.relation where l.pid = @pid and c.relname like '%acme%'",
                    ("pid", holderPid)))
                .Should().BePositive("the holder really locks the partition and its index, whose names carry the tenant");

            await using AsyncServiceScope circuit = host.Circuit();
            ISessionActivityService service = circuit.ServiceProvider.GetRequiredService<ISessionActivityService>();

            SessionActivitySummary summary = await service.GetSummaryAsync(host.Scope, Token);
            string labels = string.Join("\n", summary.Locks.Select(static x => x.Target));

            string holderLocks = await LiveWait.ScalarAsync<string>(
                database,
                "select string_agg(c.relname || ' ' || c.relkind::text || ' ' || l.mode, ', ') from pg_locks l join pg_class c on c.oid = l.relation where l.pid = @pid",
                ("pid", holderPid)) ?? string.Empty;

            labels.Should().NotContain("acme", "a tenant partition's name is a tenant id").And.NotContain("pkey");
            labels.Should().Contain("a partition of " + Table, "the holder holds {0}", holderLocks);
            labels.Should().Contain("an index of " + Table, "the holder holds {0}", holderLocks);

            SessionActivityDetails details = await service.GetDetailsAsync(host.Scope, Token);
            details.Locks.Where(x => x.ProcessId == holderPid || x.ProcessId == waiterPid)
                .Select(static x => x.Relation).Where(static x => x is not null).Distinct()
                .Should().ContainSingle("the details name the table, never the partition or an index")
                .Which.Should().Be(PartitionedSchema + ".mt_doc_h3tenantnote");

            LockWaitFacts facts = await host.GatherAsync(new LockWaitCheck());
            LockWaiter waits = facts.Waiters.Should().ContainSingle(x => x.ProcessId == waiterPid).Subject;
            waits.Target.Label.Should().Be("a partition of " + Table);
            waits.Target.Name.Should().Be("mt_doc_h3tenantnote");

            // B2: with hiding configured, the waiter's statement names a document table nobody declares - the partition.
            await using AsyncServiceScope hidingCircuit = hiding.Circuit();
            SessionActivityDetails hidden = await hidingCircuit.ServiceProvider.GetRequiredService<ISessionActivityService>()
                .GetDetailsAsync(hiding.Scope, Token);

            SessionStatementText statement = hidden.Sessions.Single(x => x.ProcessId == waiterPid).Statement;
            statement.Text.Should().BeNull();
            statement.Note.Should().Be(SessionQueryText.UndeclaredTableNote);
            hidden.Sessions.Single(x => x.ProcessId == holderPid).Statement.Text.Should().Contain("mt_doc_h3tenantnote",
                "a statement naming only the declared table is shown");
        }
        finally
        {
            await held.RollbackAsync(CancellationToken.None);
            await blocked;
            await waiterTransaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(Token);
    }
}
