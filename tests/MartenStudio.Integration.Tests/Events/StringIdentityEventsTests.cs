using JasperFx;
using JasperFx.Events;

using Marten;

using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// An event store whose streams are keyed by <c>string</c> rather than by <c>Guid</c>, with the studio's
/// services on top of it.
/// </summary>
/// <remarks>
/// <para>
/// <c>StreamIdentity.AsString</c> changes the column type of <c>mt_streams.id</c> and
/// <c>mt_events.stream_id</c> from <c>uuid</c> to <c>varchar</c>, and every parameter the studio binds
/// against them with it - <c>EventTableInfo.StreamIdDbType</c>, <c>ParseStreamId</c>, the keyset cursor's
/// id half, and the "a filter for an id this store cannot hold matches nothing" branch. All of that had
/// unit coverage and no live coverage at all, which is how a <c>42804 datatype_mismatch</c> ships.
/// </para>
/// <para>
/// Stream keys here deliberately contain characters a GUID never does - a slash and a colon - because a
/// Marten stream key routinely is <c>tenant/order-17</c>, and D9 puts stream ids in the query string for
/// exactly that reason.
/// </para>
/// </remarks>
internal sealed class StringIdentityEventsFixture : IAsyncDisposable
{
    /// <summary>A stream key with the punctuation a real one has.</summary>
    public const string StreamOne = "orders/2026/order-17";

    /// <summary>A second, so a filter can be proved to exclude something.</summary>
    public const string StreamTwo = "orders/2026/order-18";

    /// <summary>The one the archive test takes, so nothing else asserts about it.</summary>
    public const string StreamToArchive = "orders/2026/order-19";

    private readonly ServiceProvider provider;
    private readonly IServiceScope serviceScope;

    private StringIdentityEventsFixture(DocumentStore store, string schema, ServiceProvider provider)
    {
        Store = store;
        Schema = schema;
        this.provider = provider;
        serviceScope = provider.CreateScope();
    }

    /// <summary>The store.</summary>
    public DocumentStore Store { get; }

    /// <summary>The schema everything lives in.</summary>
    public string Schema { get; }

    /// <summary>The scope every call is made in.</summary>
    public StudioScope Scope { get; } = new("default", string.Empty, null);

    /// <summary>The data service with every capability enabled.</summary>
    public IEventDataService Service => serviceScope.ServiceProvider.GetRequiredService<IEventDataService>();

    /// <summary>Builds the store, applies its schema and seeds three string-keyed streams.</summary>
    /// <param name="connectionString">The container's connection string.</param>
    /// <param name="schema">The schema this class owns.</param>
    public static async Task<StringIdentityEventsFixture> CreateAsync(string connectionString, string schema)
    {
        DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(connectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            options.Events.StreamIdentity = StreamIdentity.AsString;

            options.Events.AddEventType<OrderPlaced>();
            options.Events.AddEventType<ItemAdded>();
            options.Events.AddEventType<OrderShipped>();

            // A string-keyed aggregate for the same store, so the time-travel picker has a candidate whose
            // identity type matches. A Guid-keyed one would be dropped - and Marten would refuse the store.
            options.Projections.Add(
                new KeyedOrderSummaryProjection(), JasperFx.Events.Projections.ProjectionLifecycle.Inline);
        });

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var fixture = new StringIdentityEventsFixture(store, schema, BuildProvider(store));

        await fixture.SeedAsync();

        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        serviceScope.Dispose();
        await provider.DisposeAsync();
        await Store.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        await using IDocumentSession session = Store.LightweightSession();

        session.Events.StartStream<KeyedOrderSummary>(
            StreamOne, new OrderPlaced("first"), new ItemAdded("sku-1", 10));
        session.Events.StartStream<KeyedOrderSummary>(
            StreamTwo, new OrderPlaced("second"), new ItemAdded("sku-2", 20), new OrderShipped("track-2"));
        session.Events.StartStream<KeyedOrderSummary>(
            StreamToArchive, new OrderPlaced("third"));

        // Filler, so that the paging test still sees more than one page whatever order the class runs in -
        // one of the tests here archives a stream, and archived streams are hidden by default.
        for (int index = 20; index < 24; index++)
        {
            session.Events.StartStream<KeyedOrderSummary>(
                "orders/2026/order-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                new OrderPlaced("filler-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        await session.SaveChangesAsync();
    }

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
            options.DiscoverTenantIds = false;
        });

        return services.BuildServiceProvider();
    }
}

/// <summary>The aggregate a string-identified store's streams fold into.</summary>
public class KeyedOrderSummary
{
    /// <summary>The stream key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Who placed the order.</summary>
    public string Customer { get; set; } = string.Empty;

    /// <summary>What it comes to.</summary>
    public int Total { get; set; }
}

/// <summary>The inline single-stream projection over <see cref="KeyedOrderSummary" />.</summary>
public partial class KeyedOrderSummaryProjection
    : Marten.Events.Aggregation.SingleStreamProjection<KeyedOrderSummary, string>
{
    /// <summary>Starts the summary.</summary>
    public static KeyedOrderSummary Create(OrderPlaced placed) => new() { Customer = placed.Customer };

    /// <summary>Adds a line.</summary>
    public static void Apply(ItemAdded added, KeyedOrderSummary summary) => summary.Total += added.Price;
}

/// <summary>One string-identified store, built once for the class.</summary>
/// <param name="postgres">The assembly's Postgres, injected by xunit.</param>
public sealed class StringIdentityStoreFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The store, the seed and the studio's services over them.</summary>
    internal StringIdentityEventsFixture Events { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync("events_string_id");

        Events = await StringIdentityEventsFixture.CreateAsync(postgres.ConnectionString, "events_string_id");
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
/// The Events area against a store whose streams are keyed by string: the list, the detail, the feed
/// filter and the archive, each of which binds a stream-id parameter as <c>varchar</c> rather than
/// <c>uuid</c>.
/// </summary>
public class StringIdentityEventsTests(StringIdentityStoreFixture fixture) : IClassFixture<StringIdentityStoreFixture>
{
    private StringIdentityEventsFixture Events => fixture.Events;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_shape_reports_string_stream_identity()
    {
        EventStoreShape shape = await Events.Service.DescribeAsync(Events.Scope, Token);

        shape.Available.Should().BeTrue();
        shape.StreamIdentity.Should().Be(StreamIdentity.AsString);

        // Worth recording because it is not what you would guess: Marten creates mt_events.tenant_id on
        // every store, conjoined or not, and fills it with *DEFAULT* on a single-tenanted one. So
        // HasTenantId means "the column exists", not "this store is conjoined" - which is why the feed's
        // tenant predicate is a guarded `@tenant is null or ...` rather than a conditional clause.
        shape.HasTenantId.Should().BeTrue();
    }

    [PostgresFact]
    public async Task The_streams_list_reads_string_keys_and_pages_them()
    {
        StreamPage page = await Events.Service.ListStreamsAsync(
            Events.Scope, new StreamListRequest { PageSize = 2 }, Token);

        page.Error.Should().BeNull();
        page.Rows.Should().HaveCount(2);
        page.HasMore.Should().BeTrue();
        page.NextCursor.Should().NotBeNull("a page that has more must say where the next one starts");

        StreamPage next = await Events.Service.ListStreamsAsync(
            Events.Scope, new StreamListRequest { PageSize = 2, Cursor = page.NextCursor }, Token);

        next.Error.Should().BeNull("the keyset cursor's id half is a varchar parameter here");

        List<string> seen = [.. page.Rows.Select(x => x.Id), .. next.Rows.Select(x => x.Id)];

        seen.Should().OnlyHaveUniqueItems();
        seen.Should().Contain(StringIdentityEventsFixture.StreamOne);
    }

    /// <summary>The stream header and its timeline, by a key with a slash and a colon-free path in it.</summary>
    [PostgresFact]
    public async Task The_stream_detail_finds_a_stream_by_its_key()
    {
        StreamState state = await Events.Service.GetStreamAsync(
            Events.Scope, StringIdentityEventsFixture.StreamTwo, Token);

        state.Error.Should().BeNull();
        state.Exists.Should().BeTrue();
        state.Id.Should().Be(StringIdentityEventsFixture.StreamTwo);
        state.Version.Should().Be(3);

        EventPage timeline = await Events.Service.GetStreamEventsAsync(
            Events.Scope, StringIdentityEventsFixture.StreamTwo, afterVersion: 0, take: 50, Token);

        timeline.Error.Should().BeNull();
        timeline.Rows.Should().HaveCount(3);
        timeline.Rows.Should().OnlyContain(x => x.StreamId == StringIdentityEventsFixture.StreamTwo);
    }

    [PostgresFact]
    public async Task The_feed_can_be_filtered_to_one_string_stream()
    {
        EventPage filtered = await Events.Service.GetFeedAsync(
            Events.Scope,
            new EventFeedRequest { StreamId = StringIdentityEventsFixture.StreamOne, PageSize = 100 },
            Token);

        filtered.Error.Should().BeNull();
        filtered.Rows.Should().NotBeEmpty();
        filtered.Rows.Should().OnlyContain(x => x.StreamId == StringIdentityEventsFixture.StreamOne);

        EventPage everything = await Events.Service.GetFeedAsync(
            Events.Scope, new EventFeedRequest { PageSize = 100 }, Token);

        everything.Rows.Count.Should().BeGreaterThan(filtered.Rows.Count);
    }

    /// <summary>
    /// On a GUID-identified store an unparseable id is a filter that matches nothing. Here every string is
    /// a possible key, so the same box is an ordinary miss - and it must not throw.
    /// </summary>
    [PostgresFact]
    public async Task A_key_that_is_simply_not_there_is_an_empty_page_and_not_an_error()
    {
        EventPage page = await Events.Service.GetFeedAsync(
            Events.Scope, new EventFeedRequest { StreamId = "orders/2026/nope", PageSize = 100 }, Token);

        page.Error.Should().BeNull();
        page.Rows.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_stream_can_be_archived_by_its_string_key()
    {
        await Events.Service.ArchiveStreamAsync(
            Events.Scope, StringIdentityEventsFixture.StreamToArchive, Token);

        StreamState after = await Events.Service.GetStreamAsync(
            Events.Scope, StringIdentityEventsFixture.StreamToArchive, Token);

        after.Exists.Should().BeTrue();
        after.IsArchived.Should().BeTrue();

        StreamPage hidden = await Events.Service.ListStreamsAsync(
            Events.Scope, new StreamListRequest { PageSize = 100 }, Token);

        hidden.Rows.Should().NotContain(x => x.Id == StringIdentityEventsFixture.StreamToArchive);
    }

    /// <summary>
    /// Time travel closes <c>AggregateStreamAsync&lt;T&gt;</c> over the <em>string</em> overload here, which
    /// is a different method from the GUID one and is picked by the parsed id's runtime type.
    /// </summary>
    [PostgresFact]
    public async Task A_string_keyed_stream_can_be_replayed_into_its_aggregate()
    {
        IReadOnlyList<AggregateTypeCandidate> candidates =
            await Events.Service.ListAggregateCandidatesAsync(Events.Scope, Token);

        candidates.Should().Contain(x =>
            x.Type == typeof(KeyedOrderSummary) && x.Source == AggregateCandidateSource.SingleStreamProjection);

        AggregateSnapshot snapshot = await Events.Service.AggregateAtVersionAsync(
            Events.Scope,
            StringIdentityEventsFixture.StreamOne,
            typeof(KeyedOrderSummary).FullName!,
            version: 2,
            Token);

        snapshot.Error.Should().BeNull();
        snapshot.Found.Should().BeTrue();
        snapshot.Json.Should().Contain("first");
    }

    /// <summary>
    /// Marten answers <see langword="null" /> for an archived stream and for a version past the end alike;
    /// the service now says which, by reading the stream's own row on the miss path.
    /// </summary>
    [PostgresFact]
    public async Task A_replay_past_the_end_of_the_stream_says_where_the_stream_ends()
    {
        AggregateSnapshot snapshot = await Events.Service.AggregateAtVersionAsync(
            Events.Scope,
            StringIdentityEventsFixture.StreamTwo,
            typeof(KeyedOrderSummary).FullName!,
            version: 99,
            Token);

        snapshot.Found.Should().BeFalse();
        snapshot.Reason.Should().Be(AggregateMissingReason.VersionPastEnd);
        snapshot.StreamVersion.Should().Be(3);
    }
}
