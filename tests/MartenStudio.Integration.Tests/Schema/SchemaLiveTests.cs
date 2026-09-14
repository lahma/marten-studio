using MartenStudio.Services;
using MartenStudio.Services.Schema;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// The Schema screen against a real Postgres, driven through <c>ISchemaDataService</c> so that the
/// capability gate, the write policy and the audit log are exercised rather than bypassed.
/// </summary>
/// <remarks>
/// Every test owns a schema, because two of them change the database and the order test methods run in
/// is not something to depend on.
/// </remarks>
public class SchemaLiveTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task A_database_that_matches_its_configuration_reports_a_match()
    {
        await using SchemaFixture schema = await Start(nameof(A_database_that_matches_its_configuration_reports_a_match));

        SchemaCheck check = await schema.Studio.SchemaAsync(x => x.CheckAsync(SchemaFixture.Scope));

        check.Status.Should().Be(SchemaCheckStatus.Matches);
        check.DifferenceCount.Should().Be(0);
        check.Objects.Should().BeEmpty();
        check.Schemas.Should().Contain(schema.Schema).And.Contain(schema.EventSchema);
        check.AssertionMessage.Should().NotBeNullOrWhiteSpace();
    }

    [PostgresFact]
    public async Task An_index_added_to_the_configuration_shows_as_a_difference_and_in_the_preview_SQL()
    {
        await using SchemaFixture schema = await Start(nameof(An_index_added_to_the_configuration_shows_as_a_difference_and_in_the_preview_SQL));

        SchemaFixture.StudioHost drifted = schema.Drifted();

        SchemaCheck check = await drifted.SchemaAsync(x => x.CheckAsync(SchemaFixture.Scope));
        check.Status.Should().Be(SchemaCheckStatus.Differences);
        check.DifferenceCount.Should().BeGreaterThan(0);
        check.Objects.Should().Contain(x => x.Name.Contains("mt_doc_customer", StringComparison.Ordinal));

        MigrationPreview preview = await drifted.SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));
        preview.HasSql.Should().BeTrue();
        preview.Sql.Should().Contain(SchemaFixture.DriftIndexName);
        preview.ObjectCount.Should().BeGreaterThan(0);
    }

    /// <summary>Acceptance: the preview reads the catalog and writes to a string. It changes nothing.</summary>
    [PostgresFact]
    public async Task Previewing_a_migration_leaves_the_database_exactly_as_it_was()
    {
        await using SchemaFixture schema = await Start(nameof(Previewing_a_migration_leaves_the_database_exactly_as_it_was));

        IReadOnlyList<string> before = await schema.IndexNamesAsync("mt_doc_customer");

        SchemaFixture.StudioHost drifted = schema.Drifted();
        MigrationPreview preview = await drifted.SchemaAsync(x => x.PreviewAsync(SchemaFixture.Scope));

        preview.Sql.Should().Contain(SchemaFixture.DriftIndexName);

        IReadOnlyList<string> after = await schema.IndexNamesAsync("mt_doc_customer");

        after.Should().Equal(before);
        after.Should().NotContain(SchemaFixture.DriftIndexName);
    }

    [PostgresFact]
    public async Task Applying_creates_the_index_and_writes_the_SQL_to_the_audit_log()
    {
        await using SchemaFixture schema = await Start(nameof(Applying_creates_the_index_and_writes_the_SQL_to_the_audit_log));

        SchemaFixture.StudioHost drifted = schema.Drifted(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.WriteAuthorizationPolicy = SchemaFixture.AllowPolicy;
        });

        string identity = await drifted.SchemaAsync(x => x.DatabaseIdentityAsync(SchemaFixture.Scope));
        identity.Should().NotBeNullOrWhiteSpace();

        SchemaApplyResult result = await drifted.SchemaAsync(x => x.ApplyAsync(SchemaFixture.Scope, identity));

        result.Succeeded.Should().BeTrue(result.Message);

        IReadOnlyList<string> after = await schema.IndexNamesAsync("mt_doc_customer");
        after.Should().Contain(SchemaFixture.DriftIndexName);

        // The database now matches the drifted configuration.
        SchemaCheck check = await drifted.SchemaAsync(x => x.CheckAsync(SchemaFixture.Scope));
        check.Status.Should().Be(SchemaCheckStatus.Matches);

        // Audit ring: the action, the capability, and the SQL that ran.
        StudioActionLogEntry entry = drifted.Audit.GetLatest()
            .First(x => x.Action == "Apply schema changes" && x.Succeeded);

        entry.Capability.Should().Be(nameof(StudioCapability.ApplySchemaChanges));
        entry.Message.Should().Contain(SchemaFixture.DriftIndexName);

        // And event 9206, which is the record that survives the process.
        SchemaFixture.StudioHost.LogEntries
            .Where(x => x.EventId == 9206)
            .Should().Contain(x => x.Message.Contains(SchemaFixture.DriftIndexName, StringComparison.Ordinal),
                "event 9206 carries the SQL that was applied");
    }

    [PostgresFact]
    public async Task Applying_is_refused_without_the_capability_and_the_refusal_names_the_option()
    {
        await using SchemaFixture schema = await Start(nameof(Applying_is_refused_without_the_capability_and_the_refusal_names_the_option));

        SchemaFixture.StudioHost drifted = schema.Drifted();
        string identity = await drifted.SchemaAsync(x => x.DatabaseIdentityAsync(SchemaFixture.Scope));

        Func<Task> apply = () => drifted.SchemaAsync(x => x.ApplyAsync(SchemaFixture.Scope, identity));

        StudioCapabilityDeniedException denied = (await apply.Should().ThrowAsync<StudioCapabilityDeniedException>()).Which;
        denied.Capability.Should().Be(StudioCapability.ApplySchemaChanges);
        denied.Message.Should().Contain("MartenStudioOptions.Capabilities.ApplySchemaChanges");

        (await schema.IndexNamesAsync("mt_doc_customer")).Should().NotContain(SchemaFixture.DriftIndexName);
    }

    [PostgresFact]
    public async Task Applying_is_refused_when_the_write_policy_says_no_even_with_the_capability_on()
    {
        await using SchemaFixture schema = await Start(nameof(Applying_is_refused_when_the_write_policy_says_no_even_with_the_capability_on));

        SchemaFixture.StudioHost drifted = schema.Drifted(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.WriteAuthorizationPolicy = SchemaFixture.DenyPolicy;
        });

        string identity = await drifted.SchemaAsync(x => x.DatabaseIdentityAsync(SchemaFixture.Scope));

        Func<Task> apply = () => drifted.SchemaAsync(x => x.ApplyAsync(SchemaFixture.Scope, identity));

        await apply.Should().ThrowAsync<StudioNotAuthorizedException>();

        (await schema.IndexNamesAsync("mt_doc_customer")).Should().NotContain(SchemaFixture.DriftIndexName);
    }

    [PostgresFact]
    public async Task Applying_is_refused_when_the_typed_confirmation_does_not_match_the_database()
    {
        await using SchemaFixture schema = await Start(nameof(Applying_is_refused_when_the_typed_confirmation_does_not_match_the_database));

        SchemaFixture.StudioHost drifted = schema.Drifted(options => options.Capabilities = MartenStudioCapabilities.All());

        Func<Task> apply = () => drifted.SchemaAsync(x => x.ApplyAsync(SchemaFixture.Scope, "not-the-database"));

        await apply.Should().ThrowAsync<InvalidOperationException>();

        (await schema.IndexNamesAsync("mt_doc_customer")).Should().NotContain(SchemaFixture.DriftIndexName);
    }

    [PostgresFact]
    public async Task The_tables_tab_lists_the_document_tables_with_their_sizes_and_row_estimates()
    {
        await using SchemaFixture schema = await Start(nameof(The_tables_tab_lists_the_document_tables_with_their_sizes_and_row_estimates));

        SchemaTables tables = await schema.Studio.SchemaAsync(x => x.TablesAsync(SchemaFixture.Scope));

        tables.Reason.Should().BeNull();
        tables.Schemas.Should().Contain(schema.Schema);
        tables.DatabaseBytes.Should().BeGreaterThan(0);

        TableStats customer = tables.Tables.Single(x => x.Table == "mt_doc_customer");
        customer.Schema.Should().Be(schema.Schema);
        customer.CollectionAlias.Should().Be("customer");
        customer.DocumentTypeName.Should().Be(nameof(SchemaCustomer));
        customer.TotalBytes.Should().BeGreaterThan(0);

        // reltuples is -1 until the table has been analyzed, which is a value and not an error (D8).
        customer.EstimatedRows.Should().BeGreaterThanOrEqualTo(-1);

        tables.Tables.Should().Contain(x => x.Table == "mt_doc_order" && x.CollectionAlias == "order");
        tables.Tables.Should().Contain(x => x.IsEventTable, "the event store's tables live in their own schema");

        // Sorted by total size, largest first.
        tables.Tables.Select(x => x.TotalBytes).Should().BeInDescendingOrder();
    }

    [PostgresFact]
    public async Task The_indexes_tab_flags_a_never_used_index_and_a_collection_with_only_a_primary_key()
    {
        await using SchemaFixture schema = await Start(nameof(The_indexes_tab_flags_a_never_used_index_and_a_collection_with_only_a_primary_key));

        SchemaIndexes indexes = await schema.Studio.SchemaAsync(x => x.IndexesAsync(SchemaFixture.Scope));

        indexes.Reason.Should().BeNull();
        indexes.Indexes.Should().NotBeEmpty();

        // Nothing has queried the store, so the duplicated-field index has no recorded scans.
        IndexInfo email = indexes.Indexes.Single(x => x.Name == "mt_doc_customer_idx_email");
        email.DeclaredByMarten.Should().BeTrue();
        email.Scans.Should().Be(0);
        email.CollectionAlias.Should().Be("customer");

        // The primary keys are declared too, even though Marten keeps them on the table rather than in
        // the index list - an Indexes tab that called every primary key undeclared would be useless.
        indexes.Indexes.Where(x => x.IsPrimaryKey).Should().OnlyContain(x => x.DeclaredByMarten);

        // Nothing is missing: the database was applied from this very configuration.
        indexes.Missing.Should().BeEmpty();

        UnindexedCollection note = indexes.Unindexed.Single(x => x.Alias == "note");
        note.Suggestions.Should().Contain(x => x.Contains("GinIndexJsonData()", StringComparison.Ordinal));
        note.Suggestions.Should().Contain(x => x.StartsWith("opts.Schema.For<SchemaNote>().Index(", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task The_indexes_tab_reports_an_index_the_configuration_asks_for_and_the_database_lacks()
    {
        await using SchemaFixture schema = await Start(nameof(The_indexes_tab_reports_an_index_the_configuration_asks_for_and_the_database_lacks));

        SchemaFixture.StudioHost drifted = schema.Drifted();

        SchemaIndexes indexes = await drifted.SchemaAsync(x => x.IndexesAsync(SchemaFixture.Scope));

        indexes.Missing.Should().Contain(x => x.Name == SchemaFixture.DriftIndexName);
        indexes.Missing.Single(x => x.Name == SchemaFixture.DriftIndexName).CollectionAlias.Should().Be("customer");
    }

    [PostgresFact]
    public async Task The_functions_tab_lists_Martens_own_functions_with_their_definitions()
    {
        await using SchemaFixture schema = await Start(nameof(The_functions_tab_lists_Martens_own_functions_with_their_definitions));

        SchemaFunctions functions = await schema.Studio.SchemaAsync(x => x.FunctionsAsync(SchemaFixture.Scope));

        functions.Reason.Should().BeNull();
        // Marten 9.35 no longer installs a per-document mt_upsert_<alias> function - the upsert is
        // written inline by its generated code - so what is here is the shared helper set plus the event
        // store's own functions. That is a fact about Marten rather than about the studio, and asserting
        // it here is what will tell us if it ever changes back.
        functions.Functions.Select(x => x.Name).Should()
            .Contain("mt_immutable_timestamp")
            .And.Contain("mt_jsonb_patch")
            .And.Contain("mt_quick_append_events")
            .And.NotContain(x => x.StartsWith("mt_upsert_", StringComparison.Ordinal));

        FunctionInfo appendEvents = functions.Functions.First(x => x.Name == "mt_quick_append_events");
        appendEvents.Schema.Should().Be(schema.EventSchema);
        appendEvents.DeclaredByMarten.Should().BeTrue();
        appendEvents.Definition.Should().Contain("CREATE OR REPLACE FUNCTION");
        appendEvents.Signature.Should().StartWith("mt_quick_append_events(");

        // And the tokenizer makes sense of the dollar-quoted body rather than swallowing the file.
        SqlDocument document = SqlTokenizer.Tokenize(appendEvents.Definition);
        document.Lines.Should().NotBeEmpty();
        document.Lines.SelectMany(x => x.Tokens).Should()
            .Contain(x => x.Kind == SqlTokenKind.String && x.Text.Contains("$function$", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task The_DDL_tab_produces_a_script_that_names_every_table_the_store_configures()
    {
        await using SchemaFixture schema = await Start(nameof(The_DDL_tab_produces_a_script_that_names_every_table_the_store_configures));

        DdlScript script = await schema.Studio.SchemaAsync(x => x.DdlAsync(SchemaFixture.Scope));

        script.Reason.Should().BeNull();
        script.HasText.Should().BeTrue();
        script.Bytes.Should().BeGreaterThan(0);

        script.Text.Should().Contain("mt_doc_customer");
        script.Text.Should().Contain("mt_doc_order");
        script.Text.Should().Contain("mt_doc_invoice");
        script.Text.Should().Contain("mt_doc_note");
        script.Text.Should().Contain(schema.Schema);
    }

    private Task<SchemaFixture> Start(string name) =>
        SchemaFixture.StartAsync(fixture, "p7_" + name.ToLowerInvariant()[..Math.Min(name.Length, 40)]);
}
