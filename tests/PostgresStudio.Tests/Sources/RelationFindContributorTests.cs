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

    /// <summary>A browser with more tables than one list holds: <paramref name="count" /> names that contain "orders", the exact one last.</summary>
    private sealed class CrowdedObjects(int count, int cap) : IDatabaseObjectService
    {
        public List<int> Limits { get; } = [];

        private static DatabaseRelationSummary Table(string name) => new(
            DatabaseObjectKind.Table, "app", name, new DatabaseObjectOwnership(DatabaseObjectOwner.Other), null, true, 10, 8192, true, 0, 0, 0, false, true, false, null, true, DatabaseRowAccess.Granted, false);

        public Task<DatabaseObjectList> ListAsync(StudioScope scope, DatabaseObjectQuery query, CancellationToken cancellationToken = default)
        {
            Limits.Add(query.Limit ?? 0);

            List<DatabaseObjectSummary> all = query.Category == DatabaseObjectCategory.Tables
                ? [.. Enumerable.Range(0, count).Select(i => (DatabaseObjectSummary) Table("orders_" + i.ToString("0000", System.Globalization.CultureInfo.InvariantCulture))), Table("orders")]
                : [];
            int limit = Math.Min(query.Limit ?? cap, cap);

            return Task.FromResult(new DatabaseObjectList(
                query with { Limit = limit },
                [.. all.Take(limit)],
                new DatabaseOwnerCounts(all.Count, 0, all.Count),
                all.Count > limit,
                limit,
                FakeDatabaseObjects.Access(),
                DatabaseRefusal.None,
                null));
        }

        public Task<DatabaseBrowserOverview> GetOverviewAsync(StudioScope scope, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseObjectDetail> GetObjectAsync(StudioScope scope, string schema, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseObjectDefinition> GetDefinitionAsync(StudioScope scope, DatabaseObjectRef reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<DatabaseSequenceValue> GetSequenceValueAsync(StudioScope scope, string schema, string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static RelationFindContributor Crowded(CrowdedObjects objects) => new(
        objects,
        new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
        PostgresStudioDescriptor.Instance);

    [Fact]
    public async Task An_exact_name_past_the_first_hundred_that_contain_it_is_still_found_with_one_ask_a_kind()
    {
        CrowdedObjects objects = new(count: 120, cap: 5000);

        FindContribution found = await Crowded(objects).FindAsync(Scope, FindQuery.Parse("orders"), CancellationToken.None);

        found.Hits.Should().ContainSingle().Which.Title.Should().Be("app.orders");
        found.Notes.Should().BeEmpty();
        objects.Limits.Should().Equal([5000, 5000], "one ask a kind, for as many as the browser reads: a second ask would run the same statement");
    }

    [Fact]
    public async Task A_name_found_in_the_first_list_asks_only_once_per_kind()
    {
        CrowdedObjects objects = new(count: 10, cap: 5000);

        (await Crowded(objects).FindAsync(Scope, FindQuery.Parse("orders"), CancellationToken.None)).Hits.Should().ContainSingle();

        objects.Limits.Should().Equal(RelationFindContributor.ListLimit, RelationFindContributor.ListLimit);
    }

    [Fact]
    public async Task When_even_the_biggest_list_is_full_and_holds_no_exact_name_the_note_says_the_search_could_not_be_exact()
    {
        CrowdedObjects objects = new(count: 300, cap: 100);

        FindContribution found = await Crowded(objects).FindAsync(Scope, FindQuery.Parse("orders"), CancellationToken.None);

        found.Hits.Should().BeEmpty();
        found.Notes.Should().ContainSingle().Which.Should().StartWith("Tables and views: more names contain this than the browser reads");
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
