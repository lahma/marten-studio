using MartenStudio.Tests.Support;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Find;
using PostgresStudio.Services.Postgres;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// 1.2-SCOPE-3: Find's tables and views. The contributor asks the database browser's own list, which is the gate: a schema the
/// visitor may not see is not in the list, so it cannot be found, and the schema is never passed, so nothing is refused and nothing is
/// audited.
/// </summary>
public class RelationFindContributorTests
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static RelationFindContributor Contributor(FakeDatabaseObjectService objects) => new(
        objects,
        new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
        PostgresStudioDescriptor.Instance);

    [Fact]
    public async Task An_exact_name_is_a_hit_that_opens_the_object_in_the_browser()
    {
        FakeDatabaseObjectService objects = new();

        FindContribution found = await Contributor(objects).FindAsync(Scope, FindQuery.Parse("qrtz_triggers"), CancellationToken.None);

        FindHit hit = found.Hits.Should().ContainSingle().Subject;
        hit.Kind.Should().Be("Table");
        hit.Title.Should().Be("quartz.qrtz_triggers");
        hit.Url.Should().Contain("database/object").And.Contain("qrtz_triggers").And.Contain("quartz");
        hit.Scope.Should().Be(Scope);
        found.Searched.Should().Be("tables and views");
    }

    [Fact]
    public async Task A_view_is_found_under_its_own_kind()
    {
        FindContribution found = await Contributor(new FakeDatabaseObjectService()).FindAsync(Scope, FindQuery.Parse("legacy.order_totals"), CancellationToken.None);

        found.Hits.Should().ContainSingle().Which.Kind.Should().Be("View");
    }

    [Fact]
    public async Task The_match_is_exact_and_ignores_case_so_a_part_of_a_name_finds_nothing()
    {
        FakeDatabaseObjectService objects = new();
        RelationFindContributor contributor = Contributor(objects);

        (await contributor.FindAsync(Scope, FindQuery.Parse("QRTZ_TRIGGERS"), CancellationToken.None)).Hits.Should().ContainSingle();
        (await contributor.FindAsync(Scope, FindQuery.Parse("qrtz"), CancellationToken.None)).Hits.Should().BeEmpty();
        (await contributor.FindAsync(Scope, FindQuery.Parse("triggers"), CancellationToken.None)).Hits.Should().BeEmpty();
    }

    [Fact]
    public async Task A_schema_beside_the_name_is_compared_here_and_never_passed_to_the_list()
    {
        FakeDatabaseObjectService objects = new();
        RelationFindContributor contributor = Contributor(objects);

        (await contributor.FindAsync(Scope, FindQuery.Parse("quartz.qrtz_triggers"), CancellationToken.None)).Hits.Should().ContainSingle();
        (await contributor.FindAsync(Scope, FindQuery.Parse("legacy.qrtz_triggers"), CancellationToken.None)).Hits.Should().BeEmpty();

        objects.Queries.Should().NotBeEmpty();
        objects.Queries.Should().OnlyContain(static x => x.Schema == null, "a schema the gate withholds would be a refusal the service audits");
        objects.Queries.Select(static x => x.Category).Distinct().Should().BeEquivalentTo([DatabaseObjectCategory.Tables, DatabaseObjectCategory.Views]);
        objects.Queries.Should().OnlyContain(static x => x.Limit == RelationFindContributor.ListLimit);
    }

    [Fact]
    public async Task An_object_in_a_schema_the_visitor_may_not_see_is_not_in_the_list_so_it_is_not_found()
    {
        FakeDatabaseObjectService objects = new()
        {
            List = FakeDatabaseObjects.List(new DatabaseObjectQuery(DatabaseObjectCategory.Tables, NameFilter: "orders"), DatabaseRefusal.SchemaNotBrowsable),
        };

        FindContribution found = await Contributor(objects).FindAsync(Scope, FindQuery.Parse("orders"), CancellationToken.None);

        found.Hits.Should().BeEmpty();
        found.Notes.Should().BeEmpty("a withheld schema reads exactly as no match");
    }

    [Theory]
    [InlineData("#12")]
    [InlineData("12")]
    [InlineData("2f6c0e0a-1b2c-4d3e-8f90-123456789abc")]
    [InlineData("")]
    public async Task A_value_that_is_not_shaped_like_a_name_is_not_this_kinds_and_reads_nothing(string typed)
    {
        FakeDatabaseObjectService objects = new();

        FindContribution found = await Contributor(objects).FindAsync(Scope, FindQuery.Parse(typed), CancellationToken.None);

        found.Should().Be(FindContribution.NotApplicable);
        objects.Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_list_that_could_not_be_read_is_a_note_and_no_hit()
    {
        FakeDatabaseObjectService objects = new()
        {
            List = DatabaseObjectList.Refused(new DatabaseObjectQuery(DatabaseObjectCategory.Tables), DatabaseRefusal.Unavailable, "The catalog could not be read."),
        };

        FindContribution found = await Contributor(objects).FindAsync(Scope, FindQuery.Parse("orders"), CancellationToken.None);

        found.Hits.Should().BeEmpty();
        found.Notes.Should().Equal("Tables and views: could not be searched.");
    }
}
