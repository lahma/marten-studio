using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// A conjoined-tenanted event store with two tenants, and the studio's services on top of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this exists to catch.</b> <c>DeadLetterEvent</c> is registered
/// <c>Schema.For&lt;DeadLetterEvent&gt;().SingleTenanted()</c> <em>unconditionally</em> - verified in
/// Marten 9.35's <c>StoreOptions.ApplyConfiguration</c>, which does it whatever
/// <c>Events.TenancyStyle</c> is. So a conjoined store's dead-letter table has no <c>tenant_id</c> column
/// and a tenanted session applies no filter to it: the whole dead-letter surface crossed the tenant
/// boundary. So did two reads keyed by the event's <em>global</em> sequence, which is the number a dead
/// letter carries. Five places, one per test below.
/// </para>
/// <para>
/// The dead letters are written directly rather than produced by a daemon. What is under test is the
/// studio's filtering, and a daemon would add a minute of waiting to each run for a row whose shape
/// <see cref="DeadLetterLiveTests" /> already pins against the real thing - including that the daemon
/// fills <c>TenantId</c> from the failing event.
/// </para>
/// </remarks>
internal sealed class ConjoinedEventsFixture : IAsyncDisposable
{
    /// <summary>The tenant a scoped test is about.</summary>
    public const string TenantA = "acme";

    /// <summary>The tenant it must never see.</summary>
    public const string TenantB = "globex";

    private readonly ServiceProvider provider;
    private readonly IServiceScope serviceScope;

    private readonly string connectionString;

    private ConjoinedEventsFixture(DocumentStore store, string schema, string connectionString, ServiceProvider provider)
    {
        Store = store;
        Schema = schema;
        this.connectionString = connectionString;
        this.provider = provider;
        serviceScope = provider.CreateScope();
    }

    /// <summary>The store.</summary>
    public DocumentStore Store { get; }

    /// <summary>The schema everything lives in.</summary>
    public string Schema { get; }

    /// <summary>Tenant A's stream.</summary>
    public Guid StreamA { get; } = Guid.CreateVersion7();

    /// <summary>Tenant B's stream.</summary>
    public Guid StreamB { get; } = Guid.CreateVersion7();

    /// <summary>The sequence of one of tenant A's events.</summary>
    public long SequenceA { get; private set; }

    /// <summary>The sequence of one of tenant B's events - the number a scoped call must refuse.</summary>
    public long SequenceB { get; private set; }

    /// <summary>Tenant A's dead letter.</summary>
    public Guid DeadLetterA { get; } = Guid.CreateVersion7();

    /// <summary>Tenant B's dead letter.</summary>
    public Guid DeadLetterB { get; } = Guid.CreateVersion7();

    /// <summary>The data service with every capability enabled.</summary>
    public IEventDataService Service => serviceScope.ServiceProvider.GetRequiredService<IEventDataService>();

    /// <summary>The audit ring.</summary>
    public StudioActionLog Audit => serviceScope.ServiceProvider.GetRequiredService<StudioActionLog>();

    /// <summary>The scope pinned to one tenant.</summary>
    public static StudioScope ScopeFor(string? tenantId) => new("default", string.Empty, tenantId);

    /// <summary>Builds the store, applies its schema and seeds both tenants.</summary>
    /// <param name="connectionString">The container's connection string.</param>
    /// <param name="schema">The schema this class owns.</param>
    public static async Task<ConjoinedEventsFixture> CreateAsync(string connectionString, string schema)
    {
        DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(connectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // The whole point: mt_events and mt_streams get a tenant_id column.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;

            options.Events.EnableEventSkippingInProjectionsOrSubscriptions = true;

            options.Events.AddEventType<OrderPlaced>();
            options.Events.AddEventType<ItemAdded>();
            options.Events.AddEventType<OrderShipped>();

            // Marten refuses a single-tenanted aggregate over conjoined events outright
            // ("Tenancy storage style mismatch", ProjectionGraph.AssertValidity), so the aggregate is
            // conjoined too - which is what a real conjoined store looks like anyway.
            options.Schema.For<OrderSummary>().MultiTenanted();

            // Registered but never run: what the replay needs from it is Marten's aggregator for
            // OrderSummary, which only a registration provides. A daemon would add nothing here.
            options.Projections.Add(
                new OrderSummaryProjection(), JasperFx.Events.Projections.ProjectionLifecycle.Async);
        });

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var fixture = new ConjoinedEventsFixture(store, schema, connectionString, BuildProvider(store));

        await fixture.SeedAsync();

        return fixture;
    }

    /// <summary>Whether <c>mt_events.is_skipped</c> is set for one event, read straight from the table.</summary>
    public async Task<bool> IsSkippedAsync(long sequence, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"select is_skipped from {MartenStudio.Internal.Sql.SqlIdentifier.Qualify(Schema, "mt_events")} where seq_id = @seq",
            connection);

        command.Parameters.AddWithValue("seq", sequence);

        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is true;
    }

    /// <summary>Whether a dead letter row still exists, read without any tenant filter at all.</summary>
    public async Task<bool> DeadLetterExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using IQuerySession session = Store.QuerySession();

        return await session.Query<DeadLetterEvent>().AnyAsync(x => x.Id == id, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        serviceScope.Dispose();
        await provider.DisposeAsync();
        await Store.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        SequenceA = await AppendAsync(TenantA, StreamA, "acme-customer");
        SequenceB = await AppendAsync(TenantB, StreamB, "globex-customer");

        // Dead letters are single-tenanted documents, so one plain session writes both; what carries the
        // tenant is the document's own TenantId property, exactly as the daemon fills it in.
        await using IDocumentSession session = Store.LightweightSession();

        session.Store(DeadLetter(DeadLetterA, TenantA, SequenceA));
        session.Store(DeadLetter(DeadLetterB, TenantB, SequenceB));

        await session.SaveChangesAsync();
    }

    private async Task<long> AppendAsync(string tenantId, Guid streamId, string customer)
    {
        await using IDocumentSession session = Store.LightweightSession(tenantId);

        session.Events.StartStream(
            streamId,
            new OrderPlaced(customer),
            new ItemAdded("sku-" + tenantId, 15));

        await session.SaveChangesAsync();

        await using IQuerySession reader = Store.QuerySession(tenantId);

        IReadOnlyList<IEvent> events = await reader.Events.FetchStreamAsync(streamId);

        return events[^1].Sequence;
    }

    private static DeadLetterEvent DeadLetter(Guid id, string tenantId, long sequence) => new()
    {
        Id = id,
        ProjectionName = "OrderSummary",
        ShardName = "All",
        Timestamp = DateTimeOffset.UtcNow,
        ExceptionType = "InvalidOperationException",
        ExceptionMessage = "Failure to apply event #" + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        EventSequence = sequence,
        TenantId = tenantId,
    };

    private static ServiceProvider BuildProvider(IDocumentStore store)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton(store);

        services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();

            // Named rather than discovered, so the resolution is one round trip and a test that scopes to
            // a tenant is not also testing tenant discovery.
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(TenantA);
            options.KnownTenantIds.Add(TenantB);
        });

        return services.BuildServiceProvider();
    }
}

/// <summary>One conjoined store, two tenants, built once for the class.</summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class ConjoinedStoreFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The store, the seed and the studio's services over them.</summary>
    internal ConjoinedEventsFixture Events { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync("events_conjoined");

        Events = await ConjoinedEventsFixture.CreateAsync(postgres.ConnectionString, "events_conjoined");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Events is not null)
        {
            await Events.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The dead-letter surface and the two global-sequence reads stay inside the tenant the scope names
/// (P4 review finding B2).
/// </summary>
public class ConjoinedEventsTests(ConjoinedStoreFixture fixture) : IClassFixture<ConjoinedStoreFixture>
{
    private ConjoinedEventsFixture Events => fixture.Events;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The premise: the tables really do carry a tenant, so the predicates have something to bite on.</summary>
    [PostgresFact]
    public async Task The_store_reports_itself_as_tenanted()
    {
        EventStoreShape shape = await Events.Service.DescribeAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), Token);

        shape.Available.Should().BeTrue();
        shape.HasTenantId.Should().BeTrue();
        shape.TenantId.Should().Be(ConjoinedEventsFixture.TenantA);
    }

    // ------------------------------------------------------------------------------------------------
    // (1) and (2): the feed and the streams list, which were already tenant-scoped - asserted so that a
    // regression in the predicates that were right shows up beside the ones that were not.
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_feed_shows_only_the_tenant_in_scope()
    {
        EventPage scoped = await Events.Service.GetFeedAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), new EventFeedRequest { PageSize = 200 }, Token);

        scoped.Error.Should().BeNull();
        scoped.Rows.Should().NotBeEmpty();
        scoped.Rows.Should().OnlyContain(x => x.TenantId == ConjoinedEventsFixture.TenantA);

        EventPage everything = await Events.Service.GetFeedAsync(
            ConjoinedEventsFixture.ScopeFor(null), new EventFeedRequest { PageSize = 200 }, Token);

        everything.Rows.Should().Contain(x => x.TenantId == ConjoinedEventsFixture.TenantB,
            "with no tenant in scope the view is the whole store's");
    }

    /// <summary>
    /// Follow mode's delta read is the same statement as the feed's, with <c>SinceSequence</c> set - so it
    /// is tenant-scoped and page-bounded for the same reason the feed is. Worth asserting separately
    /// because the number that <em>starts</em> the delta, <c>FetchHighestEventSequenceNumber</c>, is
    /// neither: it is <c>select last_value from mt_events_sequence</c>, which is database-wide and runs
    /// ahead of what is committed. That is fine for a tripwire and would not be fine for a read.
    /// </summary>
    [PostgresFact]
    public async Task The_follow_mode_delta_read_is_tenant_scoped_and_bounded()
    {
        EventPage delta = await Events.Service.GetFeedAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA),
            new EventFeedRequest { SinceSequence = 0, PageSize = 200 },
            Token);

        delta.Error.Should().BeNull();
        delta.Rows.Should().NotBeEmpty();
        delta.Rows.Should().OnlyContain(x => x.TenantId == ConjoinedEventsFixture.TenantA);

        EventPage capped = await Events.Service.GetFeedAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA),
            new EventFeedRequest { SinceSequence = 0, PageSize = 1 },
            Token);

        capped.Rows.Count.Should().BeLessThanOrEqualTo(2, "a delta read is bounded by the page it asked for");
    }

    [PostgresFact]
    public async Task The_streams_list_shows_only_the_tenant_in_scope()
    {
        StreamPage scoped = await Events.Service.ListStreamsAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), new StreamListRequest { PageSize = 200 }, Token);

        scoped.Rows.Should().OnlyContain(x => x.TenantId == ConjoinedEventsFixture.TenantA);
        scoped.Rows.Should().Contain(x => x.Id == Events.StreamA.ToString());
        scoped.Rows.Should().NotContain(x => x.Id == Events.StreamB.ToString());
    }

    // ------------------------------------------------------------------------------------------------
    // (3) the event-type counts
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The counts are an aggregate over <c>mt_events</c>, and the builder has taken a tenant since W2 -
    /// the service simply never passed one, so a screen scoped to one tenant reported another tenant's
    /// business volume.
    /// </summary>
    [PostgresFact]
    public async Task The_event_type_counts_count_only_the_tenant_in_scope()
    {
        EventTypeList scoped = await Events.Service.ListEventTypesAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), withCounts: true, Token);
        EventTypeList everything = await Events.Service.ListEventTypesAsync(
            ConjoinedEventsFixture.ScopeFor(null), withCounts: true, Token);

        scoped.Error.Should().BeNull();

        long scopedPlaced = Count(scoped, "order_placed");
        long allPlaced = Count(everything, "order_placed");

        scopedPlaced.Should().Be(1, "acme started exactly one stream");
        allPlaced.Should().Be(2, "and the unscoped view sees both tenants'");
    }

    // ------------------------------------------------------------------------------------------------
    // (4) the dead-letter list and the discard
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_dead_letter_list_shows_only_the_tenant_in_scope()
    {
        DeadLetterPage scoped = await Events.Service.ListDeadLettersAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), new DeadLetterQuery(), Token);

        scoped.Error.Should().BeNull();
        scoped.Rows.Should().OnlyContain(x => x.TenantId == ConjoinedEventsFixture.TenantA);
        scoped.Rows.Should().Contain(x => x.Id == Events.DeadLetterA);
        scoped.Rows.Should().NotContain(x => x.Id == Events.DeadLetterB);

        DeadLetterPage everything = await Events.Service.ListDeadLettersAsync(
            ConjoinedEventsFixture.ScopeFor(null), new DeadLetterQuery(), Token);

        everything.Rows.Should().Contain(x => x.Id == Events.DeadLetterB);
    }

    /// <summary>
    /// Discarding another tenant's dead letter is refused and the row survives. The refusal is audited as
    /// a scope denial (9203) rather than as an ordinary failed action, because that is what it is.
    /// </summary>
    [PostgresFact]
    public async Task Discarding_another_tenants_dead_letter_is_refused_and_the_row_survives()
    {
        Func<Task> discard = () => Events.Service.DiscardDeadLetterAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), Events.DeadLetterB, Token);

        await discard.Should().ThrowAsync<StudioNotAuthorizedException>();

        (await Events.DeadLetterExistsAsync(Events.DeadLetterB, Token)).Should().BeTrue();

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Discard dead letter"
            && x.Target == Events.DeadLetterB.ToString()
            && !x.Succeeded
            && x.Message == "Not authorized for this store, database or tenant.");
    }

    /// <summary>Its own tenant's, on the other hand, goes.</summary>
    [PostgresFact]
    public async Task Discarding_its_own_tenants_dead_letter_works()
    {
        Guid id = Guid.CreateVersion7();

        await using (IDocumentSession session = Events.Store.LightweightSession())
        {
            session.Store(new DeadLetterEvent
            {
                Id = id,
                ProjectionName = "OrderSummary",
                ShardName = "All",
                Timestamp = DateTimeOffset.UtcNow,
                ExceptionType = "InvalidOperationException",
                ExceptionMessage = "a letter of its own, so no other test loses one",
                EventSequence = Events.SequenceA,
                TenantId = ConjoinedEventsFixture.TenantA,
            });

            await session.SaveChangesAsync(Token);
        }

        await Events.Service.DiscardDeadLetterAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), id, Token);

        (await Events.DeadLetterExistsAsync(id, Token)).Should().BeFalse();
    }

    /// <summary>
    /// An id that is not there at all is still "already gone" and not a refusal: two operators discarding
    /// the same row is routine, and calling it an authorization failure would be a lie in the audit log.
    /// </summary>
    [PostgresFact]
    public async Task Discarding_a_dead_letter_nobody_has_is_still_not_an_error()
    {
        await Events.Service.DiscardDeadLetterAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), Guid.CreateVersion7(), Token);

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Discard dead letter" && x.Succeeded && x.Message == "already gone");
    }

    // ------------------------------------------------------------------------------------------------
    // (1)/(2) again, from the other end: the read keyed by a global sequence
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The dead-letter expansion reads the offending event by its <em>global</em> sequence, which is a
    /// number that says nothing about tenancy. Without the sibling predicate it rendered another tenant's
    /// event body in full.
    /// </summary>
    [PostgresFact]
    public async Task Another_tenants_event_is_not_readable_by_its_sequence()
    {
        EventRow? own = await Events.Service.GetEventBySequenceAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), Events.SequenceA, Token);
        EventRow? other = await Events.Service.GetEventBySequenceAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), Events.SequenceB, Token);

        own.Should().NotBeNull();
        own!.TenantId.Should().Be(ConjoinedEventsFixture.TenantA);

        other.Should().BeNull("that sequence belongs to globex");

        EventRow? unscoped = await Events.Service.GetEventBySequenceAsync(
            ConjoinedEventsFixture.ScopeFor(null), Events.SequenceB, Token);

        unscoped.Should().NotBeNull("with no tenant in scope the view is the whole store's");
    }

    // ------------------------------------------------------------------------------------------------
    // (5) the skip
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>IMartenDatabase.MarkEventsAsSkipped</c> is
    /// <c>update mt_events set is_skipped = TRUE where seq_id = ANY(...)</c> with no tenant predicate of
    /// any kind (verified against Marten 9.35's <c>MartenDatabase.EventStorage</c>), so the service has to
    /// prove the sequence is in scope before it calls. It refuses, the write never happens, and the
    /// refusal is audited as a scope denial.
    /// </summary>
    [PostgresFact]
    public async Task Skipping_another_tenants_event_is_refused_and_the_flag_stays_false()
    {
        Func<Task> skip = () => Events.Service.SkipEventAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), Events.SequenceB, Token);

        await skip.Should().ThrowAsync<StudioNotAuthorizedException>();

        (await Events.IsSkippedAsync(Events.SequenceB, Token)).Should().BeFalse(
            "the update must not have run at all");

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Skip event"
            && x.Target == Events.SequenceB.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && !x.Succeeded);
    }

    /// <summary>And its own tenant's event still skips, which is the feature.</summary>
    [PostgresFact]
    public async Task Skipping_its_own_tenants_event_still_works()
    {
        await Events.Service.SkipEventAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA), Events.SequenceA, Token);

        (await Events.IsSkippedAsync(Events.SequenceA, Token)).Should().BeTrue();

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Skip event" && x.Succeeded && x.Message == "marked as skipped");
    }

    // ------------------------------------------------------------------------------------------------
    // The replay, which cannot be "all tenants" at all
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A Marten session always has exactly one tenant, and <c>EventStatement</c> filters a conjoined
    /// store's stream fetch on it - so an "all tenants" replay reads no events and used to look exactly
    /// like an aggregate that refused to build. It now says which of the two it is.
    /// </summary>
    [PostgresFact]
    public async Task Replaying_with_no_tenant_in_scope_says_a_tenant_is_needed_rather_than_nothing_happened()
    {
        AggregateSnapshot unscoped = await Events.Service.AggregateAtVersionAsync(
            ConjoinedEventsFixture.ScopeFor(null),
            Events.StreamA.ToString(),
            typeof(OrderSummary).FullName!,
            version: 2,
            Token);

        unscoped.Found.Should().BeFalse();
        unscoped.Reason.Should().Be(AggregateMissingReason.TenantRequired);

        AggregateSnapshot scoped = await Events.Service.AggregateAtVersionAsync(
            ConjoinedEventsFixture.ScopeFor(ConjoinedEventsFixture.TenantA),
            Events.StreamA.ToString(),
            typeof(OrderSummary).FullName!,
            version: 2,
            Token);

        scoped.Error.Should().BeNull();
        scoped.Found.Should().BeTrue();
        scoped.Json.Should().Contain("acme-customer");
    }

    private static long Count(EventTypeList list, string name)
    {
        foreach (EventTypeInfo type in list.Types)
        {
            if (string.Equals(type.Name, name, StringComparison.Ordinal))
            {
                return type.Count ?? -1;
            }
        }

        return -1;
    }
}
