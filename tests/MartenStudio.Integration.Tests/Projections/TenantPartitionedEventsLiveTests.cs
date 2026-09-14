using System.Globalization;
using System.Security.Claims;

using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// The two branches a store with <c>Events.UseTenantPartitionedEvents</c> takes, against a real one.
/// </summary>
/// <remarks>
/// <para>
/// Both were written from Marten's own source and neither had ever been sent to Postgres: nothing in
/// <c>tests/</c> set the flag, so the tenant-suffix progression read and the
/// <c>coalesce(max(seq_id), 0)</c> high-water read were pinned only by the text of the command they
/// build. The suffix branch carries a tenancy-isolation claim, which is not a claim to leave to a string
/// comparison in a unit test.
/// </para>
/// <para>
/// <b>The suffix filter is a substring comparison and not a <c>like</c>.</b> Marten's #5171 fixed
/// <c>name like '%:' || tenantId</c>, where <c>_</c> is both a legal tenant-id character and a
/// single-character wildcard, so tenant <c>acme_corp</c> also swept in <c>acmexcorp</c>'s rows. The
/// studio reproduces the fixed form - <c>right(name, char_length($1)) = $1</c> - and both directions are
/// asserted here: <c>acme</c> does not match <c>acme_corp</c>'s rows, and <c>acme_corp</c> does not match
/// <c>acmexcorp</c>'s.
/// </para>
/// <para>
/// <b>The high-water branch exists because the store-global sequence is dead under partitioning.</b>
/// Every tenant draws <c>seq_id</c> from an <c>mt_events_sequence_{suffix}</c> of its own, so
/// <c>mt_events_sequence</c> is never advanced and its <c>last_value</c> reads as 1 - which is asserted
/// directly, because "the studio read the right branch" is only meaningful if the wrong branch would
/// have given a different, wrong answer.
/// </para>
/// </remarks>
public class TenantPartitionedEventsLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The tenant the assertions are made from.</summary>
    private const string Acme = "acme";

    /// <summary>A tenant whose id begins with <see cref="Acme" /> and is not it.</summary>
    private const string AcmeCorp = "acme_corp";

    /// <summary>
    /// And the tenant Marten's <c>like</c> bug used to confuse with <see cref="AcmeCorp" />: same length,
    /// same shape, <c>x</c> where the underscore is.
    /// </summary>
    private const string AcmeXCorp = "acmexcorp";

    /// <summary>How many streams each tenant gets. Different per tenant, so the numbers can disagree.</summary>
    private static readonly (string Tenant, int Streams)[] Seeded =
    [
        (Acme, 3),
        (AcmeCorp, 2),
        (AcmeXCorp, 1),
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string schema = string.Empty;
    private string eventSchema = string.Empty;
    private ServiceProvider? provider;

    private ServiceProvider Provider => provider
        ?? throw new InvalidOperationException(
            "The tenant-partitioned studio was not started. A test that needs it must be a [PostgresFact] "
            + "so that it skips instead of failing when Docker is absent.");

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        schema = GetType().Name.ToLowerInvariant();
        eventSchema = schema + "_events";

        await postgres.CreateSchemaAsync(schema, Token);
        await postgres.CreateSchemaAsync(eventSchema, Token);

        provider = BuildStudio();

        var store = Provider.GetRequiredService<IDocumentStore>();

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        // Under per-tenant partitioning an append for an unregistered tenant is refused, because there is
        // no partition and no sequence for it yet.
        await store.Advanced.AddMartenManagedTenantsAsync(Token, Acme, AcmeCorp, AcmeXCorp);

        foreach ((string tenant, int streams) in Seeded)
        {
            await SeedAsync(store, tenant, streams);
        }

        // Per-tenant progression rows, written by hand: a real one needs per-tenant daemon agents, and
        // what these tests need is a row per tenant with a known name and a known position.
        await WriteProgressionRowAsync($"DailySales:All:{Acme}", 11);
        await WriteProgressionRowAsync($"DailySales:All:{AcmeCorp}", 22);
        await WriteProgressionRowAsync($"DailySales:All:{AcmeXCorp}", 33);
        await WriteProgressionRowAsync("DailySales:All", 44);
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);

        if (provider is not null)
        {
            await provider.DisposeAsync();
        }
    }

    /// <summary>
    /// The store really is in the mode these branches are about.
    /// </summary>
    /// <remarks>
    /// The premise of both tests below, asserted rather than assumed: the flag is on, the events carry a
    /// tenant, and the store-global sequence has not moved even though there are events in the table.
    /// </remarks>
    [PostgresFact]
    public async Task The_store_global_sequence_is_dead_and_the_events_table_is_not()
    {
        var store = Provider.GetRequiredService<IDocumentStore>();

        store.Options.Events.UseTenantPartitionedEvents.Should().BeTrue();

        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        long lastValue = await ScalarAsync(connection, $"""select last_value from "{eventSchema}"."mt_events_sequence" """);
        long maximum = await ScalarAsync(connection, $"""select coalesce(max(seq_id), 0) from "{eventSchema}"."mt_events" """);

        lastValue.Should().Be(1, "every tenant draws seq_id from a partition sequence of its own");
        maximum.Should().BeGreaterThan(1, "twelve events were appended across three tenants");
    }

    /// <summary>
    /// The high-water read takes the <c>max(seq_id)</c> branch, which is the only one that answers.
    /// </summary>
    /// <remarks>
    /// Both of the studio's callers of that read are asserted - the projections view's
    /// <c>HighWaterMark</c> and the feed's follow tick - because they pass the flag independently and the
    /// answer of the other branch, 1, is a number a page would render without complaint.
    /// </remarks>
    [PostgresFact]
    public async Task The_high_water_read_takes_the_events_table_branch_and_not_the_dead_sequence()
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        long maximum = await ScalarAsync(connection, $"""select max(seq_id) from "{eventSchema}"."mt_events" """);

        await using AsyncServiceScope scope = Provider.CreateAsyncScope();

        var projections = scope.ServiceProvider.GetRequiredService<IProjectionDataService>();
        var events = scope.ServiceProvider.GetRequiredService<MartenStudio.Services.Events.IEventDataService>();

        ProjectionsView view = await projections.GetProjectionsAsync(ScopeFor(null), Token);
        long? highest = await events.GetHighestSequenceAsync(ScopeFor(null), Token);

        view.HasEventStore.Should().BeTrue();
        view.HighWaterMark.Should().Be(maximum, "the partitioned branch reads the table, not the sequence");
        highest.Should().Be(maximum);
    }

    /// <summary>
    /// A tenant-scoped read returns that tenant's progression rows, and no other tenant's.
    /// </summary>
    /// <remarks>
    /// Through <see cref="IProjectionDataService" />, so the scope resolution and the tenant filter are
    /// the product's own. The rows arrive under <c>UnregisteredShards</c> rather than <c>Progress</c>,
    /// and that is correct rather than incidental: the shard identities the view has rows for come from
    /// the store's projection registration, which is store-global (<c>DailySales:All</c>), so a
    /// <c>DailySales:All:{tenant}</c> row is a progression row the configuration does not name. The
    /// studio draws those in their own section instead of inventing a projection for them.
    /// </remarks>
    [PostgresFact]
    public async Task A_tenant_scoped_read_sees_only_that_tenants_progression_rows()
    {
        IReadOnlyList<string> acme = await UnregisteredShardsAsync(Acme);
        IReadOnlyList<string> corp = await UnregisteredShardsAsync(AcmeCorp);

        acme.Should().Equal($"DailySales:All:{Acme}");
        corp.Should().Equal($"DailySales:All:{AcmeCorp}");
    }

    /// <summary>
    /// And the filter itself, sent to Postgres: a trailing-substring comparison, with none of the
    /// <c>like</c> grammar Marten had to remove.
    /// </summary>
    /// <remarks>
    /// The studio's own read, run against the real table, in every direction that has ever been wrong.
    /// <c>acme</c> must not take <c>acme_corp</c>'s row - a plain suffix mistake - and <c>acme_corp</c>
    /// must not take <c>acmexcorp</c>'s, which is #5171's <c>_</c>-as-wildcard bug. The untenanted
    /// <c>DailySales:All</c> row must stay out of every tenant's answer, and be in the unfiltered one.
    /// </remarks>
    [PostgresFact]
    public async Task The_suffix_filter_takes_one_tenants_rows_and_never_a_lookalikes()
    {
        var catalog = Provider.GetRequiredService<ColumnCatalog>();

        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        IReadOnlyList<string> acme = await ReadNamesAsync(connection, catalog, Acme);
        IReadOnlyList<string> corp = await ReadNamesAsync(connection, catalog, AcmeCorp);
        IReadOnlyList<string> everything = await ReadNamesAsync(connection, catalog, null);

        // ':acme' is not the tail of ':acme_corp', so the shorter tenant takes only its own row.
        acme.Should().Equal($"DailySales:All:{Acme}");

        // And right(name, char_length(':acme_corp')) = ':acme_corp' is a value comparison, so the '_'
        // matches an underscore and nothing else - acmexcorp's row stays out. That is #5171.
        corp.Should().Equal($"DailySales:All:{AcmeCorp}");

        everything.Should().BeEquivalentTo(
        [
            "DailySales:All",
            $"DailySales:All:{Acme}",
            $"DailySales:All:{AcmeCorp}",
            $"DailySales:All:{AcmeXCorp}",
        ]);
    }

    /// <summary>The unregistered shard names one tenant's scope reports, in order.</summary>
    private async Task<IReadOnlyList<string>> UnregisteredShardsAsync(string tenantId)
    {
        await using AsyncServiceScope scope = Provider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        ProjectionsView view = await service.GetProjectionsAsync(ScopeFor(tenantId), Token);

        return [.. view.UnregisteredShards.Select(x => x.ShardName).Order(StringComparer.Ordinal)];
    }

    /// <summary>The progression names the studio's own read returns for one tenant filter.</summary>
    private async Task<IReadOnlyList<string>> ReadNamesAsync(
        NpgsqlConnection connection,
        ColumnCatalog catalog,
        string? tenantId)
    {
        ProgressionRows rows = await ProjectionProgressQueries.ReadProgressionRowsAsync(
            connection,
            catalog,
            eventSchema,
            extendedProgressionTracking: false,
            tenantId,
            TimeSpan.FromSeconds(30),
            Token);

        rows.TableExists.Should().BeTrue();

        return [.. rows.Rows.Select(x => x.Name).Order(StringComparer.Ordinal)];
    }

    /// <summary>This store's one database, optionally pinned to a tenant.</summary>
    private static StudioScope ScopeFor(string? tenantId) =>
        new(MartenStoreRegistry.DefaultStoreKey, string.Empty, tenantId);

    private ServiceProvider BuildStudio()
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();

        services.AddMarten(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = eventSchema;
            options.AutoCreateSchemaObjects = AutoCreate.All;

            // What UseTenantPartitionedEvents requires: conjoined event tenancy and a quick append mode
            // (StoreOptions.Validate refuses Rich, because the per-tenant sequence pick lives in
            // QuickAppendEventFunction). Document tables are deliberately left *unpartitioned* - opting
            // into per-tenant events does not imply opting into that, and Marten says so - but they do
            // have to be conjoined, or registering an async projection over conjoined events into a
            // single-tenanted document is an InvalidProjectionException before the store is built.
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Events.UseTenantPartitionedEvents = true;
            options.Events.AppendMode = EventAppendMode.QuickWithServerTimestamps;
            options.Policies.AllDocumentsAreMultiTenanted();

            options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async);
        });

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;

            // Stated rather than discovered: the scope resolver refuses a tenant the store cannot
            // vouch for, and what these tests are about is the read, not the selector.
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(AcmeCorp);
            options.KnownTenantIds.Add(AcmeXCorp);
        });

        return services.BuildServiceProvider();
    }

    private static async Task SeedAsync(IDocumentStore store, string tenantId, int streams)
    {
        await using IDocumentSession session = store.LightweightSession(tenantId);

        for (var index = 0; index < streams; index++)
        {
            Guid streamId = Guid.NewGuid();
            DateTimeOffset placedAt = new(2026, 9, 14, 9, index, 0, TimeSpan.Zero);

            session.Events.StartStream(
                streamId,
                new OrderPlaced(streamId, $"Customer {index}", placedAt),
                new ItemAdded($"SKU-{index:000}", 1, 19.90m, placedAt.AddMinutes(1)));
        }

        await session.SaveChangesAsync(Token);
    }

    private async Task WriteProgressionRowAsync(string name, long sequence)
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            $"""
             insert into "{eventSchema}"."mt_event_progression" (name, last_seq_id, last_updated)
             values (@name, @sequence, now())
             on conflict (name) do update set last_seq_id = excluded.last_seq_id
             """,
            connection);

        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("sequence", sequence);

        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        object? value = await command.ExecuteScalarAsync(Token);

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Who a container-only studio's circuit belongs to: nobody.</summary>
    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
