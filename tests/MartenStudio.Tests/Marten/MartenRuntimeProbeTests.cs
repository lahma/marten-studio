using System.Reflection;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Tests.Marten;

/// <summary>
/// F1, pinned at both ends: every light-up probe finds its member exactly when the resolved package is at or
/// above the version that introduced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same test is right at the floor and at the latest.</b> At the floor - Marten 9.39.0, which brings
/// JasperFx and JasperFx.Events 2.74.0 (Marten's own <c>Directory.Packages.props</c> at <c>V9.39.0</c>) - every
/// probe in the table must find nothing: the earliest of them is Marten 9.41.0's tenant compaction and
/// JasperFx.Events 2.76.0's shard start reason. At the latest tested, 9.50.2 (JasperFx 2.87.1), every one but
/// the 2.89 classifier must find its member. A probe that silently finds nothing at the latest - a renamed
/// member, a type that moved assembly, a namespace typo - is precisely the failure that light-up would
/// otherwise hide forever, because "absent" is what the studio does without. So the general assertion is
/// "present iff the version says so", over the table itself, and a feature added to the table is covered the
/// moment it exists; and at the two versions CI runs, the exact set is pinned as well
/// (<see cref="At_the_floor_and_the_latest_the_available_features_are_exactly_the_pinned_ones" />), so a table
/// entry whose introducing version is itself wrong cannot pass by being consistently wrong.
/// </para>
/// <para>
/// Run it at the latest with <c>-p:MartenStudioMartenVersion=9.50.2</c>, or Fallout's
/// <c>--marten-version latest</c>.
/// </para>
/// <para>
/// This is deliberately not <c>MartenApiSurfaceTest</c>, which pins the floor contract. That one fails if a
/// member the studio <em>needs</em> moves; this one fails if a member the studio merely <em>uses when
/// present</em> is not found where the version says it should be.
/// </para>
/// </remarks>
public class MartenRuntimeProbeTests
{
    public static TheoryData<string> EveryFeature()
    {
        TheoryData<string> data = new();
        foreach (RuntimeFeature feature in RuntimeFeature.All)
        {
            data.Add(feature.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFeature))]
    public void A_probe_finds_its_member_exactly_when_the_resolved_version_has_it(string name)
    {
        RuntimeFeature feature = RuntimeFeature.Find(name)!;
        MartenRuntime runtime = MartenRuntime.Current;

        Version? resolved = runtime.VersionOf(feature.Package);
        resolved.Should().NotBeNull("{0}'s version must be readable for its probe to be judged", MartenRuntime.PackageName(feature.Package));

        bool expected = resolved >= feature.IntroducedIn;

        feature.HasProbe.Should().BeTrue("every feature in the table names the member it lights up on");
        feature.Probe().Should().Be(
            expected,
            "{0} first appears in {1} {2}, and this run resolved {1} {3}",
            feature.Name,
            MartenRuntime.PackageName(feature.Package),
            feature.IntroducedIn,
            resolved);

        feature.IsAvailable(runtime).Should().Be(expected, "the process's own runtime answers from the probe");
    }

    [Theory]
    [MemberData(nameof(EveryFeature))]
    public void A_made_up_runtime_answers_from_its_versions_alone(string name)
    {
        RuntimeFeature feature = RuntimeFeature.Find(name)!;
        string package = MartenRuntime.PackageName(feature.Package);

        MartenRuntime floor = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [feature.Package] = "1.0.0" });
        MartenRuntime exactly = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [feature.Package] = feature.IntroducedIn.ToString(3) });
        MartenRuntime unknown = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>());

        floor.IsProcessRuntime.Should().BeFalse();
        feature.IsAvailable(floor).Should().BeFalse("{0} 1.0.0 is older than {1}", package, feature.IntroducedIn);
        feature.IsAvailable(exactly).Should().BeTrue("the introducing version has it");
        feature.IsAvailable(unknown).Should().BeFalse("an unknown version is not assumed to have anything");
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

    /// <summary>
    /// The features this run's Marten has, pinned by name at the two versions CI runs - the floor and the
    /// latest tested, both read from <c>Directory.Packages.props</c> - so that a wrong "introduced in" cannot
    /// pass the general test above by being wrong consistently. At any other version only that test applies.
    /// </summary>
    [Fact]
    public void At_the_floor_and_the_latest_the_available_features_are_exactly_the_pinned_ones()
    {
        Version running = MartenRuntime.Current.VersionOf(RuntimePackage.Marten)!;
        Version floor = Version.Parse(PackagesProperty("MartenStudioMartenFloor"));
        Version latest = Version.Parse(PackagesProperty("MartenStudioMartenLatest"));

        string[] available = [.. RuntimeFeature.All.Where(static x => x.IsAvailable(MartenRuntime.Current)).Select(static x => x.Name).Order(StringComparer.Ordinal)];

        if (running == floor)
        {
            floor.Should().Be(new Version(9, 39, 0), "the floor moved: re-read what the table says at the new floor, and pin it here");

            // Marten 9.39.0 brings JasperFx and JasperFx.Events 2.74.0: older than every feature in the table.
            MartenRuntime.Current.VersionOf(RuntimePackage.JasperFx).Should().Be(new Version(2, 74, 0));
            MartenRuntime.Current.VersionOf(RuntimePackage.JasperFxEvents).Should().Be(new Version(2, 74, 0));
            available.Should().BeEmpty("nothing in the table is in Marten 9.39.0 or JasperFx 2.74.0");
        }
        else if (running == latest)
        {
            latest.Should().Be(new Version(9, 50, 2), "the latest tested moved: re-read what the table says there, and pin it here");

            MartenRuntime.Current.VersionOf(RuntimePackage.JasperFx).Should().Be(new Version(2, 87, 1));
            MartenRuntime.Current.VersionOf(RuntimePackage.JasperFxEvents).Should().Be(new Version(2, 87, 1));
            available.Should().Equal(
                [.. RuntimeFeature.All.Select(static x => x.Name).Where(static x => x != nameof(RuntimeFeature.TransientFailureClassifier)).Order(StringComparer.Ordinal)],
                "Marten 9.50.2 with JasperFx 2.87.1 has everything but JasperFx.Events 2.89.0's classifier");
        }
        else
        {
            Assert.Skip($"Pinned only at the floor ({floor}) and the latest tested ({latest}); this run resolved Marten {running}, "
                + "where the general probe test above still applies.");
        }
    }

    /// <summary>
    /// The JasperFx packages a Marten release brings: what a feature's "Marten X.Y+" relies on, checked against
    /// what this run resolved. At the floor that is Marten 9.39.0 with JasperFx 2.74.0.
    /// </summary>
    [Fact]
    public void A_feature_introduced_in_JasperFx_names_the_first_Marten_that_brings_it_or_says_none_does()
    {
        foreach (RuntimeFeature feature in RuntimeFeature.All.Where(static x => x.Package != RuntimePackage.Marten))
        {
            if (feature.FirstMarten is { } marten)
            {
                feature.Requirement.Should().Be("Marten " + marten.ToString(2) + "+");

                if (MartenRuntime.Current.VersionOf(RuntimePackage.Marten) is { } running && running >= marten)
                {
                    MartenRuntime.Current.VersionOf(feature.Package).Should().BeGreaterThanOrEqualTo(
                        feature.IntroducedIn,
                        "Marten {0} is said to bring {1} {2}, so a host on Marten {3} has it",
                        marten,
                        MartenRuntime.PackageName(feature.Package),
                        feature.IntroducedIn,
                        running);
                }
            }
            else
            {
                feature.Requirement.Should().Contain("in no Marten release yet");
            }
        }
    }

    private static string PackagesProperty(string name) =>
        System.Xml.Linq.XDocument.Load(MartenStudio.Tests.Conventions.RepositoryRoot.Combine("Directory.Packages.props"))
            .Descendants()
            .Where(x => x.Name.LocalName == name)
            .Select(static x => x.Value.Trim())
            .Should().ContainSingle("Directory.Packages.props defines {0} exactly once", name).Subject;

    [Theory]
    [InlineData("9.39.0+3c91afcd97ef4578", 9, 39, 0)]
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
        MartenRuntime runtime = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.39.0+abc" });

        runtime.Describe(RuntimePackage.Marten).Should().Be("Marten 9.39.0");
        runtime.Describe(RuntimePackage.Npgsql).Should().Be("Npgsql (version unknown)");
        runtime.Components.Should().HaveCount(Enum.GetValues<RuntimePackage>().Length);
    }
}
