using DataStudio.Marten.Services;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Relationships;

namespace DataStudio.Marten.Tests.Relationships;

/// <summary>
/// The Relationships page's read spans the source's own schemas and, past them, what <c>BrowsableSchemas</c> admits. Without <c>BrowseDatabase</c>
/// the two modules differ, and decide it from a fact their words carry (<c>NamedSchemasAreOwn</c>) rather than from which module they are: a plain data
/// source's exactly-named schemas are its own, so they join the read; the Marten module's are declared by the store, so nothing does.
/// </summary>
public class RelationshipSchemaSetTests
{
    private static readonly string[] Entries = ["app", "reporting", "*"];

    [Fact]
    public void Without_BrowseDatabase_a_plain_data_source_reads_the_exactly_named_schemas_and_never_the_star()
    {
        RelationshipReads.EntriesOf(PostgresStudioText.Instance, browse: false, Entries).Should().Equal("app", "reporting");
    }

    [Fact]
    public void Without_BrowseDatabase_the_Marten_module_reads_no_schema_beyond_the_stores_own()
    {
        RelationshipReads.EntriesOf(MartenStudioText.Instance, browse: false, Entries).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_BrowseDatabase_both_read_every_entry(bool marten)
    {
        RelationshipReads.EntriesOf(marten ? MartenStudioText.Instance : PostgresStudioText.Instance, browse: true, Entries).Should().Equal(Entries);
    }
}
