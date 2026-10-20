using MartenStudio.Services.Configuration;
using MartenStudio.Services.Query;

using Npgsql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>
/// LINQ mode against a real database, through the registered service: tenancy, soft deletes and hierarchies stay
/// Marten's, nothing creates a table, the plan feeds the index advice, and the audit is written by signature.
/// </summary>
/// <remarks>
/// Acceptance criteria 2, 3 and 5. Everything but the floor's "absent" case needs a Marten with DynamicQuery, so it
/// runs with <c>-p:MartenStudioMartenVersion=9.50.2</c>; at the floor the one test that runs is that the mode is not
/// there and says which Marten it needs.
/// </remarks>
public class LinqQueryLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private LinqQueryHarness harness = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        harness = await LinqQueryHarness.CreateAsync(fixture, "linqquerylive");
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private Task<LinqQueryResult> RunAsync(StudioScope scope, string alias, string where, string orderBy = "", DeletedFilter deleted = DeletedFilter.Exclude, int offset = 0, int pageSize = 50) =>
        harness.Linq.RunAsync(scope, new LinqQueryRequest(alias, where, orderBy, pageSize, offset, deleted), Token);

    private static IEnumerable<Guid> Ids(LinqQueryResult result) => result.Rows.Select(static x => Guid.Parse(x.Id));

    // ------------------------------------------------------------------------------------------------
    // Criterion 3: absent at the floor, with the version line
    // ------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task At_the_floor_the_mode_is_absent_and_names_the_Marten_it_needs()
    {
        Assert.SkipWhen(DynamicLinqAdapter.Current is not null, "This run resolved a Marten with DynamicQuery.");

        LinqModeStatus status = await harness.Linq.DescribeAsync(LinqQueryHarness.AllTenants, Token);

        status.Availability.Should().Be(LinqAvailability.MartenTooOld);
        status.Message.Should().Contain("Marten 9.48+").And.Contain(MartenRuntime.Current.Describe(RuntimePackage.Marten));
    }

    // ------------------------------------------------------------------------------------------------
    // Criterion 2: the tenant, soft deletes, hierarchies
    // ------------------------------------------------------------------------------------------------

    [LinqLiveFact]
    public async Task A_conjoined_tenants_query_never_returns_another_tenants_rows()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();

        LinqQueryResult acme = await RunAsync(LinqQueryHarness.For(LinqQueryHarness.Acme), alias, "Name == \"Ada\"");
        LinqQueryResult acmeAll = await RunAsync(LinqQueryHarness.For(LinqQueryHarness.Acme), alias, "Total > 0");
        LinqQueryResult globex = await RunAsync(LinqQueryHarness.For(LinqQueryHarness.Globex), alias, "Name == \"Ada\"");

        acme.Error.Should().BeNull();
        Ids(acme).Should().Equal(new[] { LinqQueryHarness.AcmeAda }, "globex has an Ada over 100 too, and it is globex's");
        Ids(acmeAll).Should().BeEquivalentTo(new[] { LinqQueryHarness.AcmeAda, LinqQueryHarness.AcmeBob });
        Ids(globex).Should().Equal(LinqQueryHarness.GlobexAda);

        acme.Sql.Should().Contain("tenant_id", "the filter is Marten's own");
        acme.Scope!.TenantId.Should().Be(LinqQueryHarness.Acme);
        acme.Scope.CrossTenant.Should().BeFalse();
    }

    [LinqLiveFact]
    public async Task With_no_tenant_selected_the_studio_asks_Marten_for_every_tenant_and_says_so()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();

        LinqQueryResult all = await RunAsync(LinqQueryHarness.AllTenants, alias, "Name == \"Ada\"");

        Ids(all).Should().BeEquivalentTo(new[] { LinqQueryHarness.AcmeAda, LinqQueryHarness.GlobexAda });
        all.Scope!.CrossTenant.Should().BeTrue();
    }

    [LinqLiveFact]
    public async Task Soft_deleted_rows_stay_out_unless_Martens_own_deleted_filters_are_asked_for()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();
        StudioScope acme = LinqQueryHarness.For(LinqQueryHarness.Acme);

        Ids(await RunAsync(acme, alias, string.Empty)).Should().BeEquivalentTo(new[] { LinqQueryHarness.AcmeAda, LinqQueryHarness.AcmeBob });
        Ids(await RunAsync(acme, alias, string.Empty, deleted: DeletedFilter.Include))
            .Should().BeEquivalentTo(new[] { LinqQueryHarness.AcmeAda, LinqQueryHarness.AcmeBob, LinqQueryHarness.AcmeDeleted });
        Ids(await RunAsync(acme, alias, string.Empty, deleted: DeletedFilter.Only)).Should().Equal(LinqQueryHarness.AcmeDeleted);
    }

    [LinqLiveFact]
    public async Task A_subclass_narrows_to_itself_and_the_root_reads_the_whole_hierarchy()
    {
        string root = await harness.AliasOfAsync<LinqLiveVehicle>();
        string car = await harness.AliasOfAsync<LinqLiveCar>();

        Ids(await RunAsync(LinqQueryHarness.AllTenants, root, "Make == \"Volvo\"")).Should().BeEquivalentTo(new[] { LinqQueryHarness.Car, LinqQueryHarness.Truck });

        LinqQueryResult cars = await RunAsync(LinqQueryHarness.AllTenants, car, "Make == \"Volvo\" and Doors == 4");

        Ids(cars).Should().Equal(LinqQueryHarness.Car);
        cars.Sql.Should().Contain("mt_doc_type", "the subclass filter is Marten's own");
    }

    [LinqLiveFact]
    public async Task A_hidden_type_cannot_be_queried_and_is_not_offered()
    {
        LinqModeStatus status = await harness.Linq.DescribeAsync(LinqQueryHarness.AllTenants, Token);

        status.Types.Should().NotContain(static x => x.TypeName == nameof(LinqLiveHidden));

        LinqQueryResult result = await RunAsync(LinqQueryHarness.AllTenants, "linqlivehidden", "Code == \"secret\"");

        result.Refusal!.Message.Should().Contain("no visible document type");
        result.Rows.Should().BeEmpty();
    }

    [LinqLiveFact]
    public async Task An_enum_stored_as_its_name_compares_by_equality_and_is_refused_a_range()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();
        StudioScope acme = LinqQueryHarness.For(LinqQueryHarness.Acme);

        Ids(await RunAsync(acme, alias, "Status == \"Open\"")).Should().Equal(LinqQueryHarness.AcmeAda);

        LinqQueryResult range = await RunAsync(acme, alias, "Status > \"Open\"");

        range.Refusal!.Message.Should().Contain("alphabetical");
        range.Refusal.Stage.Should().BeOneOf(LinqStage.Parse, LinqStage.Guard);
    }

    [LinqLiveFact]
    public async Task A_date_literal_compares_with_a_DateTime_member()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();

        LinqQueryResult result = await RunAsync(LinqQueryHarness.For(LinqQueryHarness.Acme), alias, "PlacedAt > \"2024-01-01\"");

        result.Error.Should().BeNull("a UTC literal is given the Kind Marten binds a DateTime member as");
        Ids(result).Should().Equal(LinqQueryHarness.AcmeAda);

        // On the boundary, so a date read in this server's time zone rather than as UTC moves a row across it: a host
        // east of Greenwich would let Ada (placed at 2024-06-01 00:00) through the first, and one west of it would
        // keep her out of the second.
        StudioScope acme = LinqQueryHarness.For(LinqQueryHarness.Acme);

        Ids(await RunAsync(acme, alias, "PlacedAt > \"2024-06-01\"")).Should().BeEmpty("the literal is the UTC date it names");
        Ids(await RunAsync(acme, alias, "PlacedAt >= \"2024-06-01\"")).Should().Equal(LinqQueryHarness.AcmeAda);
    }

    [LinqLiveFact]
    public async Task An_in_list_is_bound_as_one_array_and_matches_enums_stored_as_their_names()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();

        LinqQueryResult statuses = await RunAsync(LinqQueryHarness.AllTenants, alias, "Status in (\"Open\", \"Shipped\")", "Name, Total");

        statuses.Error.Should().BeNull();
        statuses.Sql.Should().Contain("ANY(", "Marten binds the in-list as one array parameter");
        Ids(statuses).Should().Equal(LinqQueryHarness.AcmeAda, LinqQueryHarness.GlobexAda, LinqQueryHarness.AcmeBob);

        Ids(await RunAsync(LinqQueryHarness.AllTenants, alias, "Name not in (\"Ada\")")).Should().Equal(LinqQueryHarness.AcmeBob);
    }

    [LinqLiveFact]
    public async Task A_LINQ_query_is_counted_in_the_studios_own_database_time()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();

        await using NpgsqlConnection connection = harness.Store.Storage.Database.CreateConnection();
        string key = PostgresStudio.Internal.Sql.StudioDatabaseTime.KeyOf(connection);
        long before = PostgresStudio.Internal.Sql.StudioDatabaseTime.Shared.SessionsEver(key);

        (await RunAsync(LinqQueryHarness.For(LinqQueryHarness.Acme), alias, "Name == \"Ada\"")).Error.Should().BeNull();

        PostgresStudio.Internal.Sql.StudioDatabaseTime.Shared.SessionsEver(key).Should().BeGreaterThanOrEqualTo(before + 2,
            "the read and its plan each run in a ReadOnlySqlSession, which is what the health advisor's studio-cost line counts");
    }

    [LinqLiveFact]
    public async Task Offset_pages_are_deterministic_and_disjoint()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();

        LinqQueryResult first = await RunAsync(LinqQueryHarness.AllTenants, alias, "Total > 0", "Name", pageSize: 1);
        LinqQueryResult second = await RunAsync(LinqQueryHarness.AllTenants, alias, "Total > 0", "Name", offset: 1, pageSize: 1);
        LinqQueryResult third = await RunAsync(LinqQueryHarness.AllTenants, alias, "Total > 0", "Name", offset: 2, pageSize: 1);

        first.HasMore.Should().BeTrue();
        third.HasMore.Should().BeFalse();
        Ids(first).Concat(Ids(second)).Concat(Ids(third)).Should().OnlyHaveUniqueItems()
            .And.BeEquivalentTo(new[] { LinqQueryHarness.AcmeAda, LinqQueryHarness.AcmeBob, LinqQueryHarness.GlobexAda });
        first.Sql.Should().Contain("d.id", "the identity column breaks ties so a page is the same page every time");
    }

    // ------------------------------------------------------------------------------------------------
    // No DDL
    // ------------------------------------------------------------------------------------------------

    [LinqLiveFact]
    public async Task Running_a_LINQ_query_creates_no_table()
    {
        string alias = await harness.AliasOfAsync<LinqLiveUntouched>();

        (await TableExistsAsync("mt_doc_linqliveuntouched")).Should().BeFalse("nothing in this process has stored one");

        LinqQueryResult result = await RunAsync(LinqQueryHarness.AllTenants, alias, "Name == \"x\"");

        result.Error!.SqlState.Should().Be("42P01", "the table does not exist and LINQ mode does not create it");
        (await TableExistsAsync("mt_doc_linqliveuntouched")).Should().BeFalse(
            "ToCommand would have run EnsureStorageExists under AutoCreate.All and created it");
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync(Token);
        await using var command = new NpgsqlCommand("select to_regclass(@name) is not null", connection);
        command.Parameters.AddWithValue("name", harness.Schema + "." + table);

        return (bool) (await command.ExecuteScalarAsync(Token))!;
    }

    // ------------------------------------------------------------------------------------------------
    // Criterion 5: EXPLAIN feeds the index advisor
    // ------------------------------------------------------------------------------------------------

    [LinqLiveFact]
    public async Task The_index_advice_names_the_member_the_predicate_filters_on_and_the_line_that_declares_its_index()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();

        LinqQueryResult result = await RunAsync(LinqQueryHarness.For(LinqQueryHarness.Acme), alias, "Customer.Name == \"Ada\" and Name == \"Ada\"");

        result.Error.Should().BeNull();
        result.Plan!.PlanJson.Should().NotBeNullOrWhiteSpace("the plan is EXPLAIN of Marten's own statement");
        result.Plan.PlanJson.Should().NotContain("Actual Rows", "EXPLAIN, never ANALYZE");

        LinqIndexAdvice advice = result.Advice!;
        LinqIndexVerdict customer = advice.Verdicts.Single(static x => x.Subject == "Customer.Name");
        LinqIndexVerdict name = advice.Verdicts.Single(static x => x.Subject == "Name");

        customer.Verdict.Level.Should().Be(IndexVerdictLevel.Red);
        customer.Verdict.Suggestion.Should().Contain("Duplicate(x => x.Customer.Name)").And.Contain(nameof(LinqLiveOrder));
        name.Verdict.Level.Should().Be(IndexVerdictLevel.Green, "Name is a duplicated field with an index");
        advice.Scans.Should().Contain(static x => x.Relation == "mt_doc_linqliveorder");
    }

    // ------------------------------------------------------------------------------------------------
    // Authorization: the tenant the read is asked with, and the plan's
    // ------------------------------------------------------------------------------------------------

    [LinqLiveFact]
    public async Task A_visitor_allowed_one_tenant_reads_it_and_is_refused_the_plan_which_describes_the_whole_table()
    {
        await using LinqQueryHarness acmeOnly = await LinqQueryHarness.CreateAsync(fixture, "linqqueryliveacme", static options =>
        {
            options.StoreAuthorizationPolicy = LinqQueryHarness.AcmeOnlyPolicy;
            options.WriteAuthorizationPolicy = LinqQueryHarness.AcmeOnlyPolicy;
        });

        StudioScope acme = LinqQueryHarness.For(LinqQueryHarness.Acme);
        string alias = await acmeOnly.AliasOfAsync<LinqLiveOrder>(acme);

        LinqQueryResult result = await acmeOnly.Linq.RunAsync(acme, new LinqQueryRequest(alias, "Name == \"Ada\""), Token);

        Ids(result).Should().Equal(LinqQueryHarness.AcmeAda);
        result.Plan!.UnavailableReason.Should().Be(LinqQueryService.PlanRefusedMessage);
        acmeOnly.Ring.GetLatest().Should().Contain(static x => x.Action == LinqQueryService.LinqExplainAction && !x.Succeeded && x.TenantId == null);
        acmeOnly.Logs.Entries.Should().Contain(static x => x.EventId == 9203);
    }

    [LinqLiveFact]
    public async Task A_collection_not_divided_by_tenant_is_asked_of_the_whole_database_from_a_tenant_scope()
    {
        await using LinqQueryHarness acmeOnly = await LinqQueryHarness.CreateAsync(fixture, "linqqueryliveshared", static options =>
        {
            options.StoreAuthorizationPolicy = LinqQueryHarness.AcmeOnlyPolicy;
            options.WriteAuthorizationPolicy = LinqQueryHarness.AcmeOnlyPolicy;
        });

        StudioScope acme = LinqQueryHarness.For(LinqQueryHarness.Acme);
        string alias = await acmeOnly.AliasOfAsync<LinqLiveNote>(acme);

        Func<Task> run = () => acmeOnly.Linq.RunAsync(acme, new LinqQueryRequest(alias, "Text == \"shared\""), Token);

        (await run.Should().ThrowAsync<StudioNotAuthorizedException>()).Which.Scope.TenantId.Should().BeNull(
            "a single-tenanted table is not narrowed by the tenant, so its read reaches the database as a whole");
    }

    /// <summary>
    /// And when it is allowed, the audit entry says what it reached: the ring shows an entry recorded under
    /// <c>RunLinqQueries</c> only to a visitor who may make the same read, and that read is the database's.
    /// </summary>
    [LinqLiveFact]
    public async Task A_read_that_reached_the_whole_database_is_audited_as_one()
    {
        StudioScope acme = LinqQueryHarness.For(LinqQueryHarness.Acme);
        string alias = await harness.AliasOfAsync<LinqLiveNote>(acme);

        LinqQueryResult result = await RunAsync(acme, alias, "Text == \"shared\" and Text != \"audited-whole\"");

        result.Error.Should().BeNull();
        result.Rows.Should().ContainSingle();

        StudioActionLogEntry entry = harness.Ring.GetLatest()
            .First(static x => x.Action == LinqQueryService.LinqQueryAction && x.Target.Contains("audited-whole", StringComparison.Ordinal));

        entry.Succeeded.Should().BeTrue();
        entry.TenantId.Should().BeNull("the collection is not divided by tenant, so the read was the database's");
    }

    // ------------------------------------------------------------------------------------------------
    // Criterion 4: the audit, by signature
    // ------------------------------------------------------------------------------------------------

    [LinqLiveFact]
    public async Task A_success_is_recorded_once_per_signature_and_every_failure_every_time()
    {
        string alias = await harness.AliasOfAsync<LinqLiveOrder>();
        StudioScope acme = LinqQueryHarness.For(LinqQueryHarness.Acme);
        int before = harness.Ring.GetLatest().Count;

        await RunAsync(acme, alias, "Total > 1", pageSize: 1);
        await RunAsync(acme, alias, "Total > 1", pageSize: 1, offset: 1);
        await RunAsync(acme, alias, "Total > 1", pageSize: 1);
        await RunAsync(acme, alias, "Total > 2", pageSize: 1);
        await RunAsync(acme, alias, "Name == System.IO.File.ReadAllText(\"x\")");
        await RunAsync(acme, alias, "Name == System.IO.File.ReadAllText(\"x\")");

        IReadOnlyList<StudioActionLogEntry> added = [.. harness.Ring.GetLatest().Take(harness.Ring.GetLatest().Count - before)];

        added.Where(static x => x.Action == LinqQueryService.LinqQueryAction && x.Succeeded).Should().HaveCount(2,
            "Total > 1 is one opening however many pages are turned, and Total > 2 is another");
        added.Where(static x => x.Action == LinqQueryService.LinqQueryAction && !x.Succeeded).Should().HaveCount(2);
        added.Should().OnlyContain(static x => x.Capability == nameof(StudioCapability.RunLinqQueries));
        harness.Logs.Entries.Should().Contain(static x => x.EventId == 9260 && x.Message.Contains("Total > 1"));
        harness.Logs.Entries.Should().Contain(static x => x.EventId == 9261 && x.Message.Contains("Screen"));
    }
}
