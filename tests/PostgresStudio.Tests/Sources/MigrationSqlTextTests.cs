using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Tests.Sources;

/// <summary>
/// The migration SQL a relational remedy offers (issue #8 G12), held to the one rule that matters: every name is quoted,
/// and a name that cannot be quoted honestly gets no statement at all.
/// </summary>
public class MigrationSqlTextTests
{
    /// <summary>
    /// Names carrying a character that changes what the text on the screen says - a right-to-left override, a zero-width
    /// space, a Unicode line or paragraph separator - built at run time, so that this source file holds none of them.
    /// </summary>
    public static TheoryData<string, string> InvisibleCharacterNames() =>
    [
        ("public", "rlo" + char.ConvertFromUtf32(0x202E) + "txt.exe"),
        ("public", "zero" + char.ConvertFromUtf32(0x200B) + "width"),
        ("public", "line" + char.ConvertFromUtf32(0x2028) + "separator"),
        ("public", "paragraph" + char.ConvertFromUtf32(0x2029) + "separator"),
    ];

    /// <summary>A covering index name that would plant a statement under the remedy's comment, by each line break.</summary>
    public static TheoryData<string> CommentBreakingNames() =>
    [
        "x\nALTER ROLE app SUPERUSER;\n--",
        "x\rALTER ROLE app SUPERUSER;\r--",
        "x" + char.ConvertFromUtf32(0x2028) + "ALTER ROLE app SUPERUSER;" + char.ConvertFromUtf32(0x2028) + "--",
        "x" + char.ConvertFromUtf32(0x2029) + "ALTER ROLE app SUPERUSER;" + char.ConvertFromUtf32(0x2029) + "--",
        "cover\"; ALTER ROLE app SUPERUSER; --",
    ];
    [Theory]
    [InlineData("public", "orders", "\"public\".\"orders\"")]
    [InlineData("public", "order", "\"public\".\"order\"")]
    [InlineData("select", "table", "\"select\".\"table\"")]
    [InlineData("Sales", "Line Items", "\"Sales\".\"Line Items\"")]
    [InlineData("sales", "line;items", "\"sales\".\"line;items\"")]
    [InlineData("sales", "x); drop table y; --", "\"sales\".\"x); drop table y; --\"")]
    [InlineData("ventes", "commandes_été", "\"ventes\".\"commandes_été\"")]
    [InlineData("販売", "注文", "\"販売\".\"注文\"")]
    [InlineData("public", "it's", "\"public\".\"it's\"")]
    public void Every_name_is_quoted_whatever_it_spells(string schema, string table, string qualified)
    {
        MigrationSqlText.SetStorageParameter(schema, table, "fillfactor", 80).Should().Be("ALTER TABLE " + qualified + " SET (fillfactor = 80);");
        MigrationSqlText.ResetStorageParameter(schema, table, "autovacuum_enabled").Should().Be("ALTER TABLE " + qualified + " RESET (autovacuum_enabled);");
        MigrationSqlText.CreateIndexConcurrently(schema, table).Should().Be("CREATE INDEX CONCURRENTLY ON " + qualified + " (filtered_column);");
        MigrationSqlText.DropIndexConcurrently(schema, table).Should().Be("DROP INDEX CONCURRENTLY " + qualified + ";");
        MigrationSqlText.ReindexIndexConcurrently(schema, table).Should().Be("REINDEX INDEX CONCURRENTLY " + qualified + ";");
    }

    [Theory]
    [InlineData("public", "we\"ird")]
    [InlineData("we\"ird", "orders")]
    [InlineData("public", "x\"; drop table y; --")]
    [InlineData("public", "nul\0name")]
    [InlineData("public", "tab\tname")]
    [InlineData("public", "line\nbreak")]
    [MemberData(nameof(InvisibleCharacterNames))]
    [InlineData("public", "carriage\rreturn")]
    [InlineData("public", "")]
    [InlineData("public", "   ")]
    public void A_name_that_cannot_be_quoted_honestly_gets_no_statement(string schema, string name)
    {
        MigrationSqlText.SetStorageParameter(schema, name, "fillfactor", 80).Should().BeNull();
        MigrationSqlText.ResetStorageParameter(schema, name, "autovacuum_enabled").Should().BeNull();
        MigrationSqlText.CreateIndexConcurrently(schema, name).Should().BeNull();
        MigrationSqlText.DropIndexConcurrently(schema, name).Should().BeNull();
        MigrationSqlText.ReindexIndexConcurrently(schema, name).Should().BeNull();
        MigrationSqlText.DropRedundantIndexConcurrently(schema, name, "cover").Should().BeNull();
        string hostile = schema == "public" ? name : schema;
        MigrationSqlText.DropRedundantIndexConcurrently("public", "index", hostile).Should().BeNull("the covering name is printed too");
        MigrationSqlText.IsPrintableName(hostile).Should().BeFalse();
    }

    [Theory]
    [InlineData("autovacuum_vacuum_scale_factor", "0.05", true)]
    [InlineData("autovacuum_enabled", "true", true)]
    [InlineData("fillfactor", "80", true)]
    [InlineData("fillfactor", "80); drop table x; --", false)]
    [InlineData("fillfactor = 1, x", "80", false)]
    [InlineData("FillFactor", "80", false)]
    [InlineData("fillfactor", "８０", false)]
    [InlineData("fillfactor", "80\n", false)]
    [InlineData("fillfactor\n", "80", false)]
    [InlineData("fillfactor", "", false)]
    [InlineData("fillfactor", "-1", false)]
    public void A_storage_parameter_is_held_to_a_storage_parameters_shape(string parameter, string value, bool accepted)
    {
        string? sql = MigrationSqlText.SetStorageParameter("public", "orders", parameter, value);

        if (accepted)
        {
            sql.Should().Be("ALTER TABLE \"public\".\"orders\" SET (" + parameter + " = " + value + ");");
        }
        else
        {
            sql.Should().BeNull();
        }
    }

    // ---- 0.10-A: P15's ANALYZE and P16's CREATE STATISTICS --------------------------------------------------------------

    [Fact]
    public void Analyze_quotes_both_names_and_gives_nothing_for_one_it_cannot_print()
    {
        MigrationSqlText.Analyze("public", "orders").Should().Be("ANALYZE \"public\".\"orders\";");
        MigrationSqlText.Analyze("Sales", "Line Items").Should().Be("ANALYZE \"Sales\".\"Line Items\";");
        MigrationSqlText.Analyze("public", "x\"; drop table y; --").Should().BeNull();
        MigrationSqlText.Analyze("public", "").Should().BeNull();
    }

    [Fact]
    public void CreateStatistics_names_the_object_after_the_table_and_columns_in_the_tables_schema_and_quotes_everything()
    {
        MigrationSqlText.CreateStatistics("public", "addresses", ["city", "postcode"]).Should().Be(
            "CREATE STATISTICS \"public\".\"addresses_city_postcode_stats\" (dependencies, ndistinct, mcv) ON \"city\", \"postcode\" FROM \"public\".\"addresses\";");
    }

    [Theory]
    [InlineData("city")]
    [InlineData("x\"; drop table y; --")]
    [InlineData("")]
    public void CreateStatistics_gives_nothing_for_fewer_than_two_distinct_printable_columns(string column)
    {
        MigrationSqlText.CreateStatistics("public", "addresses", [column]).Should().BeNull("one column is not correlated with anything");
        MigrationSqlText.CreateStatistics("public", "addresses", ["city", "city"]).Should().BeNull("a column twice is one column");
        MigrationSqlText.CreateStatistics("public", "addresses", ["city", column]).Should().BeNull(column.Length == 0 || column.Contains('"') ? "a name that cannot be quoted gets no statement" : "city twice");
    }

    [Fact]
    public void CreateStatistics_gives_nothing_for_a_name_past_the_sixty_three_bytes_postgres_keeps_and_for_nine_columns()
    {
        MigrationSqlText.CreateStatistics("public", new string('t', 40), [new string('a', 15), new string('b', 15)]).Should().BeNull("Postgres would cut the name without a word");
        MigrationSqlText.CreateStatistics("public", "t", [.. Enumerable.Range(1, 9).Select(static x => "c" + x)]).Should().BeNull("the statement takes at most eight columns");
        MigrationSqlText.CreateStatistics("public", "t", [.. Enumerable.Range(1, 8).Select(static x => "c" + x)]).Should().NotBeNull();
    }
}
