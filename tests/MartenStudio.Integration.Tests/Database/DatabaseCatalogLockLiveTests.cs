using System.Globalization;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// B1, F5 and F6 against a real Postgres 17: a list locks nothing it lists, is not held up by somebody
/// else's lock on a partition or a sequence, a filtered read is never cached, the overview's counts are
/// exact past any list cap, and a sequence's value is read on demand.
/// </summary>
/// <remarks>
/// The fixture adds a partitioned table with <see cref="Partitions" /> partitions and
/// <see cref="Sequences" /> sequences to the legacy schema - the shape Marten's per-tenant partitioning
/// gives a real store, where a list that locked per object took one lock per tenant.
/// </remarks>
public class DatabaseCatalogLockLiveTests(DatabaseCatalogLockLiveTests.Fixture fixture) : IClassFixture<DatabaseCatalogLockLiveTests.Fixture>
{
    private const int Partitions = 60;
    private const int Sequences = 60;

    private const int Timeout = 30;

    /// <summary>This class's schemas, with the partitions and sequences on top.</summary>
    public sealed class Fixture(PostgresFixture postgres) : DatabaseBrowserFixture(postgres)
    {
        /// <inheritdoc />
        protected override string ExtraSql =>
            "create table {l}.lock_parent (id bigint not null, k int not null) partition by list (k);\n"
            + string.Concat(Enumerable.Range(1, Partitions).Select(static i => string.Create(
                CultureInfo.InvariantCulture, $"create table {{l}}.lock_p{i} partition of {{l}}.lock_parent for values in ({i});\n")))
            + string.Concat(Enumerable.Range(1, Sequences).Select(static i => string.Create(
                CultureInfo.InvariantCulture, $"create sequence {{l}}.lock_seq_{i};\n")))
            + "select pg_catalog.nextval('{l}.lock_seq_1'::regclass);\n"
            + "select pg_catalog.nextval('{l}.lock_seq_1'::regclass);\n"
            + "create sequence {e}.mt_events_sequence_acme;\n";
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string[] Schemas => [fixture.DocumentSchema, fixture.EventSchema, fixture.QuartzSchema, fixture.LegacySchema];

    /// <summary>
    /// B1, measured: every list reader and the count query, in one transaction as a snapshot runs them,
    /// leave no lock on any relation in any user schema - and, in the same transaction, the two functions
    /// they no longer call take one per partition and one per sequence, which is what proves the probe
    /// can see a lock at all.
    /// </summary>
    [PostgresFact]
    public async Task Every_list_read_in_one_transaction_leaves_no_lock_on_anything_it_lists()
    {
        await using NpgsqlConnection connection = await fixture.Postgres.OpenAsync();

        var session = new ReadOnlySqlSession(new ReadOnlySqlOptions { StatementTimeout = TimeSpan.FromSeconds(Timeout) });
        string[] schemas = Schemas;
        string legacy = SqlIdentifier.Quote(fixture.LegacySchema);

        (IReadOnlyList<string> afterLists, IReadOnlyList<string> afterDetail, IReadOnlyList<string> afterOldCalls) =
            await session.InTransactionAsync(
                connection,
                async (transaction, token) =>
                {
                    await DatabaseCatalogQueries.PinSearchPathAsync(connection, transaction, Timeout, token);

                    CatalogList<CatalogRelation> relations = await DatabaseCatalogQueries.ReadRelationsAsync(
                        connection, transaction, schemas, null, null, DatabaseCatalog.ListCap, Timeout, token);
                    CatalogList<CatalogSequence> sequences = await DatabaseCatalogQueries.ReadSequencesAsync(
                        connection, transaction, schemas, null, null, DatabaseCatalog.ListCap, Timeout, token);

                    await DatabaseCatalogQueries.ReadRoutinesAsync(connection, transaction, schemas, null, null, DatabaseCatalog.ListCap, Timeout, token);
                    await DatabaseCatalogQueries.ReadTriggersAsync(connection, transaction, schemas, null, null, null, DatabaseCatalog.ListCap, Timeout, token);
                    await DatabaseCatalogQueries.ReadTypesAsync(connection, transaction, schemas, null, null, DatabaseCatalog.ListCap, Timeout, token);
                    await DatabaseCatalogQueries.ReadForeignKeysAsync(connection, transaction, schemas, null, null, DatabaseCatalog.ListCap, Timeout, token);
                    await DatabaseCatalogQueries.ReadViewDependenciesAsync(connection, transaction, schemas, null, DatabaseCatalog.ListCap, Timeout, token);
                    await DatabaseCatalogQueries.ReadViewReferencesAsync(connection, transaction, schemas, null, DatabaseCatalog.ListCap, Timeout, token);
                    IReadOnlyList<CatalogObjectCount> counts = await DatabaseCatalogQueries.ReadObjectCountsAsync(
                        connection, transaction, schemas, [], Timeout, token);

                    // The reads really did cover the objects in question, so "no locks" is not "read nothing".
                    relations.Items.Single(static x => x.Name == "lock_parent").PartitionCount.Should().Be(Partitions);
                    sequences.Items.Count(static x => x.Name.StartsWith("lock_seq_", StringComparison.Ordinal)).Should().Be(Sequences);
                    sequences.Items.Should().OnlyContain(static x => x.LastValue == null, "a list never reads a value");
                    counts.Single(x => x.Kind == "sequences" && x.Schema == fixture.LegacySchema).Count.Should().BeGreaterThanOrEqualTo(Sequences);

                    IReadOnlyList<string> lists = await HeldUserLocksAsync(connection, transaction, token);

                    // One table's detail: it may lock that table briefly, never its partitions.
                    CatalogRelationDetail? parent = await DatabaseCatalogQueries.ReadRelationDetailAsync(
                        connection, transaction, fixture.LegacySchema, "lock_parent", Timeout, token);
                    parent!.Relation.PartitionCount.Should().Be(Partitions);

                    IReadOnlyList<string> detail = await HeldUserLocksAsync(connection, transaction, token);

                    // The anti-vacuity half: the two calls the lists no longer make, in the same transaction.
                    await ExecuteAsync(connection, transaction, $"select pg_catalog.count(*) from pg_catalog.pg_partition_tree('{legacy}.lock_parent'::pg_catalog.regclass)", token);
                    await ExecuteAsync(
                        connection,
                        transaction,
                        $"select pg_catalog.count(pg_catalog.pg_sequence_last_value(c.oid::pg_catalog.regclass)) from pg_catalog.pg_class c where c.relnamespace = '{legacy}'::pg_catalog.regnamespace and c.relkind = 'S' and c.relname like 'lock_seq_%'",
                        token);

                    IReadOnlyList<string> old = await HeldUserLocksAsync(connection, transaction, token);

                    return (lists, detail, old);
                },
                Token);

        afterLists.Should().BeEmpty("a list reads the catalog and takes no lock on anything it describes");
        afterDetail.Should().NotContain(static x => x.Contains(".lock_p", StringComparison.Ordinal),
            "a partitioned table's detail walks pg_inherits, and locks no partition");

        afterOldCalls.Should().HaveCountGreaterThanOrEqualTo(Partitions + Sequences,
            "pg_partition_tree locks every partition and pg_sequence_last_value every sequence - which is why the lists stopped calling them");
    }

    /// <summary>
    /// B1, the symptom: somebody else holding a lock on one partition (a partition DDL) and on one sequence
    /// (an ALTER SEQUENCE) used to queue every list behind it until the three-second lock_timeout failed it
    /// with 55P03. The lists and the overview answer now, because they ask for no lock at all.
    /// </summary>
    [PostgresFact]
    public async Task A_list_is_not_held_up_by_somebody_elses_lock_on_a_partition_or_a_sequence()
    {
        await using NpgsqlConnection holder = await fixture.Postgres.OpenAsync();
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);

        string legacy = SqlIdentifier.Quote(fixture.LegacySchema);

        await ExecuteAsync(holder, held, $"lock table {legacy}.lock_p7 in access exclusive mode", Token);
        await ExecuteAsync(holder, held, $"alter sequence {legacy}.lock_seq_7 increment by 1", Token);

        await using BrowserHost host = fixture.Host();

        DatabaseBrowserOverview overview = await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope, Token));
        overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);

        foreach (DatabaseObjectCategory category in Enum.GetValues<DatabaseObjectCategory>())
        {
            DatabaseObjectList list = await host.ObjectsAsync(x => x.ListAsync(
                BrowserHost.Scope, new DatabaseObjectQuery(category, fixture.LegacySchema), Token));

            list.Refusal.Should().Be(DatabaseRefusal.None, category + ": " + list.Reason);
        }

        await held.RollbackAsync(Token);
    }

    /// <summary>B1: the value is asked for, one sequence at a time, and gated like a definition.</summary>
    [PostgresFact]
    public async Task A_sequences_value_is_read_on_demand_for_one_sequence()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseObjectList sequences = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Sequences, fixture.LegacySchema, NameFilter: "lock_seq_1")));

        DatabaseSequenceSummary listed = sequences.Items.Cast<DatabaseSequenceSummary>().Single(static x => x.Name == "lock_seq_1");
        listed.CanReadValue.Should().BeTrue();
        listed.LastValue.Should().BeNull("a list never reads a value - it would lock every sequence it lists");

        DatabaseSequenceValue called = await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.LegacySchema, "lock_seq_1", Token));
        called.Found.Should().BeTrue(called.Reason);
        called.LastValue.Should().Be(2, "the fixture called nextval twice");

        DatabaseSequenceValue never = await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.LegacySchema, "lock_seq_2", Token));
        never.Found.Should().BeTrue(never.Reason);
        never.LastValue.Should().BeNull("nothing has called it");

        DatabaseSequenceValue perTenant = await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.EventSchema, "mt_events_sequence_acme", Token));
        perTenant.Refusal.Should().Be(DatabaseRefusal.NotFound, "a per-tenant event sequence's name is a tenant id, and it is not there");

        DatabaseSequenceValue missing = await host.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.LegacySchema, "no_such_sequence", Token));
        missing.Refusal.Should().Be(DatabaseRefusal.NotFound);

        await using BrowserHost closed = fixture.Host(static options => options.Capabilities.BrowseDatabase = false);

        DatabaseSequenceValue refused = await closed.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.LegacySchema, "lock_seq_1", Token));
        refused.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        closed.Ring.GetLatest().Should().Contain(static x => x.Action == DatabaseObjectService.SequenceValueAction && !x.Succeeded);

        DatabaseSequenceValue martens = await closed.ObjectsAsync(x => x.GetSequenceValueAsync(BrowserHost.Scope, fixture.EventSchema, "mt_events_sequence", Token));
        martens.Found.Should().BeTrue("Marten's own sequence in the store's own schema is the Schema screen's: " + martens.Reason);
    }

    /// <summary>
    /// F5: a name-filtered list goes to the database every time and is never remembered - forty distinct
    /// filters leave the cache exactly as they found it - while the unfiltered read is cached as before.
    /// </summary>
    [PostgresFact]
    public async Task A_name_filtered_list_is_never_cached()
    {
        await using BrowserHost host = fixture.Host();

        DatabaseCatalog catalog = host.Services.GetRequiredService<DatabaseCatalog>();
        int before = catalog.CachedListEntries;

        for (int i = 0; i < 40; i++)
        {
            string filter = "lock_p" + i.ToString(CultureInfo.InvariantCulture);

            DatabaseObjectList filtered = await host.ObjectsAsync(x => x.ListAsync(
                BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.LegacySchema, NameFilter: filter)));

            filtered.Refusal.Should().Be(DatabaseRefusal.None, filtered.Reason);
        }

        catalog.CachedListEntries.Should().Be(before, "a filter is typed a keystroke at a time; none of it is kept");

        await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, fixture.LegacySchema)));

        catalog.CachedListEntries.Should().Be(before + 1, "the unfiltered read is still cached");
    }

    /// <summary>
    /// F6: the overview counts every kind in every schema exactly - with a list cap of three injected, they
    /// still equal what the full lists hold, and the capped list says it was cut short.
    /// </summary>
    [PostgresFact]
    public async Task The_overview_counts_are_exact_past_a_small_list_cap()
    {
        await using BrowserHost host = fixture.Host();

        Dictionary<(DatabaseObjectCategory, string), int> listed = [];

        foreach (DatabaseObjectCategory category in Enum.GetValues<DatabaseObjectCategory>())
        {
            DatabaseObjectList all = await host.ObjectsAsync(x => x.ListAsync(
                BrowserHost.Scope, new DatabaseObjectQuery(category, Limit: DatabaseCatalog.ListCap)));

            all.Refusal.Should().Be(DatabaseRefusal.None, all.Reason);
            all.Truncated.Should().BeFalse("the reference numbers must be complete");

            foreach (IGrouping<string, DatabaseObjectSummary> schema in all.Items.GroupBy(static x => x.Schema))
            {
                listed[(category, schema.Key)] = schema.Count();
            }
        }

        DatabaseCatalog catalog = host.Services.GetRequiredService<DatabaseCatalog>();
        catalog.ReadCap = 3;

        DatabaseBrowserOverview overview = await host.ObjectsAsync(x => x.GetOverviewAsync(BrowserHost.Scope));

        overview.Refusal.Should().Be(DatabaseRefusal.None, overview.Reason);
        overview.Truncated.Should().BeFalse("a count query is never capped");

        foreach (DatabaseSchemaSummary schema in overview.Schemas)
        {
            foreach (DatabaseObjectCategory category in Enum.GetValues<DatabaseObjectCategory>())
            {
                schema.CountOf(category).Should().Be(
                    listed.GetValueOrDefault((category, schema.Name)),
                    schema.Name + " " + category + " counts what its list lists");
            }
        }

        overview.Schemas.Single(x => x.Name == fixture.LegacySchema).Sequences.Should().BeGreaterThanOrEqualTo(Sequences,
            "the count is past the injected cap of three");

        DatabaseObjectList capped = await host.ObjectsAsync(x => x.ListAsync(
            BrowserHost.Scope, new DatabaseObjectQuery(DatabaseObjectCategory.Sequences, fixture.LegacySchema)));

        capped.Items.Should().HaveCount(3);
        capped.Truncated.Should().BeTrue("the list, and only the list, is bounded by the cap");
    }

    private static async Task<IReadOnlyList<string>> HeldUserLocksAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        // Test code, where raw SQL belongs: this backend's relation locks outside Postgres' own schemas.
        await using var command = new NpgsqlCommand(
            """
            select n.nspname || '.' || c.relname || ':' || l.mode
            from pg_catalog.pg_locks l
            join pg_catalog.pg_class c on c.oid = l.relation
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where l.pid = pg_catalog.pg_backend_pid()
              and l.locktype = 'relation'
              and n.nspname <> 'pg_catalog'
              and n.nspname <> 'information_schema'
              and n.nspname !~ '^pg_toast'
            order by 1
            """,
            connection,
            transaction);

        List<string> held = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            held.Add(reader.GetString(0));
        }

        return held;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
