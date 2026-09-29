using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// SEC-fix F1: what Postgres says about a failed read is shown with every withheld schema masked - qualified,
/// quoted or named on its own - and with what the visitor typed left exactly as typed.
/// </summary>
public class PostgresErrorTextTests
{
    private const string Mask = WithheldNames.Token;

    private static readonly IReadOnlySet<string> Withheld = new HashSet<string>(["hr", "Legal Dept", "hr_archive"], StringComparer.Ordinal);

    /// <summary>The messages Postgres 17 gives, verbatim, for the reads the browser makes (probed live).</summary>
    [Theory]
    [InlineData("invalid input value for enum hr.grade: \"nope\"", "invalid input value for enum " + Mask + ".grade: \"nope\"")]
    [InlineData("relation \"hr.salaries\" does not exist", "relation \"" + Mask + ".salaries\" does not exist")]
    [InlineData("function hr.norm(integer) does not exist", "function " + Mask + ".norm(integer) does not exist")]
    [InlineData("permission denied for schema hr", "permission denied for schema " + Mask)]
    [InlineData("schema \"hr\" does not exist", "schema \"" + Mask + "\" does not exist")]
    [InlineData("value for domain hr_archive.code violates check constraint \"code_check\"", "value for domain " + Mask + ".code violates check constraint \"code_check\"")]
    [InlineData("permission denied for schema Legal Dept", "permission denied for schema " + Mask)]
    [InlineData("invalid input value for enum \"Legal Dept\".grade: \"x\"", "invalid input value for enum " + Mask + ".grade: \"x\"")]
    public void A_withheld_schema_is_masked_wherever_Postgres_names_it(string message, string expected) =>
        PostgresErrorText.Redact(message, Withheld).Should().Be(expected);

    [Theory]
    [InlineData("invalid input value for enum legacy.order_status: \"shiped\"")]
    [InlineData("permission denied for table salaries")]
    [InlineData("permission denied for schema legacy")]
    [InlineData("canceling statement due to statement timeout")]
    [InlineData("invalid input value for enum grade: \"nope\"")]
    public void A_message_naming_no_withheld_schema_is_left_as_it_is(string message) =>
        PostgresErrorText.Redact(message, Withheld).Should().Be(message, "an unqualified name names no schema, and a visible one is visible");

    /// <summary>
    /// A value is echoed in double quotes as the visitor typed it. Masked, it would answer "is <c>hr</c> a schema
    /// you are hiding from me?" with a yes; left alone, the answer is the same whether it is or not.
    /// </summary>
    [Fact]
    public void A_value_the_read_bound_is_left_as_the_visitor_typed_it()
    {
        PostgresErrorText.Redact("invalid input syntax for type integer: \"hr.x\"", Withheld, ["hr.x"])
            .Should().Be("invalid input syntax for type integer: \"hr.x\"");

        PostgresErrorText.Redact("invalid input syntax for type integer: \"hr.x\"", Withheld, ["hr.y"])
            .Should().Be("invalid input syntax for type integer: \"" + Mask + ".x\"", "only the echo of what was bound is the visitor's");

        // Postgres' own words around the echo are still masked, whatever the visitor typed.
        PostgresErrorText.Redact("invalid input value for enum hr.grade: \"hr.grade\"", Withheld, ["hr.grade"])
            .Should().Be("invalid input value for enum " + Mask + ".grade: \"hr.grade\"");

        PostgresErrorText.Redact("permission denied for schema hr: \"schema hr\"", Withheld, [null, "schema hr"])
            .Should().Be("permission denied for schema " + Mask + ": \"schema hr\"");
    }

    [Fact]
    public void A_longer_withheld_name_is_never_cut_by_a_shorter_one() =>
        PostgresErrorText.Redact("permission denied for schema hr_archive", Withheld)
            .Should().Be("permission denied for schema " + Mask);

    /// <summary>
    /// POLISH P3: a withheld name is masked as a whole token wherever it stands - the word <c>schema</c> before it
    /// is English, and the server's messages need not be.
    /// </summary>
    [Theory]
    [InlineData("droit refusé pour le schéma hr", "droit refusé pour le schéma " + Mask)]
    [InlineData("keine Berechtigung für Schema hr", "keine Berechtigung für Schema " + Mask)]
    [InlineData("la relation « hr.salaries » n'existe pas", "la relation « " + Mask + ".salaries » n'existe pas")]
    [InlineData("subschema hr is fine, schemas hr too", "subschema " + Mask + " is fine, schemas " + Mask + " too")]
    [InlineData("permission denied for schema HR", "permission denied for schema " + Mask)]
    public void A_withheld_name_is_masked_as_a_whole_token_in_any_language(string message, string expected) =>
        PostgresErrorText.Redact(message, Withheld).Should().Be(expected);

    /// <summary>
    /// POLISH P3: a name with a space or a hyphen in it, printed unquoted - which no lexer reads as one
    /// identifier - is matched whole, space or hyphen included.
    /// </summary>
    [Theory]
    [InlineData("relation \"HR Data.salaries\" does not exist", "relation \"" + Mask + ".salaries\" does not exist")]
    [InlineData("permission denied for schema HR Data", "permission denied for schema " + Mask)]
    [InlineData("relation \"hr-data.salaries\" does not exist", "relation \"" + Mask + ".salaries\" does not exist")]
    [InlineData("droit refusé pour le schéma hr-data", "droit refusé pour le schéma " + Mask)]
    public void A_name_with_a_space_or_a_hyphen_is_masked_whole(string message, string expected)
    {
        IReadOnlySet<string> withheld = new HashSet<string>(["HR Data", "hr-data", "HR"], StringComparer.Ordinal);

        PostgresErrorText.Redact(message, withheld).Should().Be(expected);
    }

    /// <summary>
    /// POLISH P3: the token rule is a whole-token rule - a visible schema whose name merely begins or ends with a
    /// withheld one, or joins it with a hyphen, is left as it is.
    /// </summary>
    [Theory]
    [InlineData("permission denied for schema hr_public")]
    [InlineData("relation \"hr_public.salaries\" does not exist")]
    [InlineData("function thr.norm(integer) does not exist")]
    [InlineData("relation \"hr2.x\" does not exist")]
    [InlineData("relation \"hr-public.x\" does not exist")]
    [InlineData("permission denied for schema public-hr")]
    public void A_visible_name_that_contains_a_withheld_one_is_untouched(string message)
    {
        IReadOnlySet<string> withheld = new HashSet<string>(["hr"], StringComparer.Ordinal);

        PostgresErrorText.Redact(message, withheld).Should().Be(message);
    }

    /// <summary>
    /// The echo rule holds for the token rule too: a value the read bound is left as it was typed, and the same
    /// name in Postgres' own words around it is masked.
    /// </summary>
    [Fact]
    public void A_bound_value_is_still_echoed_as_typed_under_the_token_rule() =>
        PostgresErrorText.Redact("droit refusé pour le schéma hr : \"hr\"", Withheld, ["hr"])
            .Should().Be("droit refusé pour le schéma " + Mask + " : \"hr\"");

    [Fact]
    public void The_mask_itself_is_never_masked_again()
    {
        IReadOnlySet<string> withheld = new HashSet<string>(["withheld", "hr"], StringComparer.Ordinal);

        PostgresErrorText.Redact("relation \"hr.x\" does not exist in schema withheld", withheld)
            .Should().Be("relation \"" + Mask + ".x\" does not exist in schema " + Mask);
    }

    /// <summary>
    /// The row service's own step: a failure is masked through the gate the grant carries, and every value the
    /// statement bound - a filter value, a key - is the visitor's and left as it was.
    /// </summary>
    [Fact]
    public void A_row_read_failure_is_masked_through_the_grants_gate_and_keeps_the_bound_values()
    {
        IReadOnlyList<CatalogSchema> live =
        [
            new("pg_catalog", true, false),
            .. DatabaseTestStores.StoreSchemas().Select(static x => new CatalogSchema(x, true, false)),
            new("quartz", true, false),
            new("hr", true, false),
        ];

        DatabaseGate gate = new(
            DatabaseTestStores.StoreSchemas(), live, ["quartz"], true, false, true, DatabaseTestStores.Classifier());

        gate.WithheldSchemas.Should().Contain("hr", "the premise");

        TableRowStatement statement = TableRowQueryBuilder.BuildCount(
            "quartz",
            "qrtz_triggers",
            [new RowFilterTerm("grade", RowFilterOperator.Equal, "hr.nope", 0, 16)],
            1001);

        TableRowError failed = new("22P02", "A filter or key value does not fit its column's type.",
            "invalid input value for enum hr.grade: \"hr.nope\"");

        TableRowService.Redact(failed, gate, statement).Should().Be(failed with
        {
            PostgresMessage = "invalid input value for enum " + Mask + ".grade: \"hr.nope\"",
        });

        TableRowService.Redact(failed, gate, statement: null).PostgresMessage
            .Should().Be("invalid input value for enum " + Mask + ".grade: \"" + Mask + ".nope\"", "with no statement, nothing is known to be the visitor's");
    }

    [Fact]
    public void Nothing_withheld_or_nothing_said_is_nothing_masked()
    {
        PostgresErrorText.Redact("relation \"hr.salaries\" does not exist", new HashSet<string>())
            .Should().Be("relation \"hr.salaries\" does not exist");

        PostgresErrorText.Redact(null, Withheld).Should().BeNull();
        PostgresErrorText.Redact(string.Empty, Withheld).Should().BeEmpty();
    }
}
