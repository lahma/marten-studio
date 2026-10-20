using JasperFx;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>The event that opens a ticket and sets its natural key.</summary>
/// <param name="TicketId">The stream.</param>
/// <param name="Number">The number people quote.</param>
public sealed record LiveTicketOpened(Guid TicketId, string Number);

/// <summary>A conjoined, inline-snapshotted aggregate with a natural key - Marten's own sample shape.</summary>
public sealed class LiveTicket
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

    /// <summary>The natural key.</summary>
    [NaturalKey]
    public string Number { get; set; } = string.Empty;

    /// <summary>Sets the key.</summary>
    [NaturalKeySource]
    public void Apply(LiveTicketOpened opened)
    {
        Id = opened.TicketId;
        Number = opened.Number;
    }
}

/// <summary>A conjoined store whose tickets can be found by number, in two tenants.</summary>
public sealed class NaturalKeyFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The tenant a scoped lookup is about.</summary>
    public const string TenantA = "acme";

    /// <summary>The tenant that must not see tenant A's tickets.</summary>
    public const string TenantB = "globex";

    private readonly List<ServiceProvider> providers = [];

    /// <summary>The store.</summary>
    public DocumentStore Store { get; private set; } = null!;

    /// <summary>
    /// A second conjoined store whose lookup table has had its <c>tenant_id</c> dropped - a table created before
    /// the store went conjoined, or by hand - beside an <c>mt_streams</c> that still carries the tenant.
    /// </summary>
    public DocumentStore Legacy { get; private set; } = null!;

    /// <summary>T-LEGACY in acme, in the legacy store.</summary>
    public Guid AcmeLegacy { get; } = Guid.CreateVersion7();

    /// <summary>The studio over the legacy store.</summary>
    internal IEventDataService LegacyEvents { get; private set; } = null!;

    /// <summary>The studio over the main store, with <c>LiveTicket</c> hidden by <c>IsDocumentTypeVisible</c>.</summary>
    internal IEventDataService HiddenEvents { get; private set; } = null!;

    /// <summary>T-100 in acme.</summary>
    public Guid AcmeShared { get; } = Guid.CreateVersion7();

    /// <summary>T-100 in globex - the same number, another tenant's stream.</summary>
    public Guid GlobexShared { get; } = Guid.CreateVersion7();

    /// <summary>T-ACME, which only acme has.</summary>
    public Guid AcmeOnly { get; } = Guid.CreateVersion7();

    /// <summary>T-OLD in acme, archived.</summary>
    public Guid AcmeArchived { get; } = Guid.CreateVersion7();

    /// <summary>The studio's event service over the store.</summary>
    internal IEventDataService Events { get; private set; } = null!;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        Store = await BuildStoreAsync("events_natural_keys");

        await OpenAsync(Store, TenantA, AcmeShared, "T-100");
        await OpenAsync(Store, TenantB, GlobexShared, "T-100");
        await OpenAsync(Store, TenantA, AcmeOnly, "T-ACME");
        await OpenAsync(Store, TenantA, AcmeArchived, "T-OLD");

        await using (IDocumentSession session = Store.LightweightSession(TenantA))
        {
            session.Events.ArchiveStream(AcmeArchived);
            await session.SaveChangesAsync();
        }

        Events = Studio(Store, isVisible: null);
        HiddenEvents = Studio(Store, isVisible: static type => type != typeof(LiveTicket));

        // The legacy shape: the lookup row is written while the table still has tenant_id, then the column is
        // dropped (and with it the primary key and the foreign key that named it), leaving mt_streams the only
        // place the stream's tenant is recorded.
        const string legacySchema = "events_natural_keys_legacy";
        Legacy = await BuildStoreAsync(legacySchema);
        await OpenAsync(Legacy, TenantA, AcmeLegacy, "T-LEGACY");

        await using (var connection = new NpgsqlConnection(postgres.ConnectionString))
        {
            await connection.OpenAsync();

            await using var drop = new NpgsqlCommand(
                "alter table " + SqlIdentifier.Qualify(legacySchema, "mt_natural_key_liveticket") + " drop column tenant_id cascade",
                connection);

            await drop.ExecuteNonQueryAsync();
        }

        LegacyEvents = Studio(Legacy, isVisible: null);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider provider in providers)
        {
            await provider.DisposeAsync();
        }

        if (Store is not null)
        {
            await Store.DisposeAsync();
        }

        if (Legacy is not null)
        {
            await Legacy.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private async Task<DocumentStore> BuildStoreAsync(string schema)
    {
        await postgres.CreateSchemaAsync(schema);

        DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Schema.For<LiveTicket>().MultiTenanted();
            options.Projections.Snapshot<LiveTicket>(SnapshotLifecycle.Inline);
        });

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        return store;
    }

    private IEventDataService Studio(IDocumentStore store, Func<Type, bool>? isVisible)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton(store);
        services.AddMartenStudio(options =>
        {
            options.IsDocumentTypeVisible = isVisible;
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(TenantA);
            options.KnownTenantIds.Add(TenantB);
        });

        ServiceProvider provider = services.BuildServiceProvider();
        providers.Add(provider);

        return provider.CreateScope().ServiceProvider.GetRequiredService<IEventDataService>();
    }

    private static async Task OpenAsync(DocumentStore store, string tenantId, Guid id, string number)
    {
        await using IDocumentSession session = store.LightweightSession(tenantId);
        session.Events.StartStream<LiveTicket>(id, new LiveTicketOpened(id, number));
        await session.SaveChangesAsync();
    }
}

/// <summary>
/// F5 against a real Postgres (acceptance criterion 3): the Streams box finds a stream by its natural key, in
/// the scope's tenant only, and honours "include archived".
/// </summary>
public class NaturalKeyLiveTests(NaturalKeyFixture fixture) : IClassFixture<NaturalKeyFixture>
{
    private static readonly StudioScope Everyone = new("default", string.Empty, null);

    private static readonly StudioScope Acme = Everyone with { TenantId = NaturalKeyFixture.TenantA };

    private static readonly StudioScope Globex = Everyone with { TenantId = NaturalKeyFixture.TenantB };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_store_says_it_has_a_natural_key_to_look_streams_up_by()
    {
        EventStoreShape shape = await fixture.Events.DescribeAsync(Acme, Token);

        shape.NaturalKeys.Should().Equal("LiveTicket.Number");
    }

    [PostgresFact]
    public async Task A_lookup_by_natural_key_returns_the_stream()
    {
        NaturalKeyLookup found = await fixture.Events.FindStreamsByNaturalKeyAsync(Acme, "T-ACME", includeArchived: false, Token);

        found.Error.Should().BeNull();
        found.Searched.Should().Equal("LiveTicket.Number");
        NaturalKeyMatch match = found.Matches.Should().ContainSingle().Subject;
        match.Key.Should().Be("LiveTicket.Number");
        match.Stream.Id.Should().Be(fixture.AcmeOnly.ToString());
        match.Stream.TenantId.Should().Be(NaturalKeyFixture.TenantA);
        match.Stream.AggregateType.Should().Be("live_ticket");
    }

    [PostgresFact]
    public async Task A_natural_key_another_tenant_holds_returns_nothing()
    {
        NaturalKeyLookup found = await fixture.Events.FindStreamsByNaturalKeyAsync(Globex, "T-ACME", includeArchived: true, Token);

        found.Error.Should().BeNull();
        found.Searched.Should().NotBeEmpty("the key was looked up");
        found.Matches.Should().BeEmpty("T-ACME is acme's, and globex's scope finds none of acme's streams");
    }

    [PostgresFact]
    public async Task The_same_key_in_two_tenants_is_each_tenants_own_stream()
    {
        NaturalKeyLookup acme = await fixture.Events.FindStreamsByNaturalKeyAsync(Acme, "T-100", includeArchived: false, Token);
        NaturalKeyLookup globex = await fixture.Events.FindStreamsByNaturalKeyAsync(Globex, "T-100", includeArchived: false, Token);
        NaturalKeyLookup everyone = await fixture.Events.FindStreamsByNaturalKeyAsync(Everyone, "T-100", includeArchived: false, Token);

        acme.Matches.Should().ContainSingle().Which.Stream.Id.Should().Be(fixture.AcmeShared.ToString());
        globex.Matches.Should().ContainSingle().Which.Stream.Id.Should().Be(fixture.GlobexShared.ToString());
        everyone.Matches.Select(static x => x.Stream.Id)
            .Should().BeEquivalentTo(new[] { fixture.AcmeShared.ToString(), fixture.GlobexShared.ToString() });
    }

    /// <summary>
    /// Archival is read off <c>mt_streams</c>: Marten never copies it onto the lookup row (#5344), so a lookup
    /// that trusted <c>nk.is_archived</c> would find an archived stream as live.
    /// </summary>
    [PostgresFact]
    public async Task An_archived_stream_is_found_only_when_archived_streams_are_asked_for()
    {
        NaturalKeyLookup live = await fixture.Events.FindStreamsByNaturalKeyAsync(Acme, "T-OLD", includeArchived: false, Token);
        NaturalKeyLookup all = await fixture.Events.FindStreamsByNaturalKeyAsync(Acme, "T-OLD", includeArchived: true, Token);

        live.Matches.Should().BeEmpty();
        all.Matches.Should().ContainSingle().Which.Stream.IsArchived.Should().BeTrue();
    }

    /// <summary>
    /// EV-fix 5: with no <c>tenant_id</c> on the lookup table, the stream's own tenant on <c>mt_streams</c> still
    /// decides - acme's ticket is acme's, and globex's scope finds nothing.
    /// </summary>
    [PostgresFact]
    public async Task A_lookup_table_without_a_tenant_column_still_finds_nothing_for_another_tenant()
    {
        NaturalKeyLookup acme = await fixture.LegacyEvents.FindStreamsByNaturalKeyAsync(Acme, "T-LEGACY", includeArchived: true, Token);
        NaturalKeyLookup globex = await fixture.LegacyEvents.FindStreamsByNaturalKeyAsync(Globex, "T-LEGACY", includeArchived: true, Token);

        acme.Error.Should().BeNull();
        acme.Matches.Should().ContainSingle().Which.Stream.Id.Should().Be(fixture.AcmeLegacy.ToString());

        globex.Error.Should().BeNull();
        globex.Searched.Should().NotBeEmpty("the key was looked up");
        globex.Matches.Should().BeEmpty("the stream is acme's on mt_streams, whatever the lookup table no longer says");
    }

    /// <summary>EV-fix 7: a hidden aggregate's natural key is not offered, not named and not looked up.</summary>
    [PostgresFact]
    public async Task A_hidden_aggregates_natural_key_is_neither_offered_nor_looked_up()
    {
        (await fixture.HiddenEvents.DescribeAsync(Acme, Token)).NaturalKeys.Should().BeEmpty();

        NaturalKeyLookup found = await fixture.HiddenEvents.FindStreamsByNaturalKeyAsync(Acme, "T-ACME", includeArchived: true, Token);

        found.Searchable.Should().BeFalse();
        found.Matches.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task A_value_no_stream_has_finds_nothing_and_says_what_was_searched()
    {
        NaturalKeyLookup found = await fixture.Events.FindStreamsByNaturalKeyAsync(Acme, "T-404", includeArchived: true, Token);

        found.Matches.Should().BeEmpty();
        found.Searched.Should().Equal("LiveTicket.Number");
    }
}
