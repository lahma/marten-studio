using System.Reflection;

using MartenStudio.Services.Configuration;
using MartenStudio.Tests.Conventions;

using Microsoft.Extensions.DependencyInjection;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The rules every health check is held to, over the studio's real assembly - and proof that the rules can
/// still fail.
/// </summary>
/// <remarks>
/// <para>
/// A check is discovered rather than registered, so there is no list a reviewer could read to notice that
/// two checks share a code, or that one landed in the wrong area's docs file. These tests are that list:
/// every check in <c>src/</c> is found by reflection and held to <see cref="HealthCheckRegistry.Problems" />,
/// to a heading in its area's docs file and to the probe table.
/// </para>
/// <para>
/// The live half - that every check has a test that makes it fire and one that proves it quiet - lives in the
/// integration project (<c>HealthLiveTestConventionTests</c>), because this project cannot see that assembly.
/// </para>
/// </remarks>
public class HealthCheckConventionTests
{
    /// <summary>Every check class in the studio's check assemblies - the shared engine's and MartenStudio's own.</summary>
    private static IReadOnlyList<Type> Discovered =>
    [
        .. MartenStudio.Services.MartenStudioDescriptor.Instance.HealthCheckAssemblies
            .Select(static x => x.Assembly).Distinct().SelectMany(HealthCheckRegistry.Discover),
    ];

    [Fact]
    public void The_studio_ships_K01_K02_and_K03_and_finds_them_by_reflection()
    {
        StudioHealthChecks.Registry.All.Select(static x => x.Info.Code).Should().Contain(["K01", "K02", "K03"]);
        Discovered.Should().NotBeEmpty("a discovery that finds nothing would pass every rule below for the wrong reason");
    }

    /// <summary>
    /// Review item 1: the registry a real host's container builds is the one with every check in it. A public
    /// constructor taking <c>IEnumerable&lt;Type&gt;</c> made the container build it empty - the container
    /// resolves any enumerable, and picks the greediest constructor.
    /// </summary>
    [Fact]
    public void The_registry_a_real_host_resolves_holds_every_shipped_check()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddMartenStudio();

        using ServiceProvider provider = services.BuildServiceProvider();
        HealthCheckRegistry registry = provider.GetRequiredService<HealthCheckRegistry>();

        registry.All.Select(static x => x.Info.Code).Should().Contain(["K01", "K02", "K03"]);
        registry.All.Should().HaveCount(Discovered.Count);
        typeof(HealthCheckRegistry).GetConstructors().Should().ContainSingle(
            "a second public constructor is one the container may prefer")
            .Which.GetParameters().Select(static x => x.ParameterType).Should().Equal(
                [typeof(PostgresStudio.Services.Settings.StudioDescriptor), typeof(IStudioRuntime)],
                "the descriptor is resolvable only where a studio registered one, and then it is the list to use");
    }

    [Fact]
    public void Every_check_in_the_studio_is_well_formed()
    {
        HealthCheckRegistry.Problems(Discovered).Should().BeEmpty();
    }

    [Fact]
    public void No_two_checks_share_a_code()
    {
        Discovered.Select(static x => HealthCheckRegistry.Describe(x).Code).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_check_lives_in_the_checks_folder()
    {
        // Each assembly's own Services/Health/Checks: the shared engine's and MartenStudio's are one folder until the move.
        Discovered.Should().OnlyContain(
            static x => x.Namespace == x.Assembly.GetName().Name + ".Services.Health.Checks",
            "a check is added by adding its file to its assembly's Services/Health/Checks, and nowhere else");
    }

    /// <summary>The anti-vacuity half: each rule has to be able to fail.</summary>
    [Theory]
    [InlineData(typeof(DuplicateOfK01), "is used by both")]
    [InlineData(typeof(WrongAreaCheck), "is in area")]
    [InlineData(typeof(StatefulCheck), "holds instance state")]
    [InlineData(typeof(UnattributedCheck), "has no [HealthCheck] attribute")]
    [InlineData(typeof(UnknownFeatureCheck), "not in the probe table")]
    [InlineData(typeof(ShapelessCodeCheck), "is not a letter and two digits")]
    [InlineData(typeof(UngatedStatementsCheck), "not behind the activity gate")]
    [InlineData(typeof(UntypedCheck), "without deriving from HealthCheck<TFacts>")]
    [InlineData(typeof(ConstructedCheck), "no parameterless constructor")]
    public void The_rules_catch_a_check_that_breaks_them(Type offender, string problem)
    {
        Type k01 = StudioHealthChecks.Registry.Find("K01")!.Info.CheckType;

        HealthCheckRegistry.Problems([k01, offender]).Should().Contain(x => x.Contains(problem, StringComparison.Ordinal));

        Action build = () => _ = HealthCheckRegistry.ForTypes([k01, offender]);
        build.Should().Throw<InvalidOperationException>("a registry with a broken check refuses to build rather than running some of them");
    }

    [Fact]
    public void A_well_formed_check_passes_every_rule()
    {
        HealthCheckRegistry.Problems([typeof(WellFormedCheck)]).Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------------
    // Docs: one "## <code> <title>" heading per check, in its area's file
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Every_check_has_its_heading_in_its_areas_docs_file()
    {
        List<string> problems = [];

        foreach (HealthCheckDescriptor check in StudioHealthChecks.Registry.All)
        {
            string path = RepositoryRoot.Combine(check.Info.DocsFile.Split('/'));

            if (!File.Exists(path))
            {
                problems.Add(check.Info.Code + ": " + check.Info.DocsFile + " does not exist.");
                continue;
            }

            if (!Headings(File.ReadAllText(path)).Contains(check.Info.DocsHeading))
            {
                problems.Add(check.Info.Code + ": " + check.Info.DocsFile + " has no heading '" + check.Info.DocsHeading + "'.");
            }
        }

        problems.Should().BeEmpty("the page's About link opens that anchor: " + string.Join(" | ", problems));
    }

    [Fact]
    public void The_docs_index_exists_and_links_every_area_file()
    {
        string index = File.ReadAllText(RepositoryRoot.Combine("docs", "health.md"));

        foreach (HealthArea area in Enum.GetValues<HealthArea>())
        {
            index.Should().Contain("(health/" + HealthCheckInfo.AreaFileName(area) + ")", "the index links every area's file");
        }
    }

    /// <summary>The heading reader has to find a real heading and ignore everything that only looks like one.</summary>
    [Theory]
    [InlineData("# T\n\n## K01 AutoCreate in Production\n\ntext", true)]
    [InlineData("## K01 AutoCreate in Production\r\nx", true)]
    [InlineData("### K01 AutoCreate in Production", false)]
    [InlineData("Text about ## K01 AutoCreate in Production", false)]
    [InlineData("```\n## K01 AutoCreate in Production\n```", false)]
    [InlineData("## K01 AutoCreate in production", false)]
    [InlineData("## K01  AutoCreate in Production", false)]
    public void The_heading_reader_finds_a_heading_and_ignores_prose_and_code(string markdown, bool found)
    {
        Headings(markdown).Contains("## K01 AutoCreate in Production").Should().Be(found);
    }

    [Theory]
    [InlineData("K01 AutoCreate in Production", "k01-autocreate-in-production")]
    [InlineData("D01 High-water mark pinned behind a gap", "d01-high-water-mark-pinned-behind-a-gap")]
    [InlineData("T06 Low HOT-update ratio (fillfactor)", "t06-low-hot-update-ratio-fillfactor")]
    public void An_anchor_is_derived_the_way_GitHub_derives_one(string heading, string anchor)
    {
        HealthCheckInfo.Anchor(heading).Should().Be(anchor);
    }

    /// <summary>Review item 10: a release links to its own tag's docs, anything else to main.</summary>
    [Theory]
    [InlineData("0.4.0+3c91afc", "blob/v0.4.0/docs/health/")]
    [InlineData("0.4.0", "blob/v0.4.0/docs/health/")]
    [InlineData("0.4.0-rc.1+3c91afc", "blob/main/docs/health/")]
    [InlineData("0.4", "blob/main/docs/health/")]
    [InlineData("local", "blob/main/docs/health/")]
    [InlineData(null, "blob/main/docs/health/")]
    public void The_docs_link_is_pinned_to_the_release_tag(string? informational, string expected)
    {
        HealthCheckInfo.DocsRootFor(informational).Should().Be("https://github.com/lahma/marten-studio/" + expected);
    }

    // -----------------------------------------------------------------------------------------------
    // The probe table
    // -----------------------------------------------------------------------------------------------

    /// <summary>
    /// A remedy can only name a feature from the table: <see cref="RuntimeFeature" />'s constructor is
    /// private, so nothing outside <c>partial class RuntimeFeature</c> can make one. This pins that it stays so.
    /// </summary>
    [Fact]
    public void A_runtime_feature_can_only_come_from_the_probe_table()
    {
        typeof(RuntimeFeature).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().OnlyContain(static x => x.IsPrivate, "a non-private constructor would let a remedy claim a version the probe does not check");

        RuntimeFeature.All.Should().NotBeEmpty();
        RuntimeFeature.All.Select(static x => x.Name).Should().OnlyHaveUniqueItems();

        IEnumerable<string> fields = typeof(RuntimeFeature)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static x => x.FieldType == typeof(RuntimeFeature))
            .Select(static x => x.Name);

        RuntimeFeature.All.Select(static x => x.Name).Should().BeEquivalentTo(fields, "a feature's name is its field's, which is what a check's RequiresFeature names");
    }

    [Fact]
    public void Every_feature_a_check_requires_is_in_the_probe_table()
    {
        foreach (HealthCheckDescriptor check in StudioHealthChecks.Registry.All)
        {
            HealthCheckAttribute attribute = check.Info.CheckType.GetCustomAttribute<HealthCheckAttribute>()!;

            if (attribute.RequiresFeature is { } name)
            {
                RuntimeFeature.Find(name).Should().NotBeNull("{0} requires {1}", check.Info.Code, name);
                check.Info.Requires.Should().BeSameAs(RuntimeFeature.Find(name));
            }
        }
    }

    /// <summary>A heading line is one that starts with exactly two hashes and a space, outside a fenced block.</summary>
    internal static HashSet<string> Headings(string markdown)
    {
        HashSet<string> headings = new(StringComparer.Ordinal);
        bool fenced = false;

        foreach (string raw in markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.TrimEnd();

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;
                continue;
            }

            if (!fenced && line.StartsWith("## ", StringComparison.Ordinal))
            {
                headings.Add(line);
            }
        }

        return headings;
    }

    // -----------------------------------------------------------------------------------------------
    // Made-up checks, each breaking one rule
    // -----------------------------------------------------------------------------------------------

    private sealed record Nothing;

    private abstract class Quiet : HealthCheck<Nothing>
    {
        public override Task<Nothing> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) => Task.FromResult(new Nothing());

        public override HealthVerdict Evaluate(Nothing facts, HealthEvaluation evaluation) => HealthVerdict.Quiet();
    }

    [HealthCheck("K01", HealthArea.Configuration, HealthCostTier.InProcess, "A second K01")]
    private sealed class DuplicateOfK01 : Quiet;

    [HealthCheck("K80", HealthArea.Tables, HealthCostTier.InProcess, "K in the tables area")]
    private sealed class WrongAreaCheck : Quiet;

    [HealthCheck("K81", HealthArea.Configuration, HealthCostTier.InProcess, "Remembers")]
    private sealed class StatefulCheck : Quiet
    {
        private int sweeps;

        public override HealthVerdict Evaluate(Nothing facts, HealthEvaluation evaluation)
        {
            sweeps++;
            return HealthVerdict.Quiet(sweeps.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class UnattributedCheck : Quiet;

    [HealthCheck("K82", HealthArea.Configuration, HealthCostTier.InProcess, "Needs a feature nobody probes", RequiresFeature = "TimeTravel")]
    private sealed class UnknownFeatureCheck : Quiet;

    [HealthCheck("K8", HealthArea.Configuration, HealthCostTier.InProcess, "A one-digit code")]
    private sealed class ShapelessCodeCheck : Quiet;

    [HealthCheck("T80", HealthArea.Tables, HealthCostTier.Statements, "Reads statements in the open")]
    private sealed class UngatedStatementsCheck : Quiet;

    [HealthCheck("K83", HealthArea.Configuration, HealthCostTier.InProcess, "Hand-rolled")]
    private sealed class UntypedCheck : IHealthCheck
    {
        public Task<object> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) => Task.FromResult<object>(new Nothing());

        public IEnumerable<HealthSamplePart> Sample(object facts) => [];

        public HealthVerdict Evaluate(object facts, HealthEvaluation evaluation) => HealthVerdict.Quiet();
    }

    [HealthCheck("K84", HealthArea.Configuration, HealthCostTier.InProcess, "Wants a constructor argument")]
    private sealed class ConstructedCheck(string greeting) : Quiet
    {
        public override HealthVerdict Evaluate(Nothing facts, HealthEvaluation evaluation) => HealthVerdict.Quiet(greeting);
    }

    [HealthCheck("K85", HealthArea.Configuration, HealthCostTier.InProcess, "Well formed", Gate = HealthGate.None)]
    private sealed class WellFormedCheck : Quiet;
}
