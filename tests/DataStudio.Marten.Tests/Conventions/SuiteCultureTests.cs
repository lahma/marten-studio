using System.Globalization;
using System.Runtime.CompilerServices;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// The fast suites run in fi-FI (testing.md, <i>The fast suites run in fi-FI</i>). This file is linked into all five, and its module
/// initializer makes that culture the process's default before the first test, so it holds for every thread the suite runs code on. That
/// includes a <c>TestServer</c> request, which does not carry the test's execution context; xunit's own <c>culture</c> setting would reach
/// only the test's flow. The culture groups thousands with a no-break space and writes its minus sign as U+2212, so text or markup the
/// studio formats in the server's culture fails a test (#99) instead of passing in the English culture CI runs in. The tests hold the
/// setting in force.
/// </summary>
public partial class SuiteCultureTests
{
    /// <summary>The culture of the suite, without the machine's regional overrides, so it is ICU's fi-FI on every machine.</summary>
    internal static readonly CultureInfo Culture = new("fi-FI", useUserOverride: false);

    /// <summary>Makes <see cref="Culture" /> the default of every thread of the process; a test that needs another culture sets its own and restores it.</summary>
    [ModuleInitializer]
    internal static void SetTheSuiteCulture()
    {
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    [Fact]
    public void The_suite_runs_in_a_culture_that_writes_numbers_unlike_the_invariant_one()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;

        culture.Name.Should().Be("fi-FI", "the module initializer sets it before the first test");
        culture.UseUserOverride.Should().BeFalse("the culture is the same on every machine, whatever its regional settings");
        culture.NumberFormat.NumberGroupSeparator.Should().NotBe(",", "a count formatted in this culture must not read like the invariant one");
        culture.NumberFormat.NegativeSign.Should().Be("\u2212", "a negative number formatted in this culture must not read like the invariant one");
    }

    [Fact]
    public async Task A_thread_that_does_not_carry_the_tests_context_runs_in_it_too()
    {
        Task<string> elsewhere;

        using (ExecutionContext.SuppressFlow())
        {
            elsewhere = Task.Run(static () => CultureInfo.CurrentCulture.Name);
        }

        (await elsewhere).Should().Be("fi-FI", "a TestServer request runs on such a thread, and what it renders is held to the suite's culture too");
    }
}
