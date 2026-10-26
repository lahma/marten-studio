using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Daemon;

using Marten;
using Marten.Schema;

using MartenStudio.Integration.Tests.Tenancy;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// DL: the three read paths that used to reach Marten's <c>EnsureStorageExists</c> - the dead-letter list, the aggregate
/// replay and the document page's stream link - against a store that has never touched the types they read.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape that bit.</b> The host's deployment applied the schema - here, a second <c>DocumentStore</c> over the same
/// database, standing in for the process that ran the migration and the daemon - and the studio runs in a process whose
/// store has never read an event or a dead letter. That store also has an <c>int</c>-id document type, so Marten's HiLo
/// feature is active and <c>mt_hilo</c> does not exist yet: the first <c>EnsureStorageExistsAsync</c> in the process runs
/// <c>AllObjects()</c> for post-processing, which creates it (hard rule 14). A table that exists is no protection; the
/// catalog probe the dead-letter list used to make answered exactly that question and no other.
/// </para>
/// <para>
/// <b>The measurement contains none of the studio's code.</b> The class owns a database (<see cref="Database" />), each
/// test starts by dropping the store's two schemas from it, and <see cref="ServerSnapshot" /> reads every object in it from
/// the catalog - oid, columns, indexes, constraints, routines by digest - before and after; <c>to_regclass</c> answers for
/// the two tables that matter by name. Tests in a class run one after another, so the snapshot is the running test's
/// alone. One database rather than one per test, because dropping a database forces an immediate checkpoint (Postgres
/// 15 and later), which is load every other class on the shared container pays for.
/// <see cref="Martens_own_read_on_the_same_database_really_does_write_DDL" /> is the anti-vacuity theory: the very Marten
/// call each read replaced, on the same setup, creates <c>mt_hilo</c> below Marten 9.50.3 - and from 9.50.3, where that
/// setup's hazard is gone (#5665), creates its storage on the empty database.
/// </para>
/// </remarks>
public class ReadPathNoDdlLiveTests(PostgresFixture fixture)
{
    /// <summary>The prefix of the database this class creates, so the snapshot's database list is this class's own.</summary>
    private const string Prefix = "dl_nddl_";

    /// <summary>The class's one database.</summary>
    private const string Database = Prefix + "reads";

    private const string DocumentSchema = "dl_docs";

    private const string EventSchema = "dl_events";

    private const string DeadLetterTable = "mt_doc_deadletterevent";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static readonly DateTimeOffset Then = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Hard rule 14's oldest trap, for all three reads at once: a database with nothing of the store's in it - no event
    /// store, no dead-letter table, no <c>mt_hilo</c>, not even the store's schemas. Each read answers as a value, and none
    /// of them creates anything.
    /// </summary>
    [PostgresFact]
    public async Task Each_read_on_an_empty_database_answers_and_creates_nothing()
    {
        await using Host host = await Host.StartAsync(fixture);

        ServerSnapshot before = await host.SnapshotAsync();
        before.Objects.Should().NotContain(
            static x => x.Contains(": relation ", StringComparison.Ordinal), "the store's schemas were dropped and nothing else is in the database");

        DeadLetterPage page = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery(), Token));
        long? count = await host.EventsAsync(x => x.CountDeadLettersAsync(Scope, Token));
        AggregateSnapshot replay = await host.EventsAsync(
            x => x.AggregateAtVersionAsync(Scope, Guid.NewGuid().ToString(), typeof(LedgerSummary).FullName!, 0, Token));
        bool exists = await host.DocumentsAsync(x => x.StreamExistsAsync(Scope, Guid.NewGuid().ToString(), Token));
        DeadLetterGroupList groups = await host.EventsAsync(x => x.ListDeadLetterGroupsAsync(Scope, Token));

        page.Should().BeSameAs(DeadLetterPage.Empty, "no table is an empty list, not a failure");
        groups.Error.Should().BeNull("M3: no table is no groups, not a failure");
        groups.Groups.Should().BeEmpty();
        count.Should().Be(0);
        replay.Error.Should().BeNull("no event store is a stream that does not exist, not a failure");
        replay.Found.Should().BeFalse();
        replay.Reason.Should().Be(AggregateMissingReason.StreamMissing);
        exists.Should().BeFalse();

        (await host.ExistsAsync(EventSchema, "mt_events")).Should().BeFalse("to_regclass: no event store was created");
        (await host.ExistsAsync(EventSchema, DeadLetterTable)).Should().BeFalse("nor the dead-letter table");
        (await host.ExistsAsync(DocumentSchema, "mt_hilo")).Should().BeFalse("nor Marten's HiLo bookkeeping");
        before.ShouldBeUnchangedIn(await host.SnapshotAsync());
    }

    /// <summary>
    /// Acceptance 3: a database with an event store and no dead-letter table, a store that has never touched
    /// <c>DeadLetterEvent</c>, <c>AutoCreate.CreateOrUpdate</c>. The page's own reads create nothing - not the table, not
    /// <c>mt_hilo</c>, nothing.
    /// </summary>
    [PostgresFact]
    public async Task Opening_the_dead_letter_screen_where_there_is_no_table_creates_nothing()
    {
        await using Host host = await Host.StartAsync(fixture);
        await host.SeedAsync(withDeadLetters: false);

        (await host.ExistsAsync(EventSchema, DeadLetterTable)).Should().BeFalse("the deployment's dead-letter table was dropped");
        ServerSnapshot before = await host.SnapshotAsync();

        // The page's three reads, in its order: the shape, the list, and the projections its Rewind button needs - and the
        // navigation badge, which counts the same table.
        EventStoreShape shape = await host.EventsAsync(x => x.DescribeAsync(Scope, Token));
        DeadLetterPage page = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery(), Token));
        ProjectionsView projections = await host.ProjectionsAsync(x => x.GetProjectionsAsync(Scope, Token));
        long? count = await host.EventsAsync(x => x.CountDeadLettersAsync(Scope, Token));
        DeadLetterGroupList groups = await host.EventsAsync(x => x.ListDeadLetterGroupsAsync(Scope, Token));

        shape.EventTablesExist.Should().BeTrue("the event store is there - only the dead-letter table is not");
        page.Should().BeSameAs(DeadLetterPage.Empty, "no table is an empty list, not a failure");
        groups.Error.Should().BeNull("M3's grouped view: no table is no groups");
        groups.Groups.Should().BeEmpty();
        projections.HasEventStore.Should().BeTrue();
        count.Should().Be(0);

        (await host.ExistsAsync(EventSchema, DeadLetterTable)).Should().BeFalse("to_regclass: the screen did not create the table");
        (await host.ExistsAsync(DocumentSchema, "mt_hilo")).Should().BeFalse("nor Marten's HiLo bookkeeping");
        before.ShouldBeUnchangedIn(await host.SnapshotAsync());
    }

    /// <summary>
    /// The list reads a table this process's store never ensured, works as it did - order, filters, paging - and writes
    /// nothing.
    /// </summary>
    [PostgresFact]
    public async Task Listing_dead_letters_a_store_never_ensured_reads_them_and_creates_nothing()
    {
        await using Host host = await Host.StartAsync(fixture);
        await host.SeedAsync(withDeadLetters: true);

        ServerSnapshot before = await host.SnapshotAsync();

        DeadLetterPage all = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery(), Token));
        DeadLetterPage ledger = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery { ProjectionName = "Ledger" }, Token));
        DeadLetterPage percent = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery { ExceptionType = "%" }, Token));
        DeadLetterPage first = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery { PageSize = 2 }, Token));
        DeadLetterPage second = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery { PageSize = 2, Offset = 2 }, Token));
        long? count = await host.EventsAsync(x => x.CountDeadLettersAsync(Scope, Token));

        // M3: the grouped view, a group opened in the list, and the skip dialog's count - every read it adds.
        DeadLetterGroupList groups = await host.EventsAsync(x => x.ListDeadLetterGroupsAsync(Scope, Token));
        groups.Error.Should().BeNull();
        groups.TotalLetters.Should().Be(3);
        DeadLetterGroupKey key = groups.Groups[0].Key;
        DeadLetterPage opened = await host.EventsAsync(x => x.ListDeadLettersAsync(Scope, new DeadLetterQuery { Group = key }, Token));
        DeadLetterGroupSkipPlan plan = await host.EventsAsync(x => x.DescribeDeadLetterGroupSkipAsync(Scope, key, Token));
        opened.Error.Should().BeNull();
        opened.Rows.Should().HaveCount((int) groups.Groups[0].Letters);
        plan.Should().NotBeNull();

        all.Error.Should().BeNull();
        all.Rows.Select(static x => x.ExceptionMessage).Should().Equal(["third", "second", "first"], "newest failure first");
        all.Rows[0].Should().Be(new DeadLetterRow(
            Letters[2].Id, "Ledger", "All", Letters[2].Timestamp, "Npgsql.PostgresException", "third", 3, "*DEFAULT*"));

        ledger.Rows.Select(static x => x.ExceptionMessage).Should().Equal(["third", "first"]);
        percent.Rows.Select(static x => x.ExceptionMessage).Should().Equal(
            ["second"], "a % in the filter is a character to find, not a wildcard");

        first.Rows.Should().HaveCount(2);
        first.HasMore.Should().BeTrue();
        second.Rows.Select(static x => x.ExceptionMessage).Should().Equal(["first"]);
        second.HasMore.Should().BeFalse();
        count.Should().Be(3);

        (await host.ExistsAsync(DocumentSchema, "mt_hilo")).Should().BeFalse("to_regclass: no HiLo bookkeeping was created");
        before.ShouldBeUnchangedIn(await host.SnapshotAsync());
    }

    /// <summary>
    /// The replay folds a stream this process's store never read, with Marten's own selector and aggregator, and writes
    /// nothing.
    /// </summary>
    [PostgresFact]
    public async Task Replaying_a_stream_a_store_never_ensured_folds_it_and_creates_nothing()
    {
        await using Host host = await Host.StartAsync(fixture);
        Guid stream = await host.SeedAsync(withDeadLetters: true);

        ServerSnapshot before = await host.SnapshotAsync();
        string type = typeof(LedgerSummary).FullName!;

        AggregateSnapshot atTwo = await host.EventsAsync(x => x.AggregateAtVersionAsync(Scope, stream.ToString(), type, 2, Token));
        AggregateSnapshot atEnd = await host.EventsAsync(x => x.AggregateAtVersionAsync(Scope, stream.ToString(), type, 0, Token));
        AggregateSnapshot pastEnd = await host.EventsAsync(x => x.AggregateAtVersionAsync(Scope, stream.ToString(), type, 9, Token));
        AggregateSnapshot missing = await host.EventsAsync(x => x.AggregateAtVersionAsync(Scope, Guid.NewGuid().ToString(), type, 0, Token));

        atTwo.Error.Should().BeNull();
        atTwo.Found.Should().BeTrue();
        atTwo.Json.Should().Contain("\"Balance\":10").And.Contain("\"Owner\":\"acme\"");
        atTwo.Json.Should().Contain(stream.ToString(), "the aggregate carries the stream's id, as Marten's own replay sets it");

        atEnd.Json.Should().Contain("\"Balance\":25").And.Contain("\"Credits\":2");

        pastEnd.Found.Should().BeFalse();
        pastEnd.Reason.Should().Be(AggregateMissingReason.VersionPastEnd);
        pastEnd.StreamVersion.Should().Be(3);

        missing.Found.Should().BeFalse();
        missing.Reason.Should().Be(AggregateMissingReason.StreamMissing);

        (await host.ExistsAsync(DocumentSchema, "mt_hilo")).Should().BeFalse("to_regclass: no HiLo bookkeeping was created");
        before.ShouldBeUnchangedIn(await host.SnapshotAsync());
    }

    /// <summary>The document page's "view stream" check: right answer, no DDL.</summary>
    [PostgresFact]
    public async Task Asking_whether_a_stream_exists_answers_and_creates_nothing()
    {
        await using Host host = await Host.StartAsync(fixture);
        Guid stream = await host.SeedAsync(withDeadLetters: false);

        ServerSnapshot before = await host.SnapshotAsync();

        (await host.DocumentsAsync(x => x.StreamExistsAsync(Scope, stream.ToString(), Token))).Should().BeTrue();
        (await host.DocumentsAsync(x => x.StreamExistsAsync(Scope, Guid.NewGuid().ToString(), Token))).Should().BeFalse();
        (await host.DocumentsAsync(x => x.StreamExistsAsync(Scope, "not-a-guid", Token))).Should().BeFalse();

        (await host.ExistsAsync(DocumentSchema, "mt_hilo")).Should().BeFalse("to_regclass: no HiLo bookkeeping was created");
        before.ShouldBeUnchangedIn(await host.SnapshotAsync());
    }

    /// <summary>
    /// The anti-vacuity theory: on exactly this setup, the Marten call each read replaced writes DDL. If a Marten release
    /// stops doing that, the tests above no longer prove anything and this is where it is noticed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Version-aware (L5).</b> On the seeded setup - the schema applied by another store, the studio's store never having
    /// read - the DDL Marten's read wrote was the HiLo feature's: the first ensure in the process runs <c>AllObjects()</c>,
    /// which below Marten 9.50.3 applies it. From 9.50.3 (#5665) <c>AllObjects()</c> describes it and applies nothing, and
    /// on that setup Marten's read writes no DDL at all - asserted, so the change is recorded rather than silent. The
    /// seeded tests above then prove their point at the floor, which still has the hazard.
    /// </para>
    /// <para>
    /// From 9.50.3 the theory's proof moves to the empty database the first test above reads: there Marten's read creates
    /// the storage it ensures at every version, so that test is never an assertion that nothing happened where nothing
    /// could have.
    /// </para>
    /// </remarks>
    [PostgresTheory]
    [InlineData("list")]
    [InlineData("replay")]
    [InlineData("state")]
    public async Task Martens_own_read_on_the_same_database_really_does_write_DDL(string read)
    {
        bool offline = MartenStudio.Services.Configuration.RuntimeFeature.OfflineSchemaEnumeration.Probe() == true;

        await using (Host host = await Host.StartAsync(fixture))
        {
            Guid stream = await host.SeedAsync(withDeadLetters: true);

            ServerSnapshot before = await host.SnapshotAsync();
            (await host.ExistsAsync(DocumentSchema, "mt_hilo")).Should().BeFalse();

            await MartensOwnReadAsync(host, read, stream);

            if (!offline)
            {
                (await host.ExistsAsync(DocumentSchema, "mt_hilo")).Should().BeTrue(
                    "{0} ensures storage first, and below Marten 9.50.3 the first ensure in the process runs AllObjects(), which "
                    + "applies the HiLo feature - if this no longer happens, the tests above prove nothing",
                    read);
                (await host.SnapshotAsync()).Objects.Should().NotEqual(before.Objects);
                return;
            }

            (await host.ExistsAsync(DocumentSchema, "mt_hilo")).Should().BeFalse(
                "from Marten 9.50.3 AllObjects() describes the HiLo feature and applies nothing");
            (await host.SnapshotAsync()).Objects.Should().Equal(before.Objects,
                "on this setup the HiLo feature was the whole of the DDL Marten's {0} wrote, and from 9.50.3 it writes none", read);
        }

        // From 9.50.3: the empty database, where Marten's read still creates what it ensures.
        await using (Host empty = await Host.StartAsync(fixture))
        {
            ServerSnapshot before = await empty.SnapshotAsync();

            await MartensOwnReadAsync(empty, read, Guid.NewGuid());

            (await empty.SnapshotAsync()).Objects.Should().NotEqual(before.Objects,
                "{0} ensures storage first, and on an empty database that creates it at every Marten - if this no longer "
                + "happens, the empty-database test above proves nothing",
                read);
        }
    }

    /// <summary>
    /// The call each read replaced: the dead-letter list's LINQ query, the replay's AggregateStreamAsync, and the stream
    /// link's FetchStreamStateAsync.
    /// </summary>
    private static async Task MartensOwnReadAsync(Host host, string read, Guid stream)
    {
        await using IQuerySession session = host.Store.QuerySession();

        switch (read)
        {
            case "list":
                _ = await session.Query<DeadLetterEvent>().ToListAsync(Token);
                break;
            case "replay":
                _ = await session.Events.AggregateStreamAsync<LedgerSummary>(stream, token: Token);
                break;
            default:
                _ = await session.Events.FetchStreamStateAsync(stream, Token);
                break;
        }
    }

    /// <summary>The three dead letters every list test reads, oldest first.</summary>
    private static readonly DeadLetterEvent[] Letters =
    [
        Letter("Ledger", "System.InvalidOperationException", "first", 1, Then.AddMinutes(1)),
        Letter("Audit", "Odd_50%_Exception", "second", 2, Then.AddMinutes(2)),
        Letter("Ledger", "Npgsql.PostgresException", "third", 3, Then.AddMinutes(3)),
    ];

    private static DeadLetterEvent Letter(string projection, string exception, string message, long sequence, DateTimeOffset at) => new()
    {
        Id = Guid.CreateVersion7(),
        ProjectionName = projection,
        ShardName = "All",
        Timestamp = at,
        ExceptionType = exception,
        ExceptionMessage = message,
        EventSequence = sequence,
        TenantId = "*DEFAULT*",
    };

    /// <summary>One studio over this class's database, and the deployment's store that made its schema.</summary>
    private sealed class Host : IAsyncDisposable
    {
        /// <summary>Serialises the first test's creation of the class's database.</summary>
        private static readonly SemaphoreSlim Creating = new(1, 1);

        /// <summary>The class's database, once created; the container outlives every test in the run.</summary>
        private static string? database;

        private readonly ServiceProvider provider;
        private readonly string connectionString;

        private Host(ServiceProvider provider, string connectionString)
        {
            this.provider = provider;
            this.connectionString = connectionString;
        }

        /// <summary>The studio's host store, which has never run a statement.</summary>
        public IDocumentStore Store => provider.GetRequiredService<IDocumentStore>();

        /// <summary>
        /// A fresh studio over the class's database, with nothing of the store's left in it: the two schemas a previous
        /// test's stores made are dropped first.
        /// </summary>
        public static async Task<Host> StartAsync(PostgresFixture postgres)
        {
            string connectionString = await DatabaseAsync(postgres);

            await TenancyDatabases.ExecuteAsync(
                connectionString,
                "drop schema if exists " + SqlIdentifier.Quote(DocumentSchema) + " cascade; "
                + "drop schema if exists " + SqlIdentifier.Quote(EventSchema) + " cascade",
                Token);

            var services = new ServiceCollection();

            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddAuthorization();
            services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

            services.AddMarten(options =>
            {
                Configure(options, connectionString);

                // Only here, never in the deployment's store: an int id puts Marten's HiLo feature into AllObjects(), so
                // the first EnsureStorageExistsAsync in this process would create mt_hilo.
                options.Schema.For<Ledger>();
            });

            services.AddMartenStudio(static options =>
            {
                options.AuthorizationPolicy = null;
                options.DiscoverTenantIds = false;
            });

            return new Host(services.BuildServiceProvider(), connectionString);
        }

        /// <summary>The class's one database, created by the first test that asks - dropping whatever a previous run left.</summary>
        private static async Task<string> DatabaseAsync(PostgresFixture postgres)
        {
            await Creating.WaitAsync(Token);

            try
            {
                return database ??= await TenancyDatabases.CreateAsync(postgres, Database, Token);
            }
            finally
            {
                Creating.Release();
            }
        }

        /// <summary>
        /// The deployment: a store of its own applies the schema, appends one stream and records the dead letters - all of
        /// it before the studio's store exists to have touched anything.
        /// </summary>
        public async Task<Guid> SeedAsync(bool withDeadLetters)
        {
            Guid stream = Guid.NewGuid();

            using (DocumentStore deployment = DocumentStore.For(options => Configure(options, connectionString)))
            {
                await deployment.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

                await using IDocumentSession session = deployment.LightweightSession();

                session.Events.StartStream<LedgerSummary>(stream, new LedgerOpened("acme"), new LedgerCredited(10), new LedgerCredited(15));

                if (withDeadLetters)
                {
                    foreach (DeadLetterEvent letter in Letters)
                    {
                        session.Store(letter);
                    }
                }

                await session.SaveChangesAsync(Token);
            }

            if (!withDeadLetters)
            {
                await TenancyDatabases.ExecuteAsync(
                    connectionString, "drop table if exists " + SqlIdentifier.Qualify(EventSchema, DeadLetterTable), Token);
            }

            return stream;
        }

        public Task<ServerSnapshot> SnapshotAsync() => ServerSnapshot.ReadAsync([connectionString], Prefix, null, Token);

        /// <summary>Whether a table exists in this database, by <c>to_regclass</c>.</summary>
        public async Task<bool> ExistsAsync(string schema, string table)
        {
            await using var connection = new NpgsqlConnection(TenancyDatabases.Unpooled(connectionString));
            await connection.OpenAsync(Token);

            await using var command = new NpgsqlCommand("select to_regclass(@name) is not null", connection);
            command.Parameters.AddWithValue("name", SqlIdentifier.Qualify(schema, table));

            return (bool) (await command.ExecuteScalarAsync(Token))!;
        }

        public async Task<T> EventsAsync<T>(Func<IEventDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IEventDataService>());
        }

        public async Task<T> ProjectionsAsync<T>(Func<IProjectionDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IProjectionDataService>());
        }

        public async Task<T> DocumentsAsync<T>(Func<IDocumentDataService, Task<T>> action)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IDocumentDataService>());
        }

        public ValueTask DisposeAsync() => provider.DisposeAsync();

        /// <summary>What both stores share: the schemas, Marten's own default AutoCreate said out loud, the types.</summary>
        private static void Configure(StoreOptions options, string connectionString)
        {
            options.Connection(connectionString);
            options.DatabaseSchemaName = DocumentSchema;
            options.Events.DatabaseSchemaName = EventSchema;
            options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

            options.Schema.For<LedgerSummary>();
            options.Events.AddEventType<LedgerOpened>();
            options.Events.AddEventType<LedgerCredited>();
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

/// <summary>A document with an <c>int</c> id, which is what makes Marten's HiLo feature active.</summary>
[DocumentAlias("dl_ledger")]
public class Ledger
{
    /// <summary>The HiLo-assigned identity.</summary>
    public int Id { get; set; }
}

/// <summary>A self-aggregating aggregate that never sets its own id: the replay has to.</summary>
[DocumentAlias("dl_ledger_summary")]
public class LedgerSummary
{
    /// <summary>The stream's id, set by the replay rather than by an event.</summary>
    public Guid Id { get; set; }

    /// <summary>Who opened it.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The sum of the credits.</summary>
    public int Balance { get; set; }

    /// <summary>How many credits.</summary>
    public int Credits { get; set; }

    /// <summary>Opened.</summary>
    public void Apply(LedgerOpened opened) => Owner = opened.Owner;

    /// <summary>Credited.</summary>
    public void Apply(LedgerCredited credited)
    {
        Balance += credited.Amount;
        Credits++;
    }
}

/// <summary>A ledger was opened.</summary>
/// <param name="Owner">For whom.</param>
public record LedgerOpened(string Owner);

/// <summary>A ledger was credited.</summary>
/// <param name="Amount">How much.</param>
public record LedgerCredited(int Amount);
