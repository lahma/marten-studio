using System.Text.Json;

using JasperFx;
using JasperFx.Events;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using NpgsqlTypes;

// JasperFx.Events has an EventTag of its own (what Marten appends); the studio's is what a read returns.
using EventTag = MartenStudio.Services.Events.EventTag;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>The DCB tag type the live tag stores register: a strong-typed customer id.</summary>
/// <param name="Value">The customer.</param>
public sealed record LiveCustomer(string Value);

/// <summary>The tagged event: every one carries its customer as a tag, through a <c>TagWith</c> rule.</summary>
/// <param name="Customer">Who placed it.</param>
public sealed record LiveOrderPlaced(string Customer);

/// <summary>An untagged event, so a tag filter has something to leave out.</summary>
/// <param name="Sku">What was added.</param>
public sealed record LiveItemAdded(string Sku);

/// <summary>
/// Two tag-registered stores the sample domain does not have: a conjoined tag-table store with the same
/// customer values in two tenants, seeded large enough that the planner's choice is a real one, and a small
/// hstore store. The shared sample domain is not touched.
/// </summary>
public sealed class EventTagsFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The tenant a scoped test reads.</summary>
    public const string TenantA = "acme";

    /// <summary>The tenant whose tags must never appear in tenant A's scope.</summary>
    public const string TenantB = "globex";

    /// <summary>Customers per tenant, each with one stream of one tagged and one untagged event.</summary>
    public const int CustomersPerTenant = 2_000;

    private readonly List<ServiceProvider> providers = [];

    /// <summary>The conjoined tag-table store.</summary>
    public DocumentStore Tables { get; private set; } = null!;

    /// <summary>The hstore store.</summary>
    public DocumentStore HStore { get; private set; } = null!;

    /// <summary>The tag-table store's schema.</summary>
    public string TablesSchema { get; } = "events_tags";

    /// <summary>The hstore store's schema.</summary>
    public string HStoreSchema { get; } = "events_tags_hstore";

    /// <summary>The container's connection string.</summary>
    public string ConnectionString => postgres.ConnectionString;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(TablesSchema);
        await postgres.CreateSchemaAsync(HStoreSchema);

        Tables = DocumentStore.For(options =>
        {
            Configure(options, TablesSchema);
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
        });

        HStore = DocumentStore.For(options =>
        {
            Configure(options, HStoreSchema);
            options.Events.DcbStorageMode = DcbStorageMode.HStore;
        });

        await Tables.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        await HStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        // The same customer values in both tenants: a tag that crossed tenants would be visible at once.
        await SeedAsync(Tables, TenantA, CustomersPerTenant);
        await SeedAsync(Tables, TenantB, CustomersPerTenant);
        await SeedAsync(HStore, tenantId: null, 20);

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        // Statistics, so the plans below are the planner's real choice rather than a never-analysed guess.
        foreach (string table in (string[]) ["mt_events", "mt_streams", "mt_event_tag_live_customer"])
        {
            await using var analyze = new NpgsqlCommand("analyze " + SqlIdentifier.Qualify(TablesSchema, table), connection);
            await analyze.ExecuteNonQueryAsync();
        }
    }

    /// <summary>The studio over a store; a threshold below the tag table's size defers a page's tags.</summary>
    public IServiceProvider Studio(IDocumentStore store, long? exactCountThreshold = null)
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
            options.KnownTenantIds.Add(TenantA);
            options.KnownTenantIds.Add(TenantB);

            if (exactCountThreshold is { } threshold)
            {
                options.ExactCountThreshold = threshold;
            }
        });

        ServiceProvider provider = services.BuildServiceProvider();
        providers.Add(provider);
        return provider.CreateScope().ServiceProvider;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider provider in providers)
        {
            await provider.DisposeAsync();
        }

        if (Tables is not null)
        {
            await Tables.DisposeAsync();
        }

        if (HStore is not null)
        {
            await HStore.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private void Configure(StoreOptions options, string schema)
    {
        options.Connection(postgres.ConnectionString);
        options.DatabaseSchemaName = schema;
        options.Events.DatabaseSchemaName = schema;
        options.AutoCreateSchemaObjects = AutoCreate.All;
        options.Events.AddEventType<LiveOrderPlaced>();
        options.Events.AddEventType<LiveItemAdded>();
        options.Events.RegisterTagType<LiveCustomer>("live_customer");
        options.Events.TagWith<LiveOrderPlaced>(static e => new LiveCustomer(e.Customer));
    }

    private static async Task SeedAsync(DocumentStore store, string? tenantId, int customers)
    {
        await using IDocumentSession session = tenantId is null ? store.LightweightSession() : store.LightweightSession(tenantId);

        for (int index = 0; index < customers; index++)
        {
            session.Events.StartStream(
                Guid.CreateVersion7(),
                new LiveOrderPlaced("c-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new LiveItemAdded("sku-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        await session.SaveChangesAsync();
    }
}

/// <summary>
/// F4 against a real Postgres: the tag types and their estimates, the <c>tag:</c> filter and its index, tags
/// on the feed, and tags never crossing tenants.
/// </summary>
public class EventTagsLiveTests(EventTagsFixture fixture) : IClassFixture<EventTagsFixture>
{
    private static readonly StudioScope Everyone = new("default", string.Empty, null);

    private static readonly StudioScope Acme = Everyone with { TenantId = EventTagsFixture.TenantA };

    private static readonly StudioScope Globex = Everyone with { TenantId = EventTagsFixture.TenantB };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_tag_type_is_listed_with_an_estimate_and_an_index_backed_verdict()
    {
        IEventDataService events = fixture.Studio(fixture.Tables).GetRequiredService<IEventDataService>();

        EventTagTypeList list = await events.ListTagTypesAsync(Everyone, Token);

        list.Error.Should().BeNull();
        list.Storage.Should().Be(EventTagStorage.TagTables);
        EventTagTypeInfo type = list.Types.Should().ContainSingle().Subject;
        type.Table.Should().Be("mt_event_tag_live_customer");
        type.TableExists.Should().BeTrue();
        type.Rows.Value.Should().BeGreaterThan(0, "the table was analysed after seeding");
        type.FilterLevel.Should().Be(IndexVerdictLevel.Green, type.FilterReason);
        list.VersionTableExists.Should().BeTrue("Marten creates mt_dcb_tag_version once a tag type is registered");
    }

    /// <summary>Whole-table reltuples is every tenant's tags; a tenant-scoped visitor is shown none of it.</summary>
    [PostgresFact]
    public async Task A_tenant_scoped_visitor_on_a_conjoined_store_is_shown_no_whole_table_estimate()
    {
        IEventDataService events = fixture.Studio(fixture.Tables).GetRequiredService<IEventDataService>();

        EventTagTypeList list = await events.ListTagTypesAsync(Acme, Token);

        EventTagTypeInfo type = list.Types.Should().ContainSingle().Subject;
        type.Rows.Value.Should().BeNull();
        type.RowsNote.Should().Be(EventTagText.CrossTenantEstimateNote);
        list.VersionRows.Value.Should().BeNull();
    }

    [PostgresFact]
    public async Task The_tag_filter_returns_only_that_tags_events_and_only_the_scopes_tenants()
    {
        IEventDataService events = fixture.Studio(fixture.Tables).GetRequiredService<IEventDataService>();
        var request = new EventFeedRequest { Tag = EventTagFilter.Parse("tag:live_customer=c-7"), PageSize = 50 };

        EventPage acme = await events.GetFeedAsync(Acme, request, Token);
        EventPage globex = await events.GetFeedAsync(Globex, request, Token);
        EventPage everyone = await events.GetFeedAsync(Everyone, request, Token);

        acme.Error.Should().BeNull();
        acme.Rows.Should().ContainSingle().Which.TenantId.Should().Be(EventTagsFixture.TenantA);
        acme.Rows[0].EventType.Should().Be("live_order_placed");
        globex.Rows.Should().ContainSingle().Which.TenantId.Should().Be(EventTagsFixture.TenantB);
        everyone.Rows.Select(static x => x.TenantId).Should().BeEquivalentTo(new string?[] { EventTagsFixture.TenantA, EventTagsFixture.TenantB });
        acme.Rows[0].Sequence.Should().NotBe(globex.Rows[0].Sequence);
    }

    [PostgresFact]
    public async Task An_unknown_tag_type_and_an_impossible_value_are_answers_rather_than_empty_pages()
    {
        IEventDataService events = fixture.Studio(fixture.Tables).GetRequiredService<IEventDataService>();

        EventPage unknown = await events.GetFeedAsync(Acme, new EventFeedRequest { Tag = EventTagFilter.Parse("nothing=1") }, Token);

        unknown.Error!.Message.Should().Contain("no tag type called 'nothing'").And.Contain("LiveCustomer");
    }

    /// <summary>
    /// Acceptance criterion 2: the filter is index-backed - an EXPLAIN of the statement the feed runs, on
    /// analysed tables, reads the tag table through its primary key and never scans it.
    /// </summary>
    [PostgresFact]
    public async Task The_tag_filter_is_served_by_the_tag_tables_primary_key()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Token);

        var catalog = new ColumnCatalog();
        EventTableInfo table = EventTableInfo.FromColumns(
            fixture.TablesSchema,
            StreamIdentity.AsGuid,
            await catalog.GetAsync(connection, fixture.TablesSchema, EventTableInfo.EventsTable, Token),
            await catalog.GetAsync(connection, fixture.TablesSchema, EventTableInfo.StreamsTable, Token));

        await using NpgsqlCommand feed = EventQueryBuilder.BuildFeed(table, new EventFeedQuery
        {
            TenantId = EventTagsFixture.TenantA,
            Tag = new EventTagPredicate
            {
                Table = "mt_event_tag_live_customer",
                Value = "c-7",
                ValueType = NpgsqlDbType.Text,
                TableHasTenant = true,
            },
            PageSize = 50,
        });

        feed.CommandText = "explain (format json) " + feed.CommandText;
        feed.Connection = connection;

        string plan = (string) (await feed.ExecuteScalarAsync(Token))!;
        List<(string NodeType, string? Relation, string? Index)> nodes = Nodes(plan);

        // An Index Scan, an Index Only Scan, or a Bitmap Heap Scan over a Bitmap Index Scan are all the index;
        // the one shape that is not is a Seq Scan of the tag table.
        nodes.Should().NotContain(static x => x.Relation == "mt_event_tag_live_customer" && x.NodeType == "Seq Scan",
            "the filter must be answered from an index, not by reading the tag table: " + plan);

        nodes.Should().Contain(static x => x.Index != null && x.Index.StartsWith("pk_mt_event_tag_live_customer", StringComparison.Ordinal),
            "the index that serves it is the primary key, which leads with value: " + plan);
    }

    [PostgresFact]
    public async Task A_feed_page_carries_its_events_tags_when_the_tag_table_is_small()
    {
        IEventDataService events = fixture.Studio(fixture.Tables).GetRequiredService<IEventDataService>();

        EventPage page = await events.GetFeedAsync(
            Acme, new EventFeedRequest { EventTypes = ["live_order_placed"], PageSize = 5 }, Token);

        page.TagLoad.State.Should().Be(EventTagLoadState.Loaded);
        page.Rows.Should().HaveCount(5).And.OnlyContain(static x => x.Tags.Count == 1);
        page.Rows.Should().OnlyContain(static x => x.Tags[0].TagType == "live_customer");
    }

    /// <summary>
    /// Past ExactCountThreshold the page's tags are left for the visitor to ask for, because finding them reads
    /// the whole tag table - and the explicit read, when asked, brings exactly them.
    /// </summary>
    [PostgresFact]
    public async Task Past_the_threshold_a_pages_tags_are_deferred_and_read_on_request()
    {
        IEventDataService events = fixture.Studio(fixture.Tables, exactCountThreshold: 10).GetRequiredService<IEventDataService>();

        EventPage page = await events.GetFeedAsync(
            Acme, new EventFeedRequest { EventTypes = ["live_order_placed"], PageSize = 5 }, Token);

        page.TagLoad.State.Should().Be(EventTagLoadState.Deferred);
        page.TagLoad.Reason.Should().Contain("mt_event_tag_live_customer");
        page.Rows.Should().OnlyContain(static x => x.Tags.Count == 0);

        EventTagRead read = await events.GetEventTagsAsync(Acme, [.. page.Rows.Select(static x => x.Sequence)], Token);

        read.Error.Should().BeNull();
        read.Rows.Should().HaveCount(5).And.OnlyContain(static x => x.TenantId == EventTagsFixture.TenantA);
    }

    /// <summary>Acceptance criterion 2's other half: a tenant-scoped read cannot be handed another tenant's tags.</summary>
    [PostgresFact]
    public async Task Tags_never_cross_tenants_even_for_sequences_from_another_tenant()
    {
        IEventDataService events = fixture.Studio(fixture.Tables).GetRequiredService<IEventDataService>();

        EventPage globex = await events.GetFeedAsync(
            Globex, new EventFeedRequest { EventTypes = ["live_order_placed"], PageSize = 20 }, Token);

        globex.Rows.Should().NotBeEmpty();

        EventTagRead crafted = await events.GetEventTagsAsync(Acme, [.. globex.Rows.Select(static x => x.Sequence)], Token);

        crafted.Error.Should().BeNull();
        crafted.Rows.Should().BeEmpty("globex's sequences, asked about in acme's scope, name no tag acme may see");

        EventPage acme = await events.GetFeedAsync(Acme, new EventFeedRequest { PageSize = 200 }, Token);
        acme.Rows.Should().OnlyContain(static x => x.TenantId == EventTagsFixture.TenantA);
    }

    [PostgresFact]
    public async Task In_hstore_mode_tags_arrive_with_the_event_and_the_filter_uses_containment()
    {
        IEventDataService events = fixture.Studio(fixture.HStore).GetRequiredService<IEventDataService>();

        EventTagTypeList list = await events.ListTagTypesAsync(Everyone, Token);
        list.Storage.Should().Be(EventTagStorage.HStore);
        list.Types.Should().ContainSingle().Which.FilterLevel.Should().Be(IndexVerdictLevel.Green, "Marten builds idx_mt_events_tags");

        EventPage page = await events.GetFeedAsync(
            Everyone, new EventFeedRequest { Tag = EventTagFilter.Parse("tag:LiveCustomer=c-3") }, Token);

        page.Error.Should().BeNull();
        page.TagLoad.State.Should().Be(EventTagLoadState.Loaded);
        EventRow row = page.Rows.Should().ContainSingle().Subject;
        row.Tags.Should().ContainSingle().Which.Should().Be(new EventTag("live_customer", "c-3"));
        row.Metadata.Should().NotContainKey("tags", "the hstore column is tags now, not a chip of type names");
    }

    private static List<(string NodeType, string? Relation, string? Index)> Nodes(string plan)
    {
        List<(string, string?, string?)> nodes = [];

        using JsonDocument document = JsonDocument.Parse(plan);
        foreach (JsonElement root in document.RootElement.EnumerateArray())
        {
            Walk(root.GetProperty("Plan"));
        }

        return nodes;

        void Walk(JsonElement node)
        {
            nodes.Add((
                node.GetProperty("Node Type").GetString()!,
                node.TryGetProperty("Relation Name", out JsonElement relation) ? relation.GetString() : null,
                node.TryGetProperty("Index Name", out JsonElement index) ? index.GetString() : null));

            if (node.TryGetProperty("Plans", out JsonElement children))
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    Walk(child);
                }
            }
        }
    }
}
