using System.Buffers.Text;
using System.Text;

using MartenStudio.Services.Events;

namespace MartenStudio.Tests.Events;

/// <summary>M3: a group travels in the list's query string (D9) as one opaque value, and comes back as it went.</summary>
public class DeadLetterGroupKeyTests
{
    private static readonly DeadLetterGroupKey[] Keys =
    [
        new DeadLetterGroupKey("OrderSummary", "All", "order_placed", "InvalidOperationException", "Failure to apply event #{n} Id({guid})"),
        new DeadLetterGroupKey(null, null, null, null, null),
        new DeadLetterGroupKey(string.Empty, "All", null, string.Empty, string.Empty),
        new DeadLetterGroupKey("Ünïcødé:with:colons", "n", "v12:", "System.Exception", "\"{string}\" vs '{string}', 3:4 ñ \U0001F600"),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_key_comes_back_from_its_value_as_it_went(int index) =>
        DeadLetterGroupKey.Decode(Keys[index].Encode()).Should().Be(Keys[index]);

    /// <summary>A missing part and an empty one are different keys, and stay different.</summary>
    [Fact]
    public void A_missing_part_is_not_an_empty_one()
    {
        var missing = new DeadLetterGroupKey("P", "S", null, "X", "F");
        var empty = new DeadLetterGroupKey("P", "S", string.Empty, "X", "F");

        missing.Encode().Should().NotBe(empty.Encode());
        DeadLetterGroupKey.Decode(missing.Encode())!.EventType.Should().BeNull();
        DeadLetterGroupKey.Decode(empty.Encode())!.EventType.Should().BeEmpty();
    }

    /// <summary>A circuit can put anything in the address: what this page did not write is no key, never an exception.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!")]
    [InlineData("bnZ2")]
    public void A_value_this_page_did_not_write_is_no_key(string? value) =>
        DeadLetterGroupKey.Decode(value).Should().BeNull();

    [Theory]
    [InlineData("nnnn")]
    [InlineData("nnnnnn")]
    [InlineData("v9:abcnnnn")]
    [InlineData("v-1:nnnnn")]
    [InlineData("x3:abcnnnn")]
    [InlineData("v3abcnnnn")]
    public void A_malformed_value_is_no_key(string text) =>
        DeadLetterGroupKey.Decode(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text))).Should().BeNull();

    [Fact]
    public void A_part_longer_than_the_bound_is_no_key()
    {
        string tooLong = new('x', DeadLetterGroupKey.MaxPartLength + 1);

        DeadLetterGroupKey.Decode(new DeadLetterGroupKey(tooLong, null, null, null, null).Encode()).Should().BeNull();
        DeadLetterGroupKey.Decode(new DeadLetterGroupKey(tooLong[1..], null, null, null, null).Encode()).Should().NotBeNull();
    }

    /// <summary>The one line the audit entry and the list's note carry, every missing part named as missing.</summary>
    [Fact]
    public void A_key_describes_itself_on_one_line()
    {
        new DeadLetterGroupKey("OrderSummary", "All", "order_placed", "InvalidOperationException", "Failure to apply event #{n} Id({guid})")
            .Describe().Should().Be("OrderSummary:All, event order_placed, InvalidOperationException, \"Failure to apply event #{n} Id({guid})\"");

        new DeadLetterGroupKey(null, null, null, null, null).Describe()
            .Should().Be("(no projection):(no shard), event (not in mt_events), (no exception type), (no message)");
    }
}
