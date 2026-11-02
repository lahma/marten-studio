using System.Reflection;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Tests.Marten;

/// <summary>
/// F1: the run-time light-up mechanism (<see cref="RuntimeFeature" />, <see cref="MartenRuntime" />), whose table is
/// empty at 1.1.0 and which this suite keeps honest until the next latest-tested bump puts a feature in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the table is empty.</b> The floors moved to the newest releases on 2026-11-03 (Marten 9.57.0, which brings
/// JasperFx and JasperFx.Events 2.98.0 and Weasel 9.48.0; <c>docs/maintainers/package-budget-changes.md</c>), so every
/// feature the table held, from Marten 9.31 to 9.53, became unconditional. <see cref="The_table_is_empty_because_the_floor_has_every_feature" />
/// fails the day a feature is added, so that its author pins it here at the floor and at the latest, as the old
/// <c>At_the_floor_and_the_latest_the_available_features_are_exactly_the_pinned_ones</c> did: a probe that silently
/// finds nothing at the latest - a renamed member, a type that moved assembly - is precisely the failure light-up would
/// otherwise hide forever, because "absent" is what the studio does without.
/// </para>
/// <para>
/// The mechanism itself (<see cref="RuntimeFeature.Requirement" />, <see cref="RuntimeFeature.IsAvailable" />, the
/// probe's cache and its catch) is tested through features built by reflection over the private constructor, because the
/// table is the only legitimate way to get one and it is empty.
/// </para>
/// </remarks>
public class MartenRuntimeProbeTests
{
    [Fact]
    public void The_table_is_empty_because_the_floor_has_every_feature()
    {
        Version floor = Version.Parse(PackagesProperty("MartenStudioMartenFloor"));
        Version latest = Version.Parse(PackagesProperty("MartenStudioMartenLatest"));

        latest.Should().Be(floor, "the table is empty because the latest-tested Marten is the floor; a bump past it adds features");
        MartenRuntime.Current.VersionOf(RuntimePackage.Marten)!.Should().BeGreaterThanOrEqualTo(floor);

        RuntimeFeature.All.Should().BeEmpty(
            "every feature up to Marten 9.57.0 is unconditional; a feature added for a newer Marten is pinned here at the floor and the latest");
    }

    /// <summary>The general rule, for whatever the table holds: a probe finds its member exactly when the version says so.</summary>
    [Fact]
    public void A_probe_finds_its_member_exactly_when_the_resolved_version_has_it()
    {
        MartenRuntime runtime = MartenRuntime.Current;

        foreach (RuntimeFeature feature in RuntimeFeature.All)
        {
            Version? resolved = runtime.VersionOf(feature.Package);
            resolved.Should().NotBeNull("{0}'s version must be readable for its probe to be judged", MartenRuntime.PackageName(feature.Package));

            bool expected = resolved >= feature.IntroducedIn;

            feature.HasProbe.Should().BeTrue("every feature in the table names the member it lights up on");
            feature.Probe().Should().Be(expected, "{0} first appears in {1} {2}", feature.Name, feature.Package, feature.IntroducedIn);
            feature.IsAvailable(runtime).Should().Be(expected, "the process's own runtime answers from the probe");
        }
    }

    /// <summary>
    /// The versions themselves: each read off an anchor type that really lives in that package's assembly, so
    /// a type that moves assembly in a later release fails here rather than reporting the wrong package.
    /// </summary>
    [Fact]
    public void Each_version_is_read_from_its_own_packages_assembly()
    {
        foreach (RuntimePackage package in Enum.GetValues<RuntimePackage>())
        {
            Assembly assembly = MartenRuntime.AnchorType(package).Assembly;

            assembly.GetName().Name.Should().Be(MartenRuntime.PackageName(package));
            MartenRuntime.Current.VersionOf(package).Should().NotBeNull(
                "{0} carries an informational version: {1}",
                package,
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "(none)");
        }
    }

    /// <summary>The floor's packages, as Marten 9.57.0's own <c>Directory.Packages.props</c> brings them.</summary>
    [Fact]
    public void The_floor_brings_the_packages_the_budget_says()
    {
        Version running = MartenRuntime.Current.VersionOf(RuntimePackage.Marten)!;

        if (running != new Version(9, 57, 0))
        {
            Assert.Skip($"Pinned at Marten 9.57.0, the floor and the latest tested; this run resolved Marten {running}.");
        }

        MartenRuntime.Current.VersionOf(RuntimePackage.JasperFx).Should().Be(new Version(2, 98, 0));
        MartenRuntime.Current.VersionOf(RuntimePackage.JasperFxEvents).Should().Be(new Version(2, 98, 0));
        MartenRuntime.Current.VersionOf(RuntimePackage.WeaselCore).Should().Be(new Version(9, 48, 0));
        MartenRuntime.Current.VersionOf(RuntimePackage.WeaselPostgresql).Should().Be(new Version(9, 48, 0));
    }

    // ---- the mechanism, through features the table does not hold ----------------------------------------

    /// <summary>
    /// A requirement names the patch when the first release that has the feature is a patch, so "Marten 9.50+" never tells a
    /// host on 9.50.2 it has what only 9.50.3 brought.
    /// </summary>
    [Fact]
    public void A_requirement_prints_the_patch_of_a_feature_a_patch_release_brings()
    {
        Made("PatchFeature", RuntimePackage.JasperFxEvents, "2.89.0", "9.50.3", probe: null).Requirement.Should().Be("Marten 9.50.3+");
        Made("MinorFeature", RuntimePackage.JasperFx, "2.84.0", "9.48.0", probe: null).Requirement.Should().Be("Marten 9.48+", "a .0 release is still printed in two parts");
        Made("Unreleased", RuntimePackage.JasperFxEvents, "3.0.0", firstMarten: null, probe: null).Requirement
            .Should().Be("JasperFx.Events 3.0+ (in no Marten release yet)");

        RuntimeFeature.Short(new Version(2, 89, 0)).Should().Be("2.89");
        RuntimeFeature.Short(new Version(9, 50, 3)).Should().Be("9.50.3");
        RuntimeFeature.Short(new Version(9, 51)).Should().Be("9.51", "a version with no patch at all is not given one");
    }

    [Fact]
    public void A_made_up_runtime_answers_from_its_versions_alone()
    {
        RuntimeFeature feature = Made("PatchFeature", RuntimePackage.JasperFxEvents, "2.89.0", "9.50.3", probe: () => false);

        MartenRuntime below = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.JasperFxEvents] = "2.87.1" });
        MartenRuntime exactly = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.JasperFxEvents] = "2.89.0" });
        MartenRuntime unknown = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>());

        below.IsProcessRuntime.Should().BeFalse();
        feature.IsAvailable(below).Should().BeFalse();
        feature.IsAvailable(exactly).Should().BeTrue("the introducing version has it, whatever the probe finds in this process");
        feature.IsAvailable(unknown).Should().BeFalse("an unknown version is not assumed to have anything");
    }

    [Fact]
    public void The_process_runtime_answers_from_the_probe_and_a_probe_that_throws_answers_absent()
    {
        int runs = 0;
        RuntimeFeature present = Made("Present", RuntimePackage.Marten, "9.99.0", "9.99.0", probe: () => { runs++; return true; });
        RuntimeFeature broken = Made("Broken", RuntimePackage.Marten, "9.0.0", "9.0.0", probe: () => throw new InvalidOperationException("a moved member"));

        present.IsAvailable(MartenRuntime.Current).Should().BeTrue("the probe is what a process runtime asks, not the version");
        present.IsAvailable(MartenRuntime.Current).Should().BeTrue();
        runs.Should().Be(1, "a probe is reflection over assemblies that do not change, so it runs once");

        broken.IsAvailable(MartenRuntime.Current).Should().BeFalse("a probe that throws is absent, never a fault");
    }

    private static RuntimeFeature Made(string name, RuntimePackage package, string introducedIn, string? firstMarten, Func<bool>? probe) =>
        (RuntimeFeature) typeof(RuntimeFeature)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single()
            .Invoke([name, package, introducedIn, firstMarten, "a made-up feature", probe]);

    private static string PackagesProperty(string name) =>
        System.Xml.Linq.XDocument.Load(MartenStudio.Tests.Conventions.RepositoryRoot.Combine("Directory.Packages.props"))
            .Descendants()
            .Where(x => x.Name.LocalName == name)
            .Select(static x => x.Value.Trim())
            .Should().ContainSingle("Directory.Packages.props defines {0} exactly once", name).Subject;

    [Theory]
    [InlineData("9.57.0+3c91afcd97ef4578", 9, 57, 0)]
    [InlineData("2.87.1", 2, 87, 1)]
    [InlineData("9.50.0-alpha.3+abc", 9, 50, 0)]
    [InlineData(" 10.0.12 ", 10, 0, 12)]
    public void An_informational_version_is_cut_at_its_build_metadata_and_prerelease_label(string informational, int major, int minor, int patch)
    {
        MartenRuntime.ParseVersion(informational).Should().Be(new Version(major, minor, patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("local-build")]
    [InlineData("+abc")]
    public void An_informational_version_that_is_not_one_is_unknown(string? informational)
    {
        MartenRuntime.ParseVersion(informational).Should().BeNull();
    }

    [Fact]
    public void A_version_is_described_for_a_person()
    {
        MartenRuntime runtime = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.57.0+abc" });

        runtime.Describe(RuntimePackage.Marten).Should().Be("Marten 9.57.0");
        runtime.Describe(RuntimePackage.Npgsql).Should().Be("Npgsql (version unknown)");
        runtime.Components.Should().HaveCount(Enum.GetValues<RuntimePackage>().Length);
    }
}
