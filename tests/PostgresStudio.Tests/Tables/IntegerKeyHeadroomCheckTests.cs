using static PostgresStudio.Tests.Tables.LintBuilders;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Tables;

/// <summary>T26 at its edges: an <c>int4</c> identity at 51% and 81%, a <c>bigint</c> column on an <c>AS integer</c> sequence, a <c>smallint</c> serial, the sequences it never judges, and no column <c>SELECT</c>.</summary>
public class IntegerKeyHeadroomCheckTests
{
    private static readonly StudioTable Orders = Host("orders");

    private static HealthIntegerKeyRow Identity(long largestSampled, string columnType = "integer", string sequenceType = "integer", long maximum = int.MaxValue) => new()
    {
        Schema = "app",
        Table = "orders",
        Column = "id",
        ColumnType = columnType,
        SequenceSchema = "app",
        Sequence = "orders_id_seq",
        SequenceType = sequenceType,
        FedBy = "identity",
        Maximum = maximum,
        Rows = 1_000,
        LargestSampled = largestSampled,
    };

    private static HealthVerdict Judge(params HealthIntegerKeyRow[] keys) =>
        new IntegerKeyHeadroomCheck().Evaluate(new IntegerKeyFacts(keys, [Orders]), Evaluation());

    [Fact]
    public void An_int4_identity_at_51_percent_is_a_warning_and_at_81_percent_critical()
    {
        HealthFinding warning = Judge(Identity((long) (int.MaxValue * 0.51))).Findings.Single();
        warning.Code.Should().Be("T26");
        warning.Severity.Should().Be(HealthSeverity.Warning);
        warning.Variant.Should().Be("column");
        warning.Subject.Should().Be("app.orders.id");
        warning.Title.Should().Contain("at least 51% of its integer range");
        warning.Evidence.Should().Contain(new HealthEvidence("Limit", "2,147,483,647, the largest integer value"));
        warning.Evidence.Should().Contain(x => x.Label == "Largest value stored, at least" && x.Value.EndsWith("as of the last ANALYZE", StringComparison.Ordinal));
        warning.Caveat.Should().Contain("ON CONFLICT DO NOTHING").And.Contain("Sequences list");
        warning.Remedy!.Code.Should().Be("ALTER TABLE \"app\".\"orders\" ALTER COLUMN \"id\" TYPE bigint;",
            "an identity column's sequence follows the column's type (measured on 17)");
        warning.Remedy.Explanation.Should().Contain("ACCESS EXCLUSIVE");
        warning.Link!.Query.Should().Contain(new KeyValuePair<string, string?>("kind", "sequences"));

        Judge(Identity((long) (int.MaxValue * 0.81))).Findings.Single().Severity.Should().Be(HealthSeverity.Critical);
        Judge(Identity((long) (int.MaxValue * 0.49))).Findings.Should().BeEmpty();
    }

    [Fact]
    public void A_bigint_column_on_an_as_integer_sequence_is_judged_against_the_sequence()
    {
        HealthFinding finding = Judge(Identity((long) (int.MaxValue * 0.6), columnType: "bigint") with { FedBy = "default" }).Findings.Single();

        finding.Variant.Should().Be("sequence");
        finding.Evidence.Should().Contain(new HealthEvidence("Limit", "2,147,483,647, the sequence's maximum (AS integer)"));
        finding.Remedy!.Code.Should().Be("ALTER SEQUENCE \"app\".\"orders_id_seq\" AS bigint;");
    }

    [Fact]
    public void A_smallint_serial_widens_the_column_and_its_sequence()
    {
        HealthIntegerKeyRow serial = Identity(27_000, columnType: "smallint", sequenceType: "smallint", maximum: short.MaxValue) with { FedBy = "serial" };
        HealthFinding finding = Judge(serial).Findings.Single();

        finding.Severity.Should().Be(HealthSeverity.Critical, "27,000 of 32,767 is past four fifths");
        finding.Remedy!.Code.Should().Be(
            "ALTER TABLE \"app\".\"orders\" ALTER COLUMN \"id\" TYPE bigint;\nALTER SEQUENCE \"app\".\"orders_id_seq\" AS bigint;");
    }

    [Fact]
    public void A_cycling_a_descending_and_a_negative_start_sequence_are_never_judged()
    {
        long most = int.MaxValue - 1;
        Judge(Identity(most) with { Cycles = true }).Findings.Should().BeEmpty();
        Judge(Identity(most) with { Increment = -1 }).Findings.Should().BeEmpty();
        Judge(Identity(most) with { Start = -100 }).Findings.Should().BeEmpty();
    }

    [Fact]
    public void Without_column_select_the_row_count_is_the_floor_and_the_grant_is_named()
    {
        HealthIntegerKeyRow unreadable = Identity(0) with { LargestSampled = null, StatisticsReadable = false, Rows = 1_200_000_000, Unique = true };
        HealthFinding finding = new IntegerKeyHeadroomCheck().Evaluate(
                new IntegerKeyFacts([unreadable], [Orders]) { ReadsAs = "studio_reader" },
                Evaluation())
            .Findings.Single();

        finding.Evidence.Should().Contain(new HealthEvidence("Largest value stored, at least", "1,200,000,000 - a row-count floor"));
        finding.Caveat.Should().Contain("SELECT on column id of app.orders for role studio_reader").And.Contain("row-count floor alone");

        HealthVerdict blind = new IntegerKeyHeadroomCheck().Evaluate(
            new IntegerKeyFacts([unreadable with { Unique = false }], [Orders]) { ReadsAs = "studio_reader" },
            Evaluation());
        blind.Findings.Should().BeEmpty();
        blind.Note.Should().StartWith("1 column fed by a sequence could not be estimated").And.Contain("role studio_reader");
    }

    [Fact]
    public void A_row_count_is_a_floor_only_for_a_unique_column_and_counts_from_the_start()
    {
        IntegerKeyHeadroomCheck.Headroom(Identity(0) with { LargestSampled = null, Rows = 1_000, Start = 1_000_000 })!
            .Estimate.Should().Be(1_000_999, "a thousand distinct values at or above 1,000,000");
        IntegerKeyHeadroomCheck.Headroom(Identity(0) with { LargestSampled = null, Rows = 1_000, Unique = false })
            .Should().BeNull("a column that is not unique proves nothing by its count");
    }

    [Fact]
    public void A_serial_read_twice_through_its_owned_sequence_and_its_default_is_one_key()
    {
        HealthIntegerKeyRow owned = Identity(10) with { FedBy = "serial" };
        IntegerKeyHeadroomCheck.Distinct([owned with { FedBy = "default" }, owned]).Should().ContainSingle().Which.FedBy.Should().Be("serial");
    }

    [Fact]
    public void A_hostile_column_or_sequence_name_is_quoted_or_gets_no_statement()
    {
        StudioTable odd = Host("Order Lines", "Sales");
        HealthIntegerKeyRow key = Identity((long) (int.MaxValue * 0.9)) with { Schema = "Sales", Table = "Order Lines", Column = "Id; drop", FedBy = "serial", SequenceSchema = "Sales", Sequence = "seq \"x\"" };

        new IntegerKeyHeadroomCheck().Evaluate(new IntegerKeyFacts([key], [odd]), Evaluation()).Findings.Single().Remedy
            .Should().BeNull("the sequence's name has a double quote in it, so the pair of statements is not offered");

        new IntegerKeyHeadroomCheck().Evaluate(new IntegerKeyFacts([key with { Sequence = "seq x" }], [odd]), Evaluation()).Findings.Single().Remedy!.Code
            .Should().Be("ALTER TABLE \"Sales\".\"Order Lines\" ALTER COLUMN \"Id; drop\" TYPE bigint;\nALTER SEQUENCE \"Sales\".\"seq x\" AS bigint;");
    }
}
