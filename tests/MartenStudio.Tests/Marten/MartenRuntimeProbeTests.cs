using System.Reflection;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Tests.Marten;

/// <summary>
/// F1, pinned at both ends: every light-up probe finds its member exactly when the resolved package is at or
/// above the version that introduced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same test is right at the floor and at the latest.</b> At Marten 9.31.0 (JasperFx 2.60.0) every
/// probe in the table must find nothing; at 9.50.2 (JasperFx 2.87.1) every one but the 2.89 classifier must
/// find its member. A probe that silently finds nothing at the latest - a renamed member, a type that moved
/// assembly, a namespace typo - is precisely the failure that light-up would otherwise hide forever, because
/// "absent" is what the studio does without. So the assertion is "present iff the version says so", over
/// the table itself: a feature added to the table is covered the moment it exists.
/// </para>
/// <para>
/// Run it at another Marten with the version switch P0 adds (<c>-p:MartenStudioMartenVersion=9.50.2</c>);
/// until then, by moving the <c>Marten</c> pin in <c>Directory.Packages.props</c> locally.
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
    /// The JasperFx packages a Marten release brings: what a feature's "Marten X.Y+" relies on, checked against
    /// what this run resolved. At the floor that is Marten 9.31.0 with JasperFx 2.60.0.
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

    [Theory]
    [InlineData("9.31.0+3c91afcd97ef4578", 9, 31, 0)]
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
        MartenRuntime runtime = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string> { [RuntimePackage.Marten] = "9.31.0+abc" });

        runtime.Describe(RuntimePackage.Marten).Should().Be("Marten 9.31.0");
        runtime.Describe(RuntimePackage.Npgsql).Should().Be("Npgsql (version unknown)");
        runtime.Components.Should().HaveCount(Enum.GetValues<RuntimePackage>().Length);
    }
}
