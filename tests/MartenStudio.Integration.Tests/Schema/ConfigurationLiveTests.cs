using MartenStudio.Services.Configuration;

namespace MartenStudio.Integration.Tests.Schema;

/// <summary>
/// The Configuration screen against a real store: what it reports has to be what the fixture
/// registered, and it must never be a credential.
/// </summary>
public class ConfigurationLiveTests(PostgresFixture fixture)
{
    [PostgresFact]
    public async Task The_configuration_dump_matches_what_the_fixture_registered()
    {
        await using SchemaFixture schema = await Start(nameof(The_configuration_dump_matches_what_the_fixture_registered));

        StudioConfiguration configuration = await schema.Studio
            .ConfigurationAsync(x => x.DescribeAsync(SchemaFixture.Scope));

        Value(configuration.Store.Values, "Document schema").Should().Be(schema.Schema);
        Value(configuration.Store.Values, "Auto-create").Should().Be("None", "the fixture registers AutoCreate.None");
        Value(configuration.Events.Values, "Event schema").Should().Be(schema.EventSchema);
        configuration.PostgresVersion.Should().NotBeNullOrWhiteSpace();

        // Marten registers DeadLetterEvent as a document type of its own once an event store is
        // configured, so the screen reports five types for four registrations - which is the truth about
        // the store and exactly the sort of thing this screen exists to make visible.
        configuration.DocumentTypes.Select(x => x.Alias).Should()
            .Contain(["customer", "invoice", "note", "order"])
            .And.BeInAscendingOrder(StringComparer.Ordinal);

        DocumentTypeConfiguration customer = configuration.DocumentTypes.Single(x => x.Alias == "customer");
        customer.TypeName.Should().Be(nameof(SchemaCustomer));
        customer.Table.Should().Be(schema.Schema + ".mt_doc_customer");
        customer.DuplicatedFields.Should().ContainSingle(x => x.Member == "Email" && x.Column == "email");
        customer.Indexes.Should().Contain(x => x.Name == "mt_doc_customer_idx_email");

        Value(configuration.DocumentTypes.Single(x => x.Alias == "order").Values, "Delete style")
            .Should().Be("SoftDelete");
        Value(configuration.DocumentTypes.Single(x => x.Alias == "customer").Values, "Delete style")
            .Should().Be("Remove");
        Value(configuration.DocumentTypes.Single(x => x.Alias == "invoice").Values, "Tenancy")
            .Should().Be("Conjoined");
    }

    [PostgresFact]
    public async Task The_databases_are_named_by_server_port_database_and_schema_and_never_by_a_credential()
    {
        await using SchemaFixture schema = await Start(nameof(The_databases_are_named_by_server_port_database_and_schema_and_never_by_a_credential));

        StudioConfiguration configuration = await schema.Studio
            .ConfigurationAsync(x => x.DescribeAsync(SchemaFixture.Scope));

        configuration.Store.DatabaseNotice.Should().BeNull();
        configuration.Store.Databases.Should().NotBeEmpty();

        ConfiguredDatabase database = configuration.Store.Databases[0];
        database.Server.Should().NotBeNullOrWhiteSpace();
        database.DatabaseName.Should().NotBeNullOrWhiteSpace();

        // The one that has to hold: nothing this screen renders may be, or contain, a connection string.
        //
        // The grep is for the keyword rather than for the password's value on purpose. Testcontainers'
        // Postgres uses "postgres" as the password, the user and the database name, so a value grep here
        // would fail on the database name and prove nothing. ConfigurationDescriberTests does the value
        // grep, against a store whose password is a string that appears nowhere else.
        string[] rendered = [.. Everything(configuration)];

        rendered.Should().NotBeEmpty("a vacuous grep is a broken test");
        rendered.Should().NotContain(x => x.Contains("assword", StringComparison.OrdinalIgnoreCase));
        rendered.Should().NotContain(x => x.Contains(schema.ConnectionString, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Everything(StudioConfiguration configuration)
    {
        foreach (ConfigurationValue value in configuration.Store.Values)
        {
            yield return value.Name;
            yield return value.Value;
            yield return value.Member;
        }

        foreach (ConfiguredDatabase database in configuration.Store.Databases)
        {
            yield return database.Identity;
            yield return database.Engine;
            yield return database.Server;
            yield return database.DatabaseName;
            yield return database.Schema;

            foreach (string tenant in database.TenantIds)
            {
                yield return tenant;
            }
        }

        foreach (ConfigurationValue value in configuration.Events.Values
                     .Concat(configuration.Events.Metadata)
                     .Concat(configuration.Events.Daemon))
        {
            yield return value.Value;
        }

        foreach (DocumentTypeConfiguration documentType in configuration.DocumentTypes)
        {
            yield return documentType.Alias;
            yield return documentType.FullTypeName;
            yield return documentType.Table;

            foreach (ConfigurationValue value in documentType.Values)
            {
                yield return value.Value;
            }

            foreach (ConfiguredIndex index in documentType.Indexes)
            {
                yield return index.Definition;
            }
        }
    }

    private static string Value(IReadOnlyList<ConfigurationValue> values, string name)
    {
        ConfigurationValue? found = values.FirstOrDefault(x => x.Name == name);

        return found?.Value ?? throw new InvalidOperationException(
            $"No configured value named '{name}'. There were: {string.Join(", ", values.Select(x => x.Name))}");
    }

    private Task<SchemaFixture> Start(string name) =>
        SchemaFixture.StartAsync(fixture, "p7cfg_" + name.ToLowerInvariant()[..Math.Min(name.Length, 40)]);
}
