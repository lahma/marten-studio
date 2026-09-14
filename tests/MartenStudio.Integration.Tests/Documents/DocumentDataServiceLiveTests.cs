using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Query;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// The documents browser against a real Marten store, through the studio's own services.
/// </summary>
/// <remarks>
/// Every call here goes through <c>IDocumentDataService</c> resolved from the studio's container, so the
/// scope resolver and its authorization run on each one. What is being tested is the part no unit test can
/// reach: that the SQL the builders produce is SQL Postgres accepts, against tables Marten made, for every
/// identity type and every metadata shape the sample domain has.
/// </remarks>
public class DocumentDataServiceLiveTests(DocumentDataServiceLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentDataServiceLiveTests.Fixture>
{
    /// <summary>One store, one schema and one seed for the whole class.</summary>
    /// <param name="postgres">The assembly's container.</param>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <summary>
        /// The sample's two tenants, named outright.
        /// </summary>
        /// <remarks>
        /// A host with conjoined <em>documents</em> and a single-tenant event store has to name them: the
        /// studio's tenant discovery falls back to querying <c>mt_streams</c>, and that tier only runs
        /// when the event store itself is conjoined. See the packet report - this is a gap in tenant
        /// discovery, not in the documents browser.
        /// </remarks>
        protected override void ConfigureStudio(MartenStudioOptions options)
        {
            foreach (string tenantId in SampleStore.TenantIds)
            {
                options.KnownTenantIds.Add(tenantId);
            }
        }

        /// <summary>An <c>mt_doc_*</c> table Marten knows nothing about, for the Discovered band.</summary>
        protected override async Task SeedAsync()
        {
            await using NpgsqlConnection connection = await Postgres.OpenAsync();

            await using var command = new NpgsqlCommand(
                $"""
                 create table if not exists "{Schema}"."mt_doc_orphan" (
                     id uuid primary key,
                     data jsonb not null,
                     mt_last_modified timestamptz default transaction_timestamp()
                 );
                 insert into "{Schema}"."mt_doc_orphan" (id, data)
                 values ('8f1d5a6e-0000-0000-0000-0000000000ff', @json::jsonb)
                 on conflict (id) do nothing;
                 """,
                connection);

            command.Parameters.AddWithValue("json", """{"Left":"behind"}""");

            await command.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task The_rail_lists_every_registered_collection_with_its_flags()
    {
        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, TestContext.Current.CancellationToken);

        rail.Error.Should().BeNull();

        CollectionGroup registered = rail.Groups.Single(x => x.Kind == CollectionGroupKind.Documents);

        registered.Collections.Select(x => x.Alias).Should().Contain(
            ["customer", "order", "invoice", "product", "vehicle", "auditnote", "minimalnote", "mediaasset"]);

        rail.Find("order")!.Flags.SoftDeleted.Should().BeTrue();
        rail.Find("order")!.Flags.OptimisticConcurrency.Should().BeTrue();
        rail.Find("invoice")!.Flags.Conjoined.Should().BeTrue();
        rail.Find("vehicle")!.Flags.Hierarchy.Should().BeTrue();
        rail.Find("customer")!.Flags.SoftDeleted.Should().BeFalse();
    }

    [PostgresFact]
    public async Task A_hierarchy_nests_its_subclasses_under_the_root_and_they_share_its_table()
    {
        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, TestContext.Current.CancellationToken);

        CollectionInfo vehicle = rail.Find("vehicle")!;

        vehicle.SubCollections.Select(x => x.Alias).Should().Equal("car", "truck");
        vehicle.SubCollections.Should().OnlyContain(x => x.Table == vehicle.Table && x.IsSubclass);
    }

    [PostgresFact]
    public async Task The_counts_are_estimates_and_an_exact_count_can_be_asked_for()
    {
        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, TestContext.Current.CancellationToken);

        rail.Find("customer")!.Count.IsUnavailable.Should().BeFalse();

        var exact = await documents.Service.CountExactAsync(Scope, "customer", TestContext.Current.CancellationToken);

        exact.IsEstimate.Should().BeFalse();
        exact.Value.Should().Be(25);
    }

    /// <summary>
    /// P2-fix H1: an exact count answers for the scope the page is in.
    /// </summary>
    /// <remarks>
    /// The rail's estimate is a whole-table <c>reltuples</c> and cannot be anything else, but the number a
    /// visitor asks for by pressing "=" has to be the number of rows they can see. Six invoices under a
    /// scope showing three is the studio answering a question nobody asked.
    /// </remarks>
    [PostgresFact]
    public async Task An_exact_count_of_a_conjoined_collection_counts_the_tenant_in_scope()
    {
        using var documents = Documents();

        DocumentCount everyone = await documents.Service.CountExactAsync(
            Scope, "invoice", TestContext.Current.CancellationToken);

        DocumentCount acme = await documents.Service.CountExactAsync(
            MartenFixture.ScopeFor("acme"), "invoice", TestContext.Current.CancellationToken);

        everyone.Value.Should().Be(6, "the seeder writes three invoices for each of the two tenants");
        acme.Value.Should().Be(3);
        acme.IsEstimate.Should().BeFalse();

        // ... and the list under the same scope shows exactly those rows, so the header and the grid agree.
        DocumentPage page = await documents.Service.ListAsync(
            MartenFixture.ScopeFor("acme"),
            "invoice",
            new DocumentListRequest { PageSize = 100 },
            TestContext.Current.CancellationToken);

        page.Rows.Should().HaveCount(3);
        page.Estimate.Value.Should().Be(3);
    }

    /// <summary>
    /// P2-fix H2: a table Postgres has never analysed says how big it is, or says it does not know.
    /// </summary>
    /// <remarks>
    /// <c>reltuples</c> is <c>-1</c> until the first <c>ANALYZE</c> — the state every freshly seeded
    /// collection is in, which is to say the state of every store a new user opens the studio against.
    /// Mapping that to <c>Estimate(0)</c> drew "~0 documents" over a page of twenty-five customers.
    /// </remarks>
    [PostgresFact]
    public async Task A_never_analysed_collection_reports_its_real_size_rather_than_about_zero()
    {
        // The anti-vacuity half: this really is a never-analysed table, so the assertions below are about
        // the case they claim to be about. Twenty-five rows is under autovacuum's analyze threshold of
        // fifty, so nothing will have measured it behind the test's back.
        await using (NpgsqlConnection connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken))
        {
            DocumentCount raw = await new CountEstimator()
                .EstimateAsync(connection, Schema, "mt_doc_customer", TestContext.Current.CancellationToken);

            raw.IsUnknown.Should().BeTrue("pg_class.reltuples is -1 until the first ANALYZE");
            raw.IsEstimate.Should().BeFalse();
        }

        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, TestContext.Current.CancellationToken);
        CollectionInfo customer = rail.Find("customer")!;

        customer.Count.Value.Should().Be(25);
        customer.Count.IsEstimate.Should().BeFalse("there is no estimate to be had, so the rail paid for the truth");

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest { PageSize = 5 }, TestContext.Current.CancellationToken);

        page.Estimate.IsUnavailable.Should().BeFalse();
        page.Estimate.Value.Should().Be(25, "the header must not say ~0 over a page of rows");
    }

    [PostgresFact]
    public async Task A_table_no_mapping_claims_shows_up_in_its_own_band_and_is_read_only()
    {
        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, TestContext.Current.CancellationToken);

        CollectionGroup discovered = rail.Groups.Single(x => x.Kind == CollectionGroupKind.Discovered);

        discovered.Collections.Select(x => x.Alias).Should().Contain("orphan");
        discovered.Collections.Single(x => x.Alias == "orphan").IsRegistered.Should().BeFalse();

        // And it can still be listed: everything about it comes from information_schema.
        DocumentPage page = await documents.Service.ListAsync(
            Scope, "orphan", new DocumentListRequest(), TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Loaded);
        page.Rows.Should().ContainSingle();
    }

    [PostgresFact]
    public async Task Martens_own_dead_letter_table_is_not_offered_as_a_document_collection()
    {
        using var documents = Documents();

        CollectionRail rail = await documents.Service.GetCollectionsAsync(Scope, TestContext.Current.CancellationToken);

        rail.Groups.SelectMany(x => x.Collections).Should().NotContain(x => x.Alias.Contains("deadletter", StringComparison.OrdinalIgnoreCase));
    }

    [PostgresTheory]
    // Guid, a strong-typed OrderId, an int HiLo id and a string UseIdentityKey id: four identity shapes,
    // four different id column types, one code path.
    [InlineData("customer")]
    [InlineData("order")]
    [InlineData("invoice")]
    [InlineData("product")]
    public async Task Every_identity_shape_lists_and_every_row_can_be_opened_by_its_id(string alias)
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope,
            alias,
            new DocumentListRequest { Deleted = DeletedFilter.Include },
            TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Rows.Should().NotBeEmpty();

        DocumentDetailResult detail = await documents.Service.GetDocumentAsync(
            Scope, alias, page.Rows[0].Id, TestContext.Current.CancellationToken);

        detail.Found.Should().BeTrue(detail.NotFound?.Message);
        detail.Detail!.Id.Should().Be(page.Rows[0].Id);
        detail.Detail.Json.Should().StartWith("{");
    }

    [PostgresFact]
    public async Task An_id_that_does_not_fit_the_column_is_a_friendly_not_found_rather_than_a_throw()
    {
        using var documents = Documents();

        DocumentDetailResult detail = await documents.Service.GetDocumentAsync(
            Scope, "customer", "not-a-guid", TestContext.Current.CancellationToken);

        detail.Found.Should().BeFalse();
        detail.NotFound!.IdWasMalformed.Should().BeTrue();
        detail.NotFound.Message.Should().Contain("uuid");
        detail.NotFound.Sql.Should().Contain("mt_doc_customer");
    }

    [PostgresFact]
    public async Task The_soft_delete_tri_state_answers_three_different_questions()
    {
        using var documents = Documents();

        // The sample seeder soft-deletes three of fifteen orders.
        DocumentPage live = await Orders(documents, DeletedFilter.Exclude);
        DocumentPage all = await Orders(documents, DeletedFilter.Include);
        DocumentPage deleted = await Orders(documents, DeletedFilter.Only);

        live.Rows.Should().HaveCount(12);
        all.Rows.Should().HaveCount(15);
        deleted.Rows.Should().HaveCount(3);
        deleted.Rows.Should().OnlyContain(x => x.IsDeleted);
    }

    private async Task<DocumentPage> Orders(MartenFixture.ScopedService<IDocumentDataService> documents, DeletedFilter filter) =>
        await documents.Service.ListAsync(
            Scope, "order", new DocumentListRequest { Deleted = filter, PageSize = 100 }, TestContext.Current.CancellationToken);

    [PostgresFact]
    public async Task A_conjoined_collection_shows_one_tenant_or_all_of_them()
    {
        using var documents = Documents();

        DocumentPage acme = await Invoices(documents, "acme");
        DocumentPage globex = await Invoices(documents, "globex");
        DocumentPage everyone = await Invoices(documents, null);

        acme.Rows.Should().HaveCount(3);
        acme.Rows.Should().OnlyContain(x => x.TenantId == "acme");
        globex.Rows.Should().OnlyContain(x => x.TenantId == "globex");
        everyone.Rows.Should().HaveCount(acme.Rows.Count + globex.Rows.Count);
    }

    private static async Task<DocumentPage> Invoices(MartenFixture.ScopedService<IDocumentDataService> documents, string? tenantId) =>
        await documents.Service.ListAsync(
            MartenFixture.ScopeFor(tenantId),
            "invoice",
            new DocumentListRequest { PageSize = 100 },
            TestContext.Current.CancellationToken);

    [PostgresFact]
    public async Task A_subclass_lists_only_its_own_rows_out_of_the_shared_table()
    {
        using var documents = Documents();

        DocumentPage all = await documents.Service.ListAsync(
            Scope, "vehicle", new DocumentListRequest { PageSize = 100 }, TestContext.Current.CancellationToken);
        DocumentPage cars = await documents.Service.ListAsync(
            Scope, "car", new DocumentListRequest { PageSize = 100 }, TestContext.Current.CancellationToken);

        all.Rows.Should().HaveCount(10);
        cars.Rows.Should().HaveCount(6);
        cars.Rows.Should().OnlyContain(x => x.DocumentTypeAlias == "car");
        cars.Sql.Should().Contain("mt_doc_type");
    }

    /// <summary>
    /// P2-fix follow-up 10: the chip the verdict strip renders is a term the search box accepts.
    /// </summary>
    /// <remarks>
    /// Browsing <c>/marten/documents/car</c> adds a subclass predicate, which the strip writes back out as
    /// <c>type:car</c> — a syntax the grammar had no keyword for, so copying the chip into the search box
    /// produced a free-text search for the words "type:car" instead.
    /// </remarks>
    [PostgresFact]
    public async Task The_type_keyword_filters_a_hierarchy_the_way_the_verdict_chip_spells_it()
    {
        using var documents = Documents();

        DocumentPage typed = await documents.Service.ListAsync(
            Scope,
            "vehicle",
            new DocumentListRequest { Search = "type:car", PageSize = 100 },
            TestContext.Current.CancellationToken);

        typed.State.Should().Be(DocumentListState.Loaded, typed.Error);
        typed.Rows.Should().HaveCount(6);
        typed.Rows.Should().OnlyContain(x => x.DocumentTypeAlias == "car");
        typed.Sql.Should().Contain("mt_doc_type");

        // Green, because Marten 9.35's DocumentTable adds an index on mt_doc_type for a hierarchy - so the
        // advisor's own fallback ("Marten creates no index on it", which would be amber) is not reached
        // here. Either way the read is not withheld, which is what the assertion above is about.
        typed.Verdict.FilterLevel.Should().NotBe(IndexVerdictLevel.Red);
        typed.Verdict.Chips.Should().Contain(x => x.Text == "type:car");
    }

    [PostgresFact]
    public async Task A_document_with_every_metadata_column_reports_every_one_of_them()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "auditnote", new DocumentListRequest(), TestContext.Current.CancellationToken);

        page.AvailableColumns.Select(x => x.Label).Should().Contain(
            ["last modified", "created at", "causation", "correlation", "modified by", "headers"]);

        DocumentDetailResult detail = await documents.Service.GetDocumentAsync(
            Scope, "auditnote", page.Rows[0].Id, TestContext.Current.CancellationToken);

        detail.Detail!.Columns.Select(x => x.Name).Should().Contain(
            ["id", "data", "mt_last_modified", "mt_created_at", "causation_id", "correlation_id", "last_modified_by", "headers"]);
    }

    /// <summary>
    /// The other end of the range: <c>DisableInformationalFields()</c> leaves a table of <c>id</c> and
    /// <c>data</c>, and a select list that assumed <c>mt_last_modified</c> would fail on every render.
    /// </summary>
    [PostgresFact]
    public async Task A_document_with_no_metadata_columns_at_all_still_lists_and_opens()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "minimalnote", new DocumentListRequest(), TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Rows.Should().ContainSingle();
        page.AvailableColumns.Select(x => x.Key).Should().Equal("id");
        page.SortKey.Should().Be("id", "there is no mt_last_modified to sort by");

        DocumentDetailResult detail = await documents.Service.GetDocumentAsync(
            Scope, "minimalnote", page.Rows[0].Id, TestContext.Current.CancellationToken);

        detail.Detail!.Columns.Select(x => x.Name).Should().Equal("id", "data");
    }

    [PostgresFact]
    public async Task A_two_megabyte_document_is_not_inlined_in_a_list_but_is_read_in_full_on_the_detail_page()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "mediaasset", new DocumentListRequest(), TestContext.Current.CancellationToken);

        DocumentRow row = page.Rows.Should().ContainSingle().Subject;

        row.SizeBytes.Should().BeGreaterThan(1_500_000);
        row.Json.Should().BeNull("it is over MaxInlineDocumentBytes, so the list reports a size instead");

        DocumentDetailResult detail = await documents.Service.GetDocumentAsync(
            Scope, "mediaasset", row.Id, TestContext.Current.CancellationToken);

        detail.Detail!.Json.Length.Should().BeGreaterThan(1_500_000);
        detail.Detail.TextBytes.Should().Be(row.SizeBytes);
        detail.Detail.StoredBytes.Should().BeGreaterThan(0);
        detail.Detail.StoredBytes.Should().BeLessThan(detail.Detail.TextBytes, "jsonb is TOASTed and compressed");
    }

    [PostgresFact]
    public async Task The_metadata_pane_names_the_table_and_the_upsert_function_Marten_actually_made()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest(), TestContext.Current.CancellationToken);

        DocumentDetail detail = (await documents.Service.GetDocumentAsync(
            Scope, "customer", page.Rows[0].Id, TestContext.Current.CancellationToken)).Detail!;

        detail.TableName.Should().Be($"\"{Schema}\".\"mt_doc_customer\"");

        // Marten 9 writes documents with inline SQL and creates no per-document upsert function, so the
        // pane says "none - written with inline SQL" rather than naming an object psql would not find.
        //
        // The studio no longer asks the database this question - IMartenDatabase.Functions() is
        // AllObjects(), which applies Marten's HiLo migration, and a detail page may not run DDL (P2-fix
        // B4). This test asks it instead, and is now the anti-vacuity proof for that constant: a Marten
        // that brought the upsert functions back would fail here rather than leave the pane quietly wrong.
        IReadOnlyList<Weasel.Core.DbObjectName> functions = await Store.Storage.Database.Functions();
        functions.Should().NotContain(x => x.Name == "mt_upsert_customer");
        detail.UpsertFunction.Should().BeNull();
    }

    /// <summary>
    /// P2-fix H3: a subclass row is compared against its own type, not against the hierarchy's root.
    /// </summary>
    /// <remarks>
    /// Every row of <c>mt_doc_vehicle</c> holds <c>MartenStudio.SampleDomain.Documents.Car</c> or
    /// <c>…Truck</c> in <c>mt_dotnet_type</c>, and the root mapping is <c>Vehicle</c>. Comparing the two
    /// reported a type mismatch on every subclass row in the store, which is the warning that is supposed
    /// to mean something has genuinely gone wrong.
    /// </remarks>
    [PostgresFact]
    public async Task A_subclass_row_read_through_its_root_is_not_a_dotnet_type_mismatch()
    {
        using var documents = Documents();

        DocumentPage vehicles = await documents.Service.ListAsync(
            Scope, "vehicle", new DocumentListRequest { PageSize = 100 }, TestContext.Current.CancellationToken);

        vehicles.Rows.Should().NotBeEmpty();

        foreach (DocumentRow row in vehicles.Rows)
        {
            DocumentDetail detail = (await documents.Service.GetDocumentAsync(
                Scope, "vehicle", row.Id, TestContext.Current.CancellationToken)).Detail!;

            detail.DotNetTypeMismatch.Should().BeFalse(
                "row '{0}' is a {1} and mt_dotnet_type says so", row.Id, row.DocumentTypeAlias);
            detail.DotNetTypeWarning.Should().BeNull();
            detail.ExpectedDotNetType.Should().Be(detail.StoredDotNetType!.Split(',')[0]);
        }

        // ... and reading the very same row through the subclass alias says the same thing.
        DocumentPage cars = await documents.Service.ListAsync(
            Scope, "car", new DocumentListRequest(), TestContext.Current.CancellationToken);

        DocumentDetail car = (await documents.Service.GetDocumentAsync(
            Scope, "car", cars.Rows[0].Id, TestContext.Current.CancellationToken)).Detail!;

        car.DotNetTypeMismatch.Should().BeFalse();
        car.ExpectedDotNetType.Should().EndWith(".Car");
    }

    /// <summary>
    /// The other half of H3: a row whose <c>mt_dotnet_type</c> names a type in another namespace really is
    /// a mismatch. The old comparison forgave it, because it accepted any stored name whose last segment
    /// matched — which was how it avoided flagging every subclass row, and which made the check vacuous.
    /// </summary>
    [PostgresFact]
    public async Task A_row_whose_dotnet_type_names_another_namespace_is_a_mismatch()
    {
        using var documents = Documents();

        DocumentPage cars = await documents.Service.ListAsync(
            Scope, "car", new DocumentListRequest(), TestContext.Current.CancellationToken);

        var id = cars.Rows[0].Id;

        DocumentDetail before = (await documents.Service.GetDocumentAsync(
            Scope, "car", id, TestContext.Current.CancellationToken)).Detail!;

        var original = before.StoredDotNetType!;

        try
        {
            await SetColumnAsync("mt_doc_vehicle", "mt_dotnet_type", id, "Somewhere.Else.Car, Somewhere.Else");

            DocumentDetail drifted = (await documents.Service.GetDocumentAsync(
                Scope, "car", id, TestContext.Current.CancellationToken)).Detail!;

            drifted.DotNetTypeMismatch.Should().BeTrue("a Car that says it lives in another namespace is not a Car");
        }
        finally
        {
            await SetColumnAsync("mt_doc_vehicle", "mt_dotnet_type", id, original);
        }
    }

    private async Task SetColumnAsync(string table, string column, string id, string value)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(
            $"update \"{Schema}\".\"{table}\" set \"{column}\" = @value where id = @id",
            connection);

        command.Parameters.AddWithValue("value", value);
        command.Parameters.AddWithValue("id", Guid.Parse(id));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [PostgresFact]
    public async Task A_duplicated_column_written_by_Marten_agrees_with_the_json_it_shadows()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest(), TestContext.Current.CancellationToken);

        DocumentDetail detail = (await documents.Service.GetDocumentAsync(
            Scope, "customer", page.Rows[0].Id, TestContext.Current.CancellationToken)).Detail!;

        DuplicatedFieldAgreement email = detail.Duplicated.Should().ContainSingle(x => x.ColumnName == "email").Subject;

        email.State.Should().Be(AgreementState.Agrees);
        email.ColumnValue.Should().Be(email.JsonValue);
    }

    [PostgresFact]
    public async Task A_duplicated_column_written_behind_Martens_back_is_reported_as_drift()
    {
        // The whole point of the agreement dot: a row updated with plain SQL keeps its JSON and changes
        // its column, and no other tool will tell you.
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest(), TestContext.Current.CancellationToken);

        var id = page.Rows[0].Id;

        DocumentDetail before = (await documents.Service.GetDocumentAsync(
            Scope, "customer", id, TestContext.Current.CancellationToken)).Detail!;

        var original = before.Duplicated.Single(x => x.ColumnName == "email").ColumnValue!;

        try
        {
            await SetEmailColumnAsync(id, "drifted@example.com");

            DocumentDetail detail = (await documents.Service.GetDocumentAsync(
                Scope, "customer", id, TestContext.Current.CancellationToken)).Detail!;

            detail.Duplicated.Single(x => x.ColumnName == "email").State.Should().Be(AgreementState.Differs);
        }
        finally
        {
            // The store is built once for the whole class now, so a test that writes has to put the row
            // back: the agreement test above reads the same first row and would otherwise pass or fail
            // depending on the order xunit happened to run them in.
            await SetEmailColumnAsync(id, original);
        }
    }

    private async Task SetEmailColumnAsync(string id, string email)
    {
        await using NpgsqlConnection connection = await Postgres.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = new NpgsqlCommand(
            $"update \"{Schema}\".\"mt_doc_customer\" set email = @email where id = @id",
            connection);

        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("id", Guid.Parse(id));

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [PostgresFact]
    public async Task Related_documents_follow_the_declared_foreign_key()
    {
        using var documents = Documents();

        DocumentPage orders = await documents.Service.ListAsync(
            Scope, "order", new DocumentListRequest(), TestContext.Current.CancellationToken);

        RelatedDocuments related = await documents.Service.GetRelatedAsync(
            Scope, "order", orders.Rows[0].Id, TestContext.Current.CancellationToken);

        RelatedDocumentLink link = related.Links.Should().ContainSingle().Subject;

        link.TargetAlias.Should().Be("customer");
        link.ColumnName.Should().Be("customer_id");
        link.TargetId.Should().NotBeNull();
        link.Exists.Should().BeTrue();
    }

    [PostgresFact]
    public async Task A_document_id_that_is_not_a_stream_does_not_offer_a_view_stream_link()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest(), TestContext.Current.CancellationToken);

        var exists = await documents.Service.StreamExistsAsync(
            Scope, page.Rows[0].Id, TestContext.Current.CancellationToken);

        exists.Should().BeFalse();
    }

    [PostgresFact]
    public async Task The_recent_pseudo_collection_mixes_every_collection_that_keeps_a_last_modified()
    {
        using var documents = Documents();

        IReadOnlyList<RecentDocument> recent = await documents.Service.GetRecentAsync(
            Scope, 40, TestContext.Current.CancellationToken);

        recent.Should().NotBeEmpty();
        recent.Select(x => x.Alias).Distinct().Should().HaveCountGreaterThan(1);
        recent.Should().BeInDescendingOrder(x => x.LastModified);

        // minimalnote has no mt_last_modified, so there is no honest way to place it in this list.
        recent.Should().NotContain(x => x.Alias == "minimalnote");
    }

    [PostgresFact]
    public async Task Probing_an_id_reports_only_the_collections_whose_column_could_hold_it()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest(), TestContext.Current.CancellationToken);

        IReadOnlyList<DocumentIdProbe> probes = await documents.Service.ProbeIdAsync(
            Scope, page.Rows[0].Id, TestContext.Current.CancellationToken);

        probes.Should().Contain(x => x.Alias == "customer" && x.Found);
        probes.Should().NotContain(x => x.Alias == "invoice", "an int4 id column cannot hold a GUID");
        probes.Should().Contain(x => x.Alias == "auditnote" && !x.Found);
    }

    [PostgresFact]
    public async Task A_search_that_an_index_serves_is_green_and_runs_without_being_asked_twice()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest { Search = "Email = customer01@example.com" },
            TestContext.Current.CancellationToken);

        page.Verdict.FilterLevel.Should().Be(IndexVerdictLevel.Green, "Email is duplicated with a unique index");
        page.State.Should().Be(DocumentListState.Loaded);
        page.Rows.Should().ContainSingle();
    }

    [PostgresFact]
    public async Task A_search_nothing_can_serve_is_described_but_not_run_until_it_is_asked_for()
    {
        using var documents = Documents();

        var request = new DocumentListRequest { Search = "Name ~ Customer 0" };

        DocumentPage withheld = await documents.Service.ListAsync(
            Scope, "customer", request, TestContext.Current.CancellationToken);

        withheld.Verdict.FilterLevel.Should().Be(IndexVerdictLevel.Red);
        withheld.State.Should().Be(DocumentListState.BlockedByVerdict);
        withheld.Rows.Should().BeEmpty();
        withheld.Sql.Should().NotBeEmpty("the page still shows what it would have run");

        DocumentPage run = await documents.Service.ListAsync(
            Scope, "customer", request with { RunAnyway = true }, TestContext.Current.CancellationToken);

        run.State.Should().Be(DocumentListState.Loaded);
        run.Rows.Should().NotBeEmpty();
    }

    [PostgresFact]
    public async Task A_json_property_can_be_filtered_and_shown_as_a_column()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest
            {
                Search = "Address.City = Helsinki",
                ColumnKeys = ["id", "json:Address.City"],
                RunAnyway = true,
            },
            TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Rows.Should().NotBeEmpty();
        page.Columns.Select(x => x.Key).Should().Equal("id", "json:Address.City");
        page.Rows.Should().OnlyContain(x => x.Cells[1] == "Helsinki");
    }

    [PostgresFact]
    public async Task Containment_reads_the_document_the_way_a_gin_index_would()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope,
            "order",
            new DocumentListRequest { Search = """@> {"Status":"Shipped"}""", RunAnyway = true, PageSize = 100 },
            TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Rows.Should().NotBeEmpty();
    }

    [PostgresFact]
    public async Task The_json_suggestions_come_from_the_documents_that_were_loaded()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "customer", new DocumentListRequest { PageSize = 10 }, TestContext.Current.CancellationToken);

        page.JsonSuggestions.Select(x => x.Name).Should().Contain(["Name", "Email", "Address", "Tags"]);
        page.JsonSuggestions.Should().OnlyContain(x => x.Frequency <= 10);
    }

    [PostgresFact]
    public async Task Sorting_by_a_duplicated_column_orders_by_it_and_keeps_the_id_as_the_tiebreaker()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest
            {
                SortKey = "dup:email",
                Direction = SortDirection.Ascending,
                ColumnKeys = ["id", "dup:email"],
                PageSize = 100,
                RunAnyway = true,
            },
            TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Loaded, page.Error);
        page.Sql.Should().Contain("nulls last");
        page.Rows.Select(x => x.Cells[1]).Should().BeInAscendingOrder();
    }

    /// <summary>
    /// P2-fix follow-up 2 (the ledger's W2-fix-2): a filter this collection cannot answer is a chip, not a
    /// stack trace.
    /// </summary>
    /// <remarks>
    /// Each of these parses perfectly — the grammar has no way to know whether a collection is
    /// soft-deleted, conjoined, or what shape its id column is — and used to be discovered only when the
    /// query builder threw, which happened after the verdict strip had been composed. The page rendered
    /// <c>ArgumentException.Message</c>, parameter name and all, beside a green badge.
    /// </remarks>
    [PostgresTheory]
    [InlineData("is:deleted", "*not soft-deleted*")]
    [InlineData("tenant:acme", "*not conjoined-tenanted*")]
    [InlineData("id:not-a-guid", "*not a GUID*")]
    public async Task A_filter_this_collection_cannot_answer_is_a_grammar_error_and_not_an_exception(
        string search,
        string expected)
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope,
            "customer",
            new DocumentListRequest { Search = search, RunAnyway = true },
            TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.BlockedByVerdict);
        page.Error.Should().BeNull("a refusal is a verdict, not a failure of the page");
        page.Rows.Should().BeEmpty();

        page.Verdict.HasErrors.Should().BeTrue();
        page.Verdict.Level.Should().Be(IndexVerdictLevel.Red);
        page.Verdict.FilterLevel.Should().Be(IndexVerdictLevel.Red, "the badge must not stay green");

        SearchGrammarError error = page.Verdict.Errors.Should().ContainSingle().Subject;

        error.Message.Should().Match(expected);
        error.Message.Should().NotContain("Parameter", "a C# parameter name is not something to show a visitor");
    }

    [PostgresFact]
    public async Task A_collection_that_does_not_exist_is_a_value_rather_than_a_throw()
    {
        using var documents = Documents();

        DocumentPage page = await documents.Service.ListAsync(
            Scope, "no_such_collection", new DocumentListRequest(), TestContext.Current.CancellationToken);

        page.State.Should().Be(DocumentListState.Failed);
        page.Error.Should().Contain("no_such_collection");
    }

    [PostgresFact]
    public async Task The_sample_store_is_the_one_the_sample_host_configures()
    {
        // A guard on the fixture itself: if SampleStore ever stops registering these, every test above
        // would still pass while testing nothing.
        Store.Options.AllKnownDocumentTypes().Select(x => x.Alias).Should().Contain(
            ["customer", "order", "invoice", "product", "vehicle", "auditnote", "minimalnote", "mediaasset"]);

        SampleStore.TenantIds.Should().Equal("acme", "globex");

        await Task.CompletedTask;
    }
}
