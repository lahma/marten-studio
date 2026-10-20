using Bunit;

using MartenStudio.Components.Pages.Database;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Relationships;
using MartenStudio.Services.Settings;
using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Tests.Database;

/// <summary>
/// PS-1b's seams, from the side a studio other than MartenStudio sees them: the source parameter every browser link
/// carries is the descriptor's, a lock label needs no Marten store, and a link into a screen the studio does not have
/// is plain text rather than a link to nowhere.
/// </summary>
public class DatabaseSeamTests
{
    private static readonly StudioScope Scope = new("orders", "db1.orders", null);

    private static readonly IStudioSettings Settings = MartenStudioSettings.Of(new MartenStudioOptions());

    [Fact]
    public void Every_browser_link_carries_the_scope_under_the_descriptors_source_parameter()
    {
        StudioDescriptor descriptor = WithSourceParameter("source");

        string[] links =
        [
            DatabaseLinks.ToBrowser(Settings, descriptor, Scope),
            DatabaseLinks.ToObject(Settings, descriptor, Scope, "public", "orders"),
            DatabaseLinks.ToRows(Settings, descriptor, Scope, "public", "orders", "id = 1"),
            DatabaseLinks.ToRow(Settings, descriptor, Scope, "public", "orders", [new("id", "1")]),
            DatabaseLinks.ToRelationships(Settings, descriptor, Scope, RelationshipViewMode.Tables, "public"),
            DatabaseLinks.ToQueryConsole(Settings, descriptor, Scope, "select 1"),
            SessionLinks.ToSessions(Settings, descriptor, Scope),
        ];

        foreach (string link in links)
        {
            link.Should().Contain("source=orders").And.Contain("db=db1.orders").And.NotContain("store=", link);
        }
    }

    [Fact]
    public void MartenStudios_descriptor_still_writes_store_exactly_as_before()
    {
        DatabaseLinks.ToObject(Settings, MartenStudioDescriptor.Instance, Scope, "public", "orders")
            .Should().Be("database/object?schema=public&name=orders&store=orders&db=db1.orders");
    }

    [Theory]
    [InlineData(nameof(DatabaseObjectOwner.MartenDocument))]
    [InlineData(nameof(DatabaseObjectOwner.MartenEventStore))]
    [InlineData(nameof(DatabaseObjectOwner.MartenInfrastructure))]
    public void A_studio_without_the_screen_has_nowhere_to_send_a_Marten_object(string owner)
    {
        DatabaseLinks.ToWhereItLives(new NoLinks(), Scope, new DatabaseObjectOwnership(Enum.Parse<DatabaseObjectOwner>(owner), "orders", "customer"))
            .Should().BeNull();
    }

    [Fact]
    public void A_source_with_no_advisory_locks_of_its_own_names_none_as_Martens_and_still_names_relations()
    {
        SessionLockLabels labels = SessionLockLabels.RelationsOnly("default", DatabaseTestStores.Classifier());

        labels.DaemonLockIds.Should().BeEmpty();
        labels.Advisory(4004).Kind.Should().Be(LockTargetKind.OtherAdvisoryLock, "Marten's default apply-changes id is nobody's here");
        labels.Advisory(4444).Kind.Should().Be(LockTargetKind.OtherAdvisoryLock, "and so is its default daemon id");
        labels.Describe("relation", new LockedRelation(DatabaseTestStores.DocumentSchema, "mt_doc_dbcustomer", "r")).Kind
            .Should().Be(LockTargetKind.DocumentTable, "relations are labelled by the classifier, whoever labels the advisory keys");
    }

    [Theory]
    [InlineData(nameof(DatabaseObjectOwner.MartenDocument), "a.ms-db-owner-document")]
    [InlineData(nameof(DatabaseObjectOwner.MartenEventStore), "a.ms-db-owner-events")]
    [InlineData(nameof(DatabaseObjectOwner.MartenInfrastructure), "a.ms-db-owner-internal")]
    public void The_owner_badge_is_a_link_in_MartenStudio(string owner, string selector)
    {
        using var context = new StudioComponentContext();

        IRenderedComponent<DatabaseOwnerBadge> badge = context.Render<DatabaseOwnerBadge>(parameters => parameters
            .Add(x => x.Model, new DatabaseObjectOwnership(Enum.Parse<DatabaseObjectOwner>(owner), "default", "customer")));

        badge.Find(selector).GetAttribute("href").Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(nameof(DatabaseObjectOwner.MartenDocument), "span.ms-db-owner-document")]
    [InlineData(nameof(DatabaseObjectOwner.MartenEventStore), "span.ms-db-owner-events")]
    [InlineData(nameof(DatabaseObjectOwner.MartenInfrastructure), "span.ms-db-owner-internal")]
    public void The_owner_badge_is_plain_text_where_the_studio_has_no_such_screen(string owner, string selector)
    {
        using var context = new StudioComponentContext(static services => services.AddSingleton<IStudioLinks>(new NoLinks()));

        IRenderedComponent<DatabaseOwnerBadge> badge = context.Render<DatabaseOwnerBadge>(parameters => parameters
            .Add(x => x.Model, new DatabaseObjectOwnership(Enum.Parse<DatabaseObjectOwner>(owner), "default", "customer")));

        badge.FindAll("a").Should().BeEmpty("there is nowhere to open it");
        badge.Find(selector).GetAttribute("title").Should().NotBeNullOrEmpty("the badge still says whose it is");
    }

    /// <summary>MartenStudio's descriptor, with another source parameter - a studio whose sources are not stores.</summary>
    private static StudioDescriptor WithSourceParameter(string parameter)
    {
        StudioDescriptor marten = MartenStudioDescriptor.Instance;

        return new StudioDescriptor
        {
            Key = "test",
            ProductName = marten.ProductName,
            PageAssemblies = marten.PageAssemblies,
            HealthCheckAssemblies = marten.HealthCheckAssemblies,
            Navigation = marten.Navigation,
            OverviewComponent = marten.OverviewComponent,
            QueryComponent = marten.QueryComponent,
            SourceQueryParameter = parameter,
            SourceNoun = "data source",
            SourceLabel = "Source",
            ExposedContent = marten.ExposedContent,
            CreateResource = marten.CreateResource,
            Surface = marten.Surface,
            Exposure = marten.Exposure,
            Remedies = marten.Remedies,
        };
    }

    /// <summary>A studio with no Documents, Streams, Feed, Projections or Schema screens.</summary>
    private sealed class NoLinks : IStudioLinks
    {
        public string? ToDocuments(StudioScope? scope) => null;

        public string? ToCollection(StudioScope? scope, string alias) => null;

        public string? ToDocument(StudioScope? scope, string alias, string id) => null;

        public string? ToStreams(StudioScope? scope) => null;

        public string? ToFeed(StudioScope? scope) => null;

        public string? ToProjections(StudioScope? scope) => null;

        public string? ToSchema(StudioScope? scope, string tab = "tables") => null;
    }
}
