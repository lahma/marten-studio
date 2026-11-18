using System.Text.RegularExpressions;

using DataStudio.Postgres.Internal.Sql;

namespace DataStudio.Marten.Tests.Sql;

/// <summary>
/// Issue #81: Aurora's own reads call its functions in the schema the platform read found (<c>pg_catalog</c>, or where a superuser keeps its
/// <c>aurora_version()</c>), quoted through the builder, never joined into a statement as text; and that schema is only ever one only a superuser can create in.
/// </summary>
public class AuroraQueriesTests
{
    private static readonly Regex Call = new(@"aurora_[a-z_]+\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    /// <summary>Every Aurora statement, for a schema.</summary>
    private static (string Name, HealthQuery Query)[] Every(string schema) =>
    [
        ("Instance with the identifier", AuroraQueries.Instance(schema, identifier: true)),
        ("Instance", AuroraQueries.Instance(schema, identifier: false)),
        ("ReplicaStatus", AuroraQueries.ReplicaStatus(schema)),
        ("SystemWaits", AuroraQueries.SystemWaits(schema)),
        ("WaitNames", AuroraQueries.WaitNames(schema)),
        ("Database", AuroraQueries.Database(schema)),
        ("BackendWaits", AuroraQueries.BackendWaits(schema)),
    ];

    [Theory]
    [InlineData("pg_catalog")]
    [InlineData("aurora_utils")]
    [InlineData("Mixed Case Schema")]
    public void Every_call_of_every_statement_is_qualified_with_the_schema_quoted(string schema)
    {
        foreach ((string name, HealthQuery query) in Every(schema))
        {
            string quoted = "\"" + schema + "\".\"aurora_";

            query.Sql.Should().Contain(quoted, "{0} names the schema, quoted", name);
            Call.Matches(query.Sql).Should().BeEmpty("{0}: every Aurora function is called as \"schema\".\"name\"(), never by a bare name that the search path could resolve", name);
        }
    }

    [Fact]
    public void A_schema_name_is_never_joined_in_as_text()
    {
        foreach ((string name, HealthQuery query) in Every("pg_catalog"))
        {
            query.Sql.Should().NotContain("pg_catalog.aurora_", "{0}: the function is qualified by the schema that was found, quoted", name);
        }

        // A name with a quote in it would end the identifier early: the builder refuses it, and the platform read never offers one.
        FluentActions.Invoking(() => AuroraQueries.ReplicaStatus("a\"; drop table x; --")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AuroraQueries.ReplicaStatus(string.Empty)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AuroraQueries.ReplicaStatus(null!)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_platform_read_names_a_schema_only_a_superuser_can_create_in_and_calls_nothing()
    {
        string sql = HealthServerQueries.Platform().Sql;

        sql.Should().Contain("as aurora_schema");
        sql.Should().Contain("p.proname = 'aurora_version' and o.rolsuper", "the function must be a superuser's");
        sql.Should().Contain("so.oid = n.nspowner and so.rolsuper", "the schema must be a superuser's");
        sql.Should().Contain("pg_catalog.aclexplode(coalesce(n.nspacl, pg_catalog.acldefault('n', n.nspowner)))").And.Contain("a.privilege_type = 'CREATE'");
        sql.Should().Contain("a.grantee = 0", "PUBLIC may not create there");
        sql.Should().Contain("not exists (select 1 from pg_catalog.pg_roles g where g.oid = a.grantee and g.rolsuper)", "nor any role that is not a superuser");
        sql.Should().Contain("array_agg(n.oid order by n.oid = 'pg_catalog'::pg_catalog.regnamespace desc", "pg_catalog first when there are several");
        Call.Matches(sql).Should().BeEmpty("the platform read looks Aurora's functions up in pg_proc and calls none");
    }
}
