using MartenStudio.Services.Events;

namespace MartenStudio.Tests.Events;

/// <summary>
/// M3's grouping, without a database: letters into groups by projection, shard, event type, exception type and message
/// fingerprint, with each group's counts, first and last seen, tenants and sample - and the read's bound.
/// </summary>
public class DeadLetterGroupingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static DeadLetterGroupRow Letter(
        long sequence,
        string tenant = "acme",
        string type = "order_placed",
        string exception = "InvalidOperationException",
        string projection = "Orders",
        int minutesAgo = 0,
        bool? skipped = false,
        string? message = null,
        bool eventFound = true) =>
        new(
            projection,
            "All",
            exception,
            tenant,
            Now.AddMinutes(-minutesAgo),
            message ?? "Failure to apply event #" + Text(sequence) + " Id(" + Guid.NewGuid() + ")",
            eventFound ? type : null,
            eventFound ? skipped : null,
            eventFound ? Text(sequence) + "/" + tenant : null);

    private static string Text(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static DeadLetterGroupList Group(params DeadLetterGroupRow[] rows) =>
        DeadLetterGrouping.Group(rows, maxLetters: 100, maxGroups: 10, Now, recordsSkips: true);

    [Fact]
    public void Letters_that_differ_only_in_their_message_numbers_and_ids_are_one_group()
    {
        DeadLetterGroupList list = Group(Letter(1), Letter(2), Letter(3));

        DeadLetterGroup group = list.Groups.Should().ContainSingle().Subject;
        group.Key.Should().Be(new DeadLetterGroupKey("Orders", "All", "order_placed", "InvalidOperationException", "Failure to apply event #{n} Id({guid})"));
        group.Letters.Should().Be(3);
        group.Events.Should().Be(3);
        list.TotalLetters.Should().Be(3);
        list.TotalGroups.Should().Be(1);
    }

    [Theory]
    [InlineData("projection")]
    [InlineData("type")]
    [InlineData("exception")]
    [InlineData("message")]
    public void Each_part_of_the_key_separates_groups(string part)
    {
        DeadLetterGroupRow other = part switch
        {
            "projection" => Letter(2, projection: "Shipping"),
            "type" => Letter(2, type: "order_shipped"),
            "exception" => Letter(2, exception: "DivideByZeroException"),
            _ => Letter(2, message: "Event deserialization error on sequence = 2 for event type order_placed"),
        };

        Group(Letter(1), other).Groups.Should().HaveCount(2);
    }

    /// <summary>A letter whose event the scope cannot see is a letter of the group, not one of its events, and has no event type.</summary>
    [Fact]
    public void A_letter_whose_event_is_not_in_scope_counts_as_a_letter_and_not_as_an_event()
    {
        DeadLetterGroup group = Group(Letter(1, eventFound: false), Letter(2, eventFound: false)).Groups.Single();

        group.Key.EventType.Should().BeNull();
        group.Letters.Should().Be(2);
        group.Events.Should().Be(0);
        group.EventsSkipped.Should().Be(0);
    }

    /// <summary>An event counts once however many of its letters there are, and so does its skip.</summary>
    [Fact]
    public void An_event_counts_once_however_many_letters_name_it()
    {
        DeadLetterGroup group = Group(Letter(7, skipped: true), Letter(7, skipped: true), Letter(8)).Groups.Single();

        group.Letters.Should().Be(3);
        group.Events.Should().Be(2);
        group.EventsSkipped.Should().Be(1);
        group.EventsNotSkipped.Should().Be(1);
    }

    [Fact]
    public void A_store_that_records_no_skips_counts_none()
    {
        DeadLetterGroup group = DeadLetterGrouping.Group([Letter(1, skipped: null)], 100, 10, Now, recordsSkips: false).Groups.Single();

        group.EventsSkipped.Should().BeNull();
        group.EventsNotSkipped.Should().BeNull();
    }

    [Fact]
    public void First_and_last_seen_are_the_oldest_and_newest_letters_and_the_sample_is_the_newest()
    {
        DeadLetterGroup group = Group(
            Letter(3, minutesAgo: 1, message: "Failure to apply event #3 Id(00000000-0000-0000-0000-000000000003)"),
            Letter(2, minutesAgo: 60),
            Letter(1, minutesAgo: 600)).Groups.Single();

        group.FirstSeen.Should().Be(Now.AddMinutes(-600));
        group.LastSeen.Should().Be(Now.AddMinutes(-1));
        group.SampleMessage.Should().Be("Failure to apply event #3 Id(00000000-0000-0000-0000-000000000003)",
            "the read is newest first, so the first letter is the newest");
    }

    [Fact]
    public void The_sample_is_cut_at_its_length()
    {
        string message = "Failure " + new string('x', 2 * DeadLetterGroupLimits.SampleLength);

        Group(Letter(1, message: message)).Groups.Single().SampleMessage!.Length.Should().Be(DeadLetterGroupLimits.SampleLength);
    }

    /// <summary>Every tenant is counted; the names are ordered and bounded - which a visitor is shown is decided later.</summary>
    [Fact]
    public void A_groups_tenants_are_counted_in_full_and_named_in_order_up_to_the_bound()
    {
        DeadLetterGroupRow[] rows =
        [
            .. Enumerable.Range(0, DeadLetterGroupLimits.MaxTenantNames + 5)
                .Select(static index => Letter(index, tenant: "tenant-" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))),
            Letter(1_000, tenant: "tenant-000"),
        ];

        DeadLetterGroup group = DeadLetterGrouping.Group(rows, 1_000, 10, Now, true).Groups.Single();

        group.TenantCount.Should().Be(DeadLetterGroupLimits.MaxTenantNames + 5);
        group.TenantNames.Should().HaveCount(DeadLetterGroupLimits.MaxTenantNames).And.BeInAscendingOrder(StringComparer.Ordinal);
        group.TenantNames[0].Should().Be("tenant-000");
        group.TenantsNotNamed.Should().Be(5);
    }

    [Fact]
    public void Groups_are_listed_by_letters_then_by_the_newest_letter()
    {
        DeadLetterGroupList list = Group(
            Letter(1, projection: "Few", minutesAgo: 0),
            Letter(2, projection: "Many", minutesAgo: 10),
            Letter(3, projection: "Many", minutesAgo: 10),
            Letter(4, projection: "Older", minutesAgo: 30));

        list.Groups.Select(static x => x.Key.ProjectionName).Should().Equal("Many", "Few", "Older");
    }

    /// <summary>The read asks for one letter more than it groups: that one is the proof there were more, and is not grouped.</summary>
    [Fact]
    public void A_read_past_its_bound_groups_the_newest_and_says_it_was_cut()
    {
        DeadLetterGroupRow[] rows = [.. Enumerable.Range(1, 6).Select(static index => Letter(index))];

        DeadLetterGroupList cut = DeadLetterGrouping.Group(rows, maxLetters: 5, maxGroups: 10, Now, true);
        DeadLetterGroupList whole = DeadLetterGrouping.Group(rows[..5], maxLetters: 5, maxGroups: 10, Now, true);

        cut.LettersTruncated.Should().BeTrue();
        cut.TotalLetters.Should().Be(5);
        cut.Groups.Single().Letters.Should().Be(5);
        whole.LettersTruncated.Should().BeFalse();
    }

    [Fact]
    public void More_groups_than_are_listed_are_counted()
    {
        DeadLetterGroupRow[] rows = [.. Enumerable.Range(1, 5).Select(static index => Letter(index, projection: "P" + Text(index)))];

        DeadLetterGroupList list = DeadLetterGrouping.Group(rows, maxLetters: 100, maxGroups: 3, Now, true);

        list.Groups.Should().HaveCount(3);
        list.TotalGroups.Should().Be(5);
        list.IsTruncated.Should().BeTrue();
        list.RecordsSkips.Should().BeTrue();
        list.ReadAt.Should().Be(Now);
    }
}
