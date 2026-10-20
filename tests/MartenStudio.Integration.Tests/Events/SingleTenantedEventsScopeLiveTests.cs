using JasperFx;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using StreamState = MartenStudio.Services.Events.StreamState;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// A store whose documents are conjoined and whose events are not - Marten's default event tenancy beside
/// <c>AllDocumentsAreMultiTenanted()</c> - with the studio over it, every capability on and two tenants known.
/// </summary>
/// <remarks>
/// The shape SEC1 M1 is about: the scope selector offers tenants, sessions carry them, and Marten writes the session's
/// tenant into <c>mt_streams.tenant_id</c> and <c>mt_events.tenant_id</c> on every append - while its own reads, its
/// archive and its compaction narrow by tenant only on a conjoined event store.
/// </remarks>
public sealed class SingleTenantedEventsFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The tenant a guessed id is tried from.</summary>
    public const string Acme = "acme";

    /// <summary>The tenant whose session appended the stream.</summary>
    public const string Globex = "globex";

    private ServiceProvider? provider;

    /// <summary>The schema, for the tests' own reads of the tables.</summary>
    public string Schema { get; } = "sec1_single_tenanted_events";

    /// <summary>The store.</summary>
    public DocumentStore Store { get; private set; } = null!;

    /// <summary>The studio's event service, in one circuit.</summary>
    internal IEventDataService Events { get; private set; } = null!;

    /// <summary>The process-wide audit ring.</summary>
    internal StudioActionLogService Audit { get; private set; } = null!;

    /// <summary>The scope a visitor allowed only <c>acme</c> has.</summary>
    internal StudioScope AcmeScope { get; } = new(MartenStoreRegistry.DefaultStoreKey, string.Empty, Acme);

    /// <summary>The stream's own tenant's scope.</summary>
    internal StudioScope GlobexScope { get; } = new(MartenStoreRegistry.DefaultStoreKey, string.Empty, Globex);

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema);

        Store = DocumentStore.For(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // A conjoined document type, so the store is one whose scope carries tenants; the events stay Marten's default,
            // Single - and so must the aggregate they fold into, which Marten refuses to mix (InvalidProjectionException).
            options.Schema.For<PoisonNote>().MultiTenanted();

            options.Events.AddEventType<OrderPlaced>();
            options.Events.AddEventType<ItemAdded>();

            // The registered single-stream aggregate both the replay and the compaction fold into. Registered, never run.
            options.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Async);
        });

        await Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddMartenStudio(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);
        });

        provider = services.BuildServiceProvider();

        IServiceProvider circuit = provider.CreateScope().ServiceProvider;
        Events = circuit.GetRequiredService<IEventDataService>();
        Audit = provider.GetRequiredService<StudioActionLogService>();

        // A second studio over the same store whose write policy allows a write only for a tenant's scope - never for
        // the database as a whole, which is what a write reaching more than one tenant's events has to be (F1).
        var narrow = new ServiceCollection();
        narrow.AddLogging();
        narrow.AddAuthorization();
        narrow.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        narrow.AddSingleton<IDocumentStore>(Store);
        narrow.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationService>(
            new MartenStudio.Integration.Tests.Database.ResourcePolicy(static x => x.Capability is null || x.TenantId is not null));
        narrow.AddMartenStudio(static options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.StoreAuthorizationPolicy = "sec1-store";
            options.WriteAuthorizationPolicy = "sec1-write";
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);
        });

        narrowProvider = narrow.BuildServiceProvider();
        NarrowEvents = narrowProvider.CreateScope().ServiceProvider.GetRequiredService<IEventDataService>();
        NarrowAudit = narrowProvider.GetRequiredService<StudioActionLogService>();
    }

    private ServiceProvider? narrowProvider;

    /// <summary>The event service of a visitor the write policy allows tenant-scoped writes only.</summary>
    internal IEventDataService NarrowEvents { get; private set; } = null!;

    /// <summary>That studio's audit ring.</summary>
    internal StudioActionLogService NarrowAudit { get; private set; } = null!;

    /// <summary>Gives a stream's last event to another tenant, as a session of that tenant appending to it would have.</summary>
    public async Task ReassignLastEventAsync(Guid id, string tenant)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        string events = SqlIdentifier.Qualify(Schema, "mt_events");
        await using var command = new NpgsqlCommand(
            $"update {events} set tenant_id = @tenant where stream_id = @id and version = (select max(version) from {events} where stream_id = @id)",
            connection);
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("id", id);

        (await command.ExecuteNonQueryAsync()).Should().Be(1, "the premise: one event moved");
    }

    /// <summary>Starts a typed <c>OrderSummary</c> stream of three events from a <c>globex</c> session.</summary>
    public Task<Guid> StartAsGlobexAsync() => StartAsync(Globex);

    /// <summary>Starts a typed <c>OrderSummary</c> stream of three events from a session of <paramref name="tenant" />.</summary>
    public async Task<Guid> StartAsync(string tenant)
    {
        Guid id = Guid.CreateVersion7();

        await using IDocumentSession session = Store.LightweightSession(tenant);
        session.Events.StartStream<OrderSummary>(id, new OrderPlaced("globex-customer"), new ItemAdded("sku-1", 10), new ItemAdded("sku-2", 5));
        await session.SaveChangesAsync();

        return id;
    }

    /// <summary>The stream's <c>mt_streams.tenant_id</c>, read straight from the table.</summary>
    public async Task<string?> StreamTenantAsync(Guid id) =>
        (string?) await ScalarAsync("select tenant_id from " + SqlIdentifier.Qualify(Schema, "mt_streams") + " where id = @id", id);

    /// <summary>Whether the stream's row is archived, read straight from the table.</summary>
    public async Task<bool> IsArchivedAsync(Guid id) =>
        (bool) (await ScalarAsync("select is_archived from " + SqlIdentifier.Qualify(Schema, "mt_streams") + " where id = @id", id))!;

    /// <summary>How many rows of <c>mt_events</c> the stream has, read straight from the table.</summary>
    public async Task<long> EventCountAsync(Guid id) =>
        (long) (await ScalarAsync("select count(*) from " + SqlIdentifier.Qualify(Schema, "mt_events") + " where stream_id = @id", id))!;

    private async Task<object?> ScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        object? value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (provider is not null)
        {
            await provider.DisposeAsync();
        }

        if (narrowProvider is not null)
        {
            await narrowProvider.DisposeAsync();
        }

        if (Store is not null)
        {
            await Store.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// SEC1 M1 and L5, live: on a store with single-tenanted events and tenanted sessions, a stream id is proved to be the
/// scope's before it is replayed, archived or compacted - and another tenant's stream gets exactly the answer a stream
/// that does not exist gets. Each test has its refused half (from <c>acme</c>) and its working half (from <c>globex</c>,
/// the stream's own tenant), at whichever Marten the run resolves.
/// </summary>
/// <remarks>
/// Before the fix the replay narrowed by tenant only on a conjoined event store and the archive was by id alone, so
/// from <c>acme</c> the replay returned <c>globex</c>'s aggregate and the archive archived <c>globex</c>'s stream. The
/// compaction was already refused - its read of the stream is narrowed - and the test pins that read as what keeps it
/// to one tenant, because Marten's own compaction on this store fetches by id and deletes by sequence with no tenant
/// predicate at all.
/// </remarks>
public class SingleTenantedEventsScopeLiveTests(SingleTenantedEventsFixture fixture) : IClassFixture<SingleTenantedEventsFixture>
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Aggregate => typeof(OrderSummary).FullName!;

    [PostgresFact]
    public async Task From_another_tenants_scope_a_replay_gets_the_answer_a_missing_stream_gets()
    {
        Guid id = await fixture.StartAsGlobexAsync();

        fixture.Store.Options.Events.TenancyStyle.Should().Be(TenancyStyle.Single, "the premise: single-tenanted events");
        (await fixture.StreamTenantAsync(id)).Should().Be(SingleTenantedEventsFixture.Globex,
            "the premise: Marten records the appending session's tenant on a single-tenanted event store too");

        StreamState header = await fixture.Events.GetStreamAsync(fixture.AcmeScope, id.ToString(), Token);
        header.Exists.Should().BeFalse("the premise: the stream page and its timeline already narrow by the scope's tenant");

        AggregateSnapshot fromAcme = await fixture.Events.AggregateAtVersionAsync(fixture.AcmeScope, id.ToString(), Aggregate, 3, Token);
        AggregateSnapshot missing = await fixture.Events.AggregateAtVersionAsync(fixture.AcmeScope, Guid.CreateVersion7().ToString(), Aggregate, 3, Token);

        fromAcme.Error.Should().BeNull(fromAcme.Error?.Display);
        fromAcme.Found.Should().BeFalse("acme may not read globex's stream, however its id was learned");
        fromAcme.Json.Should().BeNull();
        fromAcme.Reason.Should().Be(AggregateMissingReason.StreamMissing);
        fromAcme.Should().Be(missing, "word for word the answer a stream that does not exist gets");

        AggregateSnapshot fromGlobex = await fixture.Events.AggregateAtVersionAsync(fixture.GlobexScope, id.ToString(), Aggregate, 3, Token);

        fromGlobex.Error.Should().BeNull(fromGlobex.Error?.Display);
        fromGlobex.Found.Should().BeTrue("from the stream's own tenant it replays");
        fromGlobex.Json.Should().Contain("globex-customer");
    }

    [PostgresFact]
    public async Task From_another_tenants_scope_an_archive_is_refused_as_missing_and_audited()
    {
        Guid id = await fixture.StartAsGlobexAsync();

        Func<Task> fromAcme = () => fixture.Events.ArchiveStreamAsync(fixture.AcmeScope, id.ToString(), Token);
        Func<Task> missing = () => fixture.Events.ArchiveStreamAsync(fixture.AcmeScope, Guid.CreateVersion7().ToString(), Token);

        string refused = (await fromAcme.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        (await missing.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be(refused, "the answer a stream that does not exist gets");
        refused.Should().Be(EventDataService.StreamNotInScopeMessage);

        (await fixture.IsArchivedAsync(id)).Should().BeFalse("nothing was written to globex's stream");

        StudioActionLogEntry entry = fixture.Audit.GetLatest().First(x => x.Target == id.ToString());
        entry.Action.Should().Be(EventDataService.ArchiveStreamAction);
        entry.Succeeded.Should().BeFalse("refused, and audited as refused (hard rule 5)");
        entry.Message.Should().Be(EventDataService.StreamNotInScopeMessage);
        entry.TenantId.Should().Be(SingleTenantedEventsFixture.Acme);
        entry.Capability.Should().Be(nameof(StudioCapability.ArchiveStreams));

        await fixture.Events.ArchiveStreamAsync(fixture.GlobexScope, id.ToString(), Token);

        (await fixture.IsArchivedAsync(id)).Should().BeTrue("from the stream's own tenant it is archived");
        fixture.Audit.GetLatest().First(x => x.Target == id.ToString())
            .Should().Match<StudioActionLogEntry>(static x => x.Succeeded && x.TenantId == SingleTenantedEventsFixture.Globex);
    }

    /// <summary>
    /// SEC1-fix F1: an acme stream one of whose events a globex session appended. Marten archives and compacts it by id,
    /// globex's event with the rest, so from acme's scope both need the database as a whole - refused, and audited
    /// against <c>(store, database, null)</c>, for a visitor allowed acme only; allowed for one allowed the database.
    /// </summary>
    [PostgresFact]
    public async Task A_tenants_stream_holding_another_tenants_event_needs_the_whole_database_to_archive_or_compact()
    {
        Guid toArchive = await fixture.StartAsync(SingleTenantedEventsFixture.Acme);
        Guid toCompact = await fixture.StartAsync(SingleTenantedEventsFixture.Acme);
        Guid own = await fixture.StartAsync(SingleTenantedEventsFixture.Acme);
        await fixture.ReassignLastEventAsync(toArchive, SingleTenantedEventsFixture.Globex);
        await fixture.ReassignLastEventAsync(toCompact, SingleTenantedEventsFixture.Globex);

        Func<Task> archive = () => fixture.NarrowEvents.ArchiveStreamAsync(fixture.AcmeScope, toArchive.ToString(), Token);
        Func<Task> compact = () => fixture.NarrowEvents.CompactStreamAsync(fixture.AcmeScope, toCompact.ToString(), toCompact.ToString(), 3, Token);

        await archive.Should().ThrowAsync<StudioNotAuthorizedException>("the archive reaches globex's event too");
        await compact.Should().ThrowAsync<StudioNotAuthorizedException>("and so does the compaction");

        (await fixture.IsArchivedAsync(toArchive)).Should().BeFalse("nothing was written");
        (await fixture.EventCountAsync(toCompact)).Should().Be(3, "nothing was deleted");

        fixture.NarrowAudit.GetLatest().Where(x => x.Target == toArchive.ToString() || x.Target == toCompact.ToString())
            .Should().HaveCount(2).And.OnlyContain(static x => !x.Succeeded && x.TenantId == null,
                "each refusal is audited against the database as a whole, the question that was refused");

        // The control: the same visitor archives an acme stream that holds only acme's events.
        await fixture.NarrowEvents.ArchiveStreamAsync(fixture.AcmeScope, own.ToString(), Token);
        (await fixture.IsArchivedAsync(own)).Should().BeTrue();

        // And a visitor allowed the database as a whole does both, from acme's scope.
        await fixture.Events.ArchiveStreamAsync(fixture.AcmeScope, toArchive.ToString(), Token);
        await fixture.Events.CompactStreamAsync(fixture.AcmeScope, toCompact.ToString(), toCompact.ToString(), 3, Token);

        (await fixture.IsArchivedAsync(toArchive)).Should().BeTrue();
        (await fixture.EventCountAsync(toCompact)).Should().Be(1);
    }

    [PostgresFact]
    public async Task From_another_tenants_scope_a_compaction_is_refused_as_missing_and_nothing_is_deleted()
    {
        Guid id = await fixture.StartAsGlobexAsync();

        Func<Task> fromAcme = () => fixture.Events.CompactStreamAsync(fixture.AcmeScope, id.ToString(), id.ToString(), 3, Token);

        (await fromAcme.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be(EventDataService.StreamNotInScopeMessage, "the stream read narrows by the scope's tenant, and finds none");

        (await fixture.EventCountAsync(id)).Should().Be(3,
            "Marten's compaction here deletes by the sequences its fetch by id returned, with no tenant predicate - the studio's read is what kept it out");

        StudioActionLogEntry entry = fixture.Audit.GetLatest()
            .First(x => x.Action == EventDataService.CompactStreamAction && x.Target == id.ToString());
        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().Be(SingleTenantedEventsFixture.Acme);

        StreamState stream = await fixture.Events.GetStreamAsync(fixture.GlobexScope, id.ToString(), Token);
        CompactionPlan plan = await fixture.Events.DescribeCompactionAsync(fixture.GlobexScope, stream, Token);
        plan.IsOffered.Should().BeTrue(plan.Explanation);

        await fixture.Events.CompactStreamAsync(fixture.GlobexScope, id.ToString(), plan.StreamId, plan.Version, Token);

        (await fixture.EventCountAsync(id)).Should().Be(1, "from the stream's own tenant it compacts into one snapshot");
    }
}
