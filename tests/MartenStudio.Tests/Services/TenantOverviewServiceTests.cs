using Marten;
using Marten.Storage;

using MartenStudio.Services.Tenants;
using MartenStudio.Tests.Projections;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// 1.2-SCOPE-2 T2 to T9: who is listed, how they are paged, which columns a visitor below the activity gate is shown, and that nothing is
/// read for what is withheld. Every store here is built and never connected to: a read that is attempted fails against an
/// <c>.invalid</c> host and is visible as the row's <c>ReadProblem</c>, so "no statement ran" is a row with none.
/// </summary>
public class TenantOverviewServiceTests
{
    private const string FirstConnection = "Host=scope2-a.invalid;Database=one;Username=u;Password=p";

    private const string SecondConnection = "Host=scope2-b.invalid;Database=two;Username=u;Password=p";

    private const string StorePolicy = "store-policy";

    private const string WritePolicy = "write-policy";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly StudioScope DefaultScope = new("default", string.Empty, null);

    // -------------------------------------------------------------------------------------------------
    // Which tenants, and the first gate
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_store_with_nothing_conjoined_keeps_no_tenants()
    {
        await using Harness harness = Harness.Create(static options => options.Schema.For<PlainNote>());

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.KeepsNoTenants.Should().BeTrue();
        overview.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_scope_the_store_policy_refuses_is_a_value_and_reads_nothing()
    {
        await using Harness harness = Harness.Create(Conjoined, policies: true);
        harness.Policies.DenyEverything();

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.Refusal.Should().Be(TenantOverviewRefusal.StorePolicy);
        overview.Rows.Should().BeEmpty();
        harness.Policies.Calls.First().Resource.TenantId.Should().BeNull("the first question is the database as a whole, never the scope's tenant");
    }

    [Fact]
    public async Task A_store_nothing_is_registered_under_is_unavailable_not_an_exception()
    {
        await using Harness harness = Harness.Create(Conjoined);

        TenantOverview overview = await harness.Service.GetAsync(new StudioScope("ghost", string.Empty, null), new TenantOverviewRequest(), Token);

        overview.Refusal.Should().Be(TenantOverviewRefusal.Unavailable);
    }

    [Fact]
    public async Task The_tenants_are_the_ones_the_picker_would_offer_and_a_refused_one_is_neither_listed_nor_counted()
    {
        await using Harness harness = Harness.Create(Conjoined, policies: true, known: ["acme", "globex", "initech"]);
        harness.Policies.Allow(static resource => resource.TenantId != "globex");

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.Rows.Select(static x => x.TenantId).Should().Equal("acme", "initech");
        overview.UnderPolicy.Should().BeTrue("a policy filters the listing, so it is never claimed to be complete");

        harness.Policies.Calls.Select(static x => (x.Resource.TenantId, x.Resource.Capability))
            .Should().Contain(("globex", null), "each tenant is asked as the picker asks");
    }

    [Fact]
    public async Task The_tenants_are_paged_at_twenty_five_and_capped_at_two_hundred()
    {
        string[] known = [.. Enumerable.Range(0, 230).Select(static x => "t" + x.ToString("000", System.Globalization.CultureInfo.InvariantCulture))];
        await using Harness harness = Harness.Create(Conjoined, known: known);

        TenantOverview first = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest { Page = 0 }, Token);
        TenantOverview eighth = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest { Page = 7 }, Token);
        TenantOverview beyond = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest { Page = 99 }, Token);

        first.Rows.Should().HaveCount(25);
        first.Rows[0].TenantId.Should().Be("t000");
        first.HasNext.Should().BeTrue();
        first.ListTruncated.Should().BeTrue("230 tenants are listed 200 at most");

        eighth.Rows.Should().HaveCount(25, "page 8 of 8 holds tenants 175 to 199");
        eighth.Rows[^1].TenantId.Should().Be("t199");
        eighth.HasNext.Should().BeFalse();

        beyond.Page.Should().Be(7, "a page past the end is the last");
    }

    [Fact]
    public async Task A_prefix_narrows_the_list_in_memory_and_ignores_case()
    {
        await using Harness harness = Harness.Create(Conjoined, known: ["acme", "Acorn", "globex"]);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest { Filter = "ac" }, Token);

        overview.Rows.Select(static x => x.TenantId).Should().Equal("Acorn", "acme");
    }

    [Fact]
    public async Task With_no_page_asked_the_scopes_tenant_opens_the_page_that_holds_it()
    {
        string[] known = [.. Enumerable.Range(0, 60).Select(static x => "t" + x.ToString("00", System.Globalization.CultureInfo.InvariantCulture))];
        await using Harness harness = Harness.Create(Conjoined, known: known);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope with { TenantId = "t40" }, new TenantOverviewRequest(), Token);

        overview.Page.Should().Be(1);
        overview.Rows.Select(static x => x.TenantId).Should().Contain("t40");
    }

    [Fact]
    public async Task Known_ids_on_a_store_with_several_databases_are_placed_in_none()
    {
        await using Harness harness = Harness.CreateStatic(known: ["acme", "globex"]);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.IdsWithoutDatabases.Should().BeTrue();
        overview.Rows.Should().OnlyContain(static x => x.DatabaseId == string.Empty && x.Placement == TenantPlacement.NotPlaced);
        overview.Rows.Select(static x => x.TenantId).Should().Equal("acme", "globex");
    }

    [Fact]
    public async Task With_discovery_off_and_no_ids_there_is_nothing_to_list_and_it_says_so()
    {
        await using Harness harness = Harness.Create(Conjoined, discover: false);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.ListUnavailable.Should().BeTrue();
        overview.Rows.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------------------------------
    // Placement from the one enumeration
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_database_per_tenant_places_each_tenant_in_its_own_database_and_a_shared_one_in_shared_tables()
    {
        await using Harness harness = Harness.CreateStatic(known: null);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        TenantRow acme = overview.Rows.Single(static x => x.TenantId == "acme");
        acme.Placement.Should().Be(TenantPlacement.OwnDatabase);
        acme.PlacementText.Should().Be("Own database");

        overview.Rows.Where(static x => x.TenantId is "globex" or "initech").Should().OnlyContain(static x => x.Placement == TenantPlacement.SharedTables,
            "this database holds two tenants, so neither is exclusive");
        overview.Rows.Select(static x => x.DatabaseId).Distinct().Should().HaveCount(2, "the rows carry the database each was listed in");
    }

    // -------------------------------------------------------------------------------------------------
    // T9: the activity gate, and nothing read for what is withheld
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Below_the_activity_gate_the_volume_columns_are_withheld_and_no_statement_is_attempted()
    {
        // Capabilities default off: ViewDatabaseActivity is not granted.
        await using Harness harness = Harness.Create(Conjoined, known: ["acme", "globex"]);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.VolumesShown.Should().BeFalse();
        overview.VolumesWithheldReason.Should().Contain("ViewDatabaseActivity").And.Contain("MartenStudioOptions.Capabilities.ViewDatabaseActivity")
            .And.Contain("write policy");
        overview.Rows.Should().OnlyContain(static x => x.ReadProblem == null, "no connection was attempted: a read of an unreachable host would show as a problem");
        overview.Rows.Should().OnlyContain(static x => x.Documents.Text == "—" && x.Events.Text == "—" && x.Streams.Text == "—" && x.LastEvent.Text == "—");
    }

    [Fact]
    public async Task With_the_capability_but_not_the_write_policy_the_columns_are_still_withheld_and_the_policy_is_named()
    {
        await using Harness harness = Harness.Create(Conjoined, policies: true, known: ["acme"], activity: true);
        harness.Policies.Allow(static resource => resource.Capability is null);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.VolumesShown.Should().BeFalse();
        overview.VolumesWithheldReason.Should().Contain("write policy does not allow it for this database");
        overview.Rows.Should().OnlyContain(static x => x.ReadProblem == null);
        overview.Rows.Select(static x => x.TenantId).Should().Equal(["acme"], "the names are the picker's, not the activity gate's");
    }

    [Fact]
    public async Task Through_the_gate_a_read_is_attempted_for_the_pages_database()
    {
        await using Harness harness = Harness.Create(Conjoined, known: ["acme"], activity: true);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.VolumesShown.Should().BeTrue();
        overview.Rows.Should().ContainSingle().Which.ReadProblem.Should().StartWith("could not be read",
            "the host is unreachable, which proves the statement was attempted - and its words are the host-failure words");
    }

    [Fact]
    public async Task A_database_one_tenant_owns_still_reads_its_projections_below_the_gate()
    {
        await using Harness harness = Harness.CreateStatic(known: null);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.VolumesShown.Should().BeFalse();
        overview.Rows.Single(static x => x.TenantId == "acme").ReadProblem.Should().NotBeNull(
            "the row's own policy decides projections, so a read was attempted though the volumes are withheld");
        overview.Rows.Where(static x => x.Placement == TenantPlacement.SharedTables).Should().OnlyContain(static x => x.ReadProblem == null);
    }

    // -------------------------------------------------------------------------------------------------
    // 1.2-SCOPE-2-FU
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// F1: a host that accepts the connection and never answers. The budget (here 300 ms) is spent while the first database's connection
    /// waits out its startup <c>Timeout</c> (1 s) - Npgsql's open does not honour the token during startup - and the footer's read then
    /// meets a spent token: the page is given "not read in time", not an exception the page would swallow into a blank.
    /// </summary>
    [Fact]
    public async Task A_spent_budget_gives_not_read_in_time_and_never_an_exception()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using CancellationTokenSource accepting = new();
        List<System.Net.Sockets.TcpClient> held = [];
        _ = Task.Run(async () =>
        {
            try
            {
                while (!accepting.IsCancellationRequested)
                {
                    held.Add(await listener.AcceptTcpClientAsync(accepting.Token));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, Token);

        string silent = $"Host=127.0.0.1;Port={((System.Net.IPEndPoint) listener.LocalEndpoint).Port};Database=silent;Username=u;Password=p;Timeout=1;Pooling=false";

        await using Harness harness = Harness.Create(
            static options =>
            {
                options.Schema.For<TenantedNote>().MultiTenanted();
                options.Schema.For<PlainNote>();
            },
            known: ["acme"],
            activity: true,
            connection: silent,
            budget: TimeSpan.FromMilliseconds(300));

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        await accepting.CancelAsync();
        listener.Stop();
        foreach (System.Net.Sockets.TcpClient client in held)
        {
            client.Dispose();
        }

        overview.Rows.Should().ContainSingle().Which.ReadProblem.Should().NotBeNull("the one database could not be read");
        overview.Shared.Should().NotBeNull();
        overview.Shared!.Documents.Text.Should().Be("not read in time");
        overview.Shared.Collections.Should().Be(1);
    }

    /// <summary>F3: a duplicated KnownTenantIds entry is one row - two rows would share one @key and end the circuit.</summary>
    [Fact]
    public async Task A_duplicated_known_tenant_id_is_one_row()
    {
        await using Harness harness = Harness.Create(Conjoined, known: ["acme", "globex", "acme"]);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.Rows.Select(static x => x.TenantId).Should().Equal("acme", "globex");
        overview.Rows.Select(static x => (x.DatabaseId, x.TenantId)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_duplicated_known_tenant_id_on_a_store_with_several_databases_is_one_row()
    {
        await using Harness harness = Harness.CreateStatic(known: ["acme", "acme"]);

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.Rows.Select(static x => x.TenantId).Should().Equal("acme");
    }

    /// <summary>F5: the page enumerates nothing of its own - on a master-table shape, exactly the resolver's one BuildDatabases.</summary>
    [Fact]
    public async Task A_page_read_makes_exactly_the_resolvers_one_enumeration()
    {
        int builds = 0;

        await using Harness harness = Harness.CreateStatic(
            known: null,
            after: options =>
            {
                ObservedTenancy observed = ObservedTenancy.Over(options.Tenancy, masterTableShape: true);
                observed.OnBuildDatabases = () => Interlocked.Increment(ref builds);
                options.Tenancy = (ITenancy) (object) observed;
            });

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);

        overview.Rows.Select(static x => x.TenantId).Should().BeEquivalentTo(["acme", "globex", "initech"]);
        builds.Should().Be(1, "the resolver's enumeration; tenant lists, placement and the other database's tenants are read from it");
    }

    /// <summary>F6: a tenant with no single-value partition has no claim to "0 rows" - they may be in a shared or default partition.</summary>
    [Fact]
    public void A_tenant_with_no_partition_in_any_table_reads_a_dash_and_one_with_an_empty_partition_reads_zero()
    {
        TenantCell none = TenantOverviewService.Cell([new(TenantOverviewService.Kind.NoPartition, 0, "app.docs")], "collections");

        none.Text.Should().Be("—");
        none.Title.Should().Be("its rows, if any, are in a shared or default partition");

        TenantOverviewService.Cell([new(TenantOverviewService.Kind.Count, 0, "app.docs")], "collections").Text.Should().Be("~0");
        TenantOverviewService.Cell(
                [new(TenantOverviewService.Kind.Count, 12, "app.docs"), new(TenantOverviewService.Kind.NoPartition, 0, "app.more")],
                "collections")
            .Text.Should().Be("~12");
    }

    /// <summary>F8: a database the policy refuses as a whole lists none of its tenants, though each would be allowed one by one.</summary>
    [Fact]
    public async Task A_database_the_policy_refuses_as_a_whole_lists_none_of_its_tenants()
    {
        await using Harness harness = Harness.CreateStatic(known: null, policies: true);

        // A static tenancy enumerates in hash order, so learn which database holds what before hiding one.
        TenantOverview open = await harness.Service.GetAsync(DefaultScope, new TenantOverviewRequest(), Token);
        string kept = open.Rows.Single(static x => x.TenantId == "acme").DatabaseId;
        string hidden = open.Rows.Single(static x => x.TenantId == "globex").DatabaseId;
        hidden.Should().NotBe(kept, "the premise");

        harness.Policies.Allow(resource => !string.Equals(resource.DatabaseIdentifier, hidden, StringComparison.Ordinal) || resource.TenantId is not null);
        int before = harness.Policies.Calls.Count;

        TenantOverview overview = await harness.Service.GetAsync(DefaultScope with { DatabaseId = kept }, new TenantOverviewRequest(), Token);

        overview.Rows.Select(static x => x.TenantId).Should().Equal(["acme"], "globex and initech live in the database the header's picker hides");
        harness.Policies.Calls.Skip(before).Count(x => x.Resource.TenantId is null && x.Resource.DatabaseIdentifier == hidden)
            .Should().Be(1, "asked once for that database");
        harness.Policies.Calls.Skip(before).Should().NotContain(
            x => x.Resource.TenantId == "globex" || x.Resource.TenantId == "initech",
            "none of its tenants is asked about, since none is listed");
    }

    // -------------------------------------------------------------------------------------------------
    // T8: a tenant's projections in one cell
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_projections_cell_says_none_yet_caught_up_behind_with_the_worst_shard_or_failed()
    {
        TenantOverviewService.ProgressCell(tableExists: false, [], 100).Text.Should().Be("no progress yet");
        TenantOverviewService.ProgressCell(tableExists: true, [], 100).Text.Should().Be("no progress yet");

        TenantOverviewService.ProgressCell(true, [new MartenStudio.Internal.Sql.ProgressionRow("Orders:All:acme", 100)], 100).Text.Should().Be("caught up");

        TenantOverviewService.ProgressCell(
                true,
                [new MartenStudio.Internal.Sql.ProgressionRow("Orders:All:acme", 100), new MartenStudio.Internal.Sql.ProgressionRow("Totals:All:acme", 40)],
                1_340)
            .Text.Should().Be("1,300 behind (Totals:All:acme)", "the worst of the tenant's shards, against its own high-water mark");

        TenantOverviewService.ProgressCell(
                true,
                [new MartenStudio.Internal.Sql.ProgressionRow("Orders:All:acme", 100, FailureCategory: "Apply"), new MartenStudio.Internal.Sql.ProgressionRow("Totals:All:acme", 100)],
                100)
            .Text.Should().Be("1 failed");

        TenantOverviewService.ProgressCell(true, [new MartenStudio.Internal.Sql.ProgressionRow("Orders:All:acme", 100)], null).Text.Should().Be("no high-water mark yet");
    }

    // -------------------------------------------------------------------------------------------------

    private static void Conjoined(StoreOptions options) => options.Schema.For<TenantedNote>().MultiTenanted();

    public sealed class PlainNote
    {
        public Guid Id { get; set; }
    }

    public sealed class TenantedNote
    {
        public Guid Id { get; set; }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, TestStoreAuthorizationService policies)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Policies = policies;
        }

        public TestStoreAuthorizationService Policies { get; }

        public ITenantOverviewService Service => scope.ServiceProvider.GetRequiredService<ITenantOverviewService>();

        /// <summary>One single-database store.</summary>
        public static Harness Create(
            Action<StoreOptions> configure,
            bool policies = false,
            string[]? known = null,
            bool discover = true,
            bool activity = false,
            string? connection = null,
            TimeSpan? budget = null) =>
            Build(
                options =>
                {
                    options.Connection(connection ?? FirstConnection);
                    options.DatabaseSchemaName = "scope2";
                    configure(options);
                },
                policies,
                known,
                discover,
                activity,
                budget);

        /// <summary>A static two-database store: acme alone in the first, globex and initech together in the second.</summary>
        public static Harness CreateStatic(string[]? known, bool policies = false, Action<StoreOptions>? after = null) =>
            Build(
                options =>
                {
                    options.DatabaseSchemaName = "scope2";
                    options.MultiTenantedDatabases(tenancy =>
                    {
                        tenancy.AddMultipleTenantDatabase(FirstConnection, "tenants-a").ForTenants("acme");
                        tenancy.AddMultipleTenantDatabase(SecondConnection, "tenants-b").ForTenants("globex", "initech");
                    });
                    Conjoined(options);
                    after?.Invoke(options);
                },
                policies,
                known,
                discover: true,
                activity: false,
                budget: null);

        private static Harness Build(Action<StoreOptions> configure, bool policies, string[]? known, bool discover, bool activity, TimeSpan? budget)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var authorization = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddMarten(configure);
            services.AddMartenStudio(options =>
            {
                options.QueryTimeout = TimeSpan.FromSeconds(2);
                options.DiscoverTenantIds = discover;

                foreach (string id in known ?? [])
                {
                    options.KnownTenantIds.Add(id);
                }

                if (policies)
                {
                    options.StoreAuthorizationPolicy = StorePolicy;
                    options.WriteAuthorizationPolicy = WritePolicy;
                }

                if (activity)
                {
                    options.Capabilities = new MartenStudioCapabilities { ViewDatabaseActivity = true };
                }
            });

            services.AddSingleton<IAuthorizationService>(authorization);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            if (budget is { } spend)
            {
                services.AddScoped<ITenantOverviewService>(sp => ActivatorUtilities.CreateInstance<TenantOverviewService>(sp, spend));
            }

            return new Harness(services.BuildServiceProvider(), authorization);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
