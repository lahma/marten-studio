using DataStudio.Marten.Internal;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// <c>AddMarten</c> and <c>AddMartenDiagnostics</c> refuse Marten 10 and later with one sentence that names the way out (#103). The refusal is driven through
/// the guard's seam, because this test host runs the one Marten the build restored, which is 9.
/// </summary>
public class MartenVersionGuardTests
{
    private const string Own = "2.0.2";

    [Fact]
    public void The_Marten_this_build_restored_passes_and_so_do_both_registrations()
    {
        typeof(global::Marten.IDocumentStore).Assembly.GetName().Version!.Major.Should().BeLessThan(MartenVersionGuard.FirstUnsupportedMajor);

        FluentActions.Invoking(MartenVersionGuard.Ensure).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddDataStudio().AddMarten()).Should().NotThrow();
    }

    [Theory]
    [InlineData("9.57.0.0")]
    [InlineData("9.99.9.0")]
    [InlineData("9.0.0.0")]
    [InlineData("8.0.0.0")]
    public void Marten_9_and_below_pass(string loaded)
    {
        FluentActions.Invoking(() => MartenVersionGuard.Ensure(Version.Parse(loaded), Own)).Should().NotThrow();
    }

    [Fact]
    public void An_assembly_with_no_version_is_not_refused()
    {
        FluentActions.Invoking(() => MartenVersionGuard.Ensure(null, Own)).Should().NotThrow();
    }

    [Theory]
    [InlineData("10.0.0.0", "10.0.0")]
    [InlineData("10.4.2.0", "10.4.2")]
    [InlineData("11.0.0.0", "11.0.0")]
    public void Marten_10_and_later_are_refused_with_the_sentence(string loaded, string shown)
    {
        FluentActions.Invoking(() => MartenVersionGuard.Ensure(Version.Parse(loaded), Own))
            .Should().Throw<InvalidOperationException>()
            .WithMessage($"Data Studio {Own} supports Marten 9, and this application uses Marten {shown}. Marten 10 and later need Data Studio 3.0 or later.");
    }

    [Fact]
    public void The_refusal_names_the_studios_own_release_version_without_its_commit()
    {
        string own = PostgresVersionGuard.ReleaseVersion(typeof(MartenVersionGuard).Assembly);

        own.Should().NotContain("+").And.NotBe("?");
        MartenVersionGuard.Refusal(new Version(10, 0, 0, 0), own).Should().StartWith($"Data Studio {own} supports Marten 9");
    }
}
