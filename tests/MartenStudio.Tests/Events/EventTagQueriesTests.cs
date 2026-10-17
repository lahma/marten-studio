using JasperFx.Events;

using MartenStudio.Internal.Sql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Events;

/// <summary>
/// The SQL F4 adds: the feed's <c>tag:</c> predicate in both storage modes, and the read of a page's tags.
/// Identifiers quoted, values parameters - and the predicate shaped so the tag table's primary key serves it.
/// </summary>
public class EventTagQueriesTests
{
    private static readonly long[] ThreeThenOne = [3L, 1L];

    private static EventTableInfo Single => new()
    {
        Schema = "studio_sql",
        StreamIdentity = StreamIdentity.AsGuid,
        EventColumns = ["seq_id", "id", "stream_id", "version", "data", "type", "timestamp", "is_archived"],
        StreamColumns = ["id", "type", "version", "timestamp", "created", "is_archived"],
    };

    private static EventTableInfo Conjoined => Single with
    {
        EventColumns = ["seq_id", "id", "stream_id", "version", "data", "type", "timestamp", "is_archived", "tenant_id"],
        StreamColumns = ["id", "type", "version", "timestamp", "created", "is_archived", "tenant_id"],
    };

    private static EventTableInfo HStore => Single with
    {
        EventColumns = ["seq_id", "id", "stream_id", "version", "data", "type", "timestamp", "is_archived", "tags"],
    };

    [Fact]
    public void Without_a_tag_the_feed_names_no_tag_table()
    {
        using var command = EventQueryBuilder.BuildFeed(Single, new EventFeedQuery());

        command.CommandText.Should().NotContain("mt_event_tag_").And.NotContain("@tagValue");
        command.Parameters.Contains("tagValue").Should().BeFalse();
    }

    /// <summary>
    /// <c>value = @tagValue</c> against the tag table, which leads its primary key: an uncorrelated
    /// semi-join Postgres can drive from that index.
    /// </summary>
    [Fact]
    public void A_tag_filter_is_a_semi_join_on_the_tag_tables_value()
    {
        using var command = EventQueryBuilder.BuildFeed(Single, new EventFeedQuery
        {
            Tag = new EventTagPredicate { Table = "mt_event_tag_customer", Value = "acme-1", ValueType = NpgsqlDbType.Text },
        });

        command.CommandText.Should().Contain(
            "and e.\"seq_id\" in (select t.\"seq_id\" from \"studio_sql\".\"mt_event_tag_customer\" as t where t.\"value\" = @tagValue)");
        command.Parameters["tagValue"].Value.Should().Be("acme-1");
        command.Parameters["tagValue"].NpgsqlDbType.Should().Be(NpgsqlDbType.Text);
        command.CommandText.Should().NotContain("acme-1", "a value is never written into the statement");
    }

    /// <summary>
    /// A row-valued IN rather than Marten's correlated <c>tenant_id = d.tenant_id</c>: uncorrelated, so it
    /// can still be pulled up into a semi-join, and correct when <c>seq_id</c> repeats across tenants.
    /// </summary>
    [Fact]
    public void On_a_conjoined_store_the_tag_filter_matches_tenant_and_sequence_together()
    {
        using var command = EventQueryBuilder.BuildFeed(Conjoined, new EventFeedQuery
        {
            TenantId = "acme",
            Tag = new EventTagPredicate
            {
                Table = "mt_event_tag_customer",
                Value = 42,
                ValueType = NpgsqlDbType.Integer,
                TableHasTenant = true,
            },
        });

        command.CommandText.Should().Contain(
            "and (e.\"tenant_id\", e.\"seq_id\") in (select t.\"tenant_id\", t.\"seq_id\" from \"studio_sql\".\"mt_event_tag_customer\" as t "
            + "where t.\"value\" = @tagValue and (@tenant is null or t.\"tenant_id\" = @tenant))");
        command.Parameters["tagValue"].NpgsqlDbType.Should().Be(NpgsqlDbType.Integer);
        command.Parameters["tenant"].Value.Should().Be("acme");
    }

    [Fact]
    public void In_hstore_mode_the_filter_is_a_containment_the_gin_index_serves()
    {
        using var command = EventQueryBuilder.BuildFeed(HStore, new EventFeedQuery
        {
            Tag = new EventTagPredicate { HStoreKey = "customer", Value = "acme-1" },
        });

        command.CommandText.Should().Contain("and e.\"tags\" @> @tagSet");
        command.Parameters["tagSet"].NpgsqlDbType.Should().Be(NpgsqlDbType.Hstore);
        command.Parameters["tagSet"].Value.Should().BeEquivalentTo(new Dictionary<string, string?> { ["customer"] = "acme-1" });
    }

    [Theory]
    [InlineData(typeof(string), NpgsqlDbType.Text)]
    [InlineData(typeof(Guid), NpgsqlDbType.Uuid)]
    [InlineData(typeof(int), NpgsqlDbType.Integer)]
    [InlineData(typeof(long), NpgsqlDbType.Bigint)]
    [InlineData(typeof(short), NpgsqlDbType.Smallint)]
    public void A_tag_value_binds_as_the_type_marten_gave_its_column(Type simpleType, NpgsqlDbType expected) =>
        EventTagPredicate.DbTypeFor(simpleType).Should().Be(expected);

    // ------------------------------------------------------------------------------------------------
    // A page's tags
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_pages_tags_are_one_statement_across_every_tag_table_with_the_type_as_a_parameter()
    {
        using var command = EventTagQueries.BuildTagsForEvents(
            "studio_sql",
            [new EventTagTableRef("customer", "mt_event_tag_customer", false), new EventTagTableRef("region", "mt_event_tag_region", false)],
            [3L, 1L, 3L],
            tenantId: null);

        string sql = command.CommandText;

        sql.Should().Contain("from \"studio_sql\".\"mt_event_tag_customer\" as t");
        sql.Should().Contain("union all\n");
        sql.Should().Contain("from \"studio_sql\".\"mt_event_tag_region\" as t");
        sql.Should().Contain("where t.\"seq_id\" = any(@seqs)");
        sql.Should().Contain("null::varchar as tenant_id");
        sql.Should().EndWith("limit " + EventTagQueries.MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture));
        sql.Should().NotContain("'customer'", "the type label is a parameter, not a literal");

        command.Parameters["type0"].Value.Should().Be("customer");
        command.Parameters["type1"].Value.Should().Be("region");
        command.Parameters["seqs"].Value.Should().BeEquivalentTo(ThreeThenOne, "each sequence once");
        command.Parameters["seqs"].NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Bigint);
        command.Parameters.Contains("tenant").Should().BeFalse("a single-tenanted tag table has no tenant to narrow by");
    }

    /// <summary>
    /// The tenant is in the statement, so a sequence handed in from anywhere - a crafted request included -
    /// returns only the scope's own tags.
    /// </summary>
    [Fact]
    public void On_a_conjoined_store_a_tag_read_is_narrowed_to_the_scopes_tenant_in_the_statement()
    {
        using var command = EventTagQueries.BuildTagsForEvents(
            "studio_sql", [new EventTagTableRef("customer", "mt_event_tag_customer", true)], [7L], tenantId: "acme");

        command.CommandText.Should().Contain("and (@tenant is null or t.\"tenant_id\" = @tenant)");
        command.CommandText.Should().Contain("t.\"tenant_id\"::varchar as tenant_id");
        command.Parameters["tenant"].Value.Should().Be("acme");
    }

    [Fact]
    public void A_tag_read_is_bounded_in_how_many_events_it_asks_about()
    {
        long[] many = [.. Enumerable.Range(1, EventTagQueries.MaxSequences + 50).Select(static x => (long) x)];

        using var command = EventTagQueries.BuildTagsForEvents(
            "studio_sql", [new EventTagTableRef("customer", "mt_event_tag_customer", false)], many, null);

        ((long[]) command.Parameters["seqs"].Value!).Length.Should().Be(EventTagQueries.MaxSequences);
    }
}
