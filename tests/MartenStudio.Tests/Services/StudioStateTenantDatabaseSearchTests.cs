using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Services;

/// <summary>
/// TENANT-DB: <see cref="StudioState.SetScopeAsync" /> with a tenant and no database. In a store that keeps each tenant in a database of
/// its own, the database the usual way resolves does not list a tenant that lives in another one, and a link (a module's, or another
/// page's) names a tenant and never a database; the state looks for the tenant in the databases the visitor already sees, in listing
/// order, asking only the scope picker's own <see cref="IStudioScopeCatalog.DescribeAsync" />.
/// </summary>
public class StudioStateTenantDatabaseSearchTests
{
    private const int TenantDatabaseSearchBoundedEvent = 9229;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static List<(string StoreKey, string DatabaseId)> Calls(params (string StoreKey, string DatabaseId)[] calls) => [.. calls];

    private static TenantList Lists(params string[] ids) => new(ids, IsTruncated: false, TenantListSource.Descriptor);

    [Fact]
    public async Task A_tenant_in_another_database_opens_that_database_with_the_tenant()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        await state.SetScopeAsync("s", null, "globex", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_b", "globex"));
        state.AvailableTenants.Ids.Should().Contain("globex", "the header now describes the database the tenant was found in");
        catalog.DescribeCalls.Should().Equal(Calls(("s", "db_b")), "the usual database is not asked again, and the search stops at the first hit");
    }

    [Fact]
    public async Task A_tenant_no_database_lists_leaves_the_usual_database_and_drops_the_tenant()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        await state.SetScopeAsync("s", null, "umbrella", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_a", null), "found nowhere, the address is applied as a tenant the listing does not carry always was");
        catalog.DescribeCalls.Select(static x => x.DatabaseId).Should().Equal("db_b", "db_c");
    }

    [Fact]
    public async Task A_tenant_in_a_database_the_visitor_may_not_see_is_not_found()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        // The store policy refused db_c for this visitor: the database list omits it, whatever db_c itself would say.
        catalog.Databases["s"].RemoveAll(static x => x.Identity == "db_c");
        catalog.WithDatabaseTenants("s", "db_c", Lists("hidden"));
        await state.RefreshScopeListingsAsync(Token);
        catalog.DescribeCalls.Clear();

        await state.SetScopeAsync("s", null, "hidden", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_a", null));
        catalog.DescribeCalls.Should().NotContain(static x => x.DatabaseId == "db_c", "a database the visitor cannot see is never asked");
    }

    [Fact]
    public async Task A_tenant_the_policy_refuses_in_its_database_is_not_found()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        // db_b holds globex, but the policy lets this visitor address only initech there: its listing is filtered, and so truncated.
        catalog.WithDatabaseTenants("s", "db_b", new TenantList(["initech"], IsTruncated: true, TenantListSource.Descriptor));

        await state.SetScopeAsync("s", null, "globex", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_a", null), "the search takes the listing's word, not a free-text acceptance");
    }

    [Fact]
    public async Task A_filtered_usual_database_does_not_claim_a_tenant_it_does_not_list()
    {
        // A policy-filtered listing is always marked truncated, and a truncated one accepts any plausible id as free text.
        FakeStudioScopeCatalog catalog = new FakeStudioScopeCatalog()
            .WithStore("s", "Store S", databaseIdentities: ["db_a", "db_b"])
            .WithDatabaseTenants("s", "db_a", new TenantList(["acme"], IsTruncated: true, TenantListSource.Descriptor))
            .WithDatabaseTenants("s", "db_b", new TenantList(["globex"], IsTruncated: true, TenantListSource.Descriptor));
        StudioState state = new(catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());
        await state.EnsureInitializedAsync(Token);

        await state.SetScopeAsync("s", null, "globex", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_b", "globex"));
    }

    [Fact]
    public async Task A_database_and_a_tenant_that_disagree_are_applied_exactly_without_a_search()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        await state.SetScopeAsync("s", "db_c", "globex", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_c", null), "the address named db_c, and db_c does not list globex (D9)");
        catalog.DescribeCalls.Should().Equal(Calls(("s", "db_c")), "only the named database is described; nothing is searched");
    }

    [Fact]
    public async Task The_scope_selectors_tenant_change_names_the_current_database_and_never_searches()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        // ScopeSelector.razor passes the active scope's store and database, and the tenant the visitor chose or typed.
        await state.SetScopeAsync(state.ActiveScope!.StoreKey, state.ActiveScope.DatabaseId, "globex", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_a", null), "a tenant db_a does not list is dropped, as it always was");
        catalog.DescribeCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task No_search_is_made_when_the_usual_database_lists_the_tenant()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        await state.SetScopeAsync("s", null, "acme", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_a", "acme"));
        catalog.DescribeCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_store_change_with_a_tenant_and_no_database_describes_the_first_database_once_when_it_lists_the_tenant()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        await state.SetScopeAsync("t", null, "initech", Token);

        state.ActiveScope.Should().Be(new StudioScope("t", "db_t1", "initech"));
        catalog.DescribeCalls.Should().Equal(Calls(("t", "db_t1")), "the usual database lists the tenant: DescribeAsync is called once");
    }

    [Fact]
    public async Task A_store_change_with_a_tenant_and_no_database_searches_the_new_stores_other_databases()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        await state.SetScopeAsync("t", null, "umbrella", Token);

        state.ActiveScope.Should().Be(new StudioScope("t", "db_t2", "umbrella"));
        catalog.DescribeCalls.Should().Equal(Calls(("t", "db_t1"), ("t", "db_t2")));
    }

    [Fact]
    public async Task While_the_search_runs_the_links_name_no_database_and_the_tenant_as_named()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();

        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.DescribeGate = gate;
        Task applying = state.SetScopeAsync("s", null, "globex", Token);

        try
        {
            state.LinkScope.Should().Be(new StudioScope("s", string.Empty, "globex"),
                "never the database on screen with a tenant that is not in it");
        }
        finally
        {
            gate.SetResult();
        }

        await applying;

        state.LinkScope.Should().Be(new StudioScope("s", "db_b", "globex"));
    }

    [Fact]
    public async Task A_database_whose_listing_throws_is_skipped_and_the_search_goes_on()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();
        catalog.DescribeThrows.Add(("s", "db_b"));
        catalog.WithDatabaseTenants("s", "db_c", Lists("globex"));

        await state.SetScopeAsync("s", null, "globex", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_c", "globex"));
    }

    [Fact]
    public async Task A_database_whose_tenants_could_not_be_read_is_skipped()
    {
        (StudioState state, FakeStudioScopeCatalog catalog) = await OnAcmeAsync();
        catalog.DatabaseFacts[("s", "db_b")] = StoreScopeFacts.None;
        catalog.WithDatabaseTenants("s", "db_c", Lists("globex"));

        await state.SetScopeAsync("s", null, "globex", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_c", "globex"));
    }

    [Fact]
    public async Task The_search_stops_at_thirty_two_databases_and_says_so_once_at_debug_without_the_tenant()
    {
        CapturingLoggerProvider logs = new();
        (StudioState state, FakeStudioScopeCatalog catalog) = await ManyDatabasesAsync(35, logs);

        await state.SetScopeAsync("s", null, "needle", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_01", null), "the tenant is in the 35th database, past the bound");
        catalog.DescribeCalls.Should().HaveCount(StudioState.MaxTenantDatabaseSearch);

        CapturedLogEntry entry = logs.Entries.Should().ContainSingle(static x => x.EventId.Id == TenantDatabaseSearchBoundedEvent).Subject;
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Message.Should().Contain("32").And.Contain("store s").And.NotContain("needle");
    }

    [Fact]
    public async Task A_tenant_in_the_last_database_the_bound_allows_is_found_and_logs_nothing()
    {
        CapturingLoggerProvider logs = new();

        // db_01 is the usual database; db_02 to db_33 are the thirty-two searched.
        (StudioState state, FakeStudioScopeCatalog catalog) = await ManyDatabasesAsync(33, logs);

        await state.SetScopeAsync("s", null, "needle", Token);

        state.ActiveScope.Should().Be(new StudioScope("s", "db_33", "needle"));
        catalog.DescribeCalls.Should().HaveCount(StudioState.MaxTenantDatabaseSearch);
        logs.Entries.Should().NotContain(static x => x.EventId.Id == TenantDatabaseSearchBoundedEvent);
    }

    /// <summary>A store "s" of 40 databases, <c>db_01</c> the usual one, whose tenant "needle" lives in database number <paramref name="needleAt" />.</summary>
    private static async Task<(StudioState State, FakeStudioScopeCatalog Catalog)> ManyDatabasesAsync(int needleAt, CapturingLoggerProvider logs)
    {
        string[] databases = [.. Enumerable.Range(1, 40).Select(static x => $"db_{x:00}")];
        FakeStudioScopeCatalog catalog = new FakeStudioScopeCatalog().WithStore("s", "Store S", databaseIdentities: databases);
        foreach (string database in databases)
        {
            catalog.WithDatabaseTenants("s", database, database == $"db_{needleAt:00}" ? Lists("needle") : Lists("acme"));
        }

        StudioState state = new(catalog, logs.CreateFactory().CreateLogger<StudioState>(), new HttpContextAccessor());
        await state.EnsureInitializedAsync(Token);
        catalog.DescribeCalls.Clear();
        return (state, catalog);
    }

    /// <summary>Store "s" of three databases, each with a tenant of its own, and store "t" of two, the circuit on the first of "s" with all tenants.</summary>
    private static async Task<(StudioState State, FakeStudioScopeCatalog Catalog)> OnAcmeAsync()
    {
        FakeStudioScopeCatalog catalog = new FakeStudioScopeCatalog()
            .WithStore("s", "Store S", databaseIdentities: ["db_a", "db_b", "db_c"])
            .WithDatabaseTenants("s", "db_a", Lists("acme"))
            .WithDatabaseTenants("s", "db_b", Lists("globex"))
            .WithDatabaseTenants("s", "db_c", Lists("initech"))
            .WithStore("t", "Store T", databaseIdentities: ["db_t1", "db_t2"])
            .WithDatabaseTenants("t", "db_t1", Lists("initech"))
            .WithDatabaseTenants("t", "db_t2", Lists("umbrella"));

        StudioState state = new(catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());
        await state.EnsureInitializedAsync(Token);
        state.ActiveScope.Should().Be(new StudioScope("s", "db_a", null), "the premise: the circuit is on the first database, all tenants");
        catalog.DescribeCalls.Clear();

        return (state, catalog);
    }
}
