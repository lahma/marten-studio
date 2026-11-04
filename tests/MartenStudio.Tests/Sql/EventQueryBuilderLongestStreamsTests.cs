using JasperFx.Events;

using MartenStudio.Internal.Sql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Sql;

/// <summary>
/// The Health page's "20 longest streams" statement (1.2-H-C, E06): quoted identifiers, a column only where the table has it,
/// every value a parameter, and a limit that cannot be raised.
/// </summary>
public class EventQueryBuilderLongestStreamsTests
{
    private static EventTableInfo Full => new()
    {
        Schema = "studio_sql",
        StreamIdentity = StreamIdentity.AsGuid,
        StreamColumns = ["id", "type", "version", "timestamp", "created", "is_archived", "tenant_id", "compacted_version"],
    };

    private static EventTableInfo Minimal => new()
    {
        Schema = "studio_sql",
        StreamIdentity = StreamIdentity.AsString,
        StreamColumns = ["id", "type", "version", "timestamp"],
    };

    [Fact]
    public void The_statement_is_a_top_n_of_the_streams_table_and_never_reads_the_events()
    {
        using var command = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery());

        command.CommandText.Should().Be(
            """
            select s."id"::text as "id",
                   s."type",
                   s."version",
                   s."created"
            from "studio_sql"."mt_streams" as s
            where true
              and s."is_archived" = false
            order by s."version" desc nulls last, s."id"
            limit @limit
            """.ReplaceLineEndings("\n"));

        command.CommandText.Should().NotContain("mt_events").And.NotContain("select *").And.NotContain("group by");
        command.Parameters.Should().ContainSingle().Which.ParameterName.Should().Be("limit");
    }

    [Fact]
    public void A_table_without_the_optional_columns_names_none_of_them()
    {
        using var command = EventQueryBuilder.BuildLongestStreams(Minimal, new LongestStreamsQuery
        {
            TenantId = "acme",
            IncludeTenant = true,
            ShownTypes = ["trip"],
        });

        command.CommandText.Should().NotContain("is_archived").And.NotContain("tenant").And.NotContain("created");
        command.Parameters.Cast<Npgsql.NpgsqlParameter>().Select(static x => x.ParameterName).Should().BeEquivalentTo(["shown", "limit"]);
    }

    [Fact]
    public void The_archived_filter_is_there_only_with_the_column()
    {
        using var full = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery());
        using var minimal = EventQueryBuilder.BuildLongestStreams(Minimal, new LongestStreamsQuery());

        full.CommandText.Should().Contain("s.\"is_archived\" = false");
        minimal.CommandText.Should().NotContain("is_archived");
    }

    [Fact]
    public void The_tenant_predicate_is_a_parameter_and_there_only_with_a_tenant()
    {
        using var none = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery());
        using var narrowed = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery { TenantId = "o'brien\"; drop table x; --" });

        none.CommandText.Should().NotContain("tenant");
        narrowed.CommandText.Should().Contain("and s.\"tenant_id\" = @tenant").And.NotContain("drop table");
        narrowed.Parameters["tenant"].Value.Should().Be("o'brien\"; drop table x; --");
        narrowed.Parameters["tenant"].NpgsqlDbType.Should().Be(NpgsqlDbType.Varchar);
    }

    [Fact]
    public void The_tenant_column_is_selected_only_when_asked_for()
    {
        using var asked = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery { IncludeTenant = true });
        using var not = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery { TenantId = "acme" });

        asked.CommandText.Should().Contain("       s.\"tenant_id\",\n       s.\"created\"");
        asked.CommandText.Should().NotContain("= @tenant");
        not.CommandText.Split("from")[0].Should().NotContain("tenant_id", "a read narrowed to one tenant does not select the tenant it already knows");
    }

    [Fact]
    public void The_type_filter_is_a_lower_case_text_array_and_keeps_a_stream_with_no_type()
    {
        using var command = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery { ShownTypes = ["trip", "order"] });

        command.CommandText.Should().Contain("and (s.\"type\" is null or pg_catalog.lower(s.\"type\") = any(@shown))");
        ((string[]) command.Parameters["shown"].Value!).Should().Equal("trip", "order");
        command.Parameters["shown"].NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Text);
    }

    [Fact]
    public void No_type_filter_without_ShownTypes()
    {
        using var command = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery());

        command.CommandText.Should().NotContain("@shown").And.NotContain("lower");
    }

    [Theory]
    [InlineData(1000, 20)]
    [InlineData(20, 20)]
    [InlineData(5, 5)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void The_limit_is_clamped_to_twenty(int take, int expected)
    {
        using var command = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery { Take = take });

        command.Parameters["limit"].Value.Should().Be(expected);
    }

    [Fact]
    public void A_streams_table_without_a_length_has_no_statement()
    {
        EventTableInfo noVersion = Full with { StreamColumns = ["id", "type"] };

        FluentActions.Invoking(() => EventQueryBuilder.BuildLongestStreams(noVersion, new LongestStreamsQuery()))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Every_identifier_is_quoted_and_every_function_is_qualified()
    {
        using var command = EventQueryBuilder.BuildLongestStreams(Full, new LongestStreamsQuery { ShownTypes = ["a"], TenantId = "t", IncludeTenant = true });

        command.CommandText.Should().Contain("\"studio_sql\".\"mt_streams\"");
        command.CommandText.Should().NotMatchRegex(@"(?<!pg_catalog\.)\blower\(");
    }
}
