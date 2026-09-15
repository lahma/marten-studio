using Marten;
using Marten.Linq.Members;
using Marten.Schema;

using MartenStudio.Internal;
using MartenStudio.Internal.Sql;
using MartenStudio.Services.Documents;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// A document type whose version is a member of its own — issue #1, against a live store.
/// </summary>
/// <remarks>
/// <para>
/// <c>[Version]</c> is the documented way to do optimistic concurrency in Marten, and it made the
/// detail view of every such document answer 500. Marten registers a <em>search-only</em> duplicated
/// field for the member so that a LINQ comparison against it reads the column rather than the JSON; that
/// field is named after the metadata column the version is stored in, <c>mt_version</c>, and no column is
/// created for it. The studio read it as a duplicated column, so <c>mt_version</c> was in the
/// single-document select list twice and in the metadata pane twice, and the second <c>&lt;tr&gt;</c>
/// carrying a key the first already had threw inside Blazor's diff builder and terminated the circuit.
/// </para>
/// <para>
/// <b>Why this test is live rather than a unit test.</b> The registration happens in the
/// <c>DocumentSchema</c> constructor, which <c>DocumentMapping</c> holds behind a <c>Lazy&lt;T&gt;</c>:
/// a mapping resolved from a store that has never built its schema reports no such field at all. So the
/// bug is invisible to any test that only calls <c>DocumentStore.For(...)</c>, and visible to every
/// application. The store here has written a row, which is more than enough to build it.
/// </para>
/// </remarks>
public class DocumentDetailVersionedLiveTests(DocumentDetailVersionedLiveTests.Fixture fixture)
    : MartenTestBase(fixture), IClassFixture<DocumentDetailVersionedLiveTests.Fixture>
{
    private static readonly Guid Id = new("8f1d5a6e-0000-0000-0000-0000000000a1");

    private const string Alias = "versionedsitedocument";

    /// <summary>The store from the issue: one document type, with a <c>[Version]</c> property.</summary>
    public sealed class Fixture(PostgresFixture postgres) : MartenClassFixture(postgres)
    {
        /// <inheritdoc />
        protected override bool SeedSampleData => false;

        /// <inheritdoc />
        protected override void ConfigureStore(StoreOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            options.Schema.For<VersionedSiteDocument>();
            options.Schema.For<RevisionedSiteDocument>();
        }

        /// <inheritdoc />
        protected override async Task SeedAsync()
        {
            await using IDocumentSession session = Marten.Store.LightweightSession();

            session.Store(new VersionedSiteDocument { Id = Id, Name = "Marten Studio" });
            session.Store(new RevisionedSiteDocument { Id = Id, Name = "Marten Studio" });

            await session.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Marten really does register the alias, and it really is not a column. Everything else here
    /// depends on this being true of Marten rather than of a hand-written fake.
    /// </summary>
    [PostgresFact]
    public void Marten_registers_a_search_only_duplicated_field_for_the_version_member()
    {
        IDocumentType documentType = Store.Options.FindOrResolveDocumentType(typeof(VersionedSiteDocument));

        DuplicatedField alias = documentType.DuplicatedFields
            .Should().ContainSingle(x => x.ColumnName == "mt_version").Subject;

        alias.MemberName.Should().Be("Version");
        alias.OnlyForSearching.Should().BeTrue(
            "it exists so that LINQ can compare against the column; Marten's own DocumentTable skips it");

        MartenDuplicatedFields.Physical(documentType).Should().BeEmpty(
            "this type duplicates nothing into a column of its own");
    }

    /// <summary>
    /// The issue's own shape: <c>[Version] public long Version</c>, which Marten routes to
    /// <c>Metadata.Revision</c> rather than to <c>Metadata.Version</c> — same column, same alias.
    /// </summary>
    /// <remarks>
    /// <c>VersionAttribute</c> switches on the member's type: a <c>Guid</c> sets <c>Version.Member</c> and
    /// turns on optimistic concurrency, a <c>long</c> sets <c>Revision.Member</c>, turns on numeric
    /// revisions and <em>disables</em> <c>Version</c>. Both end up stored in <c>mt_version</c> and both
    /// therefore get the same search alias, so one filter fixes both — but the reported repro was the
    /// <c>long</c> one, and a fix tested only against the <c>Guid</c> one would not have said so.
    /// </remarks>
    [PostgresFact]
    public void A_long_version_member_routes_to_the_revision_column_and_gets_the_same_alias()
    {
        IDocumentType documentType = Store.Options.FindOrResolveDocumentType(typeof(RevisionedSiteDocument));

        DuplicatedField alias = documentType.DuplicatedFields
            .Should().ContainSingle(x => x.ColumnName == "mt_version").Subject;

        alias.OnlyForSearching.Should().BeTrue();

        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(documentType);

        table.DuplicatedColumns.Should().BeEmpty();
        table.MetadataColumns.Should().Contain(x => x.Column == DocumentMetadataColumn.Revision,
            "a long [Version] member is Marten's numeric revision");
        table.FindDuplicated(["Version"])!.ColumnName.Should().Be("mt_version",
            "it is still searchable against the column it is stored in");
    }

    /// <summary>The table the studio builds names every column once.</summary>
    [PostgresFact]
    public void The_table_the_studio_describes_names_mt_version_once()
    {
        DocumentTableInfo table = DocumentTableInfo.FromDocumentType(
            Store.Options.FindOrResolveDocumentType(typeof(VersionedSiteDocument)));

        table.MetadataColumns.Select(x => x.ColumnName).Should().Contain("mt_version");
        table.DuplicatedColumns.Should().BeEmpty();

        table.MetadataColumns.Select(x => x.ColumnName)
            .Concat(table.DuplicatedColumns.Select(x => x.ColumnName))
            .Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The read the page makes. This is the failure as a user meets it: before the fix the select list
    /// named <c>mt_version</c> twice and the pane rendered two rows under one key.
    /// </summary>
    [PostgresFact]
    public async Task The_detail_read_returns_every_column_once()
    {
        using var documents = Documents();

        DocumentDetailResult result = await documents.Service.GetDocumentAsync(
            Scope, Alias, Id.ToString(), TestContext.Current.CancellationToken);

        result.NotFound.Should().BeNull();
        result.Found.Should().BeTrue();

        DocumentDetail detail = result.Detail!;

        detail.Columns.Select(x => x.Name).Should().Contain(["id", "data", "mt_version"]);
        detail.Columns.Select(x => x.Name).Should().OnlyHaveUniqueItems(
            "a repeated name is a repeated Blazor @key, and that ends the circuit");

        detail.Duplicated.Should().BeEmpty("the version member is metadata, not a duplicated field");

        detail.Columns.Single(x => x.Name == "mt_version").Value.Should().NotBeNullOrEmpty();
    }
}

/// <summary>
/// The shape from the issue: a Guid id and a <c>[Version]</c> property, which is what Marten's
/// documentation prescribes for optimistic concurrency.
/// </summary>
public class VersionedSiteDocument
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Marten keeps this in step with <c>mt_version</c>.</summary>
    [Version]
    public Guid Version { get; set; }

    /// <summary>Something to read back.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// The other <c>[Version]</c> shape, and the one the issue actually reported: a <c>long</c>, which Marten
/// routes to its numeric revision.
/// </summary>
public class RevisionedSiteDocument
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Marten keeps this in step with <c>mt_version</c>, as an integer.</summary>
    [Version]
    public long Version { get; set; }

    /// <summary>Something to read back.</summary>
    public string Name { get; set; } = string.Empty;
}
