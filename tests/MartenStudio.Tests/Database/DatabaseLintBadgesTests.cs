using AngleSharp.Dom;

using Bunit;

using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Web;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Services.Database;

namespace MartenStudio.Tests.Database;

/// <summary>
/// W5-B1's browser lints: a foreign key no index covers, both from the table and into it; a SECURITY DEFINER function that
/// does not pin <c>search_path</c>, in the grid and in its opened row; and a sequence's headroom after one read, after two,
/// and when nothing was taken between them.
/// </summary>
public class DatabaseLintBadgesTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_foreign_key_no_index_covers_wears_the_badge_from_the_table_and_into_it()
    {
        using var context = DatabasePageData.Context();
        DatabaseObjectDetail detail = FakeDatabaseObjects.Detail("quartz", "qrtz_triggers");
        context.DatabaseObjects.Detail = detail with
        {
            ForeignKeysOut = [detail.ForeignKeysOut[0] with { Covered = false }],
            ForeignKeysIn = [detail.ForeignKeysIn[0] with { Covered = false }],
        };

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "keys");

        foreach (string direction in new[] { ".ms-db-fks-out", ".ms-db-fks-in" })
        {
            IElement badge = page.Find(direction + " tbody tr .ms-db-fk-uncovered");
            badge.TextContent.Trim().Should().Be(ObjectKeysTab.UncoveredText);
            badge.ClassList.Should().Contain("ms-db-badge-warning");
            badge.GetAttribute("title").Should().Contain("never a foreign key").And.Contain("T15");
        }
    }

    [Fact]
    public void An_index_shows_its_estimated_size_scans_and_last_scan_and_one_not_ready_says_so()
    {
        using var context = DatabasePageData.Context();
        DatabaseObjectDetail detail = FakeDatabaseObjects.Detail("quartz", "qrtz_triggers");
        context.DatabaseObjects.Detail = detail with
        {
            Indexes =
            [
                detail.Indexes[0] with
                {
                    SizeBytes = 16384,
                    Scans = 1234,
                    LastScan = new DateTimeOffset(2026, 10, 26, 9, 30, 0, TimeSpan.Zero),
                    TuplesRead = 5,
                    TuplesFetched = 3,
                },
                detail.Indexes[1] with { SizeBytes = null, Scans = null, LastScan = null, TuplesRead = null, TuplesFetched = null, IsReady = false },
            ],
        };

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "keys");

        var rows = page.FindAll(".ms-db-indexes tbody tr");
        var first = rows[0].QuerySelectorAll("td");
        first[2].TextContent.Trim().Should().StartWith("~");
        first[3].TextContent.Trim().Should().Be("1,234");
        first[3].GetAttribute("title").Should().Be("5 index entries read, 3 table rows fetched");
        first[4].TextContent.Trim().Should().Be("2026-10-26 09:30 UTC");

        var second = rows[1].QuerySelectorAll("td");
        second[2].TextContent.Trim().Should().Be("—");
        second[3].TextContent.Trim().Should().Be("—");
        second[4].TextContent.Trim().Should().Be("—");
        rows[1].TextContent.Should().Contain("not ready");
        rows[0].TextContent.Should().NotContain("not ready");
    }

    [Fact]
    public void A_covered_key_and_one_whose_coverage_was_not_read_wear_no_badge()
    {
        using var context = DatabasePageData.Context();
        DatabaseObjectDetail detail = FakeDatabaseObjects.Detail("quartz", "qrtz_triggers");
        context.DatabaseObjects.Detail = detail with
        {
            ForeignKeysOut = [detail.ForeignKeysOut[0] with { Covered = true }],
            ForeignKeysIn = [detail.ForeignKeysIn[0] with { Covered = null }],
        };

        var page = DatabasePageData.RenderObject(context, "quartz", "qrtz_triggers", "keys");

        page.FindAll(".ms-db-fk-uncovered").Should().BeEmpty("covered says nothing, and unknown is never drawn as uncovered");
    }

    [Fact]
    public async Task An_unpinned_security_definer_says_why_in_its_opened_row()
    {
        using var context = DatabasePageData.Context();

        var page = DatabasePageData.RenderBrowser(context, "kind=functions&schema=legacy");
        IElement row = FunctionRow(page, "unsafe_definer");
        IElement badge = row.QuerySelector(".ms-db-security-unpinned")!;
        badge.TextContent.Trim().Should().Be(DatabaseFunctionsGrid.UnpinnedText);
        badge.GetAttribute("title").Should().Be(RoutineSecurity.UnpinnedReason);

        await row.QuerySelector(".ms-db-expander")!.ClickAsync(new MouseEventArgs());

        page.WaitForAssertion(() => page.Find(".ms-db-definition-row .ms-db-security-note").TextContent
            .Should().Contain(DatabaseFunctionsGrid.UnpinnedText + ".").And.Contain(RoutineSecurity.UnpinnedReason));
        page.Find(".ms-db-security-note").GetAttribute("role").Should().Be("note");

        await FunctionRow(page, "archive_orders").QuerySelector(".ms-db-expander")!.ClickAsync(new MouseEventArgs());
        page.WaitForAssertion(() => page.FindAll(".ms-db-definition-row").Should().HaveCount(2));
        page.FindAll(".ms-db-security-note").Should().ContainSingle("a function that pins its search_path, or is not SECURITY DEFINER, has no note");
    }

    [Fact]
    public void One_read_says_the_share_used_and_offers_a_second_read_for_a_rate()
    {
        using var context = SequencesContext();
        var page = DatabasePageData.RenderBrowser(context, "kind=sequences");

        Read(context, page, 1_073_741_824, At);

        IElement headroom = SequenceRow(page, "invoice_no_seq").QuerySelector(".ms-db-headroom")!;
        headroom.GetAttribute("role").Should().Be("note");
        headroom.QuerySelector(".ms-db-headroom-used")!.TextContent.Trim().Should().Be("50.0% of the column's range used",
            "an integer column on a bigint sequence is judged against integer's range");
        headroom.QuerySelector(".ms-db-headroom-used")!.GetAttribute("title").Should().Contain("integer").And.Contain("after which nextval fails");
        headroom.QuerySelector("button.ms-db-read-again")!.TextContent.Trim().Should().Be("read again for a rate");
        headroom.QuerySelector(".ms-db-headroom-rate").Should().BeNull("one read makes no rate");
        headroom.QuerySelector(".ms-db-headroom-cycles").Should().BeNull();
    }

    [Fact]
    public void Two_reads_say_how_long_is_left_at_the_recent_rate()
    {
        using var context = SequencesContext();
        var page = DatabasePageData.RenderBrowser(context, "kind=sequences");

        Read(context, page, 1_073_741_824, At);
        Read(context, page, 1_073_741_924, At.AddSeconds(100), again: true);

        IElement headroom = SequenceRow(page, "invoice_no_seq").QuerySelector(".ms-db-headroom")!;
        headroom.QuerySelector(".ms-db-headroom-rate")!.TextContent.Trim().Should().Be("at the recent rate, about 34 years left",
            "a value a second, with 1,073,741,723 left before integer's limit");
        headroom.QuerySelector("button.ms-db-read-again").Should().BeNull();
        context.DatabaseObjects.SequenceValuesAsked.Should().Equal("legacy.invoice_no_seq", "legacy.invoice_no_seq");
    }

    [Fact]
    public void Two_reads_of_the_same_value_say_nothing_was_taken_and_a_cycling_sequence_says_it_cycles()
    {
        using var context = SequencesContext();
        var page = DatabasePageData.RenderBrowser(context, "kind=sequences");

        Read(context, page, 500, At, cycles: true);
        Read(context, page, 500, At.AddSeconds(60), cycles: true, again: true);

        IElement headroom = SequenceRow(page, "invoice_no_seq").QuerySelector(".ms-db-headroom")!;
        headroom.QuerySelector(".ms-db-headroom-rate")!.TextContent.Trim().Should().Be("no values taken since the last read");
        headroom.QuerySelector(".ms-db-headroom-cycles")!.TextContent.Trim().Should().Be("cycles");
        headroom.QuerySelector(".ms-db-headroom-used")!.GetAttribute("title").Should().Contain("as it cycles, starts again");
    }

    private static StudioComponentContext SequencesContext()
    {
        StudioComponentContext context = DatabasePageData.Context();
        context.DatabaseObjects.List = DatabasePageData.ListOf(
            DatabaseObjectCategory.Sequences,
            [DatabasePageData.Sequence("legacy", "invoice_no_seq", canReadValue: true)]);
        return context;
    }

    /// <summary>Answers the next value read with <paramref name="value" />, and asks for it with the row's button.</summary>
    private static void Read<T>(StudioComponentContext context, IRenderedComponent<T> page, long value, DateTimeOffset readAt, bool cycles = false, bool again = false)
        where T : Microsoft.AspNetCore.Components.IComponent
    {
        context.DatabaseObjects.SequenceValue = new DatabaseSequenceValue(new DatabaseObjectRef(DatabaseObjectKind.Sequence, "legacy", "invoice_no_seq"), value, DatabaseRefusal.None, null)
        {
            Headroom = new SequenceHeadroomFacts("bigint", 1, 1, 1, long.MaxValue, cycles, "integer", int.MaxValue, readAt),
        };

        SequenceRow(page, "invoice_no_seq").QuerySelector(again ? "button.ms-db-read-again" : "button.ms-db-read-value")!.Click();
        page.WaitForAssertion(() => SequenceRow(page, "invoice_no_seq").QuerySelector(".ms-db-headroom").Should().NotBeNull());
    }

    private static IElement FunctionRow<T>(IRenderedComponent<T> page, string name)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll(".ms-db-functions tbody tr:not(.ms-db-definition-row)")
            .First(x => x.QuerySelector(".ms-db-signature")!.TextContent.Trim().StartsWith(name + "(", StringComparison.Ordinal)
                || x.QuerySelector(".ms-db-signature")!.TextContent.Contains("." + name + "(", StringComparison.Ordinal));

    private static IElement SequenceRow<T>(IRenderedComponent<T> page, string name)
        where T : Microsoft.AspNetCore.Components.IComponent =>
        page.FindAll(".ms-db-sequences tbody tr").First(x => x.QuerySelector(".ms-db-name")!.TextContent.Trim().EndsWith(name, StringComparison.Ordinal));
}
