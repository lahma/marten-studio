using System.Globalization;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// The fast suites run in fi-FI, which <c>tests/xunit.runner.json</c> sets for all five (testing.md, <i>The fast suites run in fi-FI</i>). That
/// culture groups thousands with a no-break space and writes its minus sign as U+2212, so text or markup the studio formats in the server's
/// culture fails a test (#99) instead of passing in the English culture CI runs in. This test holds the setting in force: if xunit stopped
/// applying it, every suite would stay green and those faults would pass unseen again.
/// </summary>
public class SuiteCultureTests
{
    [Fact]
    public void The_suite_runs_in_a_culture_that_writes_numbers_unlike_the_invariant_one()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;

        culture.Name.Should().Be("fi-FI", "tests/xunit.runner.json sets it, and xunit applies it to every test");
        culture.UseUserOverride.Should().BeFalse("xunit builds the culture without the machine's regional overrides, so it is the same everywhere");
        culture.NumberFormat.NumberGroupSeparator.Should().NotBe(",", "a count formatted in this culture must not read like the invariant one");
        culture.NumberFormat.NegativeSign.Should().Be("\u2212", "a negative number formatted in this culture must not read like the invariant one");
    }
}
