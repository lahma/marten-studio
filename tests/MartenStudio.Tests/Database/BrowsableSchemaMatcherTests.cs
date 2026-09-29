using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Acceptance 3: what <c>BrowsableSchemas</c> admits, against a live-shaped schema list.
/// </summary>
/// <remarks>
/// The list below is what <c>DatabaseCatalogQueries.SchemasSql</c> returns for a database with a Quartz
/// schema, a legacy schema, a TimescaleDB-style extension schema, a schema the role has no <c>USAGE</c> on,
/// and Postgres' own - which is exactly the set of cases <c>"*"</c> has to tell apart.
/// </remarks>
public class BrowsableSchemaMatcherTests
{
    private static readonly CatalogSchema[] Live =
    [
        new("information_schema", HasUsage: true, OwnedByExtension: false),
        new("pg_catalog", HasUsage: true, OwnedByExtension: false),
        new("pg_toast", HasUsage: true, OwnedByExtension: false),
        new("public", HasUsage: true, OwnedByExtension: false),
        new("quartz", HasUsage: true, OwnedByExtension: false),
        new("Legacy", HasUsage: true, OwnedByExtension: false),
        new("_timescaledb_catalog", HasUsage: true, OwnedByExtension: true),
        new("locked", HasUsage: false, OwnedByExtension: false),
    ];

    [Fact]
    public void Everything_admits_the_user_schemas_and_nothing_of_Postgres_an_extension_or_a_schema_without_usage()
    {
        IReadOnlySet<string> matched = BrowsableSchemaMatcher.Match(["*"], Live);

        matched.Should().BeEquivalentTo(["public", "quartz", "Legacy"]);

        matched.Should().NotContain("pg_catalog", "a system schema is never browsable");
        matched.Should().NotContain("information_schema", "a system schema is never browsable");
        matched.Should().NotContain("pg_toast", "a system schema is never browsable");
        matched.Should().NotContain("_timescaledb_catalog", "an extension owns it, not the host");
        matched.Should().NotContain("locked", "nothing in a schema without USAGE could be read");
    }

    [Fact]
    public void An_exact_name_matches_exactly_and_case_sensitively()
    {
        BrowsableSchemaMatcher.Match(["quartz"], Live).Should().BeEquivalentTo(["quartz"]);
        BrowsableSchemaMatcher.Match(["Legacy"], Live).Should().BeEquivalentTo(["Legacy"]);

        BrowsableSchemaMatcher.Match(["legacy"], Live).Should().BeEmpty(
            "Postgres stores \"Legacy\" with its capital, and a lower-case entry names a different schema");
        BrowsableSchemaMatcher.Match(["QUARTZ"], Live).Should().BeEmpty();
        BrowsableSchemaMatcher.Match(["quart"], Live).Should().BeEmpty("it is a name, not a prefix");
    }

    [Fact]
    public void An_exact_name_may_admit_an_extension_schema_but_never_a_system_one_or_one_without_usage()
    {
        BrowsableSchemaMatcher.Match(["_timescaledb_catalog"], Live).Should().BeEquivalentTo(
            ["_timescaledb_catalog"], "naming an extension's schema is a host saying it wants it");

        BrowsableSchemaMatcher.Match(["pg_catalog", "information_schema"], Live).Should().BeEmpty();
        BrowsableSchemaMatcher.Match(["locked"], Live).Should().BeEmpty();
    }

    [Fact]
    public void Names_beside_everything_add_nothing_and_an_empty_list_admits_nothing()
    {
        BrowsableSchemaMatcher.Match(["quartz", "*"], Live).Should()
            .BeEquivalentTo(BrowsableSchemaMatcher.Match(["*"], Live));

        BrowsableSchemaMatcher.Match([], Live).Should().BeEmpty();
    }

    [Theory]
    [InlineData("pg_catalog", true)]
    [InlineData("pg_toast", true)]
    [InlineData("pg_temp_3", true)]
    [InlineData("pg_toast_temp_3", true)]
    [InlineData("information_schema", true)]
    [InlineData("public", false)]
    [InlineData("pgx", false)]
    [InlineData("Information_Schema", false)]
    public void System_schemas_are_told_apart_by_name(string schema, bool system) =>
        BrowsableSchemaMatcher.IsSystemSchema(schema).Should().Be(system);

    [Fact]
    public void Might_match_settles_what_the_options_alone_can()
    {
        BrowsableSchemaMatcher.MightMatch(["quartz"], "quartz").Should().BeTrue();
        BrowsableSchemaMatcher.MightMatch(["quartz"], "legacy").Should().BeFalse();
        BrowsableSchemaMatcher.MightMatch(["*"], "anything").Should().BeTrue("the live list still has to settle it");
        BrowsableSchemaMatcher.MightMatch(["*"], "pg_catalog").Should().BeFalse();
        BrowsableSchemaMatcher.MightMatch(["pg_catalog"], "pg_catalog").Should().BeFalse();
        BrowsableSchemaMatcher.MightMatch([], "public").Should().BeFalse();
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("quartz", true)]
    [InlineData("Legacy Schema", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("has\"quote", false)]
    [InlineData("has\0nul", false)]
    [InlineData("**", true)]
    public void An_entry_is_star_or_a_quotable_name(string entry, bool valid) =>
        BrowsableSchemaMatcher.IsValidEntry(entry).Should().Be(valid);

    [Fact]
    public void An_entry_longer_than_Postgres_allows_is_refused_by_its_utf8_bytes()
    {
        BrowsableSchemaMatcher.IsValidEntry(new string('a', 63)).Should().BeTrue();
        BrowsableSchemaMatcher.IsValidEntry(new string('a', 64)).Should().BeFalse();

        // 32 two-byte characters are 64 bytes: fine by length, too long for Postgres.
        BrowsableSchemaMatcher.IsValidEntry(new string('é', 32)).Should().BeFalse();
        BrowsableSchemaMatcher.IsValidEntry(new string('é', 31)).Should().BeTrue();
    }
}
