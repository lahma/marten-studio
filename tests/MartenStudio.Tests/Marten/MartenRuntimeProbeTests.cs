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
/// JasperFx.Events 2.76.0's shard start reason. At the latest tested, 9.54.1 (JasperFx 2.95.1), every one must
/// find its member - the 2.89 classifier, the 9.50.3 offline enumeration and the 9.52 floors included. A probe that silently finds
/// nothing at the latest - a renamed
/// member, a type that moved assembly, a namespace typo - is precisely the failure that light-up would
/// otherwise hide forever, because "absent" is what the studio does without. So the general assertion is
/// "present iff the version says so", over the table itself, and a feature added to the table is covered the
/// moment it exists; and at the two versions CI runs, the exact set is pinned as well
/// (<see cref="At_the_floor_and_the_latest_the_available_features_are_exactly_the_pinned_ones" />), so a table
/// entry whose introducing version is itself wrong cannot pass by being consistently wrong.
/// </para>
/// <para>
/// Run it at the latest with <c>-p:MartenStudioMartenVersion=9.54.1</c>, or Fallout's
/// <c>--marten-version latest</c>. A probe re-anchored between two releases (<c>TaggedStreamCompaction</c>, 2026-10-26)
/// is also run at the releases either side of its version - 9.50.3 and 9.51.0 for that one - where the general test is
/// what applies.
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
            latest.Should().Be(new Version(9, 54, 2),"the latest tested moved: re-read what the table says there, and pin it here");

            // Marten 9.54.2 brings JasperFx and JasperFx.Events 2.95.1 (its Directory.Packages.props at v9.54.2).
            MartenRuntime.Current.VersionOf(RuntimePackage.JasperFx).Should().Be(new Version(2, 95, 1));
            MartenRuntime.Current.VersionOf(RuntimePackage.JasperFxEvents).Should().Be(new Version(2, 95, 1));
            available.Should().Equal(
                [.. RuntimeFeature.All.Select(static x => x.Name).Order(StringComparer.Ordinal)],
                "Marten 9.54.1 with JasperFx 2.95.1 has everything in the table");

            // Named, so that a feature added to the table later cannot make this pass by being the one that moved.
            available.Should().Contain(
            [
                nameof(RuntimeFeature.TransientFailureClassifier),
                nameof(RuntimeFeature.OfflineSchemaEnumeration),
                nameof(RuntimeFeature.TenantEventSkipping),
                nameof(RuntimeFeature.TenantSubscriptionRewind),
                nameof(RuntimeFeature.TaggedStreamCompaction),
                nameof(RuntimeFeature.HighWaterProgressStall),
                nameof(RuntimeFeature.ProjectionStartFloors),
                nameof(RuntimeFeature.SystemicDatabaseFailure),
                nameof(RuntimeFeature.PerDatabaseHighWaterAdvance),
                nameof(RuntimeFeature.DocumentEditPreview),
            ]);
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
                feature.Requirement.Should().Be("Marten " + (marten.Build > 0 ? marten.ToString(3) : marten.ToString(2)) + "+");

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

    /// <summary>
    /// A requirement names the patch when the first release that has the feature is a patch: 9.50.0 to 9.50.2 have no
    /// classifier and no offline enumeration, so "Marten 9.50+" would tell a host on 9.50.2 it has them.
    /// </summary>
    [Fact]
    public void A_requirement_prints_the_patch_of_a_feature_a_patch_release_brings()
    {
        RuntimeFeature.TransientFailureClassifier.Requirement.Should().Be("Marten 9.50.3+");
        RuntimeFeature.OfflineSchemaEnumeration.Requirement.Should().Be("Marten 9.50.3+");

        RuntimeFeature.DynamicLinqQuery.Requirement.Should().Be("Marten 9.48+", "a .0 release is still printed in two parts");
        RuntimeFeature.TenantEventSkipping.Requirement.Should().Be("Marten 9.51+");
        RuntimeFeature.ProjectionStartFloors.Requirement.Should().Be("Marten 9.52+");
        RuntimeFeature.SystemicDatabaseFailure.Requirement.Should().Be("Marten 9.52+", "JasperFx.Events 2.93 is first in Marten 9.52.0");
        RuntimeFeature.PerDatabaseHighWaterAdvance.Requirement.Should().Be("Marten 9.52+");
        RuntimeFeature.DocumentEditPreview.Requirement.Should().Be("Marten 9.53+");

        RuntimeFeature.Short(new Version(2, 89, 0)).Should().Be("2.89");
        RuntimeFeature.Short(new Version(9, 50, 3)).Should().Be("9.50.3");
        RuntimeFeature.Short(new Version(9, 51)).Should().Be("9.51", "a version with no patch at all is not given one");
    }

    /// <summary>
    /// A host on 9.50.2 is told it lacks the 9.50.3 features, and one on 9.50.3 that it has them - by a made-up runtime,
    /// so the answer is the version comparison whatever this run resolved.
    /// </summary>
    [Fact]
    public void A_patch_release_feature_is_absent_one_patch_below_and_present_at_it()
    {
        MartenRuntime below = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
        {
            [RuntimePackage.Marten] = "9.50.2",
            [RuntimePackage.JasperFxEvents] = "2.87.1",
        });

        MartenRuntime at = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
        {
            [RuntimePackage.Marten] = "9.50.3",
            [RuntimePackage.JasperFxEvents] = "2.90.1",
        });

        RuntimeFeature.OfflineSchemaEnumeration.IsAvailable(below).Should().BeFalse();
        RuntimeFeature.OfflineSchemaEnumeration.IsAvailable(at).Should().BeTrue();
        RuntimeFeature.TransientFailureClassifier.IsAvailable(below).Should().BeFalse();
        RuntimeFeature.TransientFailureClassifier.IsAvailable(at).Should().BeTrue();
        RuntimeFeature.TenantEventSkipping.IsAvailable(at).Should().BeFalse("9.50.3 has no tenant overload of MarkEventsAsSkipped");
    }

    /// <summary>
    /// The edit-preview probe's halves, read directly rather than through the probe's catch: below JasperFx 2.94 there is
    /// no declaration to find (the floor's 2.74.0 has no writer interface at all), and from Marten 9.53.0 the declaration
    /// is there and <c>DocumentStore</c> implements it - not the interface's default, which throws.
    /// </summary>
    [Fact]
    public void The_edit_preview_probe_finds_Martens_own_implementation_exactly_from_9_53()
    {
        Version marten = MartenRuntime.Current.VersionOf(RuntimePackage.Marten)!;
        Version jasperFx = MartenRuntime.Current.VersionOf(RuntimePackage.JasperFx)!;
        bool expected = marten >= new Version(9, 53, 0);

        System.Reflection.MethodInfo? declared = RuntimeFeature.EditPreviewMethod();
        (declared is not null).Should().Be(
            jasperFx >= new Version(2, 94, 0),
            "JasperFx {0}: PreviewDocumentJsonAsync is declared from 2.94.0", jasperFx);

        if (declared is not null)
        {
            declared.ReturnType.Should().Be<Task<string>>();
            declared.GetParameters().Select(static x => x.ParameterType.Name).Should().Equal("DocumentWriteRequest", "CancellationToken");
        }

        RuntimeFeature.DocumentEditPreview.Probe().Should().Be(expected, "Marten {0} implements the preview from 9.53.0", marten);
        MartenStudio.Services.Documents.MartenEditPreview.IsAvailable.Should().Be(expected,
            "the studio calls it exactly where the probe finds it, and the request record has its (type, id, json) constructor");
    }

    /// <summary>
    /// The tagged-compaction probe's anchor, called directly rather than through the probe's catch: what #5694 declares -
    /// the tag table's <c>_seq_id</c> index - is read without throwing at every Marten, and is there exactly from 9.51.0.
    /// Its first anchor, a private method of <c>TenantDataCleaner</c>, went with marten#5720 in 9.52.0, and the probe
    /// then answered "absent" at every Marten after it while compaction went on deleting the tag rows.
    /// </summary>
    /// <remarks>
    /// Run at 9.50.3 and 9.51.0 as well as the floor and the latest: the release below the feature has no index and
    /// the release that brought it has one, and neither knows about the cleanup that moved.
    /// </remarks>
    [Fact]
    public void The_tagged_compaction_probe_reads_the_declared_tag_index_and_not_a_private_member()
    {
        Version marten = MartenRuntime.Current.VersionOf(RuntimePackage.Marten)!;
        bool expected = marten >= new Version(9, 51, 0);

        // Anti-vacuity: the probe's tag table is declared at every Marten from the floor on, so "false" can only mean the
        // index is missing - never a tag type that failed to register, a feature schema that moved, or a misspelt table.
        Weasel.Postgresql.Tables.Table? table = RuntimeFeature.ProbeTagTable();
        table.Should().NotBeNull("Marten {0} declares mt_event_tag_{1} for a registered tag type", marten, RuntimeFeature.TagProbeSuffix);
        table!.Identifier.Name.Should().Be("mt_event_tag_studio_probe");
        table.Columns.Select(static x => x.Name).Should().Contain("seq_id", "the column the index is on is the table's at every version");

        RuntimeFeature.DeclaresTagSequenceIndex().Should().Be(expected,
            "Marten {0}: #5694 declares idx_mt_event_tag_<suffix>_seq_id from 9.51.0, and the anchor must read it without throwing",
            marten);
        RuntimeFeature.TaggedStreamCompaction.Probe().Should().Be(expected);
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
