using JasperFx;

using Marten;
using Marten.Schema;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Tests.Configuration;

/// <summary>
/// The Configuration page's mapping, against a real <c>DocumentStore</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>DocumentStore.For</c> never opens a connection and the mappings are compiled lazily, so the whole
/// suite runs against Marten's own configuration objects with no database anywhere - the same trick
/// <c>MartenApiSurfaceTest</c> and <c>SqlTestStore</c> use. That matters more here than anywhere else in
/// the studio: this screen's entire claim is that it reports what the host configured, and a test
/// against a hand-written fake would agree with the test and disagree with Marten.
/// </para>
/// <para>
/// The connection string carries a password on purpose. <see cref="No_rendered_value_anywhere_contains_a_password" />
/// is the reason.
/// </para>
/// </remarks>
public class ConfigurationDescriberTests
{
    private const string Password = "hunter2-do-not-render-me";

    private const string Schema = "studio_config";

    private static readonly string Unreachable =
        $"Host=marten-studio-config-tests.invalid;Port=5432;Database=none;Username=none;Password={Password}";

    [Fact]
    public void The_store_card_names_the_StoreOptions_member_behind_every_value()
    {
        StoreConfiguration store = DescribeStore();

        store.Values.Should().OnlyContain(x => x.Member.Length > 0);
        Value(store, "Document schema").Value.Should().Be(Schema);
        Value(store, "Document schema").Member.Should().Be("StoreOptions.DatabaseSchemaName");
        Value(store, "Tenant id style").Member.Should().Be("StoreOptions.TenantIdStyle");
        Value(store, "Serializer").Value.Should().Contain("Json");
    }

    /// <summary>
    /// <c>AutoCreateSchemaObjects</c> is on the mutable <c>StoreOptions</c> only, so it is read from the
    /// database rather than from <c>IReadOnlyStoreOptions</c> - and when nothing supplies one, the card
    /// says "not reported" rather than guessing a default.
    /// </summary>
    [Fact]
    public void The_auto_create_mode_comes_from_the_database_and_is_not_invented_when_it_is_absent()
    {
        Value(DescribeStore(AutoCreate.CreateOrUpdate), "Auto-create").Value.Should().Be("CreateOrUpdate");
        Value(DescribeStore(autoCreate: null), "Auto-create").Value.Should().Be("not reported");
    }

    [Fact]
    public void The_event_store_card_carries_exactly_the_four_metadata_flags_Marten_has()
    {
        EventStoreConfiguration events = ConfigurationDescriber.DescribeEvents(Options.Events);

        events.Metadata.Select(x => x.Name).Should().Equal("Causation id", "Correlation id", "Headers", "User name");
        events.Metadata.Should().OnlyContain(x => x.Member.StartsWith("StoreOptions.Events.MetadataConfig.", StringComparison.Ordinal));

        Value(events.Values, "Stream identity").Member.Should().Be("StoreOptions.Events.StreamIdentity");
        Value(events.Values, "Event schema").Value.Should().Be(Schema + "_events");
        events.Daemon.Should().NotBeEmpty();
        Value(events.Daemon, "Async mode").Member.Should().Be("StoreOptions.Events.Daemon.AsyncMode");
    }

    [Fact]
    public void A_document_type_reports_its_identity_concurrency_tenancy_and_delete_style()
    {
        DocumentTypeConfiguration order = DocumentType("order");

        Value(order.Values, "Id member").Value.Should().Be("Id");
        Value(order.Values, "Id type").Value.Should().Be("Guid");
        Value(order.Values, "Id strategy").Value.Should().Contain("Guid");
        Value(order.Values, "Optimistic concurrency").Value.Should().Be("yes");
        Value(order.Values, "Tenancy").Value.Should().Be("Single");
    }

    /// <summary>
    /// The delete style is the one that cannot be read from the interface: <c>IDocumentType</c> has no
    /// <c>DeleteStyle</c>, and <c>Metadata.IsSoftDeleted.Enabled</c> reports true even for a type that is
    /// not soft-deleted at all (Appendix B addendum).
    /// </summary>
    [Fact]
    public void The_delete_style_tells_a_soft_deleted_type_from_one_that_merely_has_the_metadata_column()
    {
        Value(DocumentType("order").Values, "Delete style").Value.Should().Be("SoftDelete");
        Value(DocumentType("customer").Values, "Delete style").Value.Should().Be("Remove");

        Options.FindOrResolveDocumentType(typeof(ConfigCustomer))
            .Metadata.IsSoftDeleted.Enabled.Should().BeTrue(
                "which is exactly why the card does not read this property");
    }

    [Fact]
    public void The_metadata_column_list_is_the_structural_one_and_not_every_enabled_flag()
    {
        DocumentTypeConfiguration customer = DocumentType("customer");
        DocumentTypeConfiguration order = DocumentType("order");
        DocumentTypeConfiguration invoice = DocumentType("invoice");

        order.MetadataColumns.Should().Contain("mt_deleted").And.Contain("mt_deleted_at");
        customer.MetadataColumns.Should().NotContain("mt_deleted");

        // tenant_id exists only on a conjoined type, mt_doc_type only on a hierarchy.
        invoice.MetadataColumns.Should().Contain("tenant_id");
        order.MetadataColumns.Should().NotContain("tenant_id");
        customer.MetadataColumns.Should().Contain("mt_doc_type", "customer is a hierarchy in this store");
    }

    [Fact]
    public void A_duplicated_field_reports_its_member_its_column_and_its_Postgres_type()
    {
        DocumentTypeConfiguration customer = DocumentType("customer");

        customer.DuplicatedFields.Should().ContainSingle();
        customer.DuplicatedFields[0].Member.Should().Be("Email");
        customer.DuplicatedFields[0].Column.Should().Be("email");
        customer.DuplicatedFields[0].PgType.Should().Be("varchar");
    }

    [Fact]
    public void An_index_is_reported_with_the_statement_Weasel_would_write_for_it()
    {
        DocumentTypeConfiguration customer = DocumentType("customer");

        customer.Indexes.Should().Contain(x => x.Name == "mt_doc_customer_idx_email");
        customer.Indexes.Single(x => x.Name == "mt_doc_customer_idx_email").Definition
            .Should().Contain("CREATE UNIQUE INDEX").And.Contain(Schema + ".mt_doc_customer");
    }

    [Fact]
    public void A_foreign_key_is_reported_with_what_it_points_at()
    {
        DocumentTypeConfiguration order = DocumentType("order");

        order.ForeignKeys.Should().ContainSingle();
        order.ForeignKeys[0].Columns.Should().Be("customer_id");
        order.ForeignKeys[0].LinkedTable.Should().Be(Schema + ".mt_doc_customer");
        order.ForeignKeys[0].LinkedColumns.Should().Be("id");
    }

    [Fact]
    public void A_hierarchy_lists_its_subclasses_by_alias_and_type()
    {
        DocumentTypeConfiguration customer = DocumentType("customer");

        customer.SubClasses.Should().ContainSingle();
        customer.SubClasses[0].TypeName.Should().Be(nameof(ConfigVipCustomer));
        customer.SubClasses[0].Alias.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Document_types_come_back_in_alias_order_and_honour_the_visibility_predicate()
    {
        IReadOnlyList<DocumentTypeConfiguration> all = ConfigurationDescriber.DescribeDocumentTypes(Options);

        all.Select(x => x.Alias).Should().BeInAscendingOrder(StringComparer.Ordinal);

        IReadOnlyList<DocumentTypeConfiguration> filtered = ConfigurationDescriber.DescribeDocumentTypes(
            Options,
            static type => type != typeof(ConfigInvoice));

        filtered.Should().NotContain(x => x.Alias == "invoice");
        filtered.Should().Contain(x => x.Alias == "customer");
    }

    /// <summary>
    /// The one that has to hold: nothing this screen renders may carry a credential.
    /// </summary>
    /// <remarks>
    /// Greps every string of every DTO the describer produces, for a store whose connection string has a
    /// password in it. <c>DatabaseDescriptor</c> in particular carries a free-form <c>Properties</c>
    /// list, a <c>SubjectUri</c> and a <c>DatabaseUri()</c>, any of which a provider is free to build out
    /// of the connection string - which is why <see cref="ConfigurationDescriber.DescribeDatabase" />
    /// names five fields rather than mapping the object.
    /// </remarks>
    [Fact]
    public void No_rendered_value_anywhere_contains_a_password()
    {
        List<string> rendered = [];

        StoreConfiguration store = DescribeStore();
        foreach (ConfigurationValue value in store.Values)
        {
            rendered.Add(value.Name);
            rendered.Add(value.Value);
            rendered.Add(value.Member);
        }

        ConfiguredDatabase database = ConfigurationDescriber.DescribeDatabase(Store().Storage.Database.Describe());
        rendered.AddRange([database.Identity, database.Engine, database.Server, database.DatabaseName, database.Schema]);
        rendered.AddRange(database.TenantIds);

        EventStoreConfiguration events = ConfigurationDescriber.DescribeEvents(Options.Events);
        foreach (ConfigurationValue value in events.Values.Concat(events.Metadata).Concat(events.Daemon))
        {
            rendered.Add(value.Value);
        }

        foreach (DocumentTypeConfiguration documentType in ConfigurationDescriber.DescribeDocumentTypes(Options))
        {
            rendered.AddRange([documentType.Alias, documentType.TypeName, documentType.FullTypeName, documentType.Table]);
            rendered.AddRange(documentType.Values.Select(static x => x.Value));
            rendered.AddRange(documentType.Indexes.Select(static x => x.Definition));
            rendered.AddRange(documentType.ForeignKeys.Select(static x => x.LinkedTable));
            rendered.AddRange(documentType.MetadataColumns);
        }

        rendered.Should().NotBeEmpty("a vacuous grep is a broken test");
        rendered.Should().NotContain(x => x.Contains(Password, StringComparison.OrdinalIgnoreCase));
        rendered.Should().NotContain(x => x.Contains("Password=", StringComparison.OrdinalIgnoreCase));
        rendered.Should().NotContain(x => x.Contains("password=", StringComparison.Ordinal));
    }

    private static StoreConfiguration DescribeStore(AutoCreate? autoCreate = AutoCreate.CreateOrUpdate) =>
        ConfigurationDescriber.DescribeStore("default", "Default", Options, autoCreate, "17.2", [], null);

    private static DocumentTypeConfiguration DocumentType(string alias) =>
        ConfigurationDescriber.DescribeDocumentTypes(Options).Single(x => x.Alias == alias);

    private static ConfigurationValue Value(StoreConfiguration store, string name) => Value(store.Values, name);

    private static ConfigurationValue Value(IReadOnlyList<ConfigurationValue> values, string name)
    {
        ConfigurationValue? found = values.FirstOrDefault(x => x.Name == name);

        return found ?? throw new InvalidOperationException(
            $"No configured value named '{name}'. There were: {string.Join(", ", values.Select(x => x.Name))}");
    }

    /// <summary>
    /// The store's configuration as the read-only interface, which is what the describer takes.
    /// </summary>
    /// <remarks>
    /// <c>StoreOptions</c> implements <c>IReadOnlyStoreOptions</c> explicitly, so members like
    /// <c>FindOrResolveDocumentType</c> and the read-only <c>Events</c> are reachable only through the
    /// interface - which is the shape the studio always has, because <c>IDocumentStore.Options</c> is
    /// declared as the interface.
    /// </remarks>
    private static IReadOnlyStoreOptions Options => Store().Options;

    /// <summary>Never connected to. The host does not resolve, which is the point.</summary>
    private static DocumentStore Store() => DocumentStore.For(options =>
    {
        options.Connection(Unreachable);
        options.DatabaseSchemaName = Schema;
        options.Events.DatabaseSchemaName = Schema + "_events";

        options.Schema.For<ConfigCustomer>()
            .Duplicate(x => x.Email, configure: static index => index.IsUnique = true)
            .AddSubClass<ConfigVipCustomer>();

        options.Schema.For<ConfigOrder>()
            .SoftDeleted()
            .ForeignKey<ConfigCustomer>(x => x.CustomerId)
            .UseOptimisticConcurrency(true);

        options.Schema.For<ConfigInvoice>().MultiTenanted();
    });
}

/// <summary>A type with a duplicated unique field and a subclass.</summary>
[DocumentAlias("customer")]
public class ConfigCustomer
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

/// <summary>The subclass, so one mapping is a hierarchy.</summary>
public class ConfigVipCustomer : ConfigCustomer;

/// <summary>A soft-deleted type with a foreign key and optimistic concurrency.</summary>
[DocumentAlias("order")]
public class ConfigOrder
{
    public Guid Id { get; set; }

    public Guid CustomerId { get; set; }

    public decimal Total { get; set; }
}

/// <summary>A conjoined-tenancy type.</summary>
[DocumentAlias("invoice")]
public class ConfigInvoice
{
    public Guid Id { get; set; }

    public decimal Amount { get; set; }
}
