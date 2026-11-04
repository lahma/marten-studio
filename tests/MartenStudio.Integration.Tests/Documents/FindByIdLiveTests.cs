using System.Text.Json;

using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;

using MartenStudio.Integration.Tests.Tenancy;
using MartenStudio.Services.Find;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// 1.2-SCOPE-3: Find by id against a live server, at every tenancy the studio has. Each test searches through the circuit's real
/// <see cref="FindService" /> - the real contributors, each asking the data service of the page its hit opens - and proves what the
/// feature promises: a hit for what the page would show, nothing for what it would not (a hidden type, a tenant the policy refuses, a
/// database the scope is not on), and a server that is exactly as it was afterwards.
/// </summary>
public class FindByIdLiveTests(PostgresFixture postgres)
{
    private const string Prefix = "scope3_find_";

    private const string Schema = "app";

    private const string Events = "app_events";

    private const string Acme = "acme";

    private const string Globex = "globex";

    private static readonly StudioScope Whole = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_single_database_store_finds_a_document_its_stream_its_event_and_its_table_and_nothing_of_a_hidden_type()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "single"], Token);
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            options.Connection(database);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Events;
            options.Schema.For<FindNote>().SoftDeleted();
            options.Schema.For<FindOrder>().AddSubClass<FindPremiumOrder>();
            options.Schema.For<FindHiddenNote>();
            options.Events.AddEventType<TenancyNoteWritten>();
        }

        Guid shared = Guid.NewGuid();
        Guid deleted = Guid.NewGuid();
        Guid hidden = Guid.NewGuid();

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            await using IDocumentSession session = seed.LightweightSession();
            session.Store(new FindNote { Id = shared, Text = "both a document and a stream" });
            session.Events.StartStream(shared, new TenancyNoteWritten("started"));
            session.Store(new FindHiddenNote { Id = hidden });
            session.Store(new FindPremiumOrder { Id = "ORD-1" });
            session.Store(new FindNote { Id = deleted, Text = "to be deleted" });
            await session.SaveChangesAsync(Token);

            session.Delete<FindNote>(deleted);
            await session.SaveChangesAsync(Token);
        }

        ServerSnapshot before = await SnapshotAsync(owned);

        await using TenancyStudio studio = TenancyStudio.Start(Configure, studio: static options => options.IsDocumentTypeVisible = static type => type != typeof(FindHiddenNote));
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        FindService find = circuit.ServiceProvider.GetRequiredService<FindService>();

        // An id that is both a document and a stream: both are offered, the document first.
        FindResult both = await find.FindAsync(Whole, shared.ToString(), Token);
        both.Hits.Select(static x => x.Kind).Should().Equal("Document", "Stream");
        both.Hits[0].Title.Should().Be("find_note " + shared);
        both.Hits[0].TenantChip.Should().BeNull();
        both.Hits[0].Url.Should().Contain("documents/find_note/doc").And.Contain("id=" + shared);
        both.Searched.Should().Contain("2 document types", "the phrase counts the visible types, which is what the rail shows");
        both.Searched.Should().Contain("streams");

        // F1: Marten writes *DEFAULT* into the tenant of every stream and event of a store that is not conjoined. That is no tenant:
        // not shown, not in the link, not asked of a policy, and the scope of the hit is the scope on screen, so one hit opens by itself.
        StudioState state = circuit.ServiceProvider.GetRequiredService<StudioState>();
        await state.EnsureInitializedAsync(Token);
        StudioScope onScreen = state.ActiveScope!;
        both.Hits.Should().OnlyContain(static x => x.TenantChip == null && x.Scope.TenantId == null && !x.Url.Contains("tenant=", StringComparison.Ordinal));
        both.Hits.Should().OnlyContain(x => x.Scope == onScreen);

        // A soft-deleted document is found and marked, as the detail page links to it.
        FindResult gone = await find.FindAsync(Whole, deleted.ToString(), Token);
        gone.Hits.Should().ContainSingle().Which.Mark.Should().Be("deleted");

        // A subclass is named from mt_doc_type.
        FindResult order = await find.FindAsync(Whole, "ORD-1", Token);
        order.Hits.Should().ContainSingle().Which.Detail.Should().Be("as FindPremiumOrder");

        // An event, by the sequence the studio prints - and by the bare number.
        FindResult byHash = await find.FindAsync(Whole, "#1", Token);
        FindHit sequence = byHash.Hits.Should().ContainSingle().Subject;
        sequence.Kind.Should().Be("Event");
        sequence.Url.Should().Contain("events/streams/s").And.Contain("id=" + shared).And.Contain("v=1").And.NotContain("tenant=");
        sequence.TenantChip.Should().BeNull();
        sequence.Scope.Should().Be(onScreen);
        (await find.FindAsync(Whole, "1", Token)).Hits.Select(static x => x.Kind).Should().Contain("Event");
        (await find.FindAsync(Whole, "#99", Token)).Found.Should().BeFalse();

        // A table, by name.
        FindResult table = await find.FindAsync(Whole, Schema + ".mt_doc_find_note", Token);
        table.Hits.Should().ContainSingle().Which.Kind.Should().Be("Table");

        // A hidden type is never probed and never counted: its document, its table and its id read exactly as no match.
        FindResult nothing = await find.FindAsync(Whole, hidden.ToString(), Token);
        nothing.Found.Should().BeFalse();
        nothing.Notes.Should().BeEmpty();
        nothing.Searched.Should().Contain("2 document types", "the hidden type is not among the document types searched");
        (await find.FindAsync(Whole, Schema + ".mt_doc_find_hidden", Token)).Found.Should().BeFalse();

        // The value is a parameter: a quote is a character.
        (await find.FindAsync(Whole, "x'; drop table app.mt_doc_find_note; --", Token)).Found.Should().BeFalse();
        (await find.FindAsync(Whole, shared.ToString(), Token)).Found.Should().BeTrue("and the table is still there");

        // Find audits nothing itself.
        studio.Services.GetRequiredService<StudioActionLogService>().GetLatest().Should().BeEmpty();

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned));

        // F1 again, under a policy that allows the store and no tenant at all: the stream and the event are still the visitor's to see.
        await using TenancyStudio allowlisted = TenancyStudio.Start(
            Configure,
            extra: static services => services.AddAuthorization(static options => options.AddPolicy(
                "NoTenant",
                static policy => policy.RequireAssertion(static context => context.Resource is MartenStoreResource { TenantId: null }))),
            studio: static options => options.StoreAuthorizationPolicy = "NoTenant");
        await using AsyncServiceScope guarded = allowlisted.Services.CreateAsyncScope();
        FindService guardedFind = guarded.ServiceProvider.GetRequiredService<FindService>();

        (await guardedFind.FindAsync(Whole, shared.ToString(), Token)).Hits.Select(static x => x.Kind).Should().Equal("Document", "Stream");
        (await guardedFind.FindAsync(Whole, "#1", Token)).Hits.Should().ContainSingle().Which.Kind.Should().Be("Event");
    }

    [PostgresFact]
    public async Task A_conjoined_store_lists_the_id_once_per_tenant_and_drops_the_one_the_policy_refuses()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "conjoined"], Token);
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            options.Connection(database);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Events;
            options.Schema.For<FindNote>().MultiTenanted();
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.AddEventType<TenancyNoteWritten>();
        }

        Guid shared = Guid.NewGuid();

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            foreach (string tenant in new[] { Acme, Globex })
            {
                await using IDocumentSession session = seed.LightweightSession(tenant);
                session.Store(new FindNote { Id = shared, Text = tenant });
                await session.SaveChangesAsync(Token);
            }
        }

        // No policy, no tenant selected: one hit per tenant, each linking to its own tenant.
        await using (TenancyStudio open = TenancyStudio.Start(Configure))
        {
            await using AsyncServiceScope circuit = open.Services.CreateAsyncScope();
            FindService find = circuit.ServiceProvider.GetRequiredService<FindService>();

            FindResult all = await find.FindAsync(Whole, shared.ToString(), Token);
            all.Hits.Should().HaveCount(2);
            all.Hits.Select(static x => x.TenantChip).Should().BeEquivalentTo(["tenant acme", "tenant globex"]);
            all.Hits.Select(static x => x.Scope.TenantId).Should().BeEquivalentTo([Acme, Globex]);
            all.Hits.Should().OnlyContain(static x => x.Url.Contains("tenant=" + x.Scope.TenantId, StringComparison.Ordinal));
            all.Hits.Select(static x => x.Key).Should().OnlyHaveUniqueItems();

            // With one tenant selected, the probe is that tenant's.
            FindResult one = await find.FindAsync(Whole with { TenantId = Acme }, shared.ToString(), Token);
            one.Hits.Should().ContainSingle().Which.Scope.TenantId.Should().Be(Acme);
        }

        // A policy that refuses globex: its row is dropped before anything is counted or said.
        await using TenancyStudio guarded = TenancyStudio.Start(
            Configure,
            extra: static services => services.AddAuthorization(static options => options.AddPolicy(
                "NotGlobex",
                static policy => policy.RequireAssertion(static context => context.Resource is MartenStoreResource { TenantId: not Globex }))),
            studio: static options => options.StoreAuthorizationPolicy = "NotGlobex");
        await using AsyncServiceScope guardedCircuit = guarded.Services.CreateAsyncScope();
        FindService guardedFind = guardedCircuit.ServiceProvider.GetRequiredService<FindService>();

        FindResult visible = await guardedFind.FindAsync(Whole, shared.ToString(), Token);
        visible.Hits.Should().ContainSingle().Which.Scope.TenantId.Should().Be(Acme);
        JsonSerializer.Serialize(visible).Should().NotContain(Globex, "a tenant the policy refuses is not named, counted or hinted at");
    }

    [PostgresFact]
    public async Task More_than_five_tenants_are_five_hits_and_a_count_of_the_rest_the_visitor_may_see()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "many"], Token);
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            options.Connection(database);
            options.DatabaseSchemaName = Schema;
            options.Schema.For<FindNote>().MultiTenanted();
        }

        Guid shared = Guid.NewGuid();

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            for (int i = 1; i <= 8; i++)
            {
                await using IDocumentSession session = seed.LightweightSession("t" + i);
                session.Store(new FindNote { Id = shared });
                await session.SaveChangesAsync(Token);
            }
        }

        await using TenancyStudio studio = TenancyStudio.Start(Configure);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();

        FindResult result = await circuit.ServiceProvider.GetRequiredService<FindService>().FindAsync(Whole, shared.ToString(), Token);

        result.Hits.Should().HaveCount(5);
        result.Hits.Count(static x => x.Detail?.Contains("and 3 more tenants you may see", StringComparison.Ordinal) == true).Should().Be(1);
    }

    [PostgresFact]
    public async Task A_tenant_partitioned_store_is_not_probed_without_a_tenant_and_is_with_one()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "partitioned"], Token);
        string database = owned[0];

        void Configure(StoreOptions options)
        {
            options.Connection(database);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Events;
            options.Schema.For<FindNote>().MultiTenanted();
            options.Events.AddEventType<TenancyNoteWritten>();

            // What UseTenantPartitionedEvents requires: conjoined event tenancy and a quick append mode.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Policies.PartitionMultiTenantedDocumentsUsingMartenManagement("tenant_partitions");
        }

        Guid shared = Guid.NewGuid();

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            await seed.Advanced.AddMartenManagedTenantsAsync(Token, Acme, Globex);

            await using IDocumentSession session = seed.LightweightSession(Acme);
            session.Store(new FindNote { Id = shared });
            session.Events.StartStream(shared, new TenancyNoteWritten("started"));
            await session.SaveChangesAsync(Token);
        }

        ServerSnapshot before = await SnapshotAsync(owned);

        await using TenancyStudio studio = TenancyStudio.Start(Configure);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        FindService find = circuit.ServiceProvider.GetRequiredService<FindService>();

        FindResult without = await find.FindAsync(Whole, shared.ToString(), Token);
        without.Found.Should().BeFalse("a probe would plan and lock every tenant's partition");
        without.Notes.Should().Contain("1 document type partitioned by tenant was not searched: select a tenant.");
        without.Notes.Should().Contain("This store numbers and partitions events per tenant: select a tenant to search streams, natural keys and events.");

        FindResult sequences = await find.FindAsync(Whole, "#1", Token);
        sequences.Found.Should().BeFalse();
        sequences.Notes.Should().Contain(static x => x.StartsWith("This store numbers and partitions events per tenant", StringComparison.Ordinal));

        StudioScope inAcme = Whole with { TenantId = Acme };
        FindResult with = await find.FindAsync(inAcme, shared.ToString(), Token);
        with.Hits.Select(static x => x.Kind).Should().Equal("Document", "Stream");
        with.Notes.Should().BeEmpty();

        (await find.FindAsync(inAcme, "#1", Token)).Hits.Should().ContainSingle().Which.Kind.Should().Be("Event");
        (await find.FindAsync(Whole with { TenantId = Globex }, shared.ToString(), Token)).Found.Should().BeFalse("it is acme's");

        before.ShouldBeUnchangedIn(await SnapshotAsync(owned));
    }

    [PostgresFact]
    public async Task A_database_per_tenant_store_finds_the_id_only_in_the_database_the_scope_is_on()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "static_a", Prefix + "static_b"], Token);

        void Configure(StoreOptions options)
        {
            options.DatabaseSchemaName = Schema;
            options.Schema.For<FindNote>();
            options.MultiTenantedDatabases(tenancy =>
            {
                tenancy.AddMultipleTenantDatabase(owned[0], "find-a").ForTenants(Acme);
                tenancy.AddMultipleTenantDatabase(owned[1], "find-b").ForTenants(Globex);
            });
        }

        Guid inAcme = Guid.NewGuid();
        Guid inGlobex = Guid.NewGuid();

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            await using (IDocumentSession session = seed.LightweightSession(Acme))
            {
                session.Store(new FindNote { Id = inAcme });
                await session.SaveChangesAsync(Token);
            }

            await using (IDocumentSession session = seed.LightweightSession(Globex))
            {
                session.Store(new FindNote { Id = inGlobex });
                await session.SaveChangesAsync(Token);
            }
        }

        await using TenancyStudio studio = TenancyStudio.Start(Configure);
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        IServiceProvider services = circuit.ServiceProvider;

        StudioState state = services.GetRequiredService<StudioState>();
        await state.EnsureInitializedAsync(Token);
        state.AvailableDatabases.Should().HaveCount(2);

        FindService find = services.GetRequiredService<FindService>();
        List<(string Database, bool A, bool B)> found = [];

        foreach (DatabaseListing listing in state.AvailableDatabases)
        {
            StudioScope scope = new("default", listing.Identity, null);
            found.Add((
                listing.Identity,
                (await find.FindAsync(scope, inAcme.ToString(), Token)).Found,
                (await find.FindAsync(scope, inGlobex.ToString(), Token)).Found));
        }

        found.Count(static x => x.A).Should().Be(1, "acme's document is in exactly one of the two databases");
        found.Count(static x => x.B).Should().Be(1);
        found.Should().NotContain(static x => x.A && x.B, "and never both in one: each is searched in the database the scope is on");
    }

    [PostgresFact]
    public async Task Nothing_found_in_one_store_is_found_in_another_through_a_search_in_that_stores_first_database()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "first", Prefix + "second"], Token);

        void Configure(StoreOptions options)
        {
            options.Connection(owned[0]);
            options.DatabaseSchemaName = Schema;
            options.Schema.For<FindNote>();
        }

        static void ConfigureSecond(StoreOptions options, string connection)
        {
            options.Connection(connection);
            options.DatabaseSchemaName = "second";
            options.Schema.For<FindNote>();
        }

        Guid inSecond = Guid.NewGuid();

        await using (DocumentStore seed = DocumentStore.For(options => ConfigureSecond(options, owned[1])))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

            await using IDocumentSession session = seed.LightweightSession();
            session.Store(new FindNote { Id = inSecond });
            await session.SaveChangesAsync(Token);
        }

        await using (DocumentStore seed = DocumentStore.For(Configure))
        {
            await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        }

        await using TenancyStudio studio = TenancyStudio.Start(
            Configure,
            extra: services => services.AddMartenStore<IFindLiveSecondStore>(options => ConfigureSecond(options, owned[1])));
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        FindService find = circuit.ServiceProvider.GetRequiredService<FindService>();

        (await find.FindAsync(Whole, inSecond.ToString(), Token)).Found.Should().BeFalse("the id is in the other store");

        StudioScope other = new(nameof(IFindLiveSecondStore), string.Empty, null);
        FindResult there = await find.FindAsync(other, inSecond.ToString(), Token);

        FindHit hit = there.Hits.Should().ContainSingle().Subject;
        hit.Scope.StoreKey.Should().Be(nameof(IFindLiveSecondStore));
        hit.Scope.DatabaseId.Should().NotBeNullOrEmpty("the hit names the database the resolver matched, not the empty one that was asked");
        hit.Url.Should().Contain("store=" + nameof(IFindLiveSecondStore));
    }

    private static void ConfigureCrowd(StoreOptions options, string database)
    {
        options.Connection(database);
        options.DatabaseSchemaName = Schema;
        options.Events.DatabaseSchemaName = Events;
        options.Schema.For<FindNote>().MultiTenanted();
        options.Events.TenancyStyle = TenancyStyle.Conjoined;
        options.Events.AddEventType<TenancyNoteWritten>();
    }

    private static async Task SeedCrowdAsync(string database, Guid shared, int tenants, string[] holders)
    {
        await using DocumentStore seed = DocumentStore.For(options => ConfigureCrowd(options, database));
        await seed.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        for (int i = 1; i <= tenants; i++)
        {
            string tenant = "t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await using IDocumentSession session = seed.LightweightSession(tenant);

            session.Store(new FindNote { Id = holders.Contains(tenant, StringComparer.Ordinal) ? shared : Guid.NewGuid() });
            if (holders.Contains(tenant, StringComparer.Ordinal))
            {
                session.Events.StartStream(shared, new TenancyNoteWritten(tenant));
            }

            await session.SaveChangesAsync(Token);
        }
    }

    [PostgresFact]
    public async Task An_id_held_by_more_tenants_than_a_probe_reads_is_still_found_for_the_one_tenant_the_policy_admits()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "crowd"], Token);
        string database = owned[0];
        Guid shared = Guid.NewGuid();

        // Twenty-five tenants hold the id: more than the 21 rows one probe reads, and the one the visitor may address is the seventh.
        string[] holders = [.. Enumerable.Range(1, 25).Select(static i => "t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        await SeedCrowdAsync(database, shared, 25, holders);

        await using TenancyStudio studio = TenancyStudio.Start(
            options => ConfigureCrowd(options, database),
            extra: static services => services.AddAuthorization(static options => options.AddPolicy(
                "OnlySeven",
                static policy => policy.RequireAssertion(static context => context.Resource is MartenStoreResource { TenantId: null or "t7" }))),
            studio: static options => options.StoreAuthorizationPolicy = "OnlySeven");
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();

        FindResult found = await circuit.ServiceProvider.GetRequiredService<FindService>().FindAsync(Whole, shared.ToString(), Token);

        found.Hits.Select(static x => x.Kind).Should().Equal("Document", "Stream");
        found.Hits.Should().OnlyContain(static x => x.Scope.TenantId == "t7" && x.TenantChip == "tenant t7");
        found.Notes.Should().BeEmpty();
        JsonSerializer.Serialize(found).Should().NotContain("t8", "a tenant the visitor may not address is not named, counted or hinted at");
    }

    [PostgresFact]
    public async Task When_the_visitors_tenants_cannot_all_be_listed_nothing_is_read_across_tenants_and_a_selected_tenant_is_searched()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "truncated"], Token);
        string database = owned[0];
        Guid shared = Guid.NewGuid();

        // More tenants than the header lists (200): the list is not the whole truth, so "all of mine" cannot be said.
        await SeedCrowdAsync(database, shared, 205, ["t7"]);

        await using TenancyStudio studio = TenancyStudio.Start(
            options => ConfigureCrowd(options, database),
            extra: static services => services.AddAuthorization(static options => options.AddPolicy(
                "OnlySeven",
                static policy => policy.RequireAssertion(static context => context.Resource is MartenStoreResource { TenantId: null or "t7" }))),
            studio: static options => options.StoreAuthorizationPolicy = "OnlySeven");
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();
        FindService find = circuit.ServiceProvider.GetRequiredService<FindService>();

        FindResult across = await find.FindAsync(Whole, shared.ToString(), Token);
        across.Found.Should().BeFalse();
        across.Notes.Should().Contain(FindTenants.SelectionNeededNote);

        FindResult selected = await find.FindAsync(Whole with { TenantId = "t7" }, shared.ToString(), Token);
        selected.Hits.Select(static x => x.Kind).Should().Equal("Document", "Stream");
        selected.Notes.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Without_a_policy_the_probe_is_not_narrowed_and_says_or_more_when_its_rows_ran_out()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, [Prefix + "nopolicy"], Token);
        string database = owned[0];
        Guid shared = Guid.NewGuid();
        await SeedCrowdAsync(database, shared, 25, [.. Enumerable.Range(1, 25).Select(static i => "t" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

        await using TenancyStudio studio = TenancyStudio.Start(options => ConfigureCrowd(options, database));
        await using AsyncServiceScope circuit = studio.Services.CreateAsyncScope();

        FindResult found = await circuit.ServiceProvider.GetRequiredService<FindService>().FindAsync(Whole, shared.ToString(), Token);

        FindHit[] documents = [.. found.Hits.Where(static x => x.Kind == "Document")];
        documents.Should().HaveCount(5);
        documents[^1].Detail.Should().Be("and 16 more tenants you may see (or more)");
    }

    private static Task<ServerSnapshot> SnapshotAsync(OwnedDatabases owned) =>
        ServerSnapshot.ReadAsync(owned.ConnectionStrings, Prefix, lookupTable: null, Token);
}

/// <summary>A second store an application registered. At namespace level, as Marten generates its implementation from it.</summary>
public interface IFindLiveSecondStore : IDocumentStore;

/// <summary>A document Find can look for.</summary>
[DocumentAlias("find_note")]
public class FindNote
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Something to read back.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>A document the host hides, so Find must never answer for it.</summary>
[DocumentAlias("find_hidden")]
public class FindHiddenNote
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }
}

/// <summary>A string-keyed document with a subclass.</summary>
[DocumentAlias("find_order")]
public class FindOrder
{
    /// <summary>The identity.</summary>
    public string Id { get; set; } = string.Empty;
}

/// <summary>The subclass whose name Find reads from <c>mt_doc_type</c>.</summary>
public class FindPremiumOrder : FindOrder;
