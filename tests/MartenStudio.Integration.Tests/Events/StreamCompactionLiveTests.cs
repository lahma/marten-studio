using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

// JasperFx.Events has a StreamState of its own; the studio's is the page's record.
using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>A strong-typed id the compaction store tags one event type with, to prove the tag-table refusal.</summary>
/// <param name="Value">The customer.</param>
public sealed record CompactionCustomer(string Value);

/// <summary>An event that carries a DCB tag; every other event in this store carries none.</summary>
/// <param name="Customer">Who it is about.</param>
public sealed record CompactionTagged(string Customer);

/// <summary>
/// Two stores for F3: a single-tenanted one with one DCB tag type, and a conjoined one - each with the studio
/// on top twice, once with every capability and once with none.
/// </summary>
public sealed class StreamCompactionFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The tenant a scoped test compacts in.</summary>
    public const string TenantA = "acme";

    /// <summary>The tenant whose stream of the same id must never move.</summary>
    public const string TenantB = "globex";

    private readonly List<ServiceProvider> providers = [];

    /// <summary>The single-tenanted store.</summary>
    public DocumentStore Plain { get; private set; } = null!;

    /// <summary>The conjoined store.</summary>
    public DocumentStore Conjoined { get; private set; } = null!;

    /// <summary>
    /// A conjoined store with <c>UseTenantPartitionedEvents</c>: every tenant draws <c>seq_id</c> from a sequence
    /// of its own, so the same stream id in two tenants also has the same sequence numbers - the case Marten's
    /// delete and replace tenant filters (#5234) exist for, which plain conjoined events never reach.
    /// </summary>
    public DocumentStore Partitioned { get; private set; } = null!;

    /// <summary>
    /// A single-tenanted store with <c>EnableEventSkippingInProjectionsOrSubscriptions</c>: its stream fetch adds
    /// <c>is_skipped = FALSE</c>, so Marten compacts through the last event it was not told to skip (B1).
    /// </summary>
    public DocumentStore Skipping { get; private set; } = null!;

    /// <summary>
    /// A single-tenanted store on a multi-host data source whose <c>ReadSessionPreference</c> is a strict
    /// <c>Standby</c>, over the one server this suite has, which is a primary (B2). Every
    /// <c>ConnectionUsage.Read</c> connection fails to open - "no suitable host" - and every
    /// <c>ConnectionUsage.ReadWrite</c> one lands on the primary, so whatever succeeds here read the primary.
    /// </summary>
    public DocumentStore StandbyReads { get; private set; } = null!;

    private NpgsqlMultiHostDataSource? standbyReadsSource;

    /// <summary>The connection string, for the tests' own reads of the tables.</summary>
    public string ConnectionString => postgres.ConnectionString;

    /// <summary>The plain store's schema.</summary>
    public string PlainSchema { get; } = "events_compaction";

    /// <summary>The conjoined store's schema.</summary>
    public string ConjoinedSchema { get; } = "events_compaction_conjoined";

    /// <summary>The partitioned store's schema.</summary>
    public string PartitionedSchema { get; } = "events_compaction_partitioned";

    /// <summary>The skipping store's schema.</summary>
    public string SkippingSchema { get; } = "events_compaction_skipping";

    /// <summary>The standby-reads store's schema.</summary>
    public string StandbyReadsSchema { get; } = "events_compaction_standby_reads";

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(PlainSchema);
        await postgres.CreateSchemaAsync(ConjoinedSchema);
        await postgres.CreateSchemaAsync(PartitionedSchema);
        await postgres.CreateSchemaAsync(SkippingSchema);
        await postgres.CreateSchemaAsync(StandbyReadsSchema);

        Plain = DocumentStore.For(options =>
        {
            Configure(options, PlainSchema);

            // One tag type and one rule, on one event type only: every other stream here is untagged.
            options.Events.RegisterTagType<CompactionCustomer>("compaction_customer");
            options.Events.TagWith<CompactionTagged>(static e => new CompactionCustomer(e.Customer));
        });

        Conjoined = DocumentStore.For(options =>
        {
            Configure(options, ConjoinedSchema);
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<OrderSummary>().MultiTenanted();
        });

        Partitioned = DocumentStore.For(options =>
        {
            Configure(options, PartitionedSchema);

            // What UseTenantPartitionedEvents requires (TenantPartitionedEventsLiveTests): conjoined events, a
            // quick append mode - the per-tenant sequence pick lives in the quick append function - and
            // conjoined documents for the conjoined projection.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Policies.AllDocumentsAreMultiTenanted();
        });

        Skipping = DocumentStore.For(options =>
        {
            Configure(options, SkippingSchema);
            options.Events.EnableEventSkippingInProjectionsOrSubscriptions = true;
        });

        // Marten's own multi-host test harness shape (MultiHostConfigurationContext): reads on a standby, writes
        // on the primary - with one host, and no standby at all.
        standbyReadsSource = new NpgsqlDataSourceBuilder(postgres.ConnectionString).BuildMultiHost();
        StandbyReads = DocumentStore.For(options =>
        {
            Configure(options, StandbyReadsSchema, standbyReadsSource);
            options.Advanced.MultiHostSettings.ReadSessionPreference = TargetSessionAttributes.Standby;
            options.Advanced.MultiHostSettings.WriteSessionPreference = TargetSessionAttributes.Primary;
        });

        await Plain.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await Conjoined.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await Partitioned.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await Skipping.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await StandbyReads.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        // Under per-tenant partitioning an append for an unregistered tenant is refused: no partition, no sequence.
        await Partitioned.Advanced.AddMartenManagedTenantsAsync(CancellationToken.None, TenantA, TenantB);
    }

    /// <summary>The studio's services over a store, with every capability or with none.</summary>
    /// <param name="store">The store.</param>
    /// <param name="capabilities">Every capability, or none.</param>
    /// <param name="isVisible">The host's <c>IsDocumentTypeVisible</c>, when a test sets one.</param>
    public IServiceProvider Studio(IDocumentStore store, bool capabilities, Func<Type, bool>? isVisible = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton(store);

        services.AddMartenStudio(options =>
        {
            options.Capabilities = capabilities ? MartenStudioCapabilities.All() : new MartenStudioCapabilities();
            options.IsDocumentTypeVisible = isVisible;
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(TenantA);
            options.KnownTenantIds.Add(TenantB);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        providers.Add(provider);
        return provider.CreateScope().ServiceProvider;
    }

    /// <summary>Starts an <c>OrderSummary</c> stream - typed, so <c>mt_streams.type</c> names the aggregate.</summary>
    public static async Task<Guid> StartAsync(IDocumentStore store, string? tenantId, Guid? id = null, params object[] events)
    {
        Guid streamId = id ?? Guid.CreateVersion7();

        await using IDocumentSession session = tenantId is null ? store.LightweightSession() : store.LightweightSession(tenantId);

        object[] appended = events.Length > 0
            ? events
            : [new OrderPlaced("customer"), new ItemAdded("sku-1", 10), new ItemAdded("sku-2", 5)];

        session.Events.StartStream<OrderSummary>(streamId, appended);

        await session.SaveChangesAsync();

        return streamId;
    }

    /// <summary>One stream's rows in <c>mt_events</c>, read straight from the table: (seq_id, version, type).</summary>
    public async Task<IReadOnlyList<(long Sequence, long Version, string Type)>> EventsAsync(
        string schema,
        Guid streamId,
        string? tenantId = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        string sql = "select seq_id, version, type from " + SqlIdentifier.Qualify(schema, "mt_events")
            + " where stream_id = @id" + (tenantId is null ? string.Empty : " and tenant_id = @tenant")
            + " order by seq_id";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", streamId);
        if (tenantId is not null)
        {
            command.Parameters.AddWithValue("tenant", tenantId);
        }

        List<(long, long, string)> rows = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>A stream's <c>compacted_version</c>, read straight from <c>mt_streams</c>.</summary>
    public async Task<long> CompactedVersionAsync(string schema, Guid streamId, string? tenantId = null)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        string sql = "select compacted_version from " + SqlIdentifier.Qualify(schema, "mt_streams")
            + " where id = @id" + (tenantId is null ? string.Empty : " and tenant_id = @tenant");

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", streamId);
        if (tenantId is not null)
        {
            command.Parameters.AddWithValue("tenant", tenantId);
        }

        return (long) (await command.ExecuteScalarAsync())!;
    }

    /// <summary>Appends events to an existing stream, as a host would while a dialog is open.</summary>
    public static async Task AppendAsync(IDocumentStore store, Guid streamId, params object[] events)
    {
        await using IDocumentSession session = store.LightweightSession();
        session.Events.Append(streamId, events);
        await session.SaveChangesAsync();
    }

    /// <summary>How many rows <c>mt_events</c> holds in a schema - the whole log.</summary>
    public async Task<long> CountEventsAsync(string schema)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("select count(*) from " + SqlIdentifier.Qualify(schema, "mt_events"), connection);

        return (long) (await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Gives a single-tenanted store's stream the id <see cref="Guid.Empty" />, by SQL - the way legacy data, a
    /// bulk load or a DCB boundary can leave one: a new <c>mt_streams</c> row, the events moved onto it, the old
    /// row gone.
    /// </summary>
    public async Task MoveToEmptyIdAsync(string schema, Guid streamId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        string streams = SqlIdentifier.Qualify(schema, "mt_streams");
        string events = SqlIdentifier.Qualify(schema, "mt_events");

        await using var command = new NpgsqlCommand(
            $"insert into {streams} (id, type, version, timestamp, created, tenant_id, is_archived) "
            + $"select @empty, type, version, timestamp, created, tenant_id, is_archived from {streams} where id = @id; "
            + $"update {events} set stream_id = @empty where stream_id = @id; "
            + $"delete from {streams} where id = @id;",
            connection);

        command.Parameters.AddWithValue("empty", Guid.Empty);
        command.Parameters.AddWithValue("id", streamId);

        await command.ExecuteNonQueryAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider provider in providers)
        {
            await provider.DisposeAsync();
        }

        if (Plain is not null)
        {
            await Plain.DisposeAsync();
        }

        if (Conjoined is not null)
        {
            await Conjoined.DisposeAsync();
        }

        if (Partitioned is not null)
        {
            await Partitioned.DisposeAsync();
        }

        if (Skipping is not null)
        {
            await Skipping.DisposeAsync();
        }

        if (StandbyReads is not null)
        {
            await StandbyReads.DisposeAsync();
        }

        if (standbyReadsSource is not null)
        {
            await standbyReadsSource.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void Configure(StoreOptions options, string schema, NpgsqlDataSource? dataSource = null)
    {
        if (dataSource is null)
        {
            options.Connection(postgres.ConnectionString);
        }
        else
        {
            options.Connection(dataSource);
        }

        options.DatabaseSchemaName = schema;
        options.Events.DatabaseSchemaName = schema;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Events.AddEventType<OrderPlaced>();
        options.Events.AddEventType<ItemAdded>();

        // The registered single-stream aggregate a stream of OrderSummary compacts into. Registered, never
        // run: what compaction needs from it is Marten's aggregator, which only a registration provides.
        options.Projections.Add(new OrderSummaryProjection(), ProjectionLifecycle.Async);
    }
}

/// <summary>
/// F3 against a real Postgres, at whichever Marten the run resolves: the floor (9.39.0) and, with
/// <c>-p:MartenStudioMartenVersion=9.50.2</c>, the latest - the same assertions at both, because the
/// session-level compaction is the same code at both.
/// </summary>
public class StreamCompactionLiveTests(StreamCompactionFixture fixture) : IClassFixture<StreamCompactionFixture>
{
    private static readonly StudioScope Everyone = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_stream_compacts_into_one_snapshot_at_its_last_version_and_the_audit_says_so()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);
        IReadOnlyList<(long Sequence, long Version, string Type)> before = await fixture.EventsAsync(fixture.PlainSchema, id);

        CompactionPlan plan = await PlanAsync(events,Everyone, id.ToString(), Token);
        plan.IsOffered.Should().BeTrue(plan.Explanation);
        plan.EventsToFold.Should().Be(3);
        plan.TagTablesMayRefuse.Should().BeTrue("this store keeps one tag type in a tag table");

        plan.StreamTenantId.Should().BeNull("this store is single-tenanted, and its *DEFAULT* narrows nothing");
        string.Join(" ", StreamCompactionRules.WhatHappens(plan)).Should().NotContain("Only tenant").And.NotContain("*DEFAULT*");

        await events.CompactStreamAsync(Everyone, id.ToString(), plan.StreamId, plan.Version, Token);

        IReadOnlyList<(long Sequence, long Version, string Type)> after = await fixture.EventsAsync(fixture.PlainSchema, id);
        after.Should().ContainSingle("every event before the last is deleted");
        after[0].Sequence.Should().Be(before[^1].Sequence, "the snapshot is written in place of the last event");
        after[0].Version.Should().Be(3, "the stream's version does not move");
        after[0].Type.Should().ContainEquivalentOf("compacted");

        (await fixture.CompactedVersionAsync(fixture.PlainSchema, id)).Should().Be(3);

        // The studio's own reads see it the same way.
        StreamState state = await events.GetStreamAsync(Everyone, id.ToString(), Token);
        state.Version.Should().Be(3);
        state.CompactedVersion.Should().Be(3);
        (await PlanAsync(events,Everyone, id.ToString(), Token)).Reason
            .Should().Be(CompactionUnavailableReason.NothingToFold);

        StudioActionLogEntry entry = Latest(studio);
        entry.Action.Should().Be(EventDataService.CompactStreamAction);
        entry.Target.Should().Be(id.ToString());
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.CompactStreams));
        entry.Message.Should().Be("compacted through version 3 into one Compacted<OrderSummary> event",
            "the outcome is read back from compacted_version, and names no tenant on a single-tenanted store");
    }

    /// <summary>
    /// Conjoined: the same stream id in two tenants. Compacting acme's moves acme's only - at the floor, where
    /// the packet expected a refusal, because every statement on the session path carries the session's tenant.
    /// </summary>
    [PostgresFact]
    public async Task A_conjoined_stream_compacts_in_its_own_tenant_and_the_other_tenants_stream_of_that_id_is_untouched()
    {
        IServiceProvider studio = fixture.Studio(fixture.Conjoined, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = Guid.CreateVersion7();
        await StreamCompactionFixture.StartAsync(fixture.Conjoined, StreamCompactionFixture.TenantA, id);
        await StreamCompactionFixture.StartAsync(fixture.Conjoined, StreamCompactionFixture.TenantB, id);

        StudioScope acme = Everyone with { TenantId = StreamCompactionFixture.TenantA };

        CompactionPlan plan = await PlanAsync(events,acme, id.ToString(), Token);
        plan.IsOffered.Should().BeTrue(plan.Explanation);
        plan.NeedsTenant.Should().BeFalse();

        await events.CompactStreamAsync(acme, id.ToString(), id.ToString(), 3, Token);

        (await fixture.EventsAsync(fixture.ConjoinedSchema, id, StreamCompactionFixture.TenantA)).Should().ContainSingle();
        (await fixture.EventsAsync(fixture.ConjoinedSchema, id, StreamCompactionFixture.TenantB)).Should().HaveCount(3,
            "globex's stream with the same id is another stream, and nothing may touch it");
        (await fixture.CompactedVersionAsync(fixture.ConjoinedSchema, id, StreamCompactionFixture.TenantB)).Should().Be(0);

        StudioActionLogEntry entry = Latest(studio);
        entry.TenantId.Should().Be(StreamCompactionFixture.TenantA, "authorized and audited as (store, db, tenant)");
        entry.Message.Should().EndWith("in tenant " + StreamCompactionFixture.TenantA);
    }

    /// <summary>
    /// EV-fix 6: under <c>UseTenantPartitionedEvents</c> the same stream id in two tenants also has the same
    /// <c>seq_id</c>s, so only Marten's delete and replace tenant filters (#5234) keep compaction in one tenant.
    /// </summary>
    [PostgresFact]
    public async Task Under_tenant_partitioned_events_a_compaction_stays_in_its_own_tenant_even_where_sequences_collide()
    {
        IServiceProvider studio = fixture.Studio(fixture.Partitioned, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = Guid.CreateVersion7();
        await StreamCompactionFixture.StartAsync(fixture.Partitioned, StreamCompactionFixture.TenantA, id);
        await StreamCompactionFixture.StartAsync(fixture.Partitioned, StreamCompactionFixture.TenantB, id);

        IReadOnlyList<(long Sequence, long Version, string Type)> acmeBefore =
            await fixture.EventsAsync(fixture.PartitionedSchema, id, StreamCompactionFixture.TenantA);
        IReadOnlyList<(long Sequence, long Version, string Type)> globexBefore =
            await fixture.EventsAsync(fixture.PartitionedSchema, id, StreamCompactionFixture.TenantB);

        acmeBefore.Select(static x => x.Sequence).Should().Equal(globexBefore.Select(static x => x.Sequence),
            "each tenant draws from a sequence of its own, so the two streams' events share their numbers - the premise");

        StudioScope acme = Everyone with { TenantId = StreamCompactionFixture.TenantA };
        CompactionPlan plan = await PlanAsync(events, acme, id.ToString(), Token);
        plan.IsOffered.Should().BeTrue(plan.Explanation);

        await events.CompactStreamAsync(acme, id.ToString(), id.ToString(), plan.Version, Token);

        (await fixture.EventsAsync(fixture.PartitionedSchema, id, StreamCompactionFixture.TenantA)).Should().ContainSingle();

        IReadOnlyList<(long Sequence, long Version, string Type)> globexAfter =
            await fixture.EventsAsync(fixture.PartitionedSchema, id, StreamCompactionFixture.TenantB);
        globexAfter.Should().Equal(globexBefore,
            "globex's events with the same seq_id are neither deleted nor rewritten as acme's snapshot");
        (await fixture.CompactedVersionAsync(fixture.PartitionedSchema, id, StreamCompactionFixture.TenantB)).Should().Be(0);
    }

    /// <summary>
    /// EV-fix 2: a stream whose id is the empty GUID is the one id Marten's stream fetch reads as "no filter".
    /// Compacting it would fold the whole log into it and delete everything else; it is refused, and the log is
    /// untouched. Replaying it is refused too, because that would fold the whole log into one aggregate.
    /// </summary>
    [PostgresFact]
    public async Task A_stream_with_the_empty_guid_is_refused_and_the_rest_of_the_log_is_untouched()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid other = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);
        Guid moved = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);
        await fixture.MoveToEmptyIdAsync(fixture.PlainSchema, moved);

        string empty = Guid.Empty.ToString();
        long before = await fixture.CountEventsAsync(fixture.PlainSchema);

        CompactionPlan plan = await PlanAsync(events, Everyone, empty, Token);
        plan.IsOffered.Should().BeFalse();
        plan.Reason.Should().Be(CompactionUnavailableReason.EmptyStreamId);

        Func<Task> compact = () => events.CompactStreamAsync(Everyone, empty, empty, 3, Token);
        await compact.Should().ThrowAsync<InvalidOperationException>().WithMessage("*every event in the log*");

        (await fixture.CountEventsAsync(fixture.PlainSchema)).Should().Be(before, "nothing anywhere in the log changed");
        (await fixture.EventsAsync(fixture.PlainSchema, Guid.Empty)).Should().HaveCount(3);
        (await fixture.EventsAsync(fixture.PlainSchema, other)).Should().HaveCount(3);
        Latest(studio).Succeeded.Should().BeFalse();

        AggregateSnapshot replay = await events.AggregateAtVersionAsync(Everyone, empty, typeof(OrderSummary).FullName!, 0, Token);
        replay.Found.Should().BeFalse();
        replay.Error!.Message.Should().Contain("every event in the log");
    }

    /// <summary>
    /// EV-fix 4: what runs is bounded by what the dialog described. An event appended while the dialog was
    /// open stays after the snapshot, and the audit reports the version that was folded, read back.
    /// </summary>
    [PostgresFact]
    public async Task An_event_appended_after_the_dialog_opened_is_not_folded()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);
        CompactionPlan plan = await PlanAsync(events, Everyone, id.ToString(), Token);
        plan.Version.Should().Be(3);

        await StreamCompactionFixture.AppendAsync(fixture.Plain, id, new ItemAdded("sku-late", 7), new ItemAdded("sku-later", 8));

        await events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), plan.Version, Token);

        IReadOnlyList<(long Sequence, long Version, string Type)> after = await fixture.EventsAsync(fixture.PlainSchema, id);
        after.Select(static x => x.Version).Should().Equal(3L, 4L, 5L);
        after[0].Type.Should().ContainEquivalentOf("compacted");
        after[1].Type.Should().Be("item_added", "the events appended since are left as they were");
        (await fixture.CompactedVersionAsync(fixture.PlainSchema, id)).Should().Be(3);

        Latest(studio).Message.Should().StartWith("compacted through version 3");
    }

    [PostgresFact]
    public async Task A_version_the_stream_is_not_at_is_refused_and_nothing_changes()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);

        Func<Task> compact = () => events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), 9, Token);

        await compact.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not at*");
        (await fixture.EventsAsync(fixture.PlainSchema, id)).Should().HaveCount(3);
        Latest(studio).Succeeded.Should().BeFalse();
    }

    /// <summary>EV-fix 7: the studio never writes a hidden aggregate's state into an event.</summary>
    [PostgresFact]
    public async Task A_stream_whose_aggregate_the_host_hides_is_refused_and_nothing_changes()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: true, isVisible: static type => type != typeof(OrderSummary));
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);

        (await PlanAsync(events, Everyone, id.ToString(), Token)).Reason.Should().Be(CompactionUnavailableReason.AggregateHidden);

        Func<Task> compact = () => events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), 3, Token);

        (await compact.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().NotContain(nameof(OrderSummary));
        (await fixture.EventsAsync(fixture.PlainSchema, id)).Should().HaveCount(3);
        Latest(studio).Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// B1. On a store that records skipped events, Marten's fetch leaves them out and its compaction folds through
    /// the last event it read - so a dialog whose version is a skipped event (the dead-letter Skip marks one, so
    /// does the daemon skipping an apply error) compacts through the version before it. That is a compaction:
    /// it is audited as one, through the version Marten folded to, and says why. A retry then truthfully finds
    /// nothing to fold.
    /// </summary>
    [PostgresFact]
    public async Task On_a_skipping_store_a_compaction_whose_last_event_is_skipped_is_audited_as_the_compaction_marten_made()
    {
        IServiceProvider studio = fixture.Studio(fixture.Skipping, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(
            fixture.Skipping, tenantId: null, id: null,
            new OrderPlaced("customer"), new ItemAdded("sku-1", 10), new ItemAdded("sku-2", 5), new ItemAdded("sku-3", 1));

        IReadOnlyList<(long Sequence, long Version, string Type)> before = await fixture.EventsAsync(fixture.SkippingSchema, id);
        before.Should().HaveCount(4);

        // What the studio's own dead-letter Skip calls.
        await fixture.Skipping.Storage.Database.MarkEventsAsSkipped([before[^1].Sequence], Token);

        CompactionPlan plan = await PlanAsync(events, Everyone, id.ToString(), Token);
        plan.IsOffered.Should().BeTrue(plan.Explanation);
        plan.Version.Should().Be(4);
        plan.SkippedEventsStay.Should().BeTrue();

        string outcome = await events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), plan.Version, Token);

        outcome.Should().Be(
            "compacted through version 3 into one Compacted<OrderSummary> event, not through version 4: version 4 is a skipped event, "
            + "which Marten's stream fetch leaves out, so it stays in the stream after the snapshot");

        IReadOnlyList<(long Sequence, long Version, string Type)> after = await fixture.EventsAsync(fixture.SkippingSchema, id);
        after.Select(static x => x.Version).Should().Equal([3L, 4L], "versions 1 and 2 are deleted; the skipped 4 is neither read nor deleted");
        after[0].Sequence.Should().Be(before[2].Sequence, "the snapshot is written in place of version 3, the last event Marten read");
        after[0].Type.Should().ContainEquivalentOf("compacted");
        after[1].Should().Be(before[3], "the skipped event is untouched");
        (await fixture.CompactedVersionAsync(fixture.SkippingSchema, id)).Should().Be(3);

        StudioActionLogEntry entry = Latest(studio);
        entry.Succeeded.Should().BeTrue("events 1 and 2 are gone and 3 was rewritten: that is a compaction");
        entry.Message.Should().Be(outcome);

        // Again: by version the stream still holds two events past its watermark, but the only one Marten's fetch
        // returns is the snapshot, so it returns having done nothing - and that is what is said and audited.
        CompactionPlan again = await PlanAsync(events, Everyone, id.ToString(), Token);
        again.IsOffered.Should().BeTrue("the plan counts by version, and cannot tell a skipped event from any other");

        Func<Task> retry = () => events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), again.Version, Token);
        (await retry.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("Nothing was compacted").And.Contain("still 3").And.Contain("skipped");

        (await fixture.EventsAsync(fixture.SkippingSchema, id)).Should().Equal(after);
        Latest(studio).Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// B2. The compaction's read before the save and its read-back after it are on the primary - a standby's
    /// row can be behind, and a stale <c>compacted_version</c> after the save reads as "nothing was compacted"
    /// while the events are gone. No replica is needed to pin it: on this store a <c>ConnectionUsage.Read</c>
    /// connection asks for a standby that does not exist and fails to open, so a compaction that succeeds and
    /// confirms itself here read both times on the primary.
    /// </summary>
    [PostgresFact]
    public async Task A_compaction_reads_the_stream_on_the_primary_before_and_after_the_save()
    {
        IServiceProvider studio = fixture.Studio(fixture.StandbyReads, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.StandbyReads, tenantId: null);

        // The premise: a page's read follows ReadSessionPreference, here a standby nobody has, and fails.
        StreamState pageRead = await events.GetStreamAsync(Everyone, id.ToString(), Token);
        pageRead.Error.Should().NotBeNull("an ordinary read is ConnectionUsage.Read, which asks this data source for a standby");

        string outcome = await events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), 3, Token);

        outcome.Should().Be("compacted through version 3 into one Compacted<OrderSummary> event",
            "the read before the save found the stream and the read-back found the watermark it moved - both on the primary");
        (await fixture.EventsAsync(fixture.StandbyReadsSchema, id)).Should().ContainSingle();
        (await fixture.CompactedVersionAsync(fixture.StandbyReadsSchema, id)).Should().Be(3);

        StudioActionLogEntry entry = Latest(studio);
        entry.Succeeded.Should().BeTrue();
        entry.Message.Should().Be(outcome);
    }

    /// <summary>
    /// Found beside EV-fix 7, and older than EV: the time-travel picker offered a hidden aggregate and replayed a
    /// stream into it, putting on Stream detail the state the Documents browser never shows. It is neither
    /// offered nor replayed now, and a name typed into the request is answered as a type the store does not have.
    /// </summary>
    [PostgresFact]
    public async Task Time_travel_neither_offers_nor_replays_an_aggregate_the_host_hides()
    {
        Guid id = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);
        string typeName = typeof(OrderSummary).FullName!;

        IEventDataService shown = fixture.Studio(fixture.Plain, capabilities: false).GetRequiredService<IEventDataService>();
        (await shown.ListAggregateCandidatesAsync(Everyone, Token)).Should().Contain(static x => x.Type == typeof(OrderSummary),
            "the premise: with nothing hidden the store's own aggregate is offered");
        (await shown.AggregateAtVersionAsync(Everyone, id.ToString(), typeName, 0, Token)).Found.Should().BeTrue();

        IEventDataService hidden = fixture
            .Studio(fixture.Plain, capabilities: false, isVisible: static type => type != typeof(OrderSummary))
            .GetRequiredService<IEventDataService>();

        (await hidden.ListAggregateCandidatesAsync(Everyone, Token)).Should().NotContain(static x => x.Type == typeof(OrderSummary));

        AggregateSnapshot replay = await hidden.AggregateAtVersionAsync(Everyone, id.ToString(), typeName, 0, Token);
        replay.Found.Should().BeFalse();
        replay.Json.Should().BeNull();
        replay.Error!.Message.Should().Contain("is not one of this store's aggregate types");
    }

    /// <summary>The page's two reads: the stream, then the plan made from it.</summary>
    private static async Task<CompactionPlan> PlanAsync(IEventDataService events, StudioScope scope, string id, CancellationToken token)
    {
        StreamState stream = await events.GetStreamAsync(scope, id, token);
        return await events.DescribeCompactionAsync(scope, stream, token);
    }

    [PostgresFact]
    public async Task On_a_conjoined_store_an_all_tenants_scope_is_refused_in_the_service_and_audited()
    {
        IServiceProvider studio = fixture.Studio(fixture.Conjoined, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.Conjoined, StreamCompactionFixture.TenantA);

        (await PlanAsync(events,Everyone, id.ToString(), Token)).NeedsTenant.Should().BeTrue();

        Func<Task> compact = () => events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), 3, Token);

        await compact.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exactly one tenant*");
        (await fixture.EventsAsync(fixture.ConjoinedSchema, id, StreamCompactionFixture.TenantA)).Should().HaveCount(3);
        Latest(studio).Succeeded.Should().BeFalse();
    }

    [PostgresFact]
    public async Task A_confirmation_that_is_not_the_stream_id_is_refused_in_the_service_and_audited()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);

        Func<Task> compact = () => events.CompactStreamAsync(Everyone, id.ToString(), "not-the-id", 3, Token);

        await compact.Should().ThrowAsync<InvalidOperationException>().WithMessage(StreamCompactionRules.ConfirmationMismatch);
        (await fixture.EventsAsync(fixture.PlainSchema, id)).Should().HaveCount(3);

        StudioActionLogEntry entry = Latest(studio);
        entry.Action.Should().Be(EventDataService.CompactStreamAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(StreamCompactionRules.ConfirmationMismatch);
    }

    [PostgresFact]
    public async Task With_the_capability_off_the_service_refuses_before_anything_and_audits_it()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: false);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);

        Func<Task> compact = () => events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), 3, Token);

        (await compact.Should().ThrowAsync<StudioCapabilityDeniedException>())
            .Which.Capability.Should().Be(StudioCapability.CompactStreams);
        (await fixture.EventsAsync(fixture.PlainSchema, id)).Should().HaveCount(3);
        Latest(studio).Succeeded.Should().BeFalse();
    }

    /// <summary>
    /// The dialog's warning, proven: a tag row references its event by a foreign key with no cascade, so the
    /// delete of a tagged event fails and Marten's one transaction rolls back everything.
    /// </summary>
    [PostgresFact]
    public async Task A_stream_whose_folded_events_carry_tags_is_refused_by_postgres_and_nothing_changes()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(
            fixture.Plain, tenantId: null, id: null,
            new OrderPlaced("customer"), new CompactionTagged("acme"), new ItemAdded("sku-1", 10));

        Func<Task> compact = () => events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), 3, Token);

        await compact.Should().ThrowAsync<InvalidOperationException>().WithMessage("*mt_event_tag_compaction_customer*");
        (await fixture.EventsAsync(fixture.PlainSchema, id)).Should().HaveCount(3, "the whole SaveChanges rolled back");
        (await fixture.CompactedVersionAsync(fixture.PlainSchema, id)).Should().Be(0);

        StudioActionLogEntry entry = Latest(studio);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Contain("Nothing was changed");
    }

    [PostgresFact]
    public async Task An_archived_stream_is_not_offered_and_is_refused_if_asked_anyway()
    {
        IServiceProvider studio = fixture.Studio(fixture.Plain, capabilities: true);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid id = await StreamCompactionFixture.StartAsync(fixture.Plain, tenantId: null);
        await events.ArchiveStreamAsync(Everyone, id.ToString(), Token);

        (await PlanAsync(events,Everyone, id.ToString(), Token)).Reason
            .Should().Be(CompactionUnavailableReason.Archived);

        Func<Task> compact = () => events.CompactStreamAsync(Everyone, id.ToString(), id.ToString(), 3, Token);
        await compact.Should().ThrowAsync<InvalidOperationException>().WithMessage("*archived*");
    }

    /// <summary>
    /// SEC1-fix3, live: under per-tenant sequences two tenants' streams of one id share their <c>seq_id</c>s, so with "All
    /// tenants" neither the feed nor that stream's timeline reads anything - each says to choose a tenant - while a
    /// tenant's scope reads its own, and an id only one tenant has reads with "All tenants" as before.
    /// </summary>
    [PostgresFact]
    public async Task Under_tenant_partitioned_events_all_tenants_reads_no_feed_and_no_shared_stream()
    {
        IServiceProvider studio = fixture.Studio(fixture.Partitioned, capabilities: false);
        IEventDataService events = studio.GetRequiredService<IEventDataService>();

        Guid shared = Guid.CreateVersion7();
        Guid alone = Guid.CreateVersion7();
        await StreamCompactionFixture.StartAsync(fixture.Partitioned, StreamCompactionFixture.TenantA, shared);
        await StreamCompactionFixture.StartAsync(fixture.Partitioned, StreamCompactionFixture.TenantB, shared);
        await StreamCompactionFixture.StartAsync(fixture.Partitioned, StreamCompactionFixture.TenantA, alone);

        MartenStudio.Services.Events.EventPage feed = await events.GetFeedAsync(Everyone, new EventFeedRequest(), Token);
        feed.Rows.Should().BeEmpty();
        feed.Error!.Message.Should().Be(StreamCompactionRules.ReadNeedsTenant("the feed"));

        MartenStudio.Services.Events.EventPage timeline = await events.GetStreamEventsAsync(Everyone, shared.ToString(), 0, 50, Token);
        timeline.Rows.Should().BeEmpty("two tenants' streams of this id would interleave by their own counters");
        timeline.Error!.Message.Should().Contain("choose a tenant");

        StudioScope acme = Everyone with { TenantId = StreamCompactionFixture.TenantA };
        (await events.GetFeedAsync(acme, new EventFeedRequest(), Token)).Rows.Should().NotBeEmpty("a tenant's own feed is read");
        (await events.GetStreamEventsAsync(acme, shared.ToString(), 0, 50, Token)).Rows.Should().HaveCount(3);
        (await events.GetStreamEventsAsync(Everyone, alone.ToString(), 0, 50, Token)).Rows
            .Should().HaveCount(3, "an id only one tenant has keeps working with All tenants");
    }

    private static StudioActionLogEntry Latest(IServiceProvider studio) =>
        studio.GetRequiredService<StudioActionLogService>().GetLatest()[0];
}
