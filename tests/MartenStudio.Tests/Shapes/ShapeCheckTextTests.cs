using System.Text.Json;

using MartenStudio.Services.Shapes;

namespace MartenStudio.Tests.Shapes;

/// <summary>The sentences of the shape check, one test each, so a change to the words is a change to a test (D-SHAPE S6, S10).</summary>
public class ShapeCheckTextTests
{
    private static ShapeItemKey Key => new("1", null);

    [Fact]
    public void Dropped()
    {
        ShapeCheckText.DocumentDropped(12, 200, "$.customerName", "Order").Should().Be(
            "12 of 200 sampled documents have `$.customerName`, which `Order` no longer declares: the next save drops it.");
    }

    [Fact]
    public void Nulled()
    {
        ShapeCheckText.DocumentNulled(3, 200, "$.status", "Order").Should().Be(
            "3 of 200 sampled documents have a value at `$.status` that `Order` reads as empty: the next save writes null over it.");
    }

    [Fact]
    public void Retyped_names_both_kinds()
    {
        ShapeCheckText.DocumentRetyped(4, 200, "$.quantity", "Order", JsonValueKind.String, JsonValueKind.Number).Should().Be(
            "4 of 200 sampled documents store `$.quantity` as a string, and `Order` writes it back as a number: the next save rewrites it.");
    }

    [Fact]
    public void Rewritten()
    {
        ShapeCheckText.DocumentRewritten(1, 200, "$.placedAt").Should().Be(
            "1 of 200 sampled documents have a value at `$.placedAt` that comes back different: the next save rewrites it (precision, format or an enum's spelling).");
    }

    [Fact]
    public void Respelled()
    {
        ShapeCheckText.DocumentRespelled(9, 200, "$.CustomerName", "$.customerName", "Order").Should().Be(
            "9 of 200 sampled documents store `$.CustomerName`, which `Order` writes as `$.customerName`: the value is kept, but a duplicated field, an index or raw SQL on the old spelling stops matching it after the next save.");
    }

    [Fact]
    public void Defaulted_in_some_and_in_every_item()
    {
        ShapeCheckText.DocumentDefaulted(150, 200, "$.discount", "Order").Should().Be(
            "`$.discount` is absent from 150 of 200 sampled documents: they load with its default, and the next save writes it.");
        ShapeCheckText.DocumentDefaulted(200, 200, "$.discount", "Order").Should().Be(
            "`$.discount` is declared by `Order` and set in none of the 200 sampled documents.");
    }

    [Fact]
    public void The_rename_question_is_a_question()
    {
        ShapeCheckText.RenameQuestion("$.customerName", "$.customer").Should().Be(
            "`$.customerName` is dropped and `$.customer` is never set in about the same documents: was it renamed?");
    }

    [Fact]
    public void An_unreadable_document_names_its_path_the_stored_kind_and_the_exception_type()
    {
        ShapeFailureGroup failure = new(ShapeFailureKind.Unreadable, "JsonException", "$.total", JsonValueKind.String, null, 7, Key, null);

        ShapeCheckText.DocumentFailure(failure, 200, "Order").Should().Be(
            "7 of 200 sampled documents cannot be loaded as `Order`: `$.total` is stored as a string (`JsonException`). "
            + "Loading one throws, and the studio's editor refuses to save it.");
    }

    [Fact]
    public void An_unreadable_document_with_no_path_names_the_exception_type_alone()
    {
        ShapeFailureGroup failure = new(ShapeFailureKind.Unreadable, "NotSupportedException", null, null, null, 2, Key, null);

        ShapeCheckText.DocumentFailure(failure, 10, "Order").Should().Be(
            "2 of 10 sampled documents cannot be loaded as `Order` (`NotSupportedException`). Loading one throws, and the studio's editor refuses to save it.");
    }

    [Fact]
    public void The_other_failures()
    {
        ShapeCheckText.DocumentFailure(new ShapeFailureGroup(ShapeFailureKind.UnknownSubclass, "ArgumentOutOfRangeException", null, null, "bicycle", 1, Key, null), 5, "Vehicle")
            .Should().Be("1 of 5 sampled documents name the subclass `bicycle`, which `Vehicle` no longer registers: Marten cannot load them.");
        ShapeCheckText.DocumentFailure(new ShapeFailureGroup(ShapeFailureKind.ReadAsNothing, string.Empty, null, null, null, 1, Key, null), 5, "Order")
            .Should().Contain("load as nothing at all as `Order`");
        ShapeCheckText.DocumentFailure(new ShapeFailureGroup(ShapeFailureKind.NotWritable, "JsonException", null, null, null, 1, Key, null), 5, "Order")
            .Should().Contain("writing them back could not be checked (`JsonException`)");
    }

    [Fact]
    public void Every_sampled_document_fits_is_never_healthy()
    {
        ShapeCheckText.EveryDocumentFits("Order").Should().Be("Every sampled document fits `Order`.").And.NotContain("healthy");
    }

    [Fact]
    public void The_scope_and_table_sentences()
    {
        ShapeCheckText.NoSuchDocumentType("ghost").Should().Be("There is no registered document type called 'ghost' to check.");
        ShapeCheckText.TableMissing.Should().Be("Marten has not created this table in this database yet, so there is nothing to check.");
        ShapeCheckText.AllTenantsEdgesOnly.Should().Be(
            "With all tenants in scope only the lowest and highest keys were read; choose a tenant for a spread sample.");
        ShapeCheckText.SoftDeletedLeftOut.Should().Be("Soft-deleted documents are not checked.");
        ShapeCheckText.NotAnalysed.Should().Be("PostgreSQL has not analysed this table yet, so only the lowest and highest ids were read.");
        ShapeCheckText.TooLarge(2).Should().Be("Too large to check: 2 (over 256 KiB each, counted and not read).");
    }

    [Fact]
    public void The_summary_of_a_complete_a_sampled_and_a_partial_check()
    {
        ShapeCheckResult complete = Result(ShapeCoverage.Complete, examined: 37);
        ShapeCheckResult sampled = Result(ShapeCoverage.Sampled, examined: 200, lowest: 25, highest: 25, spread: 150);
        ShapeCheckResult partial = Result(ShapeCoverage.Partial, examined: 112, reason: ShapeCheckText.BudgetRanOut(TimeSpan.FromSeconds(15)));

        ShapeCheckText.Summary(complete, "10:42").Should().Be("Checked all 37 documents at 10:42.");
        ShapeCheckText.Summary(sampled, "10:42").Should().Be(
            "Checked 200 documents at 10:42: the 25 lowest and 25 highest ids and 150 spread across the collection.");
        ShapeCheckText.Summary(partial, "10:42").Should().Be("Stopped after 112 documents: the 15-second budget ran out.");
        ShapeCheckText.Summary(Result(ShapeCoverage.Sampled, examined: 50, lowest: 25, highest: 25), "10:42").Should().EndWith("highest ids.");
    }

    [Fact]
    public void A_result_the_cache_handed_back_says_how_old_it_is_in_the_summary_of_either_kind()
    {
        ShapeCheckResult complete = Result(ShapeCoverage.Complete, examined: 37);

        ShapeCheckText.Summary(complete, "10:42", TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(20)).Should().Be(
            "Checked all 37 documents at 10:42 (3 min ago, from the last check).");
        ShapeCheckText.EventSummary(complete, "10:42", TimeSpan.FromSeconds(40)).Should().Be(
            "Checked all 37 events at 10:42 (40 s ago, from the last check).");
        ShapeCheckText.Summary(complete, "10:42").Should().Be("Checked all 37 documents at 10:42.");
        ShapeCheckText.EventSummary(Result(ShapeCoverage.Sampled, examined: 50, lowest: 25, highest: 25), "10:42", TimeSpan.FromMinutes(1))
            .Should().Be("Checked 50 events at 10:42 (1 min ago, from the last check): the 25 oldest and 25 newest.");
    }

    [Theory]
    [InlineData(0, "1 s ago")]
    [InlineData(59, "59 s ago")]
    [InlineData(60, "1 min ago")]
    [InlineData(299, "4 min ago")]
    [InlineData(3_600, "1 h ago")]
    [InlineData(-5, "1 s ago")]
    public void An_age_is_in_whole_units_and_never_zero(int seconds, string expected)
    {
        ShapeCheckText.Ago(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
    }

    [Fact]
    public void A_sentence_splits_into_text_and_code_runs()
    {
        ShapeCheckText.Split("a `b` c `d`").Should().Equal(("a ", false), ("b", true), (" c ", false), ("d", true));
    }

    private static ShapeCheckResult Result(ShapeCoverage coverage, int examined, int lowest = 0, int highest = 0, int spread = 0, string? reason = null) =>
        new("order", coverage, DateTimeOffset.UnixEpoch, TimeSpan.Zero, examined, 0, lowest, highest, spread, reason, null, [], [], null, null, []);
}
